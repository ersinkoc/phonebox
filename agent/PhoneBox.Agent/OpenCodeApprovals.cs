using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>One pending permission request from OpenCode's queue.</summary>
/// <param name="Directory">The project directory the request was polled under (null = the server's own location).</param>
/// <param name="Save">The patterns Allow always would save for the project (OpenCode proposes them); empty = none.</param>
internal sealed record OpenCodePermission(string Id, string SessionId, string Action, string[] Resources,
    string? Message, string? Directory, string[]? Save = null);

/// <summary>One pending form from OpenCode — the structured channel agent questions travel on.</summary>
/// <param name="Directory">The project directory the form was polled under (null = the server's own location).</param>
internal sealed record OpenCodeForm(string Id, string SessionId, string? Title, OpenCodeFormField[] Fields, string? Directory);

/// <summary>One question a form asks: a title, a longer question, and the answers to pick from.</summary>
/// <param name="Options">The choices as (value, label, description); a form without options wants a typed answer.</param>
internal sealed record OpenCodeFormField(string Key, string? Title, string? Description, bool Custom,
    (string Value, string Label, string? Description)[] Options);

/// <summary>
/// OpenCode permission prompts on the phone. OpenCode 2.x runs one background service per user account
/// (opencode serve --service) and keeps its address and password in ~/.local/state/opencode/service.json — one for
/// the native account, one more for each running WSL distro that has one (see <see cref="OpenCodeServices"/>). All
/// of them are polled here over 127.0.0.1 — only while the user turned OpenCode prompts on (off by default,
/// Settings › General) and a phone is listening — and every pending request is offered through
/// <see cref="ApprovalStore"/>, the same Allow / Deny card Claude Code uses. The phone's answer is posted back to
/// the service that asked: allow → "once", always → "always" (OpenCode saves the pattern for the project), deny →
/// "reject". A prompt answered on the desktop first disappears from the queue, the offer is withdrawn and nothing is
/// posted. No answer in time (or no phone) also posts nothing, and OpenCode's own prompt on the desktop stays as
/// usual.
/// </summary>
public sealed class OpenCodeApprovals : BackgroundService
{
    /// <summary>Fields of ~/.local/state/opencode/service.json (id and pid are not needed here).</summary>
    internal sealed record ServiceInfo(string Url, string Password, string Version);

    /// <summary>Everything tracked per discovered service: its file stamp, its queue scopes, its 401 flag.</summary>
    private sealed class ServiceState
    {
        public required ServiceInfo Info;
        public string Path = "";                     // the service.json this came from (a 401 re-reads it)
        public bool Reauth;                          // a 401: the service restarted with a new password
        public List<string?> Directories = [null];   // project directories to poll; null = the server's own
        public long NextDirectoryRefresh;
    }

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private const int MaxOffered = 8;          // the phone shows one card with "1 of n"; more than 8 is a stuck batch
    private const int PollMs = 1000;           // how often the queue is read; the offer itself is pushed to the phone
    private const int DirectoryRefreshMs = 30_000; // how often the list of project directories is re-read
    private const int PathScanMs = 15_000;     // how often new services are looked for (native path, WSL distros)
    private const int MaxDirectories = 12;     // project directories polled per service
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);

    private readonly ApprovalStore _approvals;
    private readonly ConfigStore? _config;
    private readonly ILogger<OpenCodeApprovals> _log;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string[]? _servicePaths;    // fixed by tests; null = discover native + WSL

    // the offers in flight: "service.json path|request id" → the AskAsync task, the cancel that withdraws it when the
    // request vanishes from the queue (answered on the desktop, rejected with the session, service gone), and the
    // moment the card's own window ends (offers that nobody answered free their slot then, not only when the
    // AskAsync task happens to finish)
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cancel, long ExpiresAt)> _offered = new();
    // the same for forms (agent questions): form id → offer
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cancel, long ExpiresAt)> _formOffers = new();
    // forms the phone answered with "Got it" (it cannot answer them): not offered again while they stay queued, so
    // the card does not bounce back every poll; forgotten when the form leaves the queue
    private readonly ConcurrentDictionary<string, byte> _dismissed = new();
    // requests whose window ran out with no answer: their slot is free, but they are not offered again while they
    // stay queued (OpenCode's own prompt is on the desktop); forgotten when the request leaves the queue
    private readonly HashSet<string> _expired = new();
    private readonly Dictionary<string, ServiceState> _services = new(); // by service.json path
    // the stamps are also read on a 401 (GetAsync runs on the parallel queue reads' threads), so they are concurrent
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written)> _stamps = new(); // by service.json path
    private IReadOnlyList<(string Path, ServiceInfo Info)>? _wslList;  // identity of the last WSL scan's list
    private List<string> _paths = [];                    // the service.json files watched
    private long _nextPathScan;

    public OpenCodeApprovals(ApprovalStore approvals, ILogger<OpenCodeApprovals> log, ConfigStore? config = null) :
        this(approvals, log, config, null, null, null) { }

    internal OpenCodeApprovals(ApprovalStore approvals, ILogger<OpenCodeApprovals> log, ConfigStore? config,
        HttpMessageHandler? http, TimeProvider? clock, string[]? servicePaths)
    {
        _approvals = approvals;
        _config = config;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _servicePaths = servicePaths;
        _http = new HttpClient(http ?? new HttpClientHandler()) { Timeout = HttpTimeout };
    }

    private (bool Enabled, bool OnlyWhenAway, int TimeoutSec, bool WatchWsl, int CountdownSec) Settings =>
        _config?.Get().OpenCode is { } c
            ? (c.PhoneApprovals, c.OnlyWhenAway, c.TimeoutSec, c.WatchWsl, c.AlwaysCountdownSec)
            : (false, false, 90, false, 10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogDebug("opencode approvals: {Msg}", ex.Message); }
            try { await Task.Delay(PollMs, ct); } catch (OperationCanceledException) { break; }
        }
        WithdrawAll();
        _http.Dispose();
    }

    /// <summary>One pass over every OpenCode queue: offer new requests, withdraw answered ones. Also the test entry point.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var (enabled, onlyWhenAway, timeoutSec, watchWsl, countdownSec) = Settings;
        if (!enabled) { WithdrawAll(); return; }

        // offers whose window ended (nobody answered in time) free their slot again here; the request is usually
        // still in OpenCode's queue, so the next round offers it afresh with a new window
        FreeEndedOffers();

        // The queues are only read while a phone is actually listening: without one there is nothing to offer, and
        // OpenCode's own prompt on the desktop keeps waiting all the same. Offers already on their way keep their
        // window; the next poll with the phone back re-reads everything.
        if (!ApprovalStore.PhoneConnected) return;
        var services = CurrentServices(watchWsl);
        if (services.Count == 0) { WithdrawAll(); return; }

        // the requests and questions live per service; the offer keys carry the service.json path, so two services
        // (even two on the same port) can never withdraw each other's cards
        var live = new HashSet<string>();
        var liveForms = new HashSet<string>();
        var queues = new Dictionary<string, (ServiceState State, OpenCodePermission Request)>();
        var formQueues = new Dictionary<string, (ServiceState State, OpenCodeForm Form)>();
        // services whose permission or question queue could not be read this round (a timeout, an error status, a bad
        // body): the cards from that queue are neither withdrawn nor re-offered, since an unread queue is not an empty
        // one. The two queues are tracked apart, so one failing never holds back what the other confirmed.
        var unreadRequests = new List<string>();
        var unreadForms = new List<string>();
        foreach (var state in services)
        {
            var key = state.Path + "|";
            var directories = await DirectoriesAsync(state, ct);
            // all of a service's directory reads go out together: one hung request must not stretch the round to
            // (directories x timeout)
            var batches = await Task.WhenAll(directories.Select(async directory =>
                (Requests: await RequestsAsync(state, directory, ct), Forms: await FormsAsync(state, directory, ct))));
            if (batches.Any(b => b.Requests is null)) unreadRequests.Add(key);
            if (batches.Any(b => b.Forms is null)) unreadForms.Add(key);
            foreach (var batch in batches)
            {
                // the same id can come back from the server's own scope and a project scope (the queue is read per
                // directory); the one polled under a project directory is the one its card is named after
                foreach (var req in batch.Requests ?? [])
                {
                    var id = key + req.Id;
                    live.Add(id);
                    if (!queues.TryGetValue(id, out var known) || (known.Request.Directory is null && req.Directory is not null))
                        queues[id] = (state, req);
                }
                foreach (var form in batch.Forms ?? [])
                {
                    var id = key + form.Id;
                    liveForms.Add(id);
                    if (!formQueues.TryGetValue(id, out var knownForm) || (knownForm.Form.Directory is null && form.Directory is not null))
                        formQueues[id] = (state, form);
                }
            }
        }

        // a request no longer in any queue was answered on the desktop (or the session ended): stop offering it
        bool RequestsUnread(string id) => unreadRequests.Exists(id.StartsWith);
        bool FormsUnread(string id) => unreadForms.Exists(id.StartsWith);
        foreach (var id in _offered.Keys.Where(id => !live.Contains(id) && !RequestsUnread(id)).ToList()) Withdraw(id);
        foreach (var id in _formOffers.Keys.Where(id => !liveForms.Contains(id) && !FormsUnread(id)).ToList()) WithdrawForm(id);
        foreach (var key in _dismissed.Keys) if (!liveForms.Contains(key) && !FormsUnread(key)) _dismissed.TryRemove(key, out _);
        // an expired id may be either kind: it is forgotten only once both queues were read and neither holds it
        foreach (var id in _expired.Where(id => !live.Contains(id) && !liveForms.Contains(id) && !RequestsUnread(id) && !FormsUnread(id)).ToList())
            _expired.Remove(id);

        if (onlyWhenAway && Idle.Seconds() < 60) return; // the desktop prompt is right there; the phone is for away

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = now + Math.Clamp(timeoutSec, 10, 3600) * 1000L;
        foreach (var (id, (state, request)) in queues)
        {
            if (_offered.Count >= MaxOffered) break;
            if (_offered.ContainsKey(id) || _expired.Contains(id)) continue;
            var cancel = new CancellationTokenSource();
            // called directly, not via Task.Run: AskAsync registers the offer synchronously up to its first await,
            // so the phone's very next list request already sees it
            _offered[id] = (OfferAsync(request, state.Info, expiresAt, countdownSec, cancel.Token), cancel, expiresAt);
        }
        foreach (var (id, (state, form)) in formQueues)
        {
            if (_formOffers.Count >= MaxOffered) break;
            if (_formOffers.ContainsKey(id) || _dismissed.ContainsKey(id) || _expired.Contains(id)) continue;
            var cancel = new CancellationTokenSource();
            _formOffers[id] = (OfferFormAsync(id, form, state.Info, expiresAt, cancel.Token), cancel, expiresAt);
        }
    }

    /// <summary>Drops the entries whose offer has ended and remembers the ones whose window ran out: their slot
    /// frees up for new requests, but the same request is not offered again while it stays queued — OpenCode's own
    /// prompt is on the desktop, and a card that buzzes back every window is no use to anyone. The memory is
    /// forgotten when the request leaves the queue.</summary>
    private void FreeEndedOffers()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var id in _offered.Keys.ToList())
        {
            var (task, _, expiresAt) = _offered[id];
            if (expiresAt <= now) { Withdraw(id); _expired.Add(id); }
            else if (task.IsCompleted) _offered.TryRemove(id, out _);
        }
        foreach (var id in _formOffers.Keys.ToList())
        {
            var (task, _, expiresAt) = _formOffers[id];
            if (expiresAt <= now) { WithdrawForm(id); _expired.Add(id); }
            else if (task.IsCompleted) _formOffers.TryRemove(id, out _);
        }
    }

    private async Task OfferAsync(OpenCodePermission req, ServiceInfo service, long expiresAt, int countdownSec, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var saves = req.Save is { Length: > 0 };
            var approval = new Approval(
                Id: req.Id, Source: "OpenCode", Session: req.SessionId, Project: ProjectOf(req.Directory),
                Cwd: req.Directory ?? "", Tool: ToolTitle(req.Action), Detail: Detail(req), Description: null,
                CreatedAt: now, ExpiresAt: expiresAt,
                // Allow always only when OpenCode proposes what it would save: the card shows the pattern, so the
                // user can see what they would allow from then on
                Always: saves,
                AlwaysNotBefore: saves ? now + Math.Clamp(countdownSec, 0, 60) * 1000L : 0,
                AlwaysPattern: saves ? string.Join(", ", req.Save!) : null,
                // the same write classes Claude's list covers, in OpenCode's action names:
                // shell (Bash), edit (Edit/Write) and any MCP tool (`server_tool`, the
                // underscore shape — names alone do not say read from write). Questions travel
                // on their own channel and are never marked.
                HighRisk: IsHighRisk(req.Action));
            // AskAsync emits the offer to the phone, waits for its decision, and returns null once the window is over
            var decision = await _approvals.AskAsync(approval, ct);
            if (decision is "allow" or "always" or "deny")
                await ReplyAsync(req, service, decision, CancellationToken.None); // deliver a taken decision even if withdrawn
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("opencode approval {Id}: {Msg}", req.Id, ex.Message); }
    }

    /// <summary>Posts the phone's decision back to OpenCode. 404 = the desktop answered first, which is fine.</summary>
    internal async Task ReplyAsync(OpenCodePermission req, ServiceInfo service, string decision, CancellationToken ct)
    {
        var reply = decision switch { "allow" => "once", "always" => "always", _ => "reject" };
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { decision = reply }), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{service.Url}/api/session/{Uri.EscapeDataString(req.SessionId)}/permission/{Uri.EscapeDataString(req.Id)}/reply")
            { Content = content };
            request.Headers.Authorization = Auth(service);
            using var res = await _http.SendAsync(request, ct);
            if (res.IsSuccessStatusCode) _log.LogInformation("opencode approval {Id}: {Decision} from the phone", req.Id, decision);
            else if (res.StatusCode == HttpStatusCode.NotFound) _log.LogDebug("opencode approval {Id}: already answered on the desktop", req.Id);
            else _log.LogDebug("opencode approval {Id}: reply failed with {Status}", req.Id, (int)res.StatusCode);
        }
        catch (Exception ex) { _log.LogDebug("opencode approval {Id}: reply failed: {Msg}", req.Id, ex.Message); }
    }

    /// <summary>
    /// Offers an agent question to the phone. A single-field form becomes a card with one button per choice (and a
    /// text field when OpenCode allows a custom answer); anything else is shown as text, answered at the PC.
    /// </summary>
    private async Task OfferFormAsync(string key, OpenCodeForm form, ServiceInfo service, long expiresAt, CancellationToken ct)
    {
        try
        {
            var single = form.Fields.Length == 1 ? form.Fields[0] : null;
            var approval = new Approval(
                Id: form.Id, Source: "OpenCode", Session: form.SessionId, Project: ProjectOf(form.Directory),
                Cwd: form.Directory ?? "",
                Tool: single is { } f && f.Title is { Length: > 0 } t ? t : "OpenCode has a question",
                Detail: FormDetail(form), Description: null,
                CreatedAt: _clock.GetUtcNow().ToUnixTimeMilliseconds(), ExpiresAt: expiresAt,
                Kind: "question",
                Options: single is { Options.Length: > 0 } s
                    ? s.Options.Select(o => new ApprovalOption(o.Value, o.Label, o.Description)).ToArray() : null,
                Custom: single?.Custom == true);
            // AskAsync emits the offer, waits, and returns the picked answer (or null once the window is over)
            var answer = await _approvals.AskAsync(approval, ct);
            if (single is not { Options.Length: > 0 } && single?.Custom != true)
            {
                // nothing to tap and nothing to type: the only decision the store takes for such a card is "Got it",
                // which means "seen, answered at the PC" — nothing goes back to OpenCode, and the form is not
                // offered again on every poll while it stays queued
                if (answer == "skip")
                {
                    _dismissed[key] = 1;
                    _log.LogDebug("opencode question {Id}: dismissed on the phone; the PC answers", form.Id);
                }
                return;
            }
            // an answerable card: whatever the value is goes back, even when it literally reads "skip" — a choice
            // named that, or a typed one, is an answer, not a dismissal
            if (answer is { } value && single is { } field)
                await ReplyFormAsync(form, service, field, value, CancellationToken.None); // deliver a taken answer even if withdrawn
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("opencode form {Id}: {Msg}", form.Id, ex.Message); }
    }

    /// <summary>Posts the phone's answer to the form. 404/409 = answered on the desktop first, which is fine.</summary>
    internal async Task ReplyFormAsync(OpenCodeForm form, ServiceInfo service, OpenCodeFormField field, string answer, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { answer = new Dictionary<string, string> { [field.Key] = answer } }),
                Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{service.Url}/api/session/{Uri.EscapeDataString(form.SessionId)}/form/{Uri.EscapeDataString(form.Id)}/reply")
            { Content = content };
            request.Headers.Authorization = Auth(service);
            using var res = await _http.SendAsync(request, ct);
            if (res.IsSuccessStatusCode) _log.LogInformation("opencode question {Id}: answered from the phone", form.Id);
            else if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                _log.LogDebug("opencode question {Id}: already answered on the desktop", form.Id);
            else _log.LogDebug("opencode question {Id}: reply failed with {Status}", form.Id, (int)res.StatusCode);
        }
        catch (Exception ex) { _log.LogDebug("opencode question {Id}: reply failed: {Msg}", form.Id, ex.Message); }
    }

    /// <summary>The question as the card shows it when there is more than one field (or nothing to tap): plain text.</summary>
    internal static string FormDetail(OpenCodeForm form)
    {
        if (form.Fields.Length == 1)
        {
            var f = form.Fields[0];
            if (f.Description is { Length: > 0 }) return f.Description;
            if (f.Title is { Length: > 0 }) return f.Title;
            return "OpenCode is asking something; answer it on the PC if the phone shows no answers.";
        }
        var sb = new StringBuilder();
        foreach (var f in form.Fields)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(f.Title is { Length: > 0 } t ? t + ": " : "").Append(f.Description ?? "");
            foreach (var o in f.Options) sb.Append("\n  • ").Append(o.Label).Append(o.Description is { Length: > 0 } d ? " — " + d : "");
            if (f.Custom) sb.Append("\n  (you can also type your own answer)");
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "OpenCode is asking something; answer it on the PC if the phone shows no answers.";
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    /// <summary>
    /// Re-reads the native service.json when it changed, so a restarted service (new port and password) is picked
    /// up, and merges the WSL services from OpenCodeServices' minute-cadence cache. Tracked per service.json file,
    /// so a native and a WSL service that happen to share a port stay two services. WSL files are only read inside
    /// a scan that just verified the distros running; between scans neither a stat nor a read touches the share —
    /// that would wake a distro that has stopped since.
    /// </summary>
    private List<ServiceState> CurrentServices(bool watchWsl)
    {
        if (Environment.TickCount64 >= _nextPathScan)
        {
            _nextPathScan = Environment.TickCount64 + PathScanMs;
            try { _paths = _servicePaths is { Length: > 0 } fixed_ ? [.. fixed_] : OpenCodeServices.Discover(wslEnabled: false); }
            catch (Exception ex) { _log.LogDebug("opencode services: {Msg}", ex.Message); }
        }
        void Track(string path, ServiceInfo read)
        {
            if (!_services.TryGetValue(path, out var state))
            {
                state = new ServiceState { Info = read, Path = path };
                _services[path] = state;
                _log.LogInformation("opencode service at {Url} (v{Version}){Wsl}", read.Url, read.Version,
                    path.Contains("wsl.localhost", StringComparison.OrdinalIgnoreCase) ? " · WSL" : "");
            }
            else
            {
                if (state.Info.Password != read.Password || state.Info.Version != read.Version)
                    _log.LogInformation("opencode service at {Url} re-read (v{Version})", read.Url, read.Version);
                state.Info = read;
                state.Reauth = false;
            }
        }
        var seen = new HashSet<string>();
        foreach (var path in _paths.ToArray())
        {
            if (!File.Exists(path)) { _stamps.TryRemove(path, out _); continue; }
            var info = new FileInfo(path);
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (_stamps.TryGetValue(path, out var known) && stamp == known)
            {
                // unchanged, and already tracked: keep it (and mark it seen, or the sweep below would drop it)
                if (_services.ContainsKey(path)) seen.Add(path);
                continue;
            }
            var read = ReadService(path);
            _stamps[path] = read is null ? default : stamp; // unreadable (perhaps still being written): retry next pass
            if (read is null) continue;
            seen.Add(path);
            Track(path, read);
        }
        // WSL: the services come from the minute-cadence scan, which verified the distros running right before it
        // read their service.json files. Between scans the parsed list is reused untouched (no share access); a new
        // scan list means the files were just re-read, so their states update now.
        var wsl = OpenCodeServices.WslServices(watchWsl && _servicePaths is null);
        if (!ReferenceEquals(wsl, _wslList))
        {
            _wslList = wsl;
            foreach (var (path, info) in wsl)
            {
                seen.Add(path);
                Track(path, info);
            }
        }
        else
        {
            foreach (var (path, _) in wsl) seen.Add(path);
        }
        foreach (var path in _services.Keys.Where(path => !seen.Contains(path)).ToList()) _services.Remove(path);
        return _services.Values.ToList();
    }

    /// <summary>The first service.json that exists, or null. XDG_STATE_HOME wins, as OpenCode itself uses it.</summary>
    internal static string? ServicePath(string? homeDir = null, string? xdgStateHome = null, string? localAppData = null)
    {
        var home = homeDir ?? Home;
        var candidates = new List<string>();
        var xdg = xdgStateHome ?? Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg)) candidates.Add(Path.Combine(xdg, "opencode", "service.json"));
        candidates.Add(Path.Combine(home, ".local", "state", "opencode", "service.json"));
        var local = localAppData ?? Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(local))
            candidates.Add(Path.Combine(local, "opencode", "service.json"));
        if (OperatingSystem.IsMacOS())
            candidates.Add(Path.Combine(home, "Library", "Application Support", "opencode", "service.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static ServiceInfo? ReadService(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var url = Str(r, "url");
            var password = Str(r, "password");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password)) return null;
            var address = url.TrimEnd('/');
            if (!IsLoopbackService(address)) return null; // the Basic header goes to OpenCode's own service only
            return new ServiceInfo(address, password, Str(r, "version") ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// True when the address is the plain-HTTP service on this machine's loopback interface. Anything else (another
    /// host, https, or no address at all) is refused: a service.json is written by OpenCode for its own local
    /// service, and one that pointed elsewhere would send the password there or let a stranger put prompts on the
    /// phone.
    /// </summary>
    internal static bool IsLoopbackService(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "http" || u.Authority.Length == 0) return false;
        return u.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    /// <summary>
    /// The project directories the service knows, taken from its session list: every session knows where it lives,
    /// the permission queues are per directory, and (unlike the debug location list) this works for every service,
    /// WSL included. Refreshed rarely.
    /// </summary>
    private async Task<List<string?>> DirectoriesAsync(ServiceState state, CancellationToken ct)
    {
        if (Environment.TickCount64 >= state.NextDirectoryRefresh)
        {
            state.NextDirectoryRefresh = Environment.TickCount64 + DirectoryRefreshMs;
            try
            {
                using var res = await GetAsync(state, "/api/session", ct);
                if (res is { IsSuccessStatusCode: true })
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var dirs = new List<string?>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var e in data.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            var dir = e.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                                ? Str(loc, "directory") : null;
                            if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir)) dirs.Add(dir);
                            if (dirs.Count >= MaxDirectories) break;
                        }
                    if (dirs.Count > 0) state.Directories = [null, .. dirs]; // the server's own location first
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout: keep the last list */ }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
            { _log.LogDebug("opencode approvals: directories: {Msg}", ex.Message); }
        }
        return state.Directories;
    }

    private async Task<List<OpenCodePermission>?> RequestsAsync(ServiceState state, string? directory, CancellationToken ct)
    {
        var query = directory is null ? "" : "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory);
        try
        {
            using var res = await GetAsync(state, "/api/permission/request" + query, ct);
            if (res is null) return null;
            if (res.StatusCode == HttpStatusCode.NotFound) return []; // a service without this queue has nothing in it
            if (!res.IsSuccessStatusCode) return null;                // a failed read is not an empty queue
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            var list = new List<OpenCodePermission>();
            foreach (var e in data.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                var sessionId = Str(e, "sessionID");
                if (id is null || sessionId is null) continue;
                var action = Str(e, "action") ?? "";
                var resources = e.TryGetProperty("resources", out var rs) && rs.ValueKind == JsonValueKind.Array
                    ? rs.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToArray()
                    : [];
                // the patterns an Allow always would save for the project, as OpenCode proposes them
                var save = e.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.Array
                    ? sv.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : [];
                list.Add(new OpenCodePermission(id, sessionId, action, resources, Str(e, "message"), directory, save));
            }
            return list;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
        { _log.LogDebug("opencode approvals: queue ({Dir}): {Msg}", directory ?? "(default)", ex.Message); return null; }
    }

    private async Task<HttpResponseMessage?> GetAsync(ServiceState state, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, state.Info.Url + pathAndQuery);
        request.Headers.Authorization = Auth(state.Info);
        var res = await _http.SendAsync(request, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            state.Reauth = true;
            _stamps.TryRemove(state.Path, out _); // the service.json changed underneath us: read it again
            if (state.Path.Contains("wsl.localhost", StringComparison.OrdinalIgnoreCase))
                OpenCodeServices.InvalidateWsl(); // the minute-cadence WSL scan re-reads it sooner
        }
        return res;
    }

    /// <summary>The pending agent questions, from the same location-scoped channel the desktop app reads.</summary>
    private async Task<List<OpenCodeForm>?> FormsAsync(ServiceState state, string? directory, CancellationToken ct)
    {
        var query = directory is null ? "" : "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory);
        try
        {
            using var res = await GetAsync(state, "/api/form" + query, ct);
            if (res is null) return null;
            if (res.StatusCode == HttpStatusCode.NotFound) return []; // a service without this queue has nothing in it
            if (!res.IsSuccessStatusCode) return null;                // a failed read is not an empty queue
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            var list = new List<OpenCodeForm>();
            foreach (var e in data.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                var sessionId = Str(e, "sessionID");
                if (id is null || sessionId is null) continue;
                var fields = new List<OpenCodeFormField>();
                if (e.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    foreach (var fe in fs.EnumerateArray())
                    {
                        if (fe.ValueKind != JsonValueKind.Object) continue;
                        var key = Str(fe, "key");
                        if (key is null) continue;
                        var options = fe.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array
                            ? os.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object)
                                .Select(o => (Str(o, "value") ?? "", Str(o, "label") ?? Str(o, "value") ?? "", Str(o, "description")))
                                .Where(o => o.Item1.Length > 0).ToArray()
                            : [];
                        fields.Add(new OpenCodeFormField(key, Str(fe, "title"), Str(fe, "description"),
                            fe.TryGetProperty("custom", out var c) && c.ValueKind == JsonValueKind.True, options));
                    }
                list.Add(new OpenCodeForm(id, sessionId, Str(e, "title"), fields.ToArray(), directory));
            }
            return list;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
        { _log.LogDebug("opencode approvals: forms ({Dir}): {Msg}", directory ?? "(default)", ex.Message); return null; }
    }

    private static AuthenticationHeaderValue Auth(ServiceInfo service) => new("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{service.Password}")));

    private void Withdraw(string id)
    {
        if (_offered.TryRemove(id, out var offer)) offer.Cancel.Cancel();
        // AskAsync ends with no decision; its cleanup also tells the phone the card is gone (approval-done)
    }

    private void WithdrawForm(string id)
    {
        if (_formOffers.TryRemove(id, out var offer)) offer.Cancel.Cancel();
    }

    private void WithdrawAll()
    {
        foreach (var id in _offered.Keys.ToList()) Withdraw(id);
        foreach (var id in _formOffers.Keys.ToList()) WithdrawForm(id);
    }

    /// <summary>The same project name the sessions widget shows: the folder's basename.</summary>
    internal static string ProjectOf(string? directory)
    {
        var project = Path.GetFileName((directory ?? "").TrimEnd('\\', '/'));
        return project.Length == 0 ? "opencode" : project;
    }

    /// <summary>Fail-closed whitelist: only the known read-only actions pass; everything
    /// else is high risk — unknown words, plugin actions, actions OpenCode may add in
    /// the future, "shell", "edit", "execute" (it runs AI-written code), "external_directory"
    /// and every MCP tool (an MCP tool names itself <server>_<tool> — the underscore shape —
    /// and names alone do not say read from write). The list is hardcoded here (the agent is
    /// the trusted end) and can only ever be tightened, never loosened from the phone;
    /// question forms are not permissions and are never marked.</summary>
    internal static bool IsHighRisk(string action) =>
        // a subagent is not read-only: the agent it starts may write with its own permissions, so it asks twice too
        action is not ("read" or "glob" or "grep" or "webfetch" or "websearch"
            or "skill" or "question");

    /// <summary>What the card's headline says, per OpenCode permission action (plugins may add their own).</summary>
    internal static string ToolTitle(string action) => action switch
    {
        "shell" => "Run a command",
        "edit" => "Edit a file",
        "read" => "Read a file",
        "glob" => "Search for files",
        "grep" => "Search file contents",
        "webfetch" => "Fetch a web page",
        "websearch" => "Search the web",
        "subagent" => "Run a subagent",
        "skill" => "Use a skill",
        "question" => "OpenCode has a question",
        "external_directory" => "Open a folder outside the project",
        "execute" => "Use Code Mode",
        _ when action.Contains('_') => "Use " + action.Replace("_", " › "),  // an MCP tool: <server>_<tool>
        _ => "Use " + action,
    };

    /// <summary>The message and the resources (command, paths, URLs) as the card shows them, capped like Claude's.</summary>
    internal static string Detail(OpenCodePermission req)
    {
        var s = (req.Message is { Length: > 0 } m ? m + "\n\n" : "") + string.Join('\n', req.Resources);
        s = s.TrimEnd();
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
