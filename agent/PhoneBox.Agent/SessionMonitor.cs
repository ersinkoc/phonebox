using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Status">busy | waiting | shell | idle</param>
public sealed record AgentSession(string Id, string Tool, string Name, string Project, string Status,
    string? WaitingFor, long Since, long StartedAt, int? Pid = null,
    string? Model = null, string? Effort = null, long? TotalTokens = null, long? InputTokens = null,
    long? OutputTokens = null, long? CachedInputTokens = null, long? ReasoningTokens = null,
    long? LastInputTokens = null, long? ContextWindow = null, long? TurnStartedAt = null,
    string? Outcome = null, string? LastTool = null);

/// <summary>
/// Watches running AI coding sessions (local and read-only):
///  - Claude Code: ~/.claude/sessions/{pid}.json (status files written by Claude Code itself)
///  - Codex: the latest events in recent rollout files under ~/.codex/sessions
/// </summary>
public sealed class SessionMonitor(ILogger<SessionMonitor> log) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly ConcurrentDictionary<string, string> _codexCwd = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, long Length, CodexDetails Details)> _codexDetails = new();
    private volatile IReadOnlyList<AgentSession> _sessions = [];
    private string? _discoveryRoot;
    private long _nextDiscovery;
    private string[] _candidateFiles = [];

    public IReadOnlyList<AgentSession> Snapshot() => _sessions;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var list = new List<AgentSession>();
            try { list.AddRange(ReadClaude()); } catch (Exception ex) { log.LogDebug("claude sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadCodex()); } catch (Exception ex) { log.LogDebug("codex sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadAntigravity()); } catch (Exception ex) { log.LogDebug("antigravity sessions: {Msg}", ex.Message); }
            _sessions = list;
            await Task.Delay(2000, ct);
        }
    }

    // CodexBar runs `claude` in <Application Support or temp>/CodexBar/ClaudeProbe to read usage.
    // Match only that exact directory so real projects that merely contain "CodexBar" stay visible.
    internal static bool IsCodexBarProbe(string cwd) =>
        cwd.Replace('\\', '/').TrimEnd('/').EndsWith("/CodexBar/ClaudeProbe", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<AgentSession> ReadClaude(string? homeDir = null, Func<int, bool>? isAlive = null)
    {
        var home = homeDir ?? Home;
        var aliveCheck = isAlive ?? Alive;
        var dir = Path.Combine(home, ".claude", "sessions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            AgentSession? s = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                if (pid == 0 || !aliveCheck(pid)) continue;
                // a session that was killed leaves its file behind; Windows may give its PID to another program later
                if (OperatingSystem.IsWindows() && long.TryParse(Str(r, "procStart"), out var procStart) && !SameStart(pid, procStart)) continue;
                var cwd = Str(r, "cwd") ?? "";
                var name = Str(r, "name") ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));

                if (IsCodexBarProbe(cwd)) continue;

                s = new AgentSession(
                    Id: "claude:" + (Str(r, "sessionId") ?? pid.ToString()),
                    Tool: "claude",
                    Name: name,
                    Project: project,
                    Status: Str(r, "status") ?? "idle",
                    WaitingFor: Str(r, "waitingFor"),
                    Since: Long(r, "statusUpdatedAt") ?? Long(r, "updatedAt") ?? 0,
                    StartedAt: Long(r, "startedAt") ?? 0,
                    Pid: pid);
            }
            catch { /* file was being written: retry on the next pass */ }
            if (s is not null) yield return s;
        }
    }

    internal IEnumerable<AgentSession> ReadCodex(string? sessionsRoot = null)
    {
        var root = sessionsRoot ?? Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) yield break;
        var cutoff = DateTime.UtcNow.AddHours(-3);
        // A resumed session appends to its original directory, even months later.
        if (_discoveryRoot != root || Environment.TickCount64 >= _nextDiscovery)
        {
            _candidateFiles = new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc > cutoff).Select(f => f.FullName).ToArray();
            _discoveryRoot = root;
            _nextDiscovery = Environment.TickCount64 + 30_000;
        }
        var files = _candidateFiles.Select(path => new FileInfo(path)).Where(f => f.Exists && f.LastWriteTimeUtc > cutoff).ToArray();
        var active = files.Select(f => f.FullName).ToHashSet();
        foreach (var old in _codexDetails.Keys.Where(p => !active.Contains(p)))
        { _codexDetails.TryRemove(old, out _); _codexCwd.TryRemove(old, out _); }

        foreach (var f in files)
        {
            AgentSession? s = null;
            try
            {
                var cwd = _codexCwd.GetOrAdd(f.FullName, ReadCodexCwd);
                var hasCached = _codexDetails.TryGetValue(f.FullName, out var cached);
                var details = hasCached && cached.LastWrite == f.LastWriteTimeUtc && cached.Length == f.Length
                    ? cached.Details : ReadCodexDetails(f.FullName,
                        hasCached && f.Length > cached.Length ? cached.Details : null,
                        hasCached && f.Length > cached.Length ? cached.Details.ReadOffset : 0);
                _codexDetails[f.FullName] = (f.LastWriteTimeUtc, f.Length, details);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var status = details.Lifecycle == "task_started" && age < TimeSpan.FromMinutes(3) ? "busy" : "idle";
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var id = f.Name.Length > 8 ? f.Name[^14..^6] : f.Name;
                s = new AgentSession("codex:" + f.Name, "codex", $"{project.ToLowerInvariant()}-{id[..4]}", project,
                    status, null, details.LastActivityAt ?? new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds(),
                    Model: details.Model, Effort: details.Effort, TotalTokens: details.TotalTokens,
                    InputTokens: details.InputTokens, OutputTokens: details.OutputTokens,
                    CachedInputTokens: details.CachedInputTokens, ReasoningTokens: details.ReasoningTokens,
                    LastInputTokens: details.LastInputTokens, ContextWindow: details.ContextWindow,
                    TurnStartedAt: details.TurnStartedAt, Outcome: details.Lifecycle switch
                    { "task_complete" => "completed", "turn_aborted" => "aborted", _ => null },
                    LastTool: details.LastTool);
            }
            catch { }
            if (s is not null) yield return s;
        }
    }

    private static string ReadCodexCwd(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var first = sr.ReadLine();
        if (first is null) return "";
        using var doc = JsonDocument.Parse(first);
        return doc.RootElement.TryGetProperty("payload", out var p) ? Str(p, "cwd") ?? "" : "";
    }

    private sealed record CodexDetails(string? Model, string? Effort, long? TotalTokens, long? InputTokens,
        long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens, long? LastInputTokens,
        long? ContextWindow, long? LastActivityAt, long? TurnStartedAt, string? Lifecycle, string? LastTool, long ReadOffset);

    /// <summary>Scans metadata and counters once, then follows appended lines; no prompts or tool arguments leave the Mac.</summary>
    private static CodexDetails ReadCodexDetails(string path, CodexDetails? previous, long previousLength)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = previous is null ? 0 : previousLength;
        fs.Seek(start, SeekOrigin.Begin);
        using var buffer = new MemoryStream();
        fs.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var lines = Encoding.UTF8.GetString(bytes, 0, complete).Split('\n');
        var model = previous?.Model;
        var effort = previous?.Effort;
        var lifecycle = previous?.Lifecycle;
        var lastTool = previous?.LastTool;
        var lastActivityAt = previous?.LastActivityAt;
        var turnStartedAt = previous?.TurnStartedAt;
        var totalTokens = previous?.TotalTokens;
        var inputTokens = previous?.InputTokens;
        var outputTokens = previous?.OutputTokens;
        var cachedInputTokens = previous?.CachedInputTokens;
        var reasoningTokens = previous?.ReasoningTokens;
        var lastInputTokens = previous?.LastInputTokens;
        var contextWindow = previous?.ContextWindow;
        foreach (var l in lines)
        {
            if (!l.Contains("\"event_msg\"") && !l.Contains("\"turn_context\"")
                && !l.Contains("\"custom_tool_call\"") && !l.Contains("\"function_call\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                var type = Str(root, "type");
                var eventType = Str(payload, "type");
                var ts = DateTimeOffset.TryParse(Str(root, "timestamp"), out var dt) ? dt.ToUnixTimeMilliseconds() : (long?)null;
                if (type == "event_msg")
                {
                    if (eventType != "token_count") lastActivityAt = ts;
                    if (eventType == "token_count"
                        && payload.TryGetProperty("info", out var tokenInfo) && tokenInfo.ValueKind == JsonValueKind.Object)
                    {
                        var total = tokenInfo.TryGetProperty("total_token_usage", out var t) ? t : default;
                        var last = tokenInfo.TryGetProperty("last_token_usage", out var u) ? u : default;
                        totalTokens = Long(total, "total_tokens") ?? totalTokens;
                        inputTokens = Long(total, "input_tokens") ?? inputTokens;
                        outputTokens = Long(total, "output_tokens") ?? outputTokens;
                        cachedInputTokens = Long(total, "cached_input_tokens") ?? cachedInputTokens;
                        reasoningTokens = Long(total, "reasoning_output_tokens") ?? reasoningTokens;
                        lastInputTokens = Long(last, "input_tokens") ?? lastInputTokens;
                        contextWindow = Long(tokenInfo, "model_context_window") ?? contextWindow;
                    }
                    if (eventType is "task_started" or "task_complete" or "turn_aborted")
                    {
                        lifecycle = eventType;
                        if (eventType == "task_started") turnStartedAt = ts;
                    }
                }
                else if (type == "turn_context")
                {
                    model = Str(payload, "model") ?? model;
                    effort = Str(payload, "effort") ?? effort;
                }
                else if (type == "response_item" && eventType is "function_call" or "custom_tool_call")
                    lastTool = Str(payload, "name") ?? lastTool;
            }
            catch (JsonException) { }
        }
        return new CodexDetails(model, effort, totalTokens, inputTokens, outputTokens, cachedInputTokens,
            reasoningTokens, lastInputTokens, contextWindow, lastActivityAt, turnStartedAt, lifecycle, lastTool, start + complete);
    }

    internal static IEnumerable<AgentSession> ReadAntigravity(string? geminiHome = null, Func<int, bool>? isAlive = null)
    {
        var root = geminiHome ?? Path.Combine(Home, ".gemini");
        if (!Directory.Exists(root)) yield break;

        var seen = new HashSet<string>();

        // 1. Check metadata cache under antigravity-cli/cache/conversation_metadata.json
        var metaFile = Path.Combine(root, "antigravity-cli", "cache", "conversation_metadata.json");
        if (File.Exists(metaFile))
        {
            JsonDocument? doc = null;
            try
            {
                using var fs = new FileStream(metaFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                doc = JsonDocument.Parse(fs);
            }
            catch { }

            if (doc is not null)
            {
                using (doc)
                {
                    if (doc.RootElement.TryGetProperty("conversations", out var convs) && convs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in convs.EnumerateObject())
                        {
                            var cid = prop.Name;
                            var obj = prop.Value;
                            var summary = obj.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Object ? sm : default;
                            var title = (summary.ValueKind == JsonValueKind.Object ? Str(summary, "Title") : null) ?? "";
                            var wsUris = summary.ValueKind == JsonValueKind.Object && summary.TryGetProperty("WorkspaceURIs", out var wsu) && wsu.ValueKind == JsonValueKind.Array ? wsu : default;
                            var workspace = "";
                            if (wsUris.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var u in wsUris.EnumerateArray())
                                {
                                    if (u.ValueKind == JsonValueKind.String && u.GetString() is { } uriStr)
                                    {
                                        workspace = NormalizeUri(uriStr);
                                        break;
                                    }
                                }
                            }
                            var project = Path.GetFileName(workspace.TrimEnd('\\', '/'));
                            if (string.IsNullOrWhiteSpace(project)) project = "antigravity";

                            var lastModStr = Str(obj, "last_modified_time") ?? (summary.ValueKind == JsonValueKind.Object ? Str(summary, "UpdatedAt") : null);
                            var since = DateTimeOffset.TryParse(lastModStr, out var dto) ? dto.ToUnixTimeMilliseconds() : 0L;

                            seen.Add(cid);
                            yield return new AgentSession(
                                Id: "antigravity:" + cid,
                                Tool: "antigravity",
                                Name: string.IsNullOrWhiteSpace(title) ? project : title,
                                Project: project,
                                Status: (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since < 300_000) ? "busy" : "idle",
                                WaitingFor: null,
                                Since: since,
                                StartedAt: since);
                        }
                    }
                }
            }
        }

        // 2. Discover individual conversation files under antigravity/conversations and antigravity-cli/conversations
        foreach (var sub in new[] { "antigravity", "antigravity-cli" })
        {
            var convDir = Path.Combine(root, sub, "conversations");
            if (!Directory.Exists(convDir)) continue;
            foreach (var db in Directory.EnumerateFiles(convDir, "*.db"))
            {
                var name = Path.GetFileNameWithoutExtension(db);
                if (seen.Contains(name)) continue;
                var fi = new FileInfo(db);
                if (!fi.Exists) continue;
                var since = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var started = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeMilliseconds();
                var status = (DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromMinutes(5)) ? "busy" : "idle";
                seen.Add(name);
                yield return new AgentSession(
                    Id: "antigravity:" + name,
                    Tool: "antigravity",
                    Name: "antigravity-" + (name.Length > 8 ? name[..8] : name),
                    Project: "antigravity",
                    Status: status,
                    WaitingFor: null,
                    Since: since,
                    StartedAt: started);
            }
        }
    }

    private static string NormalizeUri(string uriStr)
    {
        try
        {
            var uri = new Uri(uriStr);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (OperatingSystem.IsWindows() && path.StartsWith('/') && path.Length > 2 && path[2] == ':')
                path = path[1..];
            return path.Replace('/', '\\');
        }
        catch
        {
            return uriStr;
        }
    }

    /// <summary>Claude Code writes its process' start time (a FILETIME) into the session file; another process with the same PID started later.</summary>
    private static bool SameStart(int pid, long fileTimeUtc)
    {
        try { using var p = Process.GetProcessById(pid); return Math.Abs(p.StartTime.ToFileTimeUtc() - fileTimeUtc) < 10_000_000; } // 1 s
        catch { return true; }
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}
