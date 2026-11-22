using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>
/// Plex music control. The queue is read from a Plex Companion player (Caldera Amp, by default on this computer) and
/// the user's own Plex server; a playlist or a queue item is started on that player. The token goes to the server only,
/// never to the player, and the phone sends ids only.
/// </summary>
public sealed class PlexControl : IDisposable
{
    public const string DefaultPlayerUrl = "http://127.0.0.1:32500";
    private const string NotConfigured = "Connect Plex in desktop settings";
    private const string PlayerDown = "Start Caldera Amp (or another Plex player) on this computer";
    private const string ServerDown = "The Plex server is unavailable";
    private const string Unreadable = "Plex sent an answer that could not be read";
    private const string NoWidget = "Add a Plex widget to an enabled page";
    private const string NoPlaylistWidget = "Add a Plex playlists widget to an enabled page";
    private const string SettingsChanged = "The Plex settings changed; try again";
    private const int MaxBody = 1_000_000;

    /// <summary>An error whose text is safe to show on the phone.</summary>
    private sealed class PlexError(string message, bool transient = false) : Exception(message) { public bool Transient { get; } = transient; }
    private sealed record Settings(string Url, string Token, string Player);
    /// <summary>A published snapshot with the settings it was read with, and the server key of each queue item (for skipTo). Transient: a failed read that the last good snapshot may stand in for.</summary>
    private sealed record Entry(JsonObject Value, Settings Settings, IReadOnlyDictionary<string, string> Keys, long ReadAt, bool Transient = false);
    private sealed record Item(string Id, string Key, string Name, string Artist);

    // one id per agent process, so the server sees one client
    private static readonly string ClientId = RandomNumberGenerator.GetHexString(32, lowercase: true);
    private static readonly Regex MetadataKey = new(@"\A/library/metadata/\d{1,12}\z", RegexOptions.CultureInvariant);
    private readonly ConfigStore _config;
    private readonly HttpClient _http;
    private readonly Func<long> _now;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly TimeSpan _stall;
    private volatile Entry? _snapshot;
    private volatile Entry? _good; // the last read that worked, kept for a minute so one bad poll does not empty the widget
    private volatile Tuple<string, string>? _identity; // (server URL, its machine id)
    private long _command;

    // the stall token of Send is the only time limit: HttpClient.Timeout does not cover a body read after the headers
    public PlexControl(ConfigStore config) : this(config, new HttpClient(NewPlexHandler()) { Timeout = Timeout.InfiniteTimeSpan }) { }
    internal PlexControl(ConfigStore config, HttpClient http, Func<long>? now = null, TimeSpan? stall = null)
    { _config = config; _http = http; _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); _stall = stall ?? TimeSpan.FromSeconds(8); }

    /// <summary>A redirect is never followed (it could carry the token to another host); a pooled connection is dropped after five minutes so a changed DNS answer is picked up.</summary>
    internal static SocketsHttpHandler NewPlexHandler() => new() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>Whether an enabled page shows a Plex widget. Read from the current config on every call.</summary>
    internal static bool Shown(AgentConfig cfg) => cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type is "plex-queue" or "plex-playlists");

    /// <summary>Whether an enabled page shows a plex-playlists widget that shows this action. A widget with a non-empty "ids" list shows only those; any other "ids" counts as absent.</summary>
    internal static bool PlaylistShown(AgentConfig cfg, string? id) => cfg.Widgets(enabledPagesOnly: true).Where(w => w.Type == "plex-playlists").Any(w =>
        w.Opt("ids") is not { ValueKind: JsonValueKind.Array } ids || ids.GetArrayLength() == 0
        || ids.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == id));

    /// <summary>A server or player base address: absolute http or https, no user info, query or fragment. Anything else is null.</summary>
    public static string? NormalizeUrl(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (text.Length is 0 or > 2048 || text.Any(c => c <= ' ' || c == '?' || c == '#')) return null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0 && uri.Host.Length > 0 ? text : null;
    }

    private Settings Current()
    {
        var c = _config.Get().Controls;
        return new(NormalizeUrl(c.PlexUrl) ?? "", new string((c.PlexToken ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim(), NormalizeUrl(c.PlexPlayerUrl) ?? DefaultPlayerUrl);
    }
    private static bool Digits(string? value) => value is { Length: > 0 and <= 12 } && value.All(char.IsAsciiDigit);
    private static string? Text(JsonNode? node) => node is JsonValue ? node.ToString() : null;
    private static string Cap(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;
    private long NextCommand() => Interlocked.Increment(ref _command);

    /// <summary>
    /// One request. The token header goes to the server only. A server answer other than success is an error; for the
    /// player a non-success answer is returned and the caller decides what it means. A body is never read from a
    /// non-success answer or past the size cap, and one stall limit covers the headers and every read of the body.
    /// </summary>
    private async Task<(bool Ok, string Body)> Send(HttpMethod method, string url, bool server, string? token, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_stall);
        try
        {
            using var request = new HttpRequestMessage(method, url);
            // the player is asked with no Accept header: that is what answered XML in the hand check
            if (server) request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientId);
            request.Headers.TryAddWithoutValidation("X-Plex-Product", "PhoneBox");
            if (server) request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (server && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PlexError("Check the Plex token in desktop settings");
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                if (server) throw new PlexError(ServerDown, transient: code is >= 300 and < 400 or >= 500); // a redirect is an error too: it is not followed
                return (false, "");
            }
            if (response.Content.Headers.ContentLength > MaxBody) throw new PlexError(Unreadable);
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, limit.Token)) > 0)
            {
                if (buffer.Length + read > MaxBody) throw new PlexError(Unreadable);
                buffer.Write(chunk, 0, read);
            }
            return (true, System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (server && (ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException))
        { throw new PlexError("The Plex server certificate is not trusted"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { throw new PlexError(server ? ServerDown : PlayerDown, transient: true); }
    }

    /// <summary>The MediaContainer object of a server answer. Any other kind of root or container is an answer that cannot be read.</summary>
    private static JsonObject Container(string body) =>
        JsonNode.Parse(body) is JsonObject root && root["MediaContainer"] is JsonObject container ? container : throw new PlexError(Unreadable);

    /// <summary>The Metadata list: a missing or non-array value is empty, an element that is not an object is an answer that cannot be read.</summary>
    private static List<JsonObject> Items(JsonObject container)
    {
        var list = new List<JsonObject>();
        if (container["Metadata"] is JsonArray array) foreach (var node in array) list.Add(node as JsonObject ?? throw new PlexError(Unreadable));
        return list;
    }

    private async Task<string> Identity(Settings s, bool fresh, CancellationToken ct)
    {
        if (!fresh && _identity is { } known && known.Item1 == s.Url) return known.Item2;
        var (_, body) = await Send(HttpMethod.Get, s.Url + "/identity", true, s.Token, ct);
        var id = Text(Container(body)["machineIdentifier"]);
        if (string.IsNullOrEmpty(id) || id.Length > 128) throw new PlexError(Unreadable);
        _identity = Tuple.Create(s.Url, id);
        return id;
    }

    /// <summary>The music timeline of the player, or null when it has none. A DTD is refused and nothing is fetched by the reader.</summary>
    private async Task<XElement?> Timeline(Settings s, CancellationToken ct)
    {
        var (ok, body) = await Send(HttpMethod.Get, $"{s.Player}/player/timeline/poll?wait=0&commandID={NextCommand()}", false, null, ct);
        if (!ok) throw new PlexError(PlayerDown, transient: true);
        using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Root?.Elements("Timeline").FirstOrDefault(e => (string?)e.Attribute("type") == "music");
    }

    private static JsonObject Track(Item item) => new() { ["id"] = item.Id, ["name"] = item.Name, ["artist"] = item.Artist };
    private static JsonObject Idle(bool playing, string? error = null)
    {
        var value = new JsonObject { ["connected"] = true, ["playing"] = playing, ["current"] = null, ["queue"] = new JsonArray() };
        if (error is not null) value["error"] = error;
        return value;
    }

    private async Task<Entry> Read(Settings s, CancellationToken ct)
    {
        JsonObject value;
        Dictionary<string, string> keys = [];
        var transient = false;
        try
        {
            if (s.Url.Length == 0 || s.Token.Length == 0) throw new PlexError(NotConfigured);
            var timeline = await Timeline(s, ct);
            var state = (string?)timeline?.Attribute("state");
            var active = timeline is not null && state is "playing" or "paused" or "buffering";
            var machine = await Identity(s, fresh: !active, ct);
            var playing = state == "playing";
            var queueId = (string?)timeline?.Attribute("playQueueID");
            if (timeline is null || state is not ("playing" or "paused" or "buffering")) value = Idle(false);
            else if ((string?)timeline.Attribute("machineIdentifier") != machine) value = Idle(playing, "The player is playing from another Plex server");
            else if (!Digits(queueId)) value = Idle(playing);
            else
            {
                var (_, body) = await Send(HttpMethod.Get, $"{s.Url}/playQueues/{queueId}", true, s.Token, ct);
                var items = new List<Item>();
                foreach (var node in Items(Container(body)))
                {
                    var id = Text(node["playQueueItemID"]); var key = Text(node["key"]);
                    if (!Digits(id) || key is null || !MetadataKey.IsMatch(key)) continue;
                    items.Add(new(id!, key, Cap(Text(node["title"]), 200), Cap(Text(node["grandparentTitle"]) ?? Text(node["originalTitle"]), 200)));
                }
                var at = items.FindIndex(i => i.Id == (string?)timeline.Attribute("playQueueItemID"));
                var queue = at < 0 ? [] : items.Skip(at + 1).Take(30).ToList();
                foreach (var item in queue) keys[item.Id] = item.Key;
                value = new() { ["connected"] = true, ["playing"] = playing, ["current"] = at < 0 ? null : Track(items[at]),
                    ["queue"] = new JsonArray(queue.Select(i => (JsonNode)Track(i)).ToArray()) };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // ArgumentException: a JSON object with a repeated property name throws it when a property is read
        catch (Exception ex) when (ex is PlexError or JsonException or XmlException or InvalidOperationException or FormatException or ArgumentException)
        { value = new() { ["connected"] = false, ["error"] = ex is PlexError ? ex.Message : Unreadable }; keys.Clear(); transient = ex is PlexError { Transient: true }; }
        return new(value, s, keys, _now(), transient);
    }

    private Entry? Cached(Settings s)
    {
        var entry = _snapshot;
        return entry is not null && entry.Settings == s && _now() - entry.ReadAt < 5000 ? entry : null;
    }

    /// <summary>
    /// A read that worked is remembered. A network error, a stall or a 5xx within a minute of it (same settings) returns that
    /// snapshot instead of the error, so one bad poll does not empty the widget. Any other failure forgets it.
    /// </summary>
    private Entry Keep(Entry entry)
    {
        if (entry.Value["connected"]?.GetValue<bool>() == true) { _good = entry; return entry; }
        if (!entry.Transient) { _good = null; return entry; }
        return _good is { } good && good.Settings == entry.Settings && _now() - good.ReadAt < 60_000
            ? new(good.Value, good.Settings, good.Keys, entry.ReadAt) : entry;
    }

    private async Task<Entry> Latest(CancellationToken ct)
    {
        if (Cached(Current()) is { } fast) return fast;
        await _readGate.WaitAsync(ct);
        try
        {
            // The answers describe one set of settings. A change while they are read discards the result;
            // read once more, then report the change instead of mixing the two.
            for (var attempt = 0; ; attempt++)
            {
                var s = Current();
                if (Cached(s) is { } cached) return cached;
                var entry = await Read(s, ct);
                if (Current() == s) return _snapshot = Keep(entry);
                if (attempt > 0) return new(new() { ["connected"] = false, ["error"] = SettingsChanged }, s, new Dictionary<string, string>(), _now());
            }
        }
        finally { _readGate.Release(); }
    }

    /// <summary>{ connected, playing, current, queue[] } with tracks { id, name, artist }, or { connected:false, error }. Nothing else from Plex is passed on.</summary>
    public async Task<JsonObject> Snapshot(CancellationToken ct) =>
        Shown(_config.Get()) ? (await Latest(ct)).Value : new() { ["connected"] = false, ["error"] = NoWidget };

    /// <summary>The same read for the settings page's Check connection. Not gated: the user can check before a Plex widget is on a page. Local only.</summary>
    public async Task<JsonObject> Check(CancellationToken ct) => (await Latest(ct)).Value;

    /// <summary>
    /// The audio playlists of the server, for the settings page picker: [{ id, name }]. A failed read is { error } with the
    /// same text Check shows, so a refused token is not mistaken for a server without playlists. Not gated; local only.
    /// </summary>
    public async Task<JsonNode> Playlists(CancellationToken ct)
    {
        var s = Current();
        if (s.Url.Length == 0 || s.Token.Length == 0) return new JsonArray();
        try
        {
            var (_, body) = await Send(HttpMethod.Get, s.Url + "/playlists?playlistType=audio", true, s.Token, ct);
            var list = new JsonArray();
            foreach (var node in Items(Container(body)))
            {
                var id = Text(node["ratingKey"]);
                if (Digits(id) && list.Count < 500) list.Add(new JsonObject { ["id"] = id, ["name"] = Cap(Text(node["title"]), 200) });
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PlexError or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return new JsonObject { ["error"] = ex is PlexError ? ex.Message : Unreadable }; }
    }

    public async Task<DeckResult> Act(string action, string? id, CancellationToken ct)
    {
        try
        {
            var s = Current();
            // the gate comes first: with no Plex widget on an enabled page nothing is sent
            if (action == "play-queued" && !Shown(_config.Get())) return new(false, NoWidget);
            if (action == "playlist" && !PlaylistShown(_config.Get(), id)) return new(false, NoPlaylistWidget);
            if (action == "play-queued")
            {
                if (!Digits(id)) return new(false, "That queue item is no longer available");
                var entry = await Latest(ct);
                if (entry.Value["connected"]?.GetValue<bool>() != true) return new(false, entry.Value["error"]?.GetValue<string>() ?? ServerDown);
                // the queue was read with settings that changed after this call began: its keys may belong to another server
                if (entry.Settings != s) return new(false, SettingsChanged);
                if (!entry.Keys.TryGetValue(id!, out var key)) return new(false, "That queue item is no longer available");
                return await Command(s, $"skipTo?type=music&key={Uri.EscapeDataString(key)}&playQueueItemID={id}", ct);
            }
            if (action == "playlist")
            {
                var configured = _config.Get().Controls.Actions.FirstOrDefault(a => a.Id == id && a.Kind == "plex-playlist");
                if (configured is null) return new(false, "Choose a Plex playlist in desktop settings");
                if (!configured.Enabled) return new(false, "Control is disabled");
                if (!Digits(configured.Target)) return new(false, "Choose a Plex playlist in desktop settings");
                if (s.Url.Length == 0 || s.Token.Length == 0) return new(false, NotConfigured);
                var machine = await Identity(s, fresh: false, ct);
                // a settings change during a wait stops the start: the next request would go out with the old address or token
                if (Current() != s) return new(false, SettingsChanged);
                var (_, body) = await Send(HttpMethod.Post,
                    $"{s.Url}/playQueues?type=audio&playlistID={configured.Target}&shuffle=0&continuous=0&repeat=0", true, s.Token, ct);
                var container = Container(body);
                var queueId = Text(container["playQueueID"]);
                var selected = Text(container["playQueueSelectedMetadataItemID"]);
                var key = Digits(selected) ? "/library/metadata/" + selected : Text(Items(container).FirstOrDefault()?["key"]);
                if (!Digits(queueId) || key is null || !MetadataKey.IsMatch(key)) throw new PlexError(Unreadable);
                // the queue already made on the old server is left as it is, not started against another connection
                if (Current() != s) return new(false, SettingsChanged);
                // the address fields come from the configured server, never from the player or the phone
                var server = new Uri(s.Url);
                return await Command(s, "playMedia?type=music&machineIdentifier=" + Uri.EscapeDataString(machine)
                    + "&protocol=" + server.Scheme + "&address=" + Uri.EscapeDataString(server.Host) + "&port=" + server.Port
                    + "&key=" + Uri.EscapeDataString(key) + "&containerKey=" + Uri.EscapeDataString($"/playQueues/{queueId}?own=1") + "&offset=0", ct);
            }
            return new(false, "Unknown Plex control");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PlexError or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return new(false, ex is PlexError ? ex.Message : Unreadable); }
        finally { _snapshot = null; }
    }

    private async Task<DeckResult> Command(Settings s, string pathAndQuery, CancellationToken ct)
    {
        var (ok, _) = await Send(HttpMethod.Get, $"{s.Player}/player/playback/{pathAndQuery}&commandID={NextCommand()}", false, null, ct);
        return ok ? new(true) : new(false, "The Plex player refused the command");
    }

    public void Dispose() { _http.Dispose(); _readGate.Dispose(); }
}
