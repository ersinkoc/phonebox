using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record HaListing(string Id, string Name, bool Switchable);

public sealed record HaEntity(string Id, string State, string Name, string? Unit, string? DeviceClass, string? Icon, long Changed);

/// <summary>
/// Other addresses the phone may try for Home Assistant (Home Assistant dashboard widget). A ".local" name
/// (homeassistant.local, which Home Assistant suggests) is found by mDNS, which some Android versions cannot do in a
/// WebView; the phone then tries the IPv4 addresses the name has on this computer. They come second, after the name
/// itself: this computer may be on another network than the phone (a VPN, a second Wi-Fi), with an address the phone
/// cannot reach.
/// </summary>
public static class HaAddress
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Ips, DateTime At)> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The address as the phone gets it: http or https only, and never a user name or password written into it.</summary>
    public static string? ForPhone(string? url)
    {
        var text = url?.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        if (u.UserInfo.Length == 0) return text;
        var clean = new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString();
        return text!.EndsWith('/') ? clean : clean.TrimEnd('/');
    }

    public static string[] Alternatives(string? url, Func<string, string[]>? resolve = null)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")
            || !u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return [];
        var slash = url!.Trim().EndsWith('/');
        return (resolve ?? Lookup)(u.Host).Select(ip =>
        {
            var phone = new UriBuilder(u) { Host = ip }.Uri.ToString();
            return slash ? phone : phone.TrimEnd('/');
        }).Distinct().ToArray();
    }

    // remembered for 10 minutes (a failure for 1), so building the phone's config rarely waits for a lookup
    private static string[] Lookup(string host)
    {
        if (Resolved.TryGetValue(host, out var r) && DateTime.UtcNow - r.At < TimeSpan.FromMinutes(r.Ips.Length == 0 ? 1 : 10)) return r.Ips;
        string[] ips = [];
        try
        {
            var task = System.Net.Dns.GetHostAddressesAsync(host);
            if (task.Wait(TimeSpan.FromSeconds(2)))
                ips = task.Result.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
                    .Select(a => a.ToString()).ToArray();
        }
        catch { /* not found: the phone has only the name */ }
        Resolved[host] = (ips, DateTime.UtcNow);
        return ips;
    }
}

/// <summary>
/// Home Assistant over its REST API with a long-lived access token (Settings › General). The phone only sees the
/// entities listed in its Home Assistant widgets, and may only switch those whose widget allows it.
/// The token never leaves this computer.
/// </summary>
public sealed class HomeAssistant(ConfigStore config, ILogger<HomeAssistant> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Dictionary<string, HaEntity> _states = [];
    private DateTime _statesAt = DateTime.MinValue;
    private string _cacheKey = "";
    private string _statesServer = ""; // the server and token _states was read with
    private string? _error;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // only widgets on enabled pages count, for reading states as for switching: hiding the page or removing the widget turns both off
    private static IEnumerable<WidgetConfig> Widgets(AgentConfig cfg) => cfg.Widgets(enabledPagesOnly: true).Where(w => w.Type == "ha");

    private static bool ControlAllowed(AgentConfig cfg, string id) => Widgets(cfg).Any(w => ItemsOf(w).Any(i => i.Control && i.Id == id));

    private const int MaxItems = 200;

    /// <summary>
    /// The entities a widget shows, each with whether the phone may switch it. A widget lists them either in its
    /// "items" option (one row per entity, with its own "control" flag that falls back to the widget's) or, for
    /// widgets saved before the entity editor, in the comma-separated "entities" option under the widget's flag.
    /// A non-empty "items" list wins over "entities".
    /// </summary>
    public static IEnumerable<(string Id, bool Control)> ItemsOf(WidgetConfig w)
    {
        var widgetControl = w.Bool("control") == true;
        if (w.Opt("items") is { ValueKind: JsonValueKind.Array } items && items.GetArrayLength() > 0)
        {
            // the cap counts usable rows, so a run of unusable ones cannot hide the valid ones after it
            var seen = new HashSet<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (seen.Count >= MaxItems) yield break;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString()!.Trim();
                if (!id.Contains('.') || !seen.Add(id)) continue;
                var control = item.TryGetProperty("control", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : widgetControl;
                yield return (id, control);
            }
            yield break;
        }
        foreach (var id in (w.Str("entities") ?? "").Split([',', '\n', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(e => e.Contains('.')).Take(MaxItems))
            yield return (id, widgetControl);
    }

    public static IEnumerable<string> EntitiesOf(WidgetConfig w) => ItemsOf(w).Select(i => i.Id);

    /// <summary>States of the requested entities (only those used by a widget), refreshed at most every 3 seconds.</summary>
    public async Task<object> StatesAsync(IEnumerable<string> ids)
    {
        // credentials and the visible entities come from one settings snapshot, so a save in between cannot expose
        // the old server's entity under the new widget list
        var cfg = config.Get();
        var ha = cfg.HomeAssistant;
        if (string.IsNullOrWhiteSpace(ha.Url) || string.IsNullOrWhiteSpace(ha.Token))
            return new { configured = false, error = "Set up Home Assistant in Settings › General", entities = Array.Empty<HaEntity>() };
        var (error, states) = await ReadStatesAsync(cfg, ids);
        var list = states.Select(s => s.Entity ?? new HaEntity(s.Id, "unknown", s.Id, null, null, null, 0)).ToList();
        return new { configured = true, error, entities = list };
    }

    /// <summary>
    /// The requested entities that a widget in <paramref name="cfg"/> lists, each with its state from Home Assistant
    /// (null when the latest successful read for these settings did not include it), and the read error if any.
    /// The connection must be set in <paramref name="cfg"/>.
    /// </summary>
    private async Task<(string? Error, List<(string Id, HaEntity? Entity)> States)> ReadStatesAsync(AgentConfig cfg, IEnumerable<string> ids, bool fresh = false)
    {
        var ha = cfg.HomeAssistant;
        var allowed = Widgets(cfg).SelectMany(EntitiesOf).ToHashSet();
        var server = $"{ha.Url!.TrimEnd('/')}\n{ha.Token}";
        var key = $"{server}\n{string.Join(',', allowed.OrderBy(x => x, StringComparer.Ordinal))}";
        await _lock.WaitAsync();
        try
        {
            if (fresh || DateTime.UtcNow - _statesAt > TimeSpan.FromSeconds(3) || _cacheKey != key)
            {
                try
                {
                    using var req = Request(HttpMethod.Get, "/api/states", ha);
                    using var res = await Http.SendAsync(req);
                    if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Home Assistant answered {(int)res.StatusCode} {res.ReasonPhrase}");
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    var map = new Dictionary<string, HaEntity>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var id = e.GetProperty("entity_id").GetString()!;
                        if (!allowed.Contains(id)) continue;
                        var attrs = e.TryGetProperty("attributes", out var a) ? a : default;
                        string? A(string n) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        var changed = e.TryGetProperty("last_changed", out var lc) && DateTimeOffset.TryParse(lc.GetString(), out var dt) ? dt.ToUnixTimeMilliseconds() : 0;
                        map[id] = new HaEntity(id, e.GetProperty("state").GetString() ?? "", A("friendly_name") ?? id, A("unit_of_measurement"),
                            A("device_class"), A("icon"), changed);
                    }
                    _states = map;
                    _statesServer = server;
                    _error = null;
                    _cacheKey = key;
                    _statesAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _error = ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message;
                    // states read from another server or token must not answer for these settings
                    if (_statesServer != server) { _states = []; _statesServer = server; }
                    _statesAt = DateTime.UtcNow;
                    _cacheKey = key;
                    log.LogDebug("home assistant: {Msg}", _error);
                }
            }
            var list = ids.Where(allowed.Contains).Select(id => (id, _states.TryGetValue(id, out var s) ? s : null)).ToList();
            return (_error, list);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Makes the next read go to Home Assistant, after a service call that may have changed a state.</summary>
    private async Task InvalidateStatesAsync()
    {
        await _lock.WaitAsync();
        try { _statesAt = DateTime.MinValue; }
        finally { _lock.Release(); }
    }

    /// <summary>Every entity Home Assistant knows (id and name), for the entity picker in Settings. Local only: the phone never gets this.</summary>
    public async Task<object> ListEntitiesAsync()
    {
        var ha = config.Get().HomeAssistant;
        if (string.IsNullOrWhiteSpace(ha.Url) || string.IsNullOrWhiteSpace(ha.Token))
            return new { configured = false, error = "Set up Home Assistant in Settings › General", entities = Array.Empty<object>() };
        try
        {
            using var req = Request(HttpMethod.Get, "/api/states");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return new { configured = true, error = $"Home Assistant answered {(int)res.StatusCode} {res.ReasonPhrase}", entities = Array.Empty<object>() };
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<HaListing>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("entity_id").GetString() is not { } id) continue;
                string? name = e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("friendly_name", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                list.Add(new HaListing(id, name ?? id, Switchable(id.Split('.')[0])));
            }
            return new { configured = true, error = (string?)null, entities = list.OrderBy(x => x.Id, StringComparer.Ordinal).ToList() };
        }
        catch (Exception ex) { return new { configured = true, error = ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message, entities = Array.Empty<object>() }; }
    }

    /// <summary>
    /// The dashboards and their views ("dashboard/view" paths with a name), for the Home Assistant dashboard widget's
    /// picker in Settings. Read over Home Assistant's WebSocket API, the only one that lists dashboards (which needs an
    /// administrator's token). Local only: the phone never gets this.
    /// </summary>
    public async Task<object> ListDashboardsAsync()
    {
        var ha = config.Get().HomeAssistant;
        if (string.IsNullOrWhiteSpace(ha.Url) || string.IsNullOrWhiteSpace(ha.Token)
            || !Uri.TryCreate(ha.Url.TrimEnd('/') + "/api/websocket", UriKind.Absolute, out var http) || http.Scheme is not ("http" or "https"))
            return new { configured = false, error = "Set up Home Assistant in Settings › General", views = Array.Empty<object>() };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        try
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await ws.ConnectAsync(new UriBuilder(http) { Scheme = http.Scheme == "https" ? "wss" : "ws" }.Uri, ct);
            var nextId = 0;
            async Task<JsonElement> Receive()
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                System.Net.WebSockets.WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(chunk, ct);
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) throw new InvalidOperationException("Home Assistant closed the connection");
                    buffer.Write(chunk, 0, r.Count);
                    if (buffer.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Home Assistant sent too much");
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(buffer.ToArray());
                return doc.RootElement.Clone();
            }
            Task Send(Dictionary<string, object?> message) =>
                ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
            // the result, or the error's code and message when Home Assistant refused the command
            async Task<(JsonElement? Result, string? Code, string? Error)> Call(Dictionary<string, object?> command)
            {
                var id = ++nextId;
                command["id"] = id;
                await Send(command);
                while (true)
                {
                    var m = await Receive();
                    if (!m.TryGetProperty("id", out var mid) || mid.ValueKind != JsonValueKind.Number || mid.GetInt32() != id) continue;
                    if (m.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                        return (m.TryGetProperty("result", out var result) ? result : null, null, null);
                    var e = m.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object ? err : default;
                    string? Field(string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    return (null, Field("code") ?? "unknown_error", Field("message") ?? "no reason given");
                }
            }
            string? problem = null; // a refused command: the list is shown, with this next to it

            await Receive(); // auth_required
            await Send(new() { ["type"] = "auth", ["access_token"] = ha.Token });
            var auth = await Receive();
            if (!auth.TryGetProperty("type", out var authType) || authType.GetString() != "auth_ok")
                return new { configured = true, error = "The token was rejected", phoneUrl = HaAddress.ForPhone(ha.Url), alternatives = HaAddress.Alternatives(HaAddress.ForPhone(ha.Url)), views = Array.Empty<object>() };

            // the default dashboard (at /lovelace, read without a url_path) and the ones added in Settings › Dashboards.
            // Newer Home Assistant versions list the default one too, with the title it was given.
            var dashboards = new List<(string? Path, string Name)> { (null, "Overview") };
            var dashboardList = await Call(new() { ["type"] = "lovelace/dashboards/list" });
            if (dashboardList.Error is { } listError) problem = "Home Assistant did not list its dashboards: " + listError;
            if (dashboardList.Result is { ValueKind: JsonValueKind.Array } list)
                foreach (var d in list.EnumerateArray())
                    if (d.TryGetProperty("url_path", out var p) && p.GetString() is { Length: > 0 } path)
                    {
                        var name = d.TryGetProperty("title", out var n) && n.GetString() is { Length: > 0 } title ? title : path;
                        if (path == "lovelace") dashboards[0] = (null, name);
                        else dashboards.Add((path, name));
                    }

            var views = new List<object>();
            foreach (var (path, name) in dashboards)
            {
                var root = path ?? "lovelace";
                var command = new Dictionary<string, object?> { ["type"] = "lovelace/config" };
                if (path is not null) command["url_path"] = path;
                var listed = 0;
                var read = await Call(command);
                // config_not_found: a dashboard made by a strategy, which has no configuration of its own (see below)
                if (read.Error is { } readError && read.Code != "config_not_found") problem ??= $"Home Assistant did not give the views of {name}: {readError}";
                if (read.Result is { ValueKind: JsonValueKind.Object } conf && conf.TryGetProperty("views", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    foreach (var v in vs.EnumerateArray())
                    {
                        var key = v.TryGetProperty("path", out var vp) && vp.GetString() is { Length: > 0 } viewPath ? viewPath : listed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var title = v.TryGetProperty("title", out var vt) && vt.GetString() is { Length: > 0 } viewTitle ? viewTitle : key;
                        views.Add(new { path = $"{root}/{key}", name = $"{name} › {title}" });
                        listed++;
                    }
                // a dashboard made by a strategy (the default one, until it is taken over) has no views of its own to list
                if (listed == 0) views.Add(new { path = root, name });
            }
            // closing is a courtesy: at most two seconds, and the views are returned either way
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, closing.Token);
            }
            catch { /* the server did not answer the close in time */ }
            return new { configured = true, error = problem, phoneUrl = HaAddress.ForPhone(ha.Url), alternatives = HaAddress.Alternatives(HaAddress.ForPhone(ha.Url)), views };
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Home Assistant dashboards");
            return new { configured = true, error = ex is OperationCanceledException ? "Home Assistant did not answer" : ex.Message, phoneUrl = HaAddress.ForPhone(ha.Url), alternatives = HaAddress.Alternatives(HaAddress.ForPhone(ha.Url)), views = Array.Empty<object>() };
        }
    }

    private static bool Switchable(string domain) => domain is "light" or "switch" or "fan" or "input_boolean" or "automation" or "siren" or "humidifier"
        or "group" or "cover" or "media_player" or "scene" or "script" or "button" or "input_button" or "lock" or "vacuum";

    /// <summary>Switches an entity (toggle, press, run…) if a widget lists it with "Allow switching" on.</summary>
    public async Task<string?> ToggleAsync(string id)
    {
        // the permission and the connection come from one settings snapshot
        var cfg = config.Get();
        const string notAllowed = "switching this entity is not allowed in the widget's settings";
        if (!ControlAllowed(cfg, id)) return notAllowed;
        var ha = cfg.HomeAssistant;
        if (string.IsNullOrWhiteSpace(ha.Url) || string.IsNullOrWhiteSpace(ha.Token)
            || !Uri.TryCreate(ha.Url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            return "Set up Home Assistant in Settings › General";
        var domain = id.Split('.')[0];
        var state = "";
        if (domain is "lock" or "vacuum")
        {
            // The service depends on the current state, so it must come from a successful read with the settings the
            // call goes to: a failed read or another server's cached state could unlock a door or start a vacuum.
            // a fresh read, not the widgets' 3-second cache: a door unlocked by hand a moment ago must get "lock"
            var (error, states) = await ReadStatesAsync(cfg, [id], fresh: true);
            if (error is not null) return error;
            // "unknown" (or nothing) is no state either: guessing would start a vacuum or lock a door on a hunch
            if (states.FirstOrDefault().Entity is not { State: not ("unavailable" or "unknown") } entity || string.IsNullOrWhiteSpace(entity.State))
                return "Home Assistant does not report this entity's state";
            state = entity.State;
            // the read awaited Home Assistant: the widget may have been hidden or the connection replaced meanwhile
            var now = config.Get();
            if (!ControlAllowed(now, id)) return notAllowed;
            if (now.HomeAssistant.Url != ha.Url || now.HomeAssistant.Token != ha.Token) return "Home Assistant settings changed; try again";
        }
        var (svcDomain, service) = domain switch
        {
            "light" or "switch" or "fan" or "input_boolean" or "automation" or "siren" or "humidifier" or "group" => ("homeassistant", "toggle"),
            "cover" => ("cover", "toggle"),
            "media_player" => ("media_player", "media_play_pause"),
            "scene" => ("scene", "turn_on"),
            "script" => ("script", "turn_on"),
            "button" => ("button", "press"),
            "input_button" => ("input_button", "press"),
            "lock" => ("lock", state == "locked" ? "unlock" : "lock"),
            "vacuum" => ("vacuum", state == "cleaning" ? "return_to_base" : "start"),
            _ => ("", ""),
        };
        if (service == "") return $"{domain} entities cannot be switched";
        var submitted = false;
        try
        {
            using var req = Request(HttpMethod.Post, $"/api/services/{svcDomain}/{service}", ha);
            req.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id }), Encoding.UTF8, "application/json");
            submitted = true;
            using var res = await Http.SendAsync(req);
            log.LogInformation("home assistant: {Service} {Id} -> {Code}", $"{svcDomain}.{service}", id, (int)res.StatusCode);
            return res.IsSuccessStatusCode ? null : $"Home Assistant answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message; }
        finally
        {
            // a call whose answer was lost may still have switched the entity: the cached state is no longer reliable
            if (submitted) await InvalidateStatesAsync();
        }
    }

    /// <summary>Turns an entity on or off (the charge guard's smart plug; set in the local-only settings).</summary>
    public async Task<string?> SwitchAsync(string id, bool on) =>
        (await SwitchAsync(id, on, config.Get().HomeAssistant)).Error;

    internal sealed record PowerSwitchResult(string? Error, bool Submitted);

    internal async Task<PowerSwitchResult> SwitchAsync(string id, bool on, HomeAssistantConfig settings)
    {
        if (!id.Contains('.')) return new("not an entity id", false);
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https"))
            return new("Home Assistant address must be an absolute HTTP or HTTPS URL", false);
        var submitted = false;
        try
        {
            using var req = Request(HttpMethod.Post, $"/api/services/homeassistant/{(on ? "turn_on" : "turn_off")}", settings);
            req.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id }), Encoding.UTF8, "application/json");
            submitted = true;
            using var res = await Http.SendAsync(req);
            _statesAt = DateTime.MinValue;
            return new(res.IsSuccessStatusCode ? null : $"Home Assistant answered {(int)res.StatusCode}", true);
        }
        catch (Exception ex) { return new(ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message, submitted); }
    }

    /// <summary>Settings › Test: checks a URL and token (possibly unsaved).</summary>
    public static async Task<string> TestAsync(string url, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/api/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode ? "Connected" : res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "The token was rejected" : $"Home Assistant answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "No answer (check the address)" : ex.Message; }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, HomeAssistantConfig? settings = null)
    {
        var ha = settings ?? config.Get().HomeAssistant;
        var req = new HttpRequestMessage(method, ha.Url!.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ha.Token);
        return req;
    }
}
