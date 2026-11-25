using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Status">busy | waiting | shell | idle</param>
/// <param name="Title">The chat's title: carried only while the user shares session titles — Claude Code's own setting, "Show OpenCode session titles" for OpenCode, or "Show Antigravity chat titles on the phone" for Antigravity.</param>
public sealed record AgentSession(string Id, string Tool, string Name, string Project, string Status,
    string? WaitingFor, long Since, long StartedAt, int? Pid = null,
    string? Model = null, string? Effort = null, long? TotalTokens = null, long? InputTokens = null,
    long? OutputTokens = null, long? CachedInputTokens = null, long? ReasoningTokens = null,
    long? LastInputTokens = null, long? ContextWindow = null, long? TurnStartedAt = null,
    string? Outcome = null, string? LastTool = null, string? Title = null);

/// <summary>
/// Watches running AI coding sessions (local and read-only):
///  - Claude Code: ~/.claude/sessions/{pid}.json (status files written by Claude Code itself)
///  - Codex: the latest events in recent rollout files under ~/.codex/sessions
///  - Grok: recent session metadata under ~/.grok/sessions (or GROK_HOME)
///  - Cursor (opt-in): recent agent transcripts under ~/.cursor/projects/*/agent-transcripts
///  - Antigravity (opt-in): recent user transcripts under ~/.gemini/antigravity/brain and ~/.gemini/antigravity-cli/brain
///  - OpenCode: recent sessions from the local HTTP API of its background service (native and in WSL)
/// </summary>
public sealed partial class SessionMonitor(ILogger<SessionMonitor> log, ConfigStore? config = null) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly ConcurrentDictionary<string, string> _codexCwd = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, long Length, CodexDetails Details)> _codexDetails = new();
    // the rows and the extra Claude Code folder each was read from (session id -> folder), published together so a
    // conversation is read from the folder that produced its row
    private sealed record Published(IReadOnlyList<AgentSession> Sessions, IReadOnlyDictionary<string, string> ExtraDirs);
    private volatile Published _pub = new([], new Dictionary<string, string>());
    private string? _discoveryRoot;
    private long _nextDiscovery;
    private string[] _candidateFiles = [];
    // per projects root: the Windows one and, with WSL watching on, each running distro's
    private readonly ConcurrentDictionary<string, (long Next, string[] Files)> _cursorDiscovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, long Length, string? Role)> _cursorRoles = new();
    private Dictionary<string, string>? _cursorProjectNames;
    private long _nextCursorProjectMap;

    /// <summary>
    /// The sessions, for everything that sends them anywhere (the live stream, /api/work, the rules). This is the one
    /// place they leave this class, so the title settings are applied here: Claude Code's chats carry a title only
    /// while Settings › Claude Code › "Show session titles on the phone" is on, OpenCode's only while
    /// Settings › General › "Show OpenCode session titles" is on, and Antigravity's only while
    /// Settings › General › "Show Antigravity chat titles on the phone" is on. An OpenCode row whose sharing was just turned off
    /// also loses the title from its Name — the reader puts the short session id there when titles are off, and the
    /// next monitor refresh (about two seconds) does the same; until then this keeps the title off the phone.
    /// </summary>
    public IReadOnlyList<AgentSession> Snapshot() => Shown(_pub.Sessions);

    /// <summary>The sessions (as <see cref="Snapshot"/>) and the extra Claude Code config folder <paramref name="sessionId"/>
    /// runs from (null for ~/.claude and WSL), both from one published read.</summary>
    public (IReadOnlyList<AgentSession> Sessions, string? ClaudeDir) ConversationSource(string sessionId)
    {
        var p = _pub;
        return (Shown(p.Sessions), p.ExtraDirs.GetValueOrDefault(sessionId));
    }

    private IReadOnlyList<AgentSession> Shown(IReadOnlyList<AgentSession> all)
    {
        if (all.Any(s => s.Tool == "workbuddy") && config?.Get().WorkBuddy.Sessions != true)
            all = all.Where(s => s.Tool != "workbuddy").ToList();
        if (!all.Any(s => s.Title is not null)) return all;
        var claude = ShareTitles;
        var opencode = config?.Get().OpenCode.ShareSessionTitles ?? false;
        var antigravity = config?.Get().Antigravity.ShareSessionTitles ?? false;
        if (claude && opencode && antigravity) return all;
        return all.Select(s => s.Tool switch
        {
            "claude" when !claude => s with { Title = null },
            "opencode" when !opencode => s with { Title = null, Name = OpenCodeSessions.ShortId(s.Id["opencode:".Length..]) },
            "antigravity" when !antigravity => s with { Title = null },
            _ => s,
        }).ToList();
    }

    private bool ShareTitles => config?.Get().Claude.ShareSessionTitles ?? false;

    /// <summary>For tests: what the monitor has read, without running its loop.</summary>
    internal IReadOnlyList<AgentSession> Sessions { set => _pub = _pub with { Sessions = value }; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var list = new List<AgentSession>();
            try { list.AddRange(ReadClaude(titles: ShareTitles)); } catch (Exception ex) { log.LogDebug("claude sessions: {Msg}", ex.Message); }
            var (extraRows, extraDirs) = ReadExtraClaude(config?.Get().Claude.ExtraCredentialDirs ?? [], taken: list.Select(s => s.Id).ToList());
            list.AddRange(extraRows);
            try { list.AddRange(ReadCodex()); } catch (Exception ex) { log.LogDebug("codex sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadGrok()); } catch (Exception ex) { log.LogDebug("grok sessions: {Msg}", ex.Message); }
            if (config?.Get().WorkBuddy.Sessions == true)
            {
                try { list.AddRange(ReadWorkBuddy()); }
                catch (Exception ex) { log.LogDebug("workbuddy sessions: {Kind}", ex.GetType().Name); }
            }
            // OpenCode sessions are off until Settings › General › "Show OpenCode sessions" is enabled; while off,
            // the service is not read at all — not even the every-2-seconds GET.
            if (config?.Get().OpenCode.Sessions == true)
            {
                try
                {
                    var opencode = config!.Get().OpenCode;
                    list.AddRange(await OpenCodeSessions.ReadAsync(log, ct, wslEnabled: opencode.WatchWsl, titles: opencode.ShareSessionTitles));
                }
                catch (Exception ex) { log.LogDebug("opencode sessions: {Msg}", ex.Message); }
            }
            // Cursor sessions are off until Settings › General › Cursor sessions is enabled.
            if (config?.Get().Cursor.Sessions == true)
            {
                try { list.AddRange(ReadCursor()); } catch (Exception ex) { log.LogDebug("cursor sessions: {Msg}", ex.Message); }
            }
            // Antigravity sessions are off until Settings › General › Antigravity sessions is enabled.
            if (config?.Get().Antigravity.Sessions == true)
            {
                try { list.AddRange(ReadAntigravity(titles: config!.Get().Antigravity.ShareSessionTitles)); }
                catch (Exception ex) { log.LogDebug("antigravity sessions: {Msg}", ex.Message); }
            }
            else
            {
                ClearAntigravityCache();
            }
            // Claude Code and Cursor inside WSL (Windows only; each off until its "Also watch … in WSL" is on)
            var cfg = config?.Get();
            try { list.AddRange(ReadWsl(cfg?.Claude.WatchWsl == true, cfg?.Cursor.Sessions == true && cfg.Cursor.WatchWsl)); }
            catch (Exception ex) { log.LogDebug("wsl sessions: {Msg}", ex.Message); }
            _pub = new(list, extraDirs);
            ClaudeTranscript.Trim(list.Where(s => s.Tool == "claude").Select(s => s.Id["claude:".Length..]), config?.Get().Claude.ShareConversation ?? false);
            await Task.Delay(2000, ct);
        }
    }

    // WSL is read less often than the native folders: a share read is a round trip into the VM, and each round
    // starts with a `wsl.exe --list --running` query.
    private const int WslRefreshMs = 5000;
    private IReadOnlyList<AgentSession> _wslSessions = [];
    private long _nextWsl;
    private (bool Claude, bool Cursor) _wslSources;
    // the distro and home each WSL Claude Code session was read from, for its conversation (session id -> where)
    private volatile IReadOnlyDictionary<string, (string Distro, string Home)> _wslHomes = new Dictionary<string, (string, string)>();

    /// <summary>The distro and home a Claude Code session's files are under when it runs inside WSL (null for a native
    /// one), so its conversation is read from the distro.</summary>
    public (string Distro, string Home)? WslOf(string sessionId) => _wslHomes.TryGetValue(sessionId, out var where) ? where : null;

    /// <summary>The home to read a session's conversation from: null for a native session, and for a WSL session only
    /// while its distro still runs (asked right before the read, so a distro that stopped since is not started by it).</summary>
    /// <param name="usable">False for a WSL session whose distro has stopped: nothing may be read for it.</param>
    /// <summary>Claude Code sessions of the extra config folders (Settings › Claude Code › other accounts). An id in
    /// <paramref name="taken"/> (already read from ~/.claude) is left out, so its row and its conversation come from one folder. Also returns the folder each row was read from (session id -> folder).</summary>
    internal (List<AgentSession> Rows, Dictionary<string, string> Dirs) ReadExtraClaude(IReadOnlyList<ClaudeCredentialDir> extra, Func<int, bool>? isAlive = null, IEnumerable<string>? taken = null)
    {
        var list = new List<AgentSession>();
        var dirs = new Dictionary<string, string>();
        var seen = new HashSet<string>(taken ?? []);
        foreach (var d in extra)
        {
            var dir = d.FullPath();
            try
            {
                foreach (var s in ReadClaude(isAlive: isAlive, titles: ShareTitles, configDir: dir))
                {
                    // one source per id: a folder listed twice, or a session copied from another account, shows once
                    if (!seen.Add(s.Id)) continue;
                    list.Add(s);
                    dirs[s.Id] = dir;
                }
            }
            catch (Exception ex) { log.LogDebug("claude sessions ({Label}): {Msg}", d.Label, ex.Message); }
        }
        return (list, dirs);
    }

    public string? ConversationHome(string sessionId, out bool usable, Func<string, bool>? isRunning = null)
    {
        usable = true;
        if (WslOf(sessionId) is not { } where) return null;
        usable = (isRunning ?? WslHomes.IsRunning)(where.Distro);
        return usable ? where.Home : null;
    }

    /// <summary>
    /// Claude Code sessions and Cursor chats of the running WSL distros' default users, refreshed every
    /// <see cref="WslRefreshMs"/>. The running-distro query comes right before the reads under its shares, so a
    /// distro that has stopped is not on the list and is not started by a read.
    /// </summary>
    /// <param name="running">The running distros; tests give their own, production asks wsl.exe.</param>
    internal IReadOnlyList<AgentSession> ReadWsl(bool claude, bool cursor, Func<IReadOnlyList<WslHomes.Entry>>? running = null)
    {
        if (!claude && !cursor) { _wslSessions = []; _wslHomes = new Dictionary<string, (string, string)>(); _nextWsl = 0; _wslSources = default; return []; }
        // a source turned on or off: the rows read with the other setting must not be shown until the next refresh
        if ((claude, cursor) != _wslSources) { _nextWsl = 0; _wslSources = (claude, cursor); }
        if (Environment.TickCount64 < _nextWsl) return _wslSessions;
        _nextWsl = Environment.TickCount64 + WslRefreshMs;
        var list = new List<AgentSession>();
        var homes = new Dictionary<string, (string Distro, string Home)>();
        foreach (var wsl in (running ?? (() => WslHomes.Running()))())
        {
            if (claude)
            {
                try
                {
                    foreach (var s in ReadClaude(wsl.Home, titles: ShareTitles, linuxAlive: (pid, start) => WslHomes.Alive(wsl.Root, pid, start)))
                    {
                        list.Add(s);
                        homes[s.Id] = (wsl.Distro, wsl.Home);
                    }
                }
                catch (Exception ex) { log.LogDebug("claude sessions in {Distro}: {Msg}", wsl.Distro, ex.Message); }
            }
            if (cursor)
            {
                try { list.AddRange(ReadCursor(Path.Combine(wsl.Home, ".cursor", "projects"))); }
                catch (Exception ex) { log.LogDebug("cursor sessions in {Distro}: {Msg}", wsl.Distro, ex.Message); }
            }
        }
        _wslHomes = homes;
        return _wslSessions = list;
    }

    /// <summary>Reads WorkBuddy's live status files. No transcript or database is opened.</summary>
    internal static IEnumerable<AgentSession> ReadWorkBuddy(string? homeDir = null, Func<int, bool>? isAlive = null, long? nowMs = null)
    {
        var home = homeDir ?? Home;
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alive = isAlive ?? Alive;
        var folder = Path.Combine(home, ".workbuddy", "sessions");
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            AgentSession? session = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var id) || !id.TryGetInt32(out var pid) || pid <= 0 || !alive(pid))
                    continue;
                if (Str(root, "kind") == "prewarm") continue;
                var sessionId = Str(root, "sessionId");
                var cwd = Str(root, "cwd") ?? "";
                var project = WorkBuddyNames.For(cwd, home) ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var started = Long(root, "startedAt") ?? 0;
                var heartbeat = Long(root, "lastHeartbeat") ?? Long(root, "updatedAt") ?? started;
                var recent = heartbeat > 0 && now >= heartbeat && now - heartbeat < 90_000;
                session = new AgentSession(
                    Id: "workbuddy:" + (string.IsNullOrWhiteSpace(sessionId) ? pid.ToString() : sessionId),
                    Tool: "workbuddy", Name: project, Project: project, Status: recent ? "busy" : "idle",
                    WaitingFor: null, Since: Long(root, "updatedAt") ?? heartbeat, StartedAt: started, Pid: pid,
                    Model: Str(root, "model"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { /* The status file can be replaced between poll ticks. */ }
            if (session is not null) yield return session;
        }
    }

    // CodexBar runs `claude` in <Application Support or temp>/CodexBar/ClaudeProbe to read usage.
    // Match only that exact directory so real projects that merely contain "CodexBar" stay visible.
    internal static bool IsCodexBarProbe(string cwd) =>
        cwd.Replace('\\', '/').TrimEnd('/').EndsWith("/CodexBar/ClaudeProbe", StringComparison.OrdinalIgnoreCase);

    // claude-mem starts short background Claude Code sessions in ~/.claude-mem/observer-sessions/<n> to write its memory notes.
    // They are not the user's work, and each one that finished showed a "finished" toast on the phone (#275).
    internal static bool IsClaudeMemObserver(string cwd) =>
        (cwd.Replace('\\', '/').TrimEnd('/') + "/").Contains("/.claude-mem/observer-sessions/", StringComparison.OrdinalIgnoreCase);

    /// <param name="linuxAlive">For sessions running inside WSL (<paramref name="homeDir"/> is then the distro user's
    /// home on the \\wsl.localhost share): whether the Linux process with this PID and start time (the status file's
    /// procStart, in clock ticks) still runs. Its PIDs mean nothing to Windows, so they are neither checked against
    /// Windows processes nor handed on (<see cref="AgentSession.Pid"/> stays null).</param>
    /// <param name="configDir">A Claude Code config folder (CLAUDE_CONFIG_DIR) to read instead of &lt;home&gt;/.claude.</param>
    internal static IEnumerable<AgentSession> ReadClaude(string? homeDir = null, Func<int, bool>? isAlive = null, bool titles = false,
        Func<int, long?, bool>? linuxAlive = null, string? configDir = null)
    {
        var claudeDir = configDir ?? Path.Combine(homeDir ?? Home, ".claude");
        var aliveCheck = isAlive ?? Alive;
        var dir = Path.Combine(claudeDir, "sessions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            AgentSession? s = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                if (pid == 0) continue;
                var hasStart = long.TryParse(Str(r, "procStart"), out var procStart);
                if (linuxAlive is not null)
                {
                    if (!linuxAlive(pid, hasStart ? procStart : null)) continue;
                }
                else
                {
                    if (!aliveCheck(pid)) continue;
                    // a session that was killed leaves its file behind; Windows may give its PID to another program later
                    if (OperatingSystem.IsWindows() && hasStart && !SameStart(pid, procStart)) continue;
                }
                var cwd = Str(r, "cwd") ?? "";
                var name = Str(r, "name") ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));

                if (IsCodexBarProbe(cwd) || IsClaudeMemObserver(cwd)) continue;

                s = new AgentSession(
                    Id: "claude:" + (Str(r, "sessionId") ?? pid.ToString()),
                    Tool: "claude",
                    Name: name,
                    Project: project,
                    Status: Str(r, "status") ?? "idle",
                    WaitingFor: Str(r, "waitingFor"),
                    Since: Long(r, "statusUpdatedAt") ?? Long(r, "updatedAt") ?? 0,
                    StartedAt: Long(r, "startedAt") ?? 0,
                    Pid: linuxAlive is null ? pid : null,
                    Title: titles ? ClaudeTitle(claudeDir, r, cwd, name, project) : null);
            }
            catch { /* file was being written: retry on the next pass */ }
            if (s is not null) yield return s;
        }
    }

    private sealed record TitleCacheEntry(long Length, DateTime Written, string? Custom, string? Ai);
    private static readonly ConcurrentDictionary<string, TitleCacheEntry> TitleCache = new();
    // the title is written again and again as the chat goes on, so the end of the file is enough
    private const int TitleTailBytes = 512 * 1024;

    /// <summary>
    /// The title Claude Code shows for a chat (a name you gave it, else the one it wrote itself), from the
    /// transcript at ~/.claude/projects/&lt;folder&gt;/&lt;sessionId&gt;.jsonl. Only the two title records are read: no
    /// prompts, replies or tool arguments. Null while the chat has no title yet.
    /// </summary>
    internal static string? ReadClaudeTitle(string claudeDir, string cwd, string sessionId)
    {
        try
        {
            // Claude Code names the folder after the working directory with every other character turned into "-"
            var folder = System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            var path = Path.Combine(claudeDir, "projects", folder, sessionId + ".jsonl");
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            TitleCache.TryGetValue(path, out var cached);
            if (cached is not null && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
                return cached.Custom ?? cached.Ai;

            // A title that has scrolled out of the tail we read is carried over, but only while the file only grew; a
            // shorter or replaced file starts again, and a file read whole has nothing to carry over.
            var carry = cached is not null && info.Length >= cached.Length && info.Length > TitleTailBytes;
            string? custom = carry ? cached!.Custom : null, ai = carry ? cached!.Ai : null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var start = Math.Max(0, length - TitleTailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[length - start];
            var read = 0;
            while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
            for (var i = start > 0 ? 1 : 0; i < lines.Length; i++) // after a seek the first line is cut off
            {
                var line = lines[i];
                var isCustom = line.Contains("\"custom-title\"", StringComparison.Ordinal);
                if (!isCustom && !line.Contains("\"ai-title\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var rec = doc.RootElement;
                    var type = Str(rec, "type");
                    if (type == "custom-title") custom = CleanTitle(Str(rec, "customTitle")) ?? custom;
                    else if (type == "ai-title") ai = CleanTitle(Str(rec, "aiTitle")) ?? ai;
                }
                catch (JsonException) { /* a line still being written */ }
            }
            TitleCache[path] = new TitleCacheEntry(length, info.LastWriteTimeUtc, custom, ai);
            return custom ?? ai;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal const int MaxTitleChars = 200;

    /// <summary>A title as the phone gets it, whichever file it came from: trimmed and no longer than <see cref="MaxTitleChars"/>.</summary>
    internal static string? CleanTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : title.Trim() is { Length: > MaxTitleChars } t ? t[..MaxTitleChars] : title.Trim();

    /// <summary>
    /// The chat's title: what the user named it, else the title Claude Code wrote (a derived name in the status file is
    /// only a placeholder such as "develop-int-61"). Null when there is none yet, or it is just the folder name.
    /// </summary>
    private static string? ClaudeTitle(string claudeDir, JsonElement status, string cwd, string name, string project)
    {
        var fromName = Str(status, "nameSource") != "derived" && Str(status, "name") is not null;
        var title = CleanTitle(fromName ? name : Str(status, "sessionId") is { Length: > 0 } id ? ReadClaudeTitle(claudeDir, cwd, id) : null);
        return title is null || title == project ? null : title;
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
                var hasCached = _codexDetails.TryGetValue(f.FullName, out var cached);
                var unchanged = hasCached && cached.LastWrite == f.LastWriteTimeUtc && cached.Length == f.Length;
                var appended = hasCached && f.Length > cached.Length;
                if (!_codexCwd.TryGetValue(f.FullName, out var cwd) || cwd.Length == 0 || (!unchanged && !appended))
                {
                    cwd = ReadCodexCwd(f.FullName);
                    if (cwd.Length > 0) _codexCwd[f.FullName] = cwd;
                    else _codexCwd.TryRemove(f.FullName, out _);
                }
                var details = unchanged
                    ? cached.Details : ReadCodexDetails(f.FullName,
                        appended ? cached.Details : null,
                        appended ? cached.Details.ReadOffset : 0);
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

    // the first record carries the base instructions and tool definitions, so it can be far longer than one read
    private const int MaxCodexMetaBytes = 4 * 1024 * 1024;

    /// <summary>The project folder from a rollout's first record; "" while that record is still being written.</summary>
    private static string ReadCodexCwd(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[64 * 1024];
        int len = 0, nl = -1;
        while (nl < 0)
        {
            if (len == buf.Length)
            {
                if (buf.Length >= MaxCodexMetaBytes) return "";
                Array.Resize(ref buf, Math.Min(buf.Length * 2, MaxCodexMetaBytes));
            }
            var n = fs.Read(buf, len, buf.Length - len);
            if (n <= 0) return ""; // no complete record yet: the next poll reads it again
            nl = Array.IndexOf(buf, (byte)'\n', len, n);
            len += n;
        }
        var record = buf.AsMemory(0, nl);
        // JsonDocument.Parse on bytes refuses a UTF-8 BOM (a StreamReader used to drop it)
        if (record.Span.StartsWith("﻿"u8)) record = record[3..];
        if (record.IsEmpty) return "";
        try
        {
            using var doc = JsonDocument.Parse(record);
            return doc.RootElement.TryGetProperty("payload", out var p) ? Str(p, "cwd") ?? "" : "";
        }
        catch (JsonException) { return ""; }
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

    /// <summary>
    /// Recent Cursor agent chats from ~/.cursor/projects/*/agent-transcripts.
    /// File times and the last JSONL record's role are used for status; message text is not returned.
    /// Project names come from Cursor workspaceStorage folder URIs (basename only) when available.
    /// </summary>
    internal IEnumerable<AgentSession> ReadCursor(string? projectsRoot = null, string? workspaceStorageRoot = null)
    {
        var root = projectsRoot ?? Path.Combine(Home, ".cursor", "projects");
        if (!Directory.Exists(root)) yield break;
        var cutoff = DateTime.UtcNow.AddHours(-3);
        if (!_cursorDiscovery.TryGetValue(root, out var discovered) || Environment.TickCount64 >= discovered.Next)
        {
            var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc > cutoff
                    && f.Directory?.Parent?.Name.Equals("agent-transcripts", StringComparison.OrdinalIgnoreCase) == true
                    && !f.FullName.Replace('\\', '/').Contains("/.agent-data-cleanup-", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FullName).ToArray();
            discovered = (Environment.TickCount64 + 30_000, files);
            _cursorDiscovery[root] = discovered;
        }

        var names = CursorProjectNames(workspaceStorageRoot);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in discovered.Files)
        {
            AgentSession? s = null;
            try
            {
                var f = new FileInfo(path);
                if (!f.Exists || f.LastWriteTimeUtc <= cutoff) continue;
                active.Add(f.FullName);
                var id = f.Directory?.Name ?? Path.GetFileNameWithoutExtension(f.Name);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var projectSlug = f.Directory?.Parent?.Parent?.Name ?? "";
                var project = names.TryGetValue(projectSlug, out var mapped) && !string.IsNullOrEmpty(mapped)
                    ? mapped
                    : CursorProjectFallback(projectSlug);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var lastRole = CursorLastRoleCached(f);
                // A recent assistant message means the turn finished (idle). A recent user message means the
                // agent is still working on a reply (busy). "waiting" is reserved for user attention (Claude).
                var status = age < TimeSpan.FromMinutes(3)
                    ? lastRole is "assistant" ? "idle" : "busy"
                    : "idle";
                var shortId = id.Length >= 4 ? id[..4] : id;
                s = new AgentSession(
                    Id: "cursor:" + id,
                    Tool: "cursor",
                    Name: $"{project.ToLowerInvariant()}-{shortId}",
                    Project: project,
                    Status: status,
                    WaitingFor: null,
                    Since: new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    StartedAt: new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds());
            }
            catch { }
            if (s is not null) yield return s;
        }
        // only this root's entries: the other roots' transcripts are read (and kept) by their own calls
        var prefix = root.TrimEnd('/', '\\');
        foreach (var old in _cursorRoles.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !active.Contains(p)))
            _cursorRoles.TryRemove(old, out _);
    }

    /// <summary>Reuse the last-role parse while the transcript file is unchanged (same write time and length).</summary>
    private string? CursorLastRoleCached(FileInfo f)
    {
        if (_cursorRoles.TryGetValue(f.FullName, out var cached)
            && cached.LastWrite == f.LastWriteTimeUtc && cached.Length == f.Length)
            return cached.Role;
        var role = ReadCursorLastRole(f.FullName);
        _cursorRoles[f.FullName] = (f.LastWriteTimeUtc, f.Length, role);
        return role;
    }

    /// <summary>True when the role cache holds this path at the given write time and length (for tests).</summary>
    internal bool HasCachedCursorRole(string path, DateTime lastWriteUtc, long length) =>
        _cursorRoles.TryGetValue(path, out var cached) && cached.LastWrite == lastWriteUtc && cached.Length == length;

    /// <summary>Maps Cursor project-directory slugs to workspace folder names via workspaceStorage.</summary>
    private Dictionary<string, string> CursorProjectNames(string? workspaceStorageRoot)
    {
        if (_cursorProjectNames is not null && Environment.TickCount64 < _nextCursorProjectMap)
            return _cursorProjectNames;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = workspaceStorageRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "workspaceStorage");
            // On macOS Application Support is SpecialFolder.UserProfile/Library/Application Support, not ApplicationData.
            if (!Directory.Exists(root) && OperatingSystem.IsMacOS())
                root = Path.Combine(Home, "Library", "Application Support", "Cursor", "User", "workspaceStorage");
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var file = Path.Combine(dir, "workspace.json");
                    if (!File.Exists(file)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (!doc.RootElement.TryGetProperty("folder", out var folderEl)
                            || folderEl.ValueKind != JsonValueKind.String
                            || folderEl.GetString() is not { Length: > 0 } folder)
                            continue;
                        // a folder opened over Remote - WSL (vscode-remote://wsl%2B<distro>/home/...) keeps its
                        // transcripts inside the distro, under the slug of its Linux path. .NET refuses that
                        // authority as a host, so the path is cut out by hand.
                        string local;
                        if (RemotePath(folder) is { } remote) local = remote;
                        else if (!Uri.TryCreate(folder, UriKind.Absolute, out var uri)) continue;
                        else local = uri.IsFile ? uri.LocalPath : folder;
                        if (string.IsNullOrEmpty(local)) continue;
                        var trimmed = local.Replace('\\', '/').TrimEnd('/');
                        var slug = CursorProjectSlug(trimmed);
                        if (slug.Length == 0) continue;
                        map[slug] = Path.GetFileName(trimmed.TrimEnd('/'));
                    }
                    catch { }
                }
            }
        }
        catch { }
        _cursorProjectNames = map;
        _nextCursorProjectMap = Environment.TickCount64 + 60_000;
        return map;
    }

    /// <summary>The path of a vscode-remote:// folder URI (unescaped), or null for any other kind of folder.</summary>
    internal static string? RemotePath(string folder)
    {
        const string scheme = "vscode-remote://";
        if (!folder.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var slash = folder.IndexOf('/', scheme.Length);
        return slash < 0 ? null : Uri.UnescapeDataString(folder[slash..]);
    }

    /// <summary>Slug for a workspace folder path; matches Cursor's projects directory name (no drive colon).</summary>
    internal static string CursorProjectSlug(string localPath)
    {
        var trimmed = localPath.Replace('\\', '/').TrimEnd('/');
        // Windows: "C:/Users/…" → "C-Users-…" so the key matches the projects folder name (':' is illegal in dir names).
        return trimmed.TrimStart('/').Replace('/', '-').Replace(":", "", StringComparison.Ordinal);
    }

    internal static string CursorProjectFallback(string projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug)) return "Cursor";
        if (projectSlug.All(char.IsDigit)) return "Cursor";
        var parts = projectSlug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "Cursor" : parts[^1];
    }

    // Grow the tail until the final JSONL record fits; stay well below loading whole transcripts.
    private const int CursorRoleInitialBytes = 8192;
    private const int CursorRoleMaxBytes = 1024 * 1024;

    /// <summary>
    /// Parses the last JSONL record in a bounded tail window and returns its <c>role</c> only.
    /// The record may contain message text in memory for parsing; that text is not returned, stored, or sent.
    /// </summary>
    internal static string? ReadCursorLastRole(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        if (len == 0) return null;

        var take = (int)Math.Min(len, CursorRoleInitialBytes);
        while (true)
        {
            fs.Seek(len - take, SeekOrigin.Begin);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, buf.Length);
            if (read <= 0) return null;

            var start = 0;
            var coversFileStart = take >= len;
            if (!coversFileStart)
            {
                // Window begins mid-record: drop the incomplete first line (or grow if there is no newline).
                var firstNl = Array.IndexOf(buf, (byte)'\n', 0, read);
                if (firstNl < 0)
                {
                    if (take >= len || take >= CursorRoleMaxBytes) return null;
                    take = (int)Math.Min(len, Math.Min(CursorRoleMaxBytes, (long)take * 2));
                    continue;
                }
                start = firstNl + 1;
            }

            string? lastRole = null;
            for (var i = start; i < read;)
            {
                var nl = Array.IndexOf(buf, (byte)'\n', i, read - i);
                var end = nl < 0 ? read : nl;
                if (end > i)
                {
                    try
                    {
                        var line = Encoding.UTF8.GetString(buf, i, end - i).Trim();
                        if (line.Length > 0)
                        {
                            using var doc = JsonDocument.Parse(line);
                            lastRole = Str(doc.RootElement, "role") ?? lastRole;
                        }
                    }
                    catch (JsonException) { }
                }
                if (nl < 0) break;
                i = nl + 1;
            }

            if (lastRole is not null) return lastRole;
            if (take >= len || take >= CursorRoleMaxBytes) return null;
            take = (int)Math.Min(len, Math.Min(CursorRoleMaxBytes, (long)take * 2));
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

/// <summary>The "work" data the phone gets, on the live stream and on the polling path alike: sessions and GitHub.</summary>
public static class WorkFeed
{
    public static object Build(SessionMonitor sessions, GitHubMonitor gh) => new { sessions = sessions.Snapshot(), github = gh.Snapshot() };
}
