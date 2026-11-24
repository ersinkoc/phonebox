using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// A single quota window limit. <paramref name="ResetDescription"/> carries provider-formatted reset text,
/// and <paramref name="DeltaPercent"/> is used minus expected pace percent.
/// </summary>
public sealed record QuotaLimit(string Label, double Percent, long? ResetsAt, long? WindowSec, string Severity,
    string? ResetDescription = null, double? DeltaPercent = null, double? ExpectedPercent = null, bool? PaceOnTrack = null);

public sealed record CodexUsage(string? Model, string? Effort, long? TotalInput, long? TotalCached,
    long? TotalOutput, long? TotalReasoning, long? TotalTokens, long? LastInput, long? LastOutput,
    long? ContextWindow, long UpdatedAt);

public sealed record QuotaProvider(string Id, string Name, string Plan, IReadOnlyList<QuotaLimit> Limits,
    string? Extra, long? UpdatedAt, string? Error, CodexUsage? Codex = null, string Source = "local");

/// <summary>
/// Collects quota usage of AI subscriptions:
///  - Claude: api.anthropic.com/api/oauth/usage with the OAuth token from ~/.claude/.credentials.json (on macOS, when
///    that file doesn't exist or its token is missing or expired, the "Claude Code-credentials" item from the default
///    keychain search list, normally the login Keychain, if its token expires later)
///    (the endpoint behind Claude Code's /usage command). The token is only read and NEVER refreshed;
///    Claude Code refreshes it itself (refreshing it here could break Claude Code's session).
///  - Codex: live account quota through the signed-in Codex CLI app-server, with local session logs as fallback.
///  - Antigravity: a running local server, or the signed-in agy CLI's structured /usage command.
///  - Grok: cli-chat-proxy.grok.com with the bearer from ~/.grok/auth.json (GROK_HOME overrides that folder).
///    The token is only read and never refreshed; the Grok CLI refreshes it itself.
/// </summary>
public sealed partial class QuotaMonitor(ConfigStore config, ILogger<QuotaMonitor> log) : BackgroundService
{
    internal QuotaMonitor(ILogger<QuotaMonitor> log) : this(null!, log) { }

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // IPv4 first: on networks where IPv6 is advertised but not routable, the default connect fails with
    // "Socket is not connected" instead of falling back, which breaks the quota checks.
    private HttpClient? _httpClient;
    private HttpClient Http => _httpClient ??= NewHttpClient(HttpHandler
        ?? new SocketsHttpHandler { ConnectCallback = (ctx, ct) => ConnectIpv4First(ctx.DnsEndPoint, ct) });

    /// <summary>For tests: a fake handler instead of the network.</summary>
    internal HttpMessageHandler? HttpHandler { get; init; }

    private static HttpClient NewHttpClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        return http;
    }

    private static readonly string DefaultCredentialsPath = Path.Combine(Home, ".claude", ".credentials.json");

    /// <summary>Claude Code's credentials file (read only). Tests point it at a file of their own.</summary>
    internal string CredentialsPath { get; init; } = DefaultCredentialsPath;

    /// <summary>
    /// The credentials files of Claude Code inside the running WSL distros, when WSL watching is on for Claude Code
    /// (Windows only). Tests give a finder of their own.
    /// </summary>
    internal Func<IEnumerable<string>>? FindWslCredentials { get; init; }

    /// <summary>Whether a credentials file holds a Claude Code sign-in (an access token), not just a plan.</summary>
    internal static bool HasCliSignIn(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 };
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Each running distro's credentials file with its distro (tests give paths without one, and no check).</summary>
    private IEnumerable<(string? Distro, string Path)> WslCredentialsPaths()
    {
        if (FindWslCredentials is not null) return FindWslCredentials().Select(p => ((string?)null, p));
        if (config?.Get().Claude.WatchWsl != true) return [];
        return WslHomes.Running().Select(wsl => ((string?)wsl.Distro, Path.Combine(wsl.Home, ".claude", ".credentials.json")));
    }

    /// <summary>
    /// Reads Claude Code's macOS Keychain item; null uses the `security` tool, and only for the default credentials path
    /// on macOS. Tests give a reader of their own so nothing here touches the real Keychain.
    /// </summary>
    internal Func<string?>? ReadKeychain { get; init; }

    private Func<string?>? KeychainReader()
    {
        if (ReadKeychain is not null) return ReadKeychain;
#if !WINDOWS
        if (OperatingSystem.IsMacOS() && CredentialsPath == DefaultCredentialsPath)
            return () => Sh.Run("security", "find-generic-password", "-s", "Claude Code-credentials", "-w")?.Trim();
#endif
        return null;
    }

    /// <summary>The expiry of a Claude Code sign-in with an access token (0 when it has none), or null without one.</summary>
    internal static long? SignInExpiry(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object
                || string.IsNullOrEmpty(Str(o, "accessToken"))) return null;
            return o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms) ? ms : 0;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Of the credentials file and the Keychain item, the sign-in whose token expires later. Claude Code on macOS keeps
    /// and renews its sign-in in the Keychain, so a file another tool or an older version left behind can hold a token
    /// that expired hours ago. Only called once the file's token is missing or expired; without a usable Keychain
    /// sign-in the file is returned unchanged, so a half-written or signed-out file is still reported as before.
    /// </summary>
    internal static string? PickCredentials(string? file, string? keychain)
    {
        var fileExpiry = SignInExpiry(file);
        var keychainExpiry = SignInExpiry(keychain);
        if (keychainExpiry is null) return file;
        if (fileExpiry is null) return keychain;
        // 0 is "no expiry given": it is never later than a known one, and a known one never beats it
        if (fileExpiry == 0) return file;
        return keychainExpiry == 0 || keychainExpiry > fileExpiry ? keychain : file;
    }

    /// <summary>The addresses in the order to try them: IPv4 first, each family keeping its resolver order.</summary>
    internal static IEnumerable<IPAddress> Ipv4First(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

    /// <summary>Time all connect attempts of one request may use together; the request itself times out after 15 s.</summary>
    internal static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(10);

    internal static async ValueTask<Stream> ConnectIpv4First(DnsEndPoint endpoint, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        return await ConnectInOrder(addresses, endpoint.Port, ConnectBudget, static (s, ep, t) => s.ConnectAsync(ep, t), ct);
    }

    /// <summary>
    /// Tries the addresses one after another, IPv4 first. Each attempt gets an equal share of the time that is left,
    /// so dead addresses of one family can't use up the budget before the other family is tried. The socket of an
    /// attempt is disposed on every exit that doesn't hand it to the returned stream.
    /// </summary>
    internal static async ValueTask<Stream> ConnectInOrder(IReadOnlyCollection<IPAddress> addresses, int port, TimeSpan budget,
        Func<Socket, IPEndPoint, CancellationToken, ValueTask> connect, CancellationToken ct)
    {
        var ordered = Ipv4First(addresses).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) break;
            var ip = ordered[i];
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var handedOver = false;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(left / (ordered.Count - i));
                await connect(socket, new IPEndPoint(ip, port), attempt.Token);
                var stream = new NetworkStream(socket, ownsSocket: true);
                handedOver = true;
                return stream;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
            finally
            {
                if (!handedOver) socket.Dispose();
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new HttpRequestException("Could not connect", last);
    }

    private volatile QuotaProvider? _claude;
    private volatile QuotaProvider? _codex;
    private volatile QuotaProvider? _codexLive;
    private volatile string? _codexLiveError;
    private volatile QuotaProvider? _antigravity;
    private long _claudeNextAt;           // wall-clock ms of the next Claude request
    private int _claudeFailures;          // consecutive failed requests, for the back-off
    private long _nextCodexBar;
    private long _nextAntigravity;
    private volatile IReadOnlyList<QuotaProvider> _external = [];
    private volatile QuotaOverview? _overview;

    /// <summary>Seeds quota state in unit tests without running background loops.</summary>
    internal void SetStateForTest(QuotaProvider? claude = null, IReadOnlyList<QuotaProvider>? external = null, QuotaProvider? grok = null)
    {
        _claude = claude;
        if (external is not null) _external = external;
        if (grok is not null) _grok = grok;
    }

    public IReadOnlyList<QuotaProvider> Snapshot() => Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>The snapshot at <paramref name="now"/>; tests pass their own clock so windows roll the same way every day.</summary>
    /// <summary>
    /// Like <see cref="CodexBar.Select"/>, but a successful prepaid-only reading (a balance, no limits) counts as a
    /// reading: it is not set aside for a failed or stale CodexBar answer. A fresh CodexBar reading still wins.
    /// </summary>
    internal static QuotaProvider? GrokSelect(QuotaProvider? local, QuotaProvider? external, long now)
    {
        var freshExternal = external is { Error: null, Limits.Count: > 0, UpdatedAt: { } updated } && now - updated <= CodexBar.FreshExternalMs;
        if (!freshExternal && local is { Error: null, Limits.Count: 0, Extra: not null }) return local;
        return CodexBar.Select(local, external, now);
    }

    internal IReadOnlyList<QuotaProvider> Snapshot(long now)
    {
        var external = _external;
        var core = new[] { "claude", "codex", "antigravity", "grok" }
            .Select(id => id switch
            {
                "codex" => CodexSnapshot(now, external.FirstOrDefault(p => p.Id == id)),
                "claude" => CodexBar.Select(_claude, external.FirstOrDefault(p => p.Id == id), now),
                "grok" => GrokSelect(_grok, external.FirstOrDefault(p => p.Id == id), now),
                _ => CodexBar.Select(_antigravity, external.FirstOrDefault(p => p.Id == id), now),
            })
            .OfType<QuotaProvider>();
        var extra = external
            .Where(p => p.Id is not ("claude" or "codex" or "antigravity" or "grok"))
            .Select(p => CodexBar.Select(null, p, now))
            .OfType<QuotaProvider>();
        return core.Concat(extra).Select(p => Rolled(p, now)).ToList();
    }

    /// <summary>A window whose reset time has passed started over: the last reading (kept while the tool is not used) no longer applies.</summary>
    private static QuotaProvider Rolled(QuotaProvider p, long now)
    {
        if (!p.Limits.Any(l => l.ResetsAt is { } r && r <= now)) return p;
        var limits = p.Limits.Select(l => l.ResetsAt is { } r && r <= now
            ? l with { Percent = 0, ResetsAt = null, Severity = "normal", ResetDescription = null, DeltaPercent = null, ExpectedPercent = null, PaceOnTrack = null }
            : l).ToList();
        const string reached = "Limit reached";
        var extra = p.Source != "codex-app-server" && p.Extra is { } x && x.StartsWith(reached, StringComparison.Ordinal) && !limits.Any(l => l.Percent >= 100)
            ? (x.Length > reached.Length + 3 ? x[(reached.Length + 3)..] : null) : p.Extra;
        return p with { Limits = limits, Extra = extra };
    }

    /// <summary>Returns true when an enabled page shows a quota widget with cost overview enabled.</summary>
    internal bool IsOverviewEnabled() => config?.Get().ShowQuotaOverview() == true;

    /// <summary>Returns the current cost overview when enabled and still within the 1-hour freshness window.</summary>
    public QuotaOverview? Overview() => Overview(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Returns the current cost overview evaluated at <paramref name="now"/>.</summary>
    internal QuotaOverview? Overview(long now) => CodexBar.SelectOverview(_overview, IsOverviewEnabled(), now);

    /// <summary>Grok is polled only while an enabled page shows Grok quotas. Unit tests without a config poll it.</summary>
    internal bool IsGrokEnabled() => config is null || config.Get().ShowGrokQuota();

    /// <summary>Returns true when live Codex CLI polling is opted in and an enabled page shows Codex quotas.</summary>
    internal bool IsCodexLiveEnabled() => config is null || (config.Get() is { } c && c.CodexBar.CodexLiveQuota && c.ShowCodexQuota());

    internal const long CodexLiveIntervalMs = 180_000;
    internal const long CodexLiveStaleMs = 360_000;

    /// <summary>Exponential backoff for live Codex CLI failures: 2m, 4m, 8m, 16m, capped at 30m.</summary>
    internal static long CodexLiveBackoffMs(int failures) =>
        Math.Min(1_800_000L, 120_000L << Math.Clamp(failures - 1, 0, 4));

    internal enum CodexLiveOutcome { Ok, Skipped, Failed }

    /// <summary>
    /// Periodically refreshes local/CodexBar quota providers, gated cost overview and opt-in live Codex quota in the background.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(RunUsageLoopAsync(ct), RunOverviewLoopAsync(ct), RunCodexLiveLoopAsync(ct));
    }

    private async Task RunCodexLiveLoopAsync(CancellationToken ct)
    {
        long next = 0;
        var failures = 0;
        var wakes = Wake.Count;
        while (!ct.IsCancellationRequested)
        {
            if (!IsCodexLiveEnabled())
            {
                _codexLive = null;
                _codexLiveError = null;
                failures = 0;
                next = 0;
            }
            else if (Environment.TickCount64 >= next || Wake.Count != wakes)
            {
                wakes = Wake.Count;
                var outcome = await RefreshCodexOnceAsync(CodexQuota.Find, CodexQuota.ReadAsync, ct);
                if (outcome == CodexLiveOutcome.Ok)
                {
                    failures = 0;
                    next = Environment.TickCount64 + CodexLiveIntervalMs;
                }
                else if (outcome == CodexLiveOutcome.Skipped)
                {
                    failures = 0;
                    next = Environment.TickCount64 + 60_000;
                }
                else
                {
                    failures++;
                    next = Environment.TickCount64 + CodexLiveBackoffMs(failures);
                }
            }
            await Wake.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task RunOverviewLoopAsync(CancellationToken ct)
    {
        long next = 0;
        var wakes = Wake.Count;
        while (!ct.IsCancellationRequested)
        {
            if (!IsOverviewEnabled())
            {
                _overview = null;
                next = 0;
            }
            else if (Environment.TickCount64 >= next || Wake.Count != wakes)
            {
                wakes = Wake.Count;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var ok = await RefreshOverviewOnceAsync(() => ToolProcess.Find("codexbar"), CodexBar.ReadCostAsync, now, ct);
                next = Environment.TickCount64 + (ok ? 1_200_000 : 300_000);
            }
            await Wake.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Refreshes the cost overview once when enabled, expiring cached data after one hour without a successful refresh.</summary>
    internal async Task<bool> RefreshOverviewOnceAsync(Func<string?> findTool,
        Func<string, CancellationToken, Task<QuotaOverview?>> readCost, long now, CancellationToken ct)
    {
        if (!IsOverviewEnabled())
        {
            _overview = null;
            return false;
        }
        QuotaOverview? latest = null;
        var path = findTool();
        if (path is not null)
        {
            try { latest = await readCost(path, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogDebug("CodexBar cost: {Message}", ex.Message); }
        }
        if (latest is not null)
        {
            _overview = latest;
            return true;
        }
        if (_overview is { } old && now - old.UpdatedAt > CodexBar.OverviewStaleMs)
            _overview = null;
        return false;
    }

    /// <summary>
    /// The last successful Claude reading, so a restart (an update) or a stretch of 429s shows the last values with
    /// their age instead of nothing. Only usage percentages and reset times; no token.
    /// </summary>
    internal string ClaudeCachePath { get; init; } = Path.Combine(AppPaths.LocalDir, "claude-quota.json");

    /// <summary>A cached reading older than this is not shown any more.</summary>
    private const long ClaudeCacheMaxAgeMs = 7L * 24 * 3600 * 1000;

    internal void LoadClaudeCache(long nowMs) =>
        _claude ??= ReadClaudeCache(nowMs); // limits whose reset has passed are shown as reset (see Snapshot)

    /// <summary>The cached reading, without an error, or null when there is none, it's broken or it's too old.</summary>
    private QuotaProvider? ReadClaudeCache(long nowMs)
    {
        try
        {
            if (!File.Exists(ClaudeCachePath)) return null;
            var cached = JsonSerializer.Deserialize<QuotaProvider>(File.ReadAllText(ClaudeCachePath), ConfigStore.WriteOpts);
            if (cached is not { Id: "claude", Limits.Count: > 0, UpdatedAt: { } at } || nowMs - at > ClaudeCacheMaxAgeMs) return null;
            return cached with { Error = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log.LogDebug("claude quota cache: {Msg}", ex.Message);
            return null;
        }
    }

    internal void SaveClaudeCache(QuotaProvider reading) =>
        WriteShared(ClaudeCachePath, JsonSerializer.Serialize(reading, ConfigStore.WriteOpts));

    private async Task RunUsageLoopAsync(CancellationToken ct)
    {
        LoadClaudeCache(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var wakes = Wake.Count;
        while (!ct.IsCancellationRequested)
        {
            // Capture before the poll so a /api/refresh during CodexBar/Claude work still ends the wait below.
            var wake = Wake.Pending;
            // /api/refresh (Wake.Now) must re-poll CodexBar; otherwise Cursor stays on a stale "No session" for up to 3 minutes.
            if (Wake.Count != wakes)
            {
                wakes = Wake.Count;
                _nextCodexBar = 0;
            }
            if (Environment.TickCount64 >= _nextCodexBar)
            {
                var path = ToolProcess.Find("codexbar");
                try
                {
                    if (path is null)
                        _external = _external.Select(p => p with { Error = "CodexBar is not installed" }).ToList();
                    else
                    {
                        var latest = await CodexBar.ReadAsync(path, ct);
                        _external = CodexBar.Merge(_external, latest, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                    // Keep the usual 3-minute interval even when Cursor fails persistently under launchd (avoids hammering Claude's rate-limited usage endpoint).
                    _nextCodexBar = Environment.TickCount64 + 180_000;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogDebug("CodexBar: {Message}", ex.Message);
                    // Do not wipe last-good limits — only mark an error when there is nothing to show.
                    _external = _external.Select(p => p.Limits.Count > 0 ? p : p with { Error = "CodexBar refresh failed" }).ToList();
                    _nextCodexBar = Environment.TickCount64 + 60_000;
                }
            }
            CodexTick();

            if (Environment.TickCount64 >= _nextAntigravity)
            {
                var ok = await RefreshAntigravity(ct);
                _nextAntigravity = Environment.TickCount64 + (ok ? 60_000 : 30_000);
            }

            try { await ClaudeTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning("claude quota: {Msg}", ex.Message); } // never end the loop for Codex, Antigravity and Grok
            try { await GrokTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning("grok quota: {Msg}", ex.Message); }
            // Zero when the CodexBar deadline has already passed (do not sleep another 30 s); otherwise min(30 s, remaining).
            var untilCodex = Math.Max(0L, _nextCodexBar - Environment.TickCount64);
            var wait = TimeSpan.FromMilliseconds(Math.Min(30_000L, untilCodex));
            await Wake.Delay(wait, ct, wake);
        }
    }

    // ------------------------------------------------------------------ Claude
    // The usage endpoint is rate limited per account and shared with Claude Code, so every request counts:
    //  - an expired, missing or refused token is waited out locally (the sign-in is read once a minute), never sent;
    //  - the schedule and the last reading are shared through files, so a restart (an update) or a second agent on
    //    this computer doesn't ask again before the 3 minutes or a Retry-After are over;
    //  - a token the server refused (401) is sent again only after 30 minutes; a renewed one is used within a minute.

    internal const long ClaudeHealthyMs = 180_000;
    private const long ClaudeMaxWaitMs = 3_605_000;  // the longest wait ClaudeDelay can ask for
    private const long ClaudeTokenMarginMs = 60_000; // a token this close to expiry would be refused on the way
    internal const int ClaudeOfflineRetries = 3;     // quick retries while the network is not reachable
    internal const long ClaudeOfflineRetryMs = 30_000;
    private const long ClaudeUnrecognizedMs = 900_000;
    private const long ClaudeTokenRecheckMs = 60_000;
    private const long ClaudeRefusedRetryMs = 1_800_000; // a refusal may be a passing one (a proxy, an outage)

    private int _claudeOffline;         // consecutive requests that could not reach the server
    private string? _claudeRefused;     // fingerprint of the token the server refused (never the token itself)
    private long _claudeRefusedAt;
    private bool _claudeAwaitingToken;  // expired, missing or refused: only the sign-in is read until it changes
    private long _claudeCredsReadAt;

    /// <summary>The shared schedule: when the next request may go out, and the message to show until then.</summary>
    /// <param name="Refused">Fingerprint (a hash prefix, not the token) of the token Anthropic refused, and when.</param>
    private sealed record ClaudeSchedule(long NextAt, int Failures, string? Error, string? Refused = null, long RefusedAt = 0);

    internal string ClaudeSchedulePath =>
        Path.Combine(Path.GetDirectoryName(ClaudeCachePath)!, Path.GetFileNameWithoutExtension(ClaudeCachePath) + "-schedule.json");

    private enum ClaudeResult { Ok, Limited, Refused, Offline, Unrecognized, Failed }

    internal const string NoCliSignIn = "No Claude Code sign-in here; Claude Desktop doesn't share its own. Run claude to sign in";

    /// <summary>One pass of the usage loop for Claude, at wall-clock time <paramref name="nowMs"/>.</summary>
    internal async Task ClaudeTickAsync(long nowMs, CancellationToken ct)
    {
        // The sign-in is read when a request is due, and once a minute while waiting for Claude Code to renew an
        // expired or refused token (on macOS each read starts the `security` tool).
        // The schedule is wall-clock time, shared with other agents through a file. A clock set back (an NTP correction,
        // a resumed VM) would otherwise hold it up for as long as it moved: no wait is longer than ClaudeMaxWaitMs.
        if (_claudeNextAt - nowMs > ClaudeMaxWaitMs) _claudeNextAt = nowMs + ClaudeMaxWaitMs;
        if (_claudeCredsReadAt > nowMs) _claudeCredsReadAt = 0;
        if (_claudeAwaitingToken ? nowMs - _claudeCredsReadAt < ClaudeTokenRecheckMs : nowMs < _claudeNextAt) return;
        _claudeCredsReadAt = nowMs;
        var clock = System.Diagnostics.Stopwatch.StartNew(); // the reads below (the Keychain, the shared files) take time too

        // Claude Code may be rewriting the file right now: try again on the next pass
        string? json = null;
        try { if (File.Exists(CredentialsPath)) json = await File.ReadAllTextAsync(CredentialsPath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        // Claude Code signed in only inside WSL (Also watch Claude Code in WSL): its sign-in there, when Windows has no
        // file or only Claude Desktop's plan without a token. A Windows sign-in with a token always comes first.
        if ((CredentialsPath == DefaultCredentialsPath || FindWslCredentials is not null) && !HasCliSignIn(json))
        {
            // the first running distro whose file holds a sign-in; one with only a plan (or unreadable) is passed over.
            // Each distro is asked about again right before its file is read: one that stopped while an earlier file was
            // read is skipped instead of started by the read.
            foreach (var (distro, wsl) in WslCredentialsPaths())
            {
                if (distro is not null && !WslHomes.IsRunning(distro)) continue;
                try
                {
                    var wslJson = await File.ReadAllTextAsync(wsl, ct);
                    if (HasCliSignIn(wslJson)) { json = wslJson; break; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        // macOS: Claude Code stores the credentials in the login Keychain (read-only access). It is read when there is
        // no file, and when the file's token is missing or expired: a stale file must not hide a renewed Keychain sign-in.
        if (KeychainReader() is { } readKeychain
            && (json is null || SignInExpiry(json) is not { } fileExpiry || fileExpiry > 0 && fileExpiry - nowMs < ClaudeTokenMarginMs))
            json = PickCredentials(json, readKeychain());
        if (string.IsNullOrEmpty(json))
        {
            // Claude Code is not set up here
            _claude = null;
            _claudeAwaitingToken = false;
            _claudeNextAt = nowMs + ClaudeHealthyMs;
            return;
        }

        string plan, token;
        long expires;
        try
        {
            using var creds = JsonDocument.Parse(json);
            if (creds.RootElement.ValueKind != JsonValueKind.Object
                || !creds.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object)
            {
                _claude = Keep("Claude", "Not signed in to Claude Code");
                _claudeAwaitingToken = true;
                return;
            }
            plan = PlanLabel(Str(o, "subscriptionType"), Str(o, "rateLimitTier"));
            token = Str(o, "accessToken") ?? "";
            // TryGetInt64 throws on a non-number; an unknown expiry is left to the server to judge
            expires = o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms) ? ms : 0;
        }
        catch (JsonException)
        {
            return; // read mid-write; the next pass reads the whole file
        }

        var print = token.Length == 0 ? "" : Fingerprint(token);
        // A plan without a token is what the Claude Desktop app leaves in the file: it keeps its own sign-in to itself,
        // so only a Claude Code command-line sign-in can be used here (#247)
        var wait = token.Length == 0 ? NoCliSignIn
            : expires > 0 && expires - nowMs < ClaudeTokenMarginMs ? "Token expired; it will refresh when Claude Code runs"
            : print == _claudeRefused && nowMs - _claudeRefusedAt is >= 0 and < ClaudeRefusedRetryMs ? RefusedMessage : null;
        _claudeAwaitingToken = wait is not null;
        if (wait is not null)
        {
            _claude = Keep(plan, wait);
            return;
        }

        if (nowMs < _claudeNextAt) return;
        if (AdoptShared(plan, print, nowMs) || nowMs < _claudeNextAt) return;

        var (result, retryAfter) = await FetchClaude(plan, token, nowMs, ct);
        var answeredMs = nowMs + clock.ElapsedMilliseconds;
        long next;
        switch (result)
        {
            case ClaudeResult.Ok:
                _claudeFailures = _claudeOffline = 0;
                next = nowMs + ClaudeHealthyMs;
                break;
            case ClaudeResult.Refused:
                _claudeRefused = print;
                _claudeRefusedAt = nowMs;
                _claudeAwaitingToken = true;
                next = nowMs + ClaudeTokenRecheckMs;
                break;
            case ClaudeResult.Offline when _claudeOffline <= ClaudeOfflineRetries:
                // usually a computer that just woke up: keep the reading (its age is shown) and look again soon
                next = nowMs + ClaudeOfflineRetryMs;
                break;
            case ClaudeResult.Unrecognized:
                _claudeOffline = 0;
                next = nowMs + Math.Max(ClaudeUnrecognizedMs, (long)ClaudeDelay(false, ++_claudeFailures, null).TotalMilliseconds);
                break;
            default:
                if (result != ClaudeResult.Offline) _claudeOffline = 0;
                // a Retry-After counts from the answer, not from when the request went out
                next = (retryAfter is null ? nowMs : answeredMs) + (long)ClaudeDelay(false, ++_claudeFailures, retryAfter).TotalMilliseconds;
                if (result == ClaudeResult.Limited)
                    _claude = Keep(plan, $"Anthropic is limiting quota checks; trying again at {DateTimeOffset.FromUnixTimeMilliseconds(next).ToLocalTime():HH:mm}");
                break;
        }
        // Another agent may have written the schedule while this request ran: keep a longer wait it was given, and a
        // refusal it got, unless this request just showed that very token works.
        var error = _claude?.Error;
        var (refused, refusedAt) = (_claudeRefused, _claudeRefusedAt);
        if (ReadClaudeSchedule() is { } other)
        {
            if (other.NextAt > next && other.NextAt - answeredMs <= ClaudeMaxWaitMs)
                (next, error) = (other.NextAt, other.Error ?? error);
            if (other.Refused is { } theirs && (theirs != refused || other.RefusedAt > refusedAt)
                && answeredMs - other.RefusedAt is >= 0 and < ClaudeRefusedRetryMs
                && !(result == ClaudeResult.Ok && theirs == print))
                (refused, refusedAt) = (theirs, other.RefusedAt);
        }
        _claudeNextAt = next;
        SaveClaudeSchedule(new ClaudeSchedule(next, _claudeFailures, error, refused, refusedAt));
    }

    private const string RefusedMessage = "Anthropic did not accept the sign-in; it will refresh when Claude Code runs";

    /// <summary>Sends the one request and sets <see cref="_claude"/> from its answer.</summary>
    private async Task<(ClaudeResult, TimeSpan?)> FetchClaude(string plan, string token, long nowMs, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var res = await Http.SendAsync(req, ct);
            _claudeOffline = 0;
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                // Asking again before Retry-After only keeps the limit in place, so wait at least that long.
                return (ClaudeResult.Limited, RetryAfter(res.Headers.RetryAfter) ?? TimeSpan.FromMinutes(5));
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _claude = Keep(plan, RefusedMessage);
                return (ClaudeResult.Refused, null);
            }
            if (!res.IsSuccessStatusCode)
            {
                _claude = Keep(plan, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
                return (ClaudeResult.Failed, null);
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            List<QuotaLimit> limits = [];
            string? extra = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    limits = ParseClaudeLimits(doc.RootElement);
                    extra = ClaudeExtra(doc.RootElement);
                }
            }
            catch (JsonException) { }
            if (limits.Count == 0)
            {
                // a captive portal, or a reply format this version doesn't know: keep the last reading and the cache
                log.LogDebug("claude quota: unrecognized reply ({Length} bytes)", body.Length);
                _claude = Keep(plan, "Anthropic sent a quota reply this version doesn't recognize");
                return (ClaudeResult.Unrecognized, null);
            }
            _claude = new QuotaProvider("claude", "Claude", plan, limits, extra, nowMs, null);
            SaveClaudeCache(_claude);
            return (ClaudeResult.Ok, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            // the request never reached the server: show it only once the quick retries are used up, or when there is
            // no reading to show instead
            log.LogDebug("claude quota: {Msg}", ex.Message);
            if (++_claudeOffline > ClaudeOfflineRetries || _claude is not { Limits.Count: > 0 })
                _claude = Keep(plan, "Could not reach Anthropic: " + ex.Message);
            return (ClaudeResult.Offline, null);
        }
        catch (Exception ex)
        {
            log.LogDebug("claude quota: {Msg}", ex.Message);
            _claude = Keep(plan, "Could not fetch quota: " + ex.Message);
            return (ClaudeResult.Failed, null);
        }
    }

    /// <summary>
    /// Takes over what another agent on this computer (or this one before a restart) already knows: a newer reading,
    /// and the time the next request may go out.
    /// </summary>
    /// <returns>True when the token was refused before (by another agent, or before a restart): don't send it.</returns>
    private bool AdoptShared(string plan, string print, long nowMs)
    {
        if (ReadClaudeCache(nowMs) is { UpdatedAt: { } at } cached && at > (_claude?.UpdatedAt ?? 0) && at <= nowMs)
            _claude = cached with { Plan = plan };
        if (_claude?.UpdatedAt is { } updated && updated <= nowMs && nowMs - updated < ClaudeHealthyMs)
            _claudeNextAt = Math.Max(_claudeNextAt, updated + ClaudeHealthyMs);

        var shared = ReadClaudeSchedule();
        if (shared is { Refused: { } refused } && refused == print && nowMs - shared.RefusedAt is >= 0 and < ClaudeRefusedRetryMs)
        {
            (_claudeRefused, _claudeRefusedAt, _claudeAwaitingToken) = (refused, shared.RefusedAt, true);
            _claude = Keep(plan, RefusedMessage);
            return true;
        }
        // A schedule further ahead than any wait was written before the clock was set back: it no longer applies. One
        // that ran out recently still carries the failure count, so a restart doesn't reset the back-off.
        if (shared is null || shared.NextAt - nowMs > ClaudeMaxWaitMs || nowMs - shared.NextAt > ClaudeMaxWaitMs) return false;
        _claudeFailures = Math.Max(_claudeFailures, shared.Failures);
        if (shared.NextAt <= nowMs) return false;
        _claudeNextAt = Math.Max(_claudeNextAt, shared.NextAt);
        if (shared.Error is { Length: > 0 } error) _claude = Keep(plan, error);
        return false;
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    private ClaudeSchedule? ReadClaudeSchedule()
    {
        try
        {
            return File.Exists(ClaudeSchedulePath)
                ? JsonSerializer.Deserialize<ClaudeSchedule>(File.ReadAllText(ClaudeSchedulePath), ConfigStore.WriteOpts) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log.LogDebug("claude quota schedule: {Msg}", ex.Message);
            return null;
        }
    }

    private void SaveClaudeSchedule(ClaudeSchedule schedule) =>
        WriteShared(ClaudeSchedulePath, JsonSerializer.Serialize(schedule, ConfigStore.WriteOpts));

    /// <summary>Atomic replace through a temporary file of this writer's own, so two agents can't trip over each other.</summary>
    private void WriteShared(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AppPaths.WriteAllTextPrivate(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug("claude quota files: {Msg}", ex.Message);
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// When to check the Claude quota next: 3 minutes after a success; after a failure 1, 2, 4 … minutes (at most 15),
    /// and never sooner than a Retry-After (at most an hour).
    /// </summary>
    internal static TimeSpan ClaudeDelay(bool ok, int failures, TimeSpan? retryAfter)
    {
        if (ok) return TimeSpan.FromMinutes(3);
        var backoff = TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Clamp(failures, 1, 10) - 1)));
        if (retryAfter is not { } wait) return backoff;
        wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0, 3600) + 5); // a little past the server's time
        return wait > backoff ? wait : backoff;
    }

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta is { } delta ? delta
        : header?.Date is { } date ? (date - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero)
        : null;

    /// <summary>On errors, keeps the last good values and attaches the error.</summary>
    private QuotaProvider Keep(string plan, string error) =>
        _claude is { Limits.Count: > 0 } prev ? prev with { Plan = plan, Error = error }
            : new QuotaProvider("claude", "Claude", plan, [], null, null, error);

    private static List<QuotaLimit> ParseClaudeLimits(JsonElement root)
    {
        var list = new List<QuotaLimit>();
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var kind = Str(l, "kind");
                var model = l.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object
                    && sc.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "display_name") : null;
                var (label, window) = kind switch
                {
                    "session" => ("5-hour session", 5 * 3600L),
                    "weekly_all" => ("Weekly", 7 * 86400L),
                    "weekly_scoped" => ($"Weekly · {model ?? "model"}", 7 * 86400L),
                    _ => (kind ?? "Limit", 0L),
                };
                list.Add(new QuotaLimit(label, Num(l, "percent") ?? 0, Iso(Str(l, "resets_at")),
                    window > 0 ? window : null, Str(l, "severity") ?? "normal"));
            }
        }
        if (list.Count == 0)
        {
            // Legacy format: five_hour / seven_day
            foreach (var (key, label, window) in new[] { ("five_hour", "5-hour session", 18000L), ("seven_day", "Weekly", 604800L) })
                if (root.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object)
                    list.Add(new QuotaLimit(label, Num(o, "utilization") ?? 0, Iso(Str(o, "resets_at")), window, "normal"));
        }
        return list;
    }

    private static string? ClaudeExtra(JsonElement root)
    {
        if (root.TryGetProperty("extra_usage", out var x) && x.ValueKind == JsonValueKind.Object
            && x.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True)
            return $"Extra usage on · {Num(x, "utilization") ?? 0:0}%";
        return null;
    }

    private static string PlanLabel(string? sub, string? tier)
    {
        var s = sub switch { "team" => "Team", "max" => "Max", "pro" => "Pro", "enterprise" => "Enterprise", null => "", _ => sub };
        var m = tier is null ? null : MaxTier().Match(tier);
        return m is { Success: true } ? $"{s} · Max {m.Groups[1].Value}".Trim(' ', '·') : (s == "" ? "Claude" : s);
    }

    [GeneratedRegex(@"max_(\d+x)")]
    private static partial Regex MaxTier();

    // ------------------------------------------------------------------ Codex
    /// <summary>Codex's session logs (read only). Tests point it at a folder of their own.</summary>
    internal string CodexSessionsRoot { get; init; } = Path.Combine(Home, ".codex", "sessions");

    /// <summary>One pass of the usage loop for Codex.</summary>
    internal void CodexTick()
    {
        try { _codex = ReadCodex(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && _codex is { Limits.Count: > 0 })
        {
            log.LogDebug("codex quota: {Msg}", ex.Message); // the log is busy for a moment; it's read again in 30 s
        }
        catch (Exception ex) { _codex = Fail("codex", "Codex", ex.Message); }
    }

    /// <summary>Refresh account-wide quota without creating a thread or inference turn.</summary>
    internal Task<CodexLiveOutcome> RefreshCodexOnceAsync(Func<string?> findTool,
        Func<string, CancellationToken, Task<QuotaProvider>> read, CancellationToken ct) =>
        RefreshCodexOnceAsync(findTool, read, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);

    internal async Task<CodexLiveOutcome> RefreshCodexOnceAsync(Func<string?> findTool,
        Func<string, CancellationToken, Task<QuotaProvider>> read, long now, CancellationToken ct)
    {
        try
        {
            if (!IsCodexLiveEnabled())
            {
                _codexLive = null;
                _codexLiveError = null;
                return CodexLiveOutcome.Skipped;
            }
            var external = _external.FirstOrDefault(p => p.Id == "codex");
            if (external is { Error: null, Limits.Count: > 0, UpdatedAt: { } updated } && now - updated <= 600_000)
            {
                _codexLiveError = null;
                return CodexLiveOutcome.Skipped;
            }
            var path = findTool();
            if (path is null)
            {
                _codexLiveError = "Codex CLI is not installed";
                _codexLive = null;
                return CodexLiveOutcome.Failed;
            }
            _codexLive = await read(path, ct);
            _codexLiveError = null;
            return CodexLiveOutcome.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug("codex live quota: {Message}", ex.Message);
            _codexLive = null;
            _codexLiveError = "Live Codex quota unavailable";
            return CodexLiveOutcome.Failed;
        }
    }

    internal QuotaProvider? CodexSnapshot(long now, QuotaProvider? external = null)
    {
        var local = _codex;
        if (!IsCodexLiveEnabled())
            return CodexBar.Select(local, external, now);
        var live = _codexLive;
        var fresh = live is { Error: null, Limits.Count: > 0, UpdatedAt: { } at } && now - at <= CodexLiveStaleMs;
        var direct = fresh ? live! with { Codex = local?.Codex } : local;
        var selected = CodexBar.Select(direct, external, now);
        if (selected?.Source is "codex-app-server" or "codexbar") return selected;
        var error = _codexLiveError ?? (live is not null && !fresh ? "Live Codex quota is stale" : null);
        if (error is null) return selected;
        return selected is { Limits.Count: > 0 }
            ? selected with { Error = error + "; showing local session snapshot" }
            : selected is not null ? selected with { Error = error } : Fail("codex", "Codex", error);
    }

    private QuotaProvider? ReadCodex()
    {
        var root = CodexSessionsRoot;
        if (!Directory.Exists(root)) return null;

        var files = new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories);
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
        {
            var line = LastLineWith(f.FullName, "\"rate_limits\"");
            if (line is null) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var p) || !p.TryGetProperty("rate_limits", out var rl)
                || rl.ValueKind != JsonValueKind.Object) continue;

            var limits = new List<QuotaLimit>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                var mins = (long)(Num(w, "window_minutes") ?? 0);
                var label = mins switch { 300 => "5-hour", 10080 => "Weekly", >= 1440 => $"{mins / 1440}-day", > 0 => $"{mins / 60}-hour", _ => key };
                var resets = w.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var ra) ? ra * 1000
                    : Num(w, "resets_in_seconds") is { } rs ? new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() + (long)(rs * 1000) : (long?)null;
                var pct = Num(w, "used_percent") ?? 0;
                limits.Add(new QuotaLimit(label, pct, resets, mins > 0 ? mins * 60 : null, pct >= 95 ? "critical" : pct >= 80 ? "warning" : "normal"));
            }

            string? extra = null;
            if (rl.TryGetProperty("credits", out var cr) && cr.ValueKind == JsonValueKind.Object)
            {
                if (cr.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True) extra = "Credits: unlimited";
                else if (cr.TryGetProperty("has_credits", out var has) && has.ValueKind == JsonValueKind.True
                    && Str(cr, "balance") is { } b && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bal))
                    extra = "Credits: " + bal.ToString("#,0", CultureInfo.InvariantCulture);
                else if (has.ValueKind == JsonValueKind.False) extra = "Credits not enabled";
            }
            var reached = limits.Any(l => l.Percent >= 100)
                || rl.TryGetProperty("rate_limit_reached_type", out var reachedType) && reachedType.ValueKind == JsonValueKind.String
                || rl.TryGetProperty("spend_control_reached", out var spend) && spend.ValueKind == JsonValueKind.True;
            if (reached) extra = string.IsNullOrEmpty(extra) ? "Limit reached" : "Limit reached · " + extra;
            var plan = Str(rl, "plan_type") is { Length: > 0 } pt ? char.ToUpper(pt[0]) + pt[1..] : "Codex";
            return new QuotaProvider("codex", "Codex", plan, limits, extra,
                new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(), null, ReadCodexUsage(f.FullName));
        }
        return new QuotaProvider("codex", "Codex", "Codex", [], null, null, "No quota data in recent sessions");
    }

    private static CodexUsage? ReadCodexUsage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fileLen = fs.Length;
        var len = Math.Min(fileLen, 1 << 20);
        fs.Seek(fileLen - len, SeekOrigin.Begin); // from the length read above: an append since then must not shift the window past a record minIndex trusts
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        var minIndex = fileLen > len ? 1 : 0;
        if (minIndex == 0 && lines.Length > 0) lines[0] = lines[0].TrimStart('\uFEFF');
        string? model = null, effort = null;
        JsonElement? info = null;
        long updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        for (var i = lines.Length - 1; i >= minIndex && (info is null || model is null); i--)
        {
            var line = lines[i];
            if (!line.Contains("\"token_count\"") && !line.Contains("\"turn_context\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                if (info is null && Str(payload, "type") == "token_count"
                    && payload.TryGetProperty("info", out var tokenInfo) && tokenInfo.ValueKind == JsonValueKind.Object)
                {
                    info = tokenInfo.Clone();
                    if (DateTimeOffset.TryParse(Str(root, "timestamp"), out var timestamp)) updatedAt = timestamp.ToUnixTimeMilliseconds();
                }
                if (model is null && root.TryGetProperty("type", out var type) && type.GetString() == "turn_context")
                {
                    model = Str(payload, "model");
                    effort = Str(payload, "effort");
                }
            }
            catch (JsonException) { /* partial line at the start of the tail */ }
        }
        if (info is null && model is null) return null;
        var token = info ?? default;
        var total = token.ValueKind == JsonValueKind.Object && token.TryGetProperty("total_token_usage", out var t) ? t : default;
        var last = token.ValueKind == JsonValueKind.Object && token.TryGetProperty("last_token_usage", out var l) ? l : default;
        return new CodexUsage(model, effort, Long(total, "input_tokens"), Long(total, "cached_input_tokens"),
            Long(total, "output_tokens"), Long(total, "reasoning_output_tokens"), Long(total, "total_tokens"),
            Long(last, "input_tokens"), Long(last, "output_tokens"), Long(token, "model_context_window"), updatedAt);
    }

    /// <summary>
    /// Reads the last 1 MB of a file and returns the last complete JSON line containing the given text. A line cut off
    /// mid-write (Codex stopped, or the file read while it appends) is skipped for the one before it.
    /// </summary>
    private static string? LastLineWith(string path, string needle)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fileLen = fs.Length;
        var len = Math.Min(fileLen, 1 << 20);
        fs.Seek(fileLen - len, SeekOrigin.Begin); // from the length read above: an append since then must not shift the window past a record minIndex trusts
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        var minIndex = fileLen > len ? 1 : 0;
        if (minIndex == 0 && lines.Length > 0) lines[0] = lines[0].TrimStart('﻿'); // the first line may be partial when reading a tail slice
        for (var i = lines.Length - 1; i >= minIndex; i--)
        {
            if (!lines[i].Contains(needle) || !lines[i].Contains("\"primary\"")) continue;
            try
            {
                using var _ = JsonDocument.Parse(lines[i]);
                return lines[i];
            }
            catch (JsonException) { }
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers
    private static QuotaProvider Fail(string id, string name, string msg) => new(id, name, name, [], null, null, msg);
    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static long? Iso(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : null;
}
