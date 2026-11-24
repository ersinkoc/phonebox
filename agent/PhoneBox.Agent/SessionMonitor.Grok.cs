using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class SessionMonitor
{
    private const int GrokDiscoveryIntervalMs = 30_000;
    private const int GrokTailBytes = 256 * 1024;
    private static readonly TimeSpan GrokWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan GrokBusyWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GrokStartSkew = TimeSpan.FromSeconds(2);
    /// <summary>2001-09-09 in Unix microseconds. Clock ticks since boot stay far below it.</summary>
    private const long GrokUnixMicrosFloor = 1_000_000_000_000_000;

    /// <summary>Sessions shown at most, newest first, like Antigravity.</summary>
    private const int GrokMaxSessions = 50;
    /// <summary>A metadata file larger than this is skipped: usage.json grows with every turn.</summary>
    private const long GrokMaxJsonBytes = 4 * 1024 * 1024;
    /// <summary>Parsed metadata per session folder, reused while none of its files changed (the pass runs every 2 s).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, GrokParsed Parsed)> _grokParsed = new();

    private string? _grokDiscoveryHome;
    private long _nextGrokDiscovery;
    private string[] _grokSessionDirs = [];

    private readonly record struct GrokLive(int Pid, long PidStart);
    private readonly record struct GrokTail(bool Open, bool Waiting, string? Outcome, string? LastTool, long? TurnStartedAt);
    private sealed record GrokParsed(string Project, long Since, long StartedAt, string? Model, string? Effort,
        long? TotalTokens, long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens,
        long? LastInputTokens, long? ContextWindow);

    /// <summary>
    /// Recent Grok CLI sessions under <paramref name="homeDir"/> (or <c>GROK_HOME</c>, or <c>~/.grok</c>).
    /// Reads <c>active_sessions.json</c> plus each session's <c>summary.json</c>, <c>usage.json</c>,
    /// <c>signals.json</c> and a tail of <c>events.jsonl</c>. The phone gets the project folder, status,
    /// model, effort and token counters. Prompts, titles, turn summaries and costs are not copied out.
    /// </summary>
    internal IEnumerable<AgentSession> ReadGrok(string? homeDir = null, Func<int, long, bool>? sameProcess = null)
    {
        var home = string.IsNullOrWhiteSpace(homeDir) ? GrokHome() : homeDir;
        var root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) yield break;
        var alive = sameProcess ?? GrokSameProcess;
        var cutoff = DateTime.UtcNow - GrokWindow;
        if (_grokDiscoveryHome != home || Environment.TickCount64 >= _nextGrokDiscovery)
        {
            var found = new List<string>();
            foreach (var projectDir in Directory.EnumerateDirectories(root))
            {
                // A project folder that is removed or unreadable mid-scan must not hide the other projects.
                try
                {
                    foreach (var sessionDir in Directory.EnumerateDirectories(projectDir))
                    {
                        if (GrokNewest(sessionDir) is { } at && at > cutoff) found.Add(sessionDir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _grokSessionDirs = found.ToArray();
            _grokDiscoveryHome = home;
            _nextGrokDiscovery = Environment.TickCount64 + GrokDiscoveryIntervalMs;
        }

        var live = ReadGrokActive(Path.Combine(home, "active_sessions.json"));
        var recent = new List<(string Dir, string Id, DateTime Newest)>();
        foreach (var dir in _grokSessionDirs)
        {
            try
            {
                var id = Path.GetFileName(dir);
                if (GrokId(id) && GrokNewest(dir) is { } newest && newest > cutoff) recent.Add((dir, id, newest));
            }
            catch { /* a folder removed mid-pass */ }
        }
        var shown = recent.OrderByDescending(r => r.Newest).Take(GrokMaxSessions).ToList();
        foreach (var gone in _grokParsed.Keys.Where(k => !shown.Any(s => s.Dir == k)).ToList()) _grokParsed.TryRemove(gone, out _);
        foreach (var (dir, id, newest) in shown)
        {
            AgentSession? session = null;
            try
            {
                live.TryGetValue(id, out var running);
                var runningNow = running.Pid > 0 && alive(running.Pid, running.PidStart);
                var tail = ReadGrokTail(Path.Combine(dir, "events.jsonl"));
                session = GrokSession(ParsedGrok(dir), tail, id, runningNow ? running.Pid : null, newest);
            }
            catch { /* one session being written must not drop the rest */ }
            if (session is not null) yield return session;
        }
    }

    /// <summary>The parsed metadata of one session folder, read again only when one of its files changed.</summary>
    private GrokParsed ParsedGrok(string dir)
    {
        var stamp = string.Join('|', new[] { "summary.json", "usage.json", "signals.json" }.Select(name =>
        {
            var info = new FileInfo(Path.Combine(dir, name));
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
        }));
        if (_grokParsed.TryGetValue(dir, out var cached) && cached.Stamp == stamp) return cached.Parsed;
        var parsed = ParseGrokSession(dir, out var transient);
        // a file that could not be read this time (locked, access denied) is tried again on the next pass
        if (transient) _grokParsed.TryRemove(dir, out _);
        else _grokParsed[dir] = (stamp, parsed);
        return parsed;
    }

    /// <summary>Status depends on the clock and the running process, so it is worked out on every pass.</summary>
    private static AgentSession GrokSession(GrokParsed p, GrokTail tail, string id, int? pid, DateTime newest)
    {
        var fresh = DateTime.UtcNow - newest < GrokBusyWindow;
        var waiting = tail.Open && tail.Waiting && pid is not null;
        var status = waiting ? "waiting" : tail.Open && fresh ? "busy" : "idle";
        return new AgentSession(
            Id: "grok:" + id,
            Tool: "grok",
            Name: p.Project.ToLowerInvariant() + "-" + id[..4],
            Project: p.Project,
            Status: status,
            WaitingFor: waiting ? "permission" : null,
            Since: p.Since > 0 ? p.Since : new DateTimeOffset(newest).ToUnixTimeMilliseconds(),
            StartedAt: p.StartedAt,
            Pid: pid,
            Model: p.Model,
            Effort: p.Effort,
            TotalTokens: p.TotalTokens,
            InputTokens: p.InputTokens,
            OutputTokens: p.OutputTokens,
            CachedInputTokens: p.CachedInputTokens,
            ReasoningTokens: p.ReasoningTokens,
            LastInputTokens: p.LastInputTokens,
            ContextWindow: p.ContextWindow,
            TurnStartedAt: tail.Open ? tail.TurnStartedAt : null,
            Outcome: tail.Open ? null : GrokOutcome(tail.Outcome),
            LastTool: GrokLabel(tail.LastTool, 64));
    }

    private static GrokParsed ParseGrokSession(string dir, out bool transient)
    {
        transient = false;
        using var summary = ReadSharedJson(Path.Combine(dir, "summary.json"), ref transient);
        using var usage = ReadSharedJson(Path.Combine(dir, "usage.json"), ref transient);
        using var signals = ReadSharedJson(Path.Combine(dir, "signals.json"), ref transient);
        var summaryRoot = summary?.RootElement ?? default;
        var usageRoot = usage?.RootElement ?? default;
        var signalsRoot = signals?.RootElement ?? default;
        var session = usageRoot.ValueKind == JsonValueKind.Object && usageRoot.TryGetProperty("session", out var body)
            && body.ValueKind == JsonValueKind.Object ? body : default;
        var cwd = summaryRoot.ValueKind == JsonValueKind.Object && summaryRoot.TryGetProperty("info", out var info)
            ? Str(info, "cwd") : null;
        if (string.IsNullOrEmpty(cwd))
        {
            try { cwd = Uri.UnescapeDataString(Path.GetFileName(Path.GetDirectoryName(dir) ?? "")); }
            catch (UriFormatException) { cwd = null; }
        }
        var lastTurn = LastGrokTurn(usageRoot);
        return new GrokParsed(
            Project: GrokProject(cwd),
            Since: IsoMs(Str(summaryRoot, "last_active_at")) ?? IsoMs(Str(usageRoot, "updatedAt")) ?? 0,
            StartedAt: IsoMs(Str(summaryRoot, "created_at")) ?? 0,
            Model: GrokLabel(Str(session, "primaryModelId") ?? Str(summaryRoot, "current_model_id") ?? Str(signalsRoot, "primaryModelId"), 64),
            Effort: GrokLabel(Str(summaryRoot, "reasoning_effort"), 16),
            TotalTokens: NonNeg(Long(session, "totalTokens")),
            InputTokens: NonNeg(Long(session, "inputTokens")),
            OutputTokens: NonNeg(Long(session, "outputTokens")),
            CachedInputTokens: NonNeg(Long(session, "cachedReadTokens")),
            ReasoningTokens: NonNeg(Long(session, "reasoningTokens")),
            LastInputTokens: NonNeg(Long(lastTurn, "inputTokens")),
            ContextWindow: NonNeg(Long(signalsRoot, "contextWindowTokens")));
    }

    private static JsonElement LastGrokTurn(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object || !usage.TryGetProperty("turns", out var turns)
            || turns.ValueKind != JsonValueKind.Array) return default;
        JsonElement last = default;
        foreach (var turn in turns.EnumerateArray()) last = turn;
        return last;
    }

    private static GrokTail ReadGrokTail(string path)
    {
        byte[] buf;
        var skipped = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - GrokTailBytes);
            skipped = start > 0;
            fs.Seek(start, SeekOrigin.Begin);
            buf = new byte[fs.Length - start];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < buf.Length) buf = buf[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }

        var end = Array.LastIndexOf(buf, (byte)'\n');
        if (end < 0) return default;
        var lines = Encoding.UTF8.GetString(buf, 0, end).Split('\n');
        var seq = 0;
        int? startSeq = null, endSeq = null, phaseSeq = null;
        long? turnStartedAt = null;
        string? outcome = null, phase = null, lastTool = null;
        for (var i = skipped ? 1 : 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("\"turn_started\"", StringComparison.Ordinal)
                && !line.Contains("\"turn_ended\"", StringComparison.Ordinal)
                && !line.Contains("\"phase_changed\"", StringComparison.Ordinal)
                && !line.Contains("\"tool_started\"", StringComparison.Ordinal)
                && !line.Contains("\"tool_completed\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = Str(root, "type");
                if (type is not ("turn_started" or "turn_ended" or "phase_changed" or "tool_started" or "tool_completed"))
                    continue;
                seq++;
                var ts = IsoMs(Str(root, "ts"));
                switch (type)
                {
                    case "turn_started":
                        startSeq = seq;
                        turnStartedAt = ts ?? turnStartedAt;
                        break;
                    case "turn_ended":
                        endSeq = seq;
                        outcome = Str(root, "outcome");
                        break;
                    case "phase_changed":
                        phaseSeq = seq;
                        phase = Str(root, "phase");
                        break;
                    default:
                        lastTool = Str(root, "tool_name") ?? lastTool;
                        break;
                }
            }
            catch (JsonException) { }
        }
        var open = (startSeq is not null && (endSeq is null || startSeq > endSeq))
            || (phaseSeq is not null && (endSeq is null || phaseSeq > endSeq))
            || (seq > 0 && endSeq is null && startSeq is null);
        // A later tool means the prompt was answered. Only the latest phase keeps the session waiting.
        var waiting = open && phase == "permission_prompt" && phaseSeq is not null && phaseSeq == seq;
        return new GrokTail(open, waiting, outcome, lastTool, turnStartedAt);
    }

    private static Dictionary<string, GrokLive> ReadGrokActive(string path)
    {
        var map = new Dictionary<string, GrokLive>(StringComparer.Ordinal);
        using var doc = ReadSharedJson(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return map;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            // One odd row (a string or null pid, a value that is not an object) must not hide the other sessions.
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = Str(row, "session_id");
            if (!GrokId(id)) continue;
            var pid = row.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;
            map[id!] = new GrokLive(pid, Long(row, "pid_start") ?? 0);
        }
        return map;
    }

    private static DateTime? GrokNewest(string dir)
    {
        DateTime? newest = null;
        foreach (var name in new[] { "signals.json", "usage.json", "summary.json", "events.jsonl" })
        {
            var info = new FileInfo(Path.Combine(dir, name));
            if (!info.Exists) continue;
            if (newest is null || info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
        }
        return newest;
    }

    private static string GrokHome()
    {
        var root = Environment.GetEnvironmentVariable("GROK_HOME");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Home, ".grok")
            : root;
    }

    /// <summary>
    /// The pid is the Grok process only when it is still running and started with the recorded time. On macOS the
    /// Grok CLI records the start as Unix microseconds. A smaller value is not a Unix time (on Linux it may be clock
    /// ticks since boot), so only the running pid is checked then; a wrong guess would mark a live session stopped.
    /// </summary>
    internal static bool GrokSameProcess(int pid, long pidStartMicros)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (pidStartMicros < GrokUnixMicrosFloor) return true;
            var expected = DateTimeOffset.FromUnixTimeMilliseconds(pidStartMicros / 1000);
            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (actual - expected).Duration() < GrokStartSkew;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    private static JsonDocument? ReadSharedJson(string path)
    {
        var transient = false;
        return ReadSharedJson(path, ref transient);
    }

    /// <summary><paramref name="transient"/> is set when an existing file could not be read (locked, access denied);
    /// a missing, oversized or malformed file is a result, not a failure to retry.</summary>
    private static JsonDocument? ReadSharedJson(string path, ref bool transient)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > GrokMaxJsonBytes) return null;
            return JsonDocument.Parse(fs);
        }
        catch (JsonException) { return null; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { transient = true; return null; }
    }

    private static bool GrokId(string? id)
    {
        if (id is null || id.Length is < 8 or > 64) return false;
        foreach (var c in id)
            if (!char.IsAsciiHexDigit(c) && c != '-') return false;
        return true;
    }

    private static string GrokProject(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "grok";
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        if (name is "" or "." or "..") return "grok";
        if (name.Any(char.IsControl)) name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (name.Length > 100) name = name[..100];
        return name.Length == 0 ? "grok" : name;
    }

    /// <summary>A short label (model, effort, tool name). Anything with a space or a path stays on the Mac.</summary>
    private static string? GrokLabel(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max) return null;
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) return null;
        return value;
    }

    private static string? GrokOutcome(string? outcome) => outcome switch
    {
        "completed" => "completed",
        "cancelled" or "canceled" or "aborted" => "aborted",
        "failed" or "error" => "failed",
        _ => null,
    };

    private static long? NonNeg(long? value) => value is >= 0 ? value : null;

    private static long? IsoMs(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUnixTimeMilliseconds() : null;
}
