using System.Globalization;
using System.Net.Http.Headers;
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
///  - Claude: api.anthropic.com/api/oauth/usage with the OAuth token from ~/.claude/.credentials.json
///    (the endpoint behind Claude Code's /usage command). The token is only read and NEVER refreshed;
///    Claude Code refreshes it itself (refreshing it here could break Claude Code's session).
///  - Codex: the last "rate_limits" record in the newest rollout file under ~/.codex/sessions (fully local).
///  - Antigravity: a running local server, or the signed-in agy CLI's structured /usage command.
/// </summary>
public sealed partial class QuotaMonitor(ILogger<QuotaMonitor> log) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private volatile QuotaProvider? _claude;
    private volatile QuotaProvider? _codex;
    private volatile QuotaProvider? _antigravity;
    private long _nextClaude;
    private long _nextCodexBar;
    private long _nextAntigravity;
    private volatile IReadOnlyList<QuotaProvider> _external = [];

    /// <summary>Seeds quota state in unit tests without running background loops.</summary>
    internal void SetStateForTest(QuotaProvider? claude = null, IReadOnlyList<QuotaProvider>? external = null)
    {
        _claude = claude;
        if (external is not null) _external = external;
    }

    public IReadOnlyList<QuotaProvider> Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var external = _external;
        var core = new[] { "claude", "codex", "antigravity" }
            .Select(id => CodexBar.Select(id == "claude" ? _claude : id == "codex" ? _codex : _antigravity,
                external.FirstOrDefault(p => p.Id == id), now))
            .OfType<QuotaProvider>();
        var extra = external
            .Where(p => p.Id is not ("claude" or "codex" or "antigravity"))
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
        var extra = p.Extra is { } x && x.StartsWith(reached, StringComparison.Ordinal) && !limits.Any(l => l.Percent >= 100)
            ? (x.Length > reached.Length + 3 ? x[(reached.Length + 3)..] : null) : p.Extra;
        return p with { Limits = limits, Extra = extra };
    }

    /// <summary>
    /// Periodically refreshes local and CodexBar quota providers in the background.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= _nextCodexBar)
            {
                var path = ToolProcess.Find("codexbar");
                try
                {
                    if (path is null) _external = _external.Select(p => p with { Error = "CodexBar is not installed" }).ToList();
                    else
                    {
                        var latest = await CodexBar.ReadAsync(path, ct);
                        _external = latest.Select(p => p.Limits.Count == 0 && _external.FirstOrDefault(old => old.Id == p.Id) is { } cached
                            ? cached with { Error = p.Error ?? "CodexBar returned no data" } : p)
                            .Concat(_external.Where(old => !latest.Any(p => p.Id == old.Id)).Select(p => p with { Limits = [], Error = "CodexBar omitted this provider" })).ToList();
                    }
                    _nextCodexBar = Environment.TickCount64 + 180_000;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogDebug("CodexBar: {Message}", ex.Message);
                    _external = _external.Select(p => p with { Error = "CodexBar refresh failed" }).ToList();
                    _nextCodexBar = Environment.TickCount64 + 60_000;
                }
            }
            try { _codex = ReadCodex(); }
            catch (Exception ex) { _codex = Fail("codex", "Codex", ex.Message); }

            if (Environment.TickCount64 >= _nextAntigravity)
            {
                var ok = await RefreshAntigravity(ct);
                _nextAntigravity = Environment.TickCount64 + (ok ? 60_000 : 30_000);
            }

            if (Environment.TickCount64 >= _nextClaude)
            {
                // Every 3 minutes when healthy; retry after 1 minute on errors.
                var ok = await RefreshClaude(ct);
                _nextClaude = Environment.TickCount64 + (ok ? 180_000 : 60_000);
            }
            await Wake.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    // ------------------------------------------------------------------ Claude
    private async Task<bool> RefreshClaude(CancellationToken ct)
    {
        var path = Path.Combine(Home, ".claude", ".credentials.json");
        string? json = null;
        // Claude Code may be rewriting the file right now: try again on the next round instead of ending the service
        try { if (File.Exists(path)) json = await File.ReadAllTextAsync(path, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
#if !WINDOWS
        // macOS: Claude Code stores the credentials in the login Keychain instead of a file (read-only access).
        if (json is null && OperatingSystem.IsMacOS()) json = Sh.Run("security", "find-generic-password", "-s", "Claude Code-credentials", "-w")?.Trim();
#endif
        if (string.IsNullOrEmpty(json)) { _claude = null; return true; }

        string plan = "Claude";
        try
        {
            using var creds = JsonDocument.Parse(json);
            var o = creds.RootElement.GetProperty("claudeAiOauth");
            plan = PlanLabel(Str(o, "subscriptionType"), Str(o, "rateLimitTier"));
            var token = Str(o, "accessToken");
            var expires = o.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms) ? ms : 0;

            if (string.IsNullOrEmpty(token)) { _claude = Keep(plan, "Not signed in to Claude Code"); return false; }
            if (expires > 0 && expires < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                _claude = Keep(plan, "Token expired; it will refresh when Claude Code runs");
                return false;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _claude = Keep(plan, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
                return false;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            _claude = new QuotaProvider("claude", "Claude", plan, ParseClaudeLimits(doc.RootElement),
                ClaudeExtra(doc.RootElement), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug("claude quota: {Msg}", ex.Message);
            _claude = Keep(plan, "Could not fetch quota: " + ex.Message);
            return false;
        }
    }

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
    private static QuotaProvider? ReadCodex()
    {
        var root = Path.Combine(Home, ".codex", "sessions");
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
        var len = Math.Min(fs.Length, 1 << 20);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        string? model = null, effort = null;
        JsonElement? info = null;
        long updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        for (var i = lines.Length - 1; i >= 1 && (info is null || model is null); i--)
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

    /// <summary>Reads the last 1 MB of a file and returns the last line containing the given text.</summary>
    private static string? LastLineWith(string path, string needle)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = Math.Min(fs.Length, 1 << 20);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        for (var i = lines.Length - 1; i >= 1; i--) // the first line may be partial
            if (lines[i].Contains(needle) && lines[i].Contains("\"primary\"")) return lines[i];
        return null;
    }

    // ------------------------------------------------------------------ helpers
    private static QuotaProvider Fail(string id, string name, string msg) => new(id, name, name, [], null, null, msg);
    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static long? Iso(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : null;
}
