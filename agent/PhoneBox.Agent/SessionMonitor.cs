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

public sealed record AgentSessionDetail(string Id, string Tool, string Name, string Project, string Status,
    string? WaitingFor, long Since, long StartedAt, string? LastPrompt, string? LastResponse, int? Pid = null);

/// <summary>
/// Watches running AI coding sessions (local and read-only):
///  - Claude Code: ~/.claude/sessions/{pid}.json (status files written by Claude Code itself)
///  - Codex: the latest events in recent rollout files under ~/.codex/sessions
///  - Antigravity: ~/.gemini/antigravity daemon/conversation state
///  - OpenClaw: ~/.openclaw local state
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
            try { list.AddRange(ReadOpenClaw()); } catch (Exception ex) { log.LogDebug("openclaw sessions: {Msg}", ex.Message); }
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

    private static int[] FindProcessIds(string name)
    {
        try
        {
            var procs = Process.GetProcessesByName(name);
            var ids = new int[procs.Length];
            for (var i = 0; i < procs.Length; i++)
            {
                using var p = procs[i];
                ids[i] = p.Id;
            }
            return ids;
        }
        catch { return []; }
    }

    internal static IEnumerable<AgentSession> ReadAntigravity(
        string? homeDir = null,
        Func<string, int[]>? getProcessIds = null,
        Func<int, bool>? isAlive = null)
    {
        var home = homeDir ?? Home;
        var agyDir = Path.Combine(home, ".gemini", "antigravity");
        if (!Directory.Exists(agyDir)) yield break;

        var pLookup = getProcessIds ?? FindProcessIds;
        var aliveCheck = isAlive ?? Alive;
        int? agyPid = null;
        foreach (var procName in new[] { "Antigravity", "antigravity" })
        {
            foreach (var pid in pLookup(procName))
            {
                if (pid > 0 && aliveCheck(pid)) { agyPid = pid; break; }
            }
            if (agyPid is not null) break;
        }

        var daemonDir = Path.Combine(agyDir, "daemon");
        if (agyPid is null && Directory.Exists(daemonDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(daemonDir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (doc.RootElement.TryGetProperty("pid", out var pElem) && pElem.TryGetInt32(out var pid) && pid > 0 && aliveCheck(pid))
                        {
                            agyPid = pid;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        if (agyPid is null) yield break;

        var detectedProject = "antigravity";
        var newestWriteUtc = DateTime.MinValue;

        var convDir = Path.Combine(agyDir, "conversations");
        if (Directory.Exists(convDir))
        {
            try
            {
                foreach (var f in new DirectoryInfo(convDir).EnumerateFiles())
                {
                    if ((f.Extension.StartsWith(".db", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".pb", StringComparison.OrdinalIgnoreCase))
                        && f.LastWriteTimeUtc > newestWriteUtc)
                        newestWriteUtc = f.LastWriteTimeUtc;
                }
            }
            catch { }
        }

        var brainDir = Path.Combine(agyDir, "brain");
        if (Directory.Exists(brainDir))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(brainDir))
                {
                    var transcript = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(transcript)) continue;
                    var wt = File.GetLastWriteTimeUtc(transcript);
                    if (wt > newestWriteUtc) newestWriteUtc = wt;
                }
            }
            catch { }
        }

        var walFile = Path.Combine(agyDir, "conversation_summaries.db-wal");
        if (File.Exists(walFile))
        {
            try
            {
                var fi = new FileInfo(walFile);
                if (fi.LastWriteTimeUtc > newestWriteUtc) newestWriteUtc = fi.LastWriteTimeUtc;
                using var fs = new FileStream(walFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[(int)Math.Min(fs.Length, 65536)];
                var read = fs.Read(buf, 0, buf.Length);
                var txt = Encoding.UTF8.GetString(buf, 0, read);
                var match = System.Text.RegularExpressions.Regex.Match(txt, @"file:///[^""\s<>]+");
                if (match.Success)
                {
                    var p = match.Value["file://".Length..].TrimEnd('/', '\\');
                    var candidate = Path.GetFileName(p);
                    if (!string.IsNullOrWhiteSpace(candidate)) detectedProject = candidate;
                }
            }
            catch { }
        }

        var age = DateTime.UtcNow - newestWriteUtc;
        var status = newestWriteUtc > DateTime.MinValue && age < TimeSpan.FromMinutes(2) ? "busy" : "idle";
        var since = newestWriteUtc > DateTime.MinValue
            ? new DateTimeOffset(newestWriteUtc).ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        yield return new AgentSession(
            Id: "antigravity:main",
            Tool: "antigravity",
            Name: "Antigravity",
            Project: detectedProject,
            Status: status,
            WaitingFor: null,
            Since: since,
            StartedAt: since,
            Pid: agyPid);
    }

    internal static IEnumerable<AgentSession> ReadOpenClaw(
        string? homeDir = null,
        Func<string, int[]>? getProcessIds = null,
        Func<int, bool>? isAlive = null)
    {
        var pLookup = getProcessIds ?? FindProcessIds;
        var aliveCheck = isAlive ?? Alive;
        int? clawPid = null;
        foreach (var procName in new[] { "openclaw", "OpenClaw" })
        {
            foreach (var pid in pLookup(procName))
            {
                if (pid > 0 && aliveCheck(pid)) { clawPid = pid; break; }
            }
            if (clawPid is not null) break;
        }
        if (clawPid is null) yield break;

        var home = homeDir ?? Home;
        var clawDir = Path.Combine(home, ".openclaw");
        var newestWriteUtc = DateTime.MinValue;
        if (Directory.Exists(clawDir))
        {
            try
            {
                var scanned = 0;
                foreach (var f in new DirectoryInfo(clawDir).EnumerateFiles())
                {
                    if (f.LastWriteTimeUtc > newestWriteUtc) newestWriteUtc = f.LastWriteTimeUtc;
                    if (++scanned >= 300) break;
                }
                if (scanned < 300)
                {
                    foreach (var sub in new DirectoryInfo(clawDir).EnumerateDirectories())
                    {
                        if (sub.Name is "node_modules" or ".git") continue;
                        foreach (var f in sub.EnumerateFiles())
                        {
                            if (f.LastWriteTimeUtc > newestWriteUtc) newestWriteUtc = f.LastWriteTimeUtc;
                            if (++scanned >= 300) break;
                        }
                        if (scanned >= 300) break;
                    }
                }
            }
            catch { }
        }

        var age = DateTime.UtcNow - newestWriteUtc;
        var status = newestWriteUtc > DateTime.MinValue && age < TimeSpan.FromMinutes(2) ? "busy" : "idle";
        var since = newestWriteUtc > DateTime.MinValue
            ? new DateTimeOffset(newestWriteUtc).ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        yield return new AgentSession(
            Id: "openclaw:main",
            Tool: "openclaw",
            Name: "OpenClaw",
            Project: "openclaw",
            Status: status,
            WaitingFor: null,
            Since: since,
            StartedAt: since,
            Pid: clawPid);
    }

    public AgentSessionDetail? GetDetail(string id, string? homeDir = null)
    {
        var s = _sessions.FirstOrDefault(x => x.Id == id);
        if (s is null) return null;
        var (prompt, response) = ReadSessionDetail(s.Tool, s.Id, homeDir);
        return new AgentSessionDetail(s.Id, s.Tool, s.Name, s.Project, s.Status,
            s.WaitingFor, s.Since, s.StartedAt, prompt, response, s.Pid);
    }

    internal static (string? prompt, string? response) ReadSessionDetail(string tool, string sessionId, string? homeDir = null)
    {
        var home = homeDir ?? Home;
        try
        {
            return tool switch
            {
                "claude" => ReadClaudeChat(home, sessionId.StartsWith("claude:", StringComparison.Ordinal) ? sessionId["claude:".Length..] : sessionId),
                "codex" => ReadCodexChat(home, sessionId.StartsWith("codex:", StringComparison.Ordinal) ? sessionId["codex:".Length..] : sessionId),
                "antigravity" => ReadAntigravityChat(home),
                _ => (null, null),
            };
        }
        catch { return (null, null); }
    }

    internal static bool IsSafeSessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var ch in token)
            if (!char.IsLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
                return false;
        return true;
    }

    internal static (string? prompt, string? response) ReadClaudeChat(string home, string sessionId)
    {
        if (!IsSafeSessionToken(sessionId)) return (null, null);
        try
        {
            var projectsDir = Path.Combine(home, ".claude", "projects");
            if (!Directory.Exists(projectsDir)) return (null, null);
            var targetName = sessionId + ".jsonl";
            var match = new DirectoryInfo(projectsDir)
                .EnumerateFiles(targetName, SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return match is not null ? ReadJsonlChat(match.FullName, "claude") : (null, null);
        }
        catch { return (null, null); }
    }

    internal static (string? prompt, string? response) ReadCodexChat(string home, string rolloutName)
    {
        if (!IsSafeSessionToken(rolloutName) || !rolloutName.StartsWith("rollout-", StringComparison.Ordinal))
            return (null, null);
        try
        {
            var root = Path.Combine(home, ".codex", "sessions");
            if (!Directory.Exists(root)) return (null, null);
            var targetName = rolloutName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? rolloutName
                : rolloutName + ".jsonl";
            var match = new DirectoryInfo(root)
                .EnumerateFiles(targetName, SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return match is not null ? ReadJsonlChat(match.FullName, "codex") : (null, null);
        }
        catch { return (null, null); }
    }

    internal static (string? prompt, string? response) ReadAntigravityChat(string home)
    {
        try
        {
            var agyDir = Path.Combine(home, ".gemini", "antigravity");
            foreach (var sub in new[] { "brain", "conversations" })
            {
                var root = Path.Combine(agyDir, sub);
                if (!Directory.Exists(root)) continue;
                var file = new DirectoryInfo(root)
                    .EnumerateDirectories()
                    .Select(d => Path.Combine(d.FullName, ".system_generated", "logs", "transcript.jsonl"))
                    .Where(File.Exists)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (file is not null) return ReadJsonlChat(file.FullName, "antigravity");
            }
            return (null, null);
        }
        catch { return (null, null); }
    }

    private static (string? prompt, string? response) ReadJsonlChat(string path, string tool)
    {
        string? prompt = null;
        string? response = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = Math.Min(fs.Length, 512 * 1024);
            if (fs.Length > len) fs.Seek(-len, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var lines = sr.ReadToEnd().Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (string.IsNullOrEmpty(l)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(l);
                    var root = doc.RootElement;
                    if (tool == "claude")
                    {
                        var type = Str(root, "type");
                        if (prompt is null && type == "last-prompt")
                            prompt = Str(root, "lastPrompt");
                        if (prompt is null && type == "user" && root.TryGetProperty("message", out var userMsg))
                            prompt = ExtractClaudeText(userMsg);
                        if (response is null && type == "assistant" && root.TryGetProperty("message", out var assistantMsg))
                            response = ExtractClaudeText(assistantMsg);
                    }
                    else if (tool == "antigravity")
                    {
                        var type = Str(root, "type");
                        if (response is null && type == "PLANNER_RESPONSE")
                            response = Str(root, "content");
                        if (prompt is null && type == "USER_INPUT")
                            prompt = Str(root, "content");
                    }
                    else if (tool == "codex" && root.TryGetProperty("payload", out var payload))
                    {
                        var rootType = Str(root, "type");
                        var eventType = Str(payload, "type");
                        if (rootType == "event_msg")
                        {
                            if (prompt is null && eventType == "user_message")
                                prompt = Str(payload, "message");
                            if (response is null && eventType == "agent_message")
                                response = Str(payload, "message");
                        }
                        else if (rootType == "response_item" && eventType == "message")
                        {
                            var role = Str(payload, "role");
                            if (prompt is null && role == "user")
                                prompt = ExtractCodexMessageText(payload);
                            if (response is null && role == "assistant")
                                response = ExtractCodexMessageText(payload);
                        }
                    }
                    if (prompt is not null && response is not null) break;
                }
                catch { }
            }
        }
        catch { }
        return (TruncateMessage(prompt), TruncateMessage(response));
    }

    private static string? ExtractClaudeText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                string? last = null;
                foreach (var item in content.EnumerateArray())
                    if (Str(item, "type") == "text" && Str(item, "text") is { Length: > 0 } t)
                        last = t;
                return last;
            }
        }
        return null;
    }

    private static string? ExtractCodexMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        string? last = null;
        foreach (var item in content.EnumerateArray())
        {
            var kind = Str(item, "type");
            if (kind is "input_text" or "output_text" or "text" && Str(item, "text") is { Length: > 0 } t)
                last = t;
        }
        return last;
    }

    private static string? TruncateMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > 1500 ? string.Concat(trimmed.AsSpan(0, 1500), "…") : trimmed;
    }

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}
