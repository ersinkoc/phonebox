using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class SessionDetailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-sessions-" + Guid.NewGuid());
    private readonly SessionMonitor _monitor = new(NullLogger<SessionMonitor>.Instance);
    private string Rollout(int daysAgo = 0)
    {
        var day = DateTime.Now.AddDays(-daysAgo);
        var dir = Path.Combine(_root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rollout-2026-10-01-12345678-abcd.jsonl");
        File.WriteAllText(path, Line("session_meta", new { cwd = "/work/project" }));
        return path;
    }
    private static string Line(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = DateTimeOffset.UtcNow, payload }) + "\n";

    [Fact]
    public void ResumedSessionIsFoundInAnOldDirectory()
    {
        var file = Rollout(40);
        File.AppendAllText(file, Line("turn_context", new { model = "test-model", effort = "high" }) + Line("event_msg", new { type = "task_started" }));
        var session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal("test-model", session.Model);
        Assert.Equal("project", session.Project);
        Assert.Equal("busy", session.Status);
    }

    [Fact]
    public void FollowsAppendsAndCompletionWithoutSendingPromptsOrToolArguments()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("turn_context", new { model = "test-model", effort = "high" })
            + Line("event_msg", new { type = "task_started" })
            + Line("event_msg", new { type = "user_message", message = "PRIVATE PROMPT" })
            + Line("response_item", new { type = "function_call", name = "read", arguments = "PRIVATE ARGUMENT" })
            + Line("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = 123, input_tokens = 100, output_tokens = 23 }, model_context_window = 1000 } }));
        var session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal(123, session.TotalTokens);
        Assert.Equal("read", session.LastTool);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(session));
        File.AppendAllText(file, Line("event_msg", new { type = "task_complete" }));
        session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal("completed", session.Outcome);
        Assert.Equal("idle", session.Status);
        Assert.Equal(123, session.TotalTokens);
    }

    [Fact]
    public void MalformedTokenCountersDoNotHideTheSession()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("event_msg", new { type = "task_started" })
            + Line("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = "unknown" } } }));
        Assert.Single(_monitor.ReadCodex(_root));
    }

    [Fact]
    public void CompletesLongPartialRecordsOnTheNextRead()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("event_msg", new { type = "task_started" }));
        Assert.Single(_monitor.ReadCodex(_root));
        var line = Line("response_item", new { type = "function_call", name = "read", arguments = new string('x', 90000) });
        File.AppendAllText(file, line[..85000]);
        Assert.Null(Assert.Single(_monitor.ReadCodex(_root)).LastTool);
        File.AppendAllText(file, line[85000..]);
        Assert.Equal("read", Assert.Single(_monitor.ReadCodex(_root)).LastTool);
        File.WriteAllText(file, Line("session_meta", new { cwd = "/work/project" }) + Line("event_msg", new { type = "turn_aborted" }));
        Assert.Equal("aborted", Assert.Single(_monitor.ReadCodex(_root)).Outcome);
    }

    [Fact]
    public void Claude_NormalSession_IsReported()
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        var json = JsonSerializer.Serialize(new
        {
            pid = 1122,
            sessionId = "sess-1",
            cwd = "/Users/test/workspace/my-app",
            status = "waiting",
            waitingFor = "permission prompt"
        });
        File.WriteAllText(Path.Combine(claudeDir, "1122.json"), json);

        var s = Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 1122));
        Assert.Equal("claude", s.Tool);
        Assert.Equal("my-app", s.Project);
        Assert.Equal("waiting", s.Status);
        Assert.Equal("permission prompt", s.WaitingFor);
        Assert.Equal(1122, s.Pid);
    }

    [Fact]
    public void ExtraClaudeConfigFoldersShowTheirSessionsAndRememberWhereTheyRun()
    {
        var work = Path.Combine(_root, "claude-work");
        Directory.CreateDirectory(Path.Combine(work, "sessions"));
        File.WriteAllText(Path.Combine(work, "sessions", "4455.json"),
            JsonSerializer.Serialize(new { pid = 4455, sessionId = "abcdef12-work", cwd = "/work/api", status = "busy" }));
        var transcript = Path.Combine(work, "projects", "-work-api", "abcdef12-work.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "");
        ClaudeCredentialDir[] dirs = [new() { Label = "work", Dir = work }, new() { Label = "again", Dir = work + "/" }];

        var (rows, folders) = _monitor.ReadExtraClaude(dirs, pid => pid == 4455);
        var s = Assert.Single(rows);
        Assert.Equal("claude:abcdef12-work", s.Id);
        Assert.Equal("api", s.Project);
        Assert.Equal(work, folders[s.Id]);
        Assert.Equal(transcript, ClaudeTranscript.FindFile("abcdef12-work", claudeDir: folders[s.Id]));

        // the same id already read from ~/.claude keeps that one source: no extra row, no extra folder for its conversation
        (rows, folders) = _monitor.ReadExtraClaude(dirs, pid => pid == 4455, taken: ["claude:abcdef12-work"]);
        Assert.Empty(rows);
        Assert.Empty(folders);

        (rows, folders) = _monitor.ReadExtraClaude([], pid => true);
        Assert.Empty(rows);
        Assert.Empty(folders);
    }

    [Fact]
    public void Claude_ClaudeProbeAndCodexBar_AreIgnored()
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        var probeJson = JsonSerializer.Serialize(new
        {
            pid = 2233,
            sessionId = "sess-probe",
            cwd = "/Users/test/Library/Application Support/CodexBar/ClaudeProbe",
            status = "waiting",
            waitingFor = "dialog open"
        });
        File.WriteAllText(Path.Combine(claudeDir, "2233.json"), probeJson);

        Assert.Empty(SessionMonitor.ReadClaude(_root, pid => pid == 2233));
    }

    [Theory]
    [InlineData("/Users/test/Library/Application Support/CodexBar/ClaudeProbe", true)]
    [InlineData("/var/folders/xy/T/CodexBar/ClaudeProbe/", true)]
    [InlineData(@"C:\Users\test\AppData\Local\Temp\CodexBar\ClaudeProbe", true)]
    [InlineData("/Users/test/Projects/CodexBar", false)]
    [InlineData("/Users/test/Projects/CodexBar/Sources", false)]
    [InlineData("/Users/test/Projects/ClaudeProbeKit", false)]
    [InlineData("/Users/test/CodexBar/ClaudeProbe/subdir", false)]
    // claude-mem's background sessions write memory notes from ~/.claude-mem/observer-sessions/<n> (#275)
    [InlineData("/Users/test/.claude-mem/observer-sessions/108", true)]
    [InlineData(@"C:\Users\test\.claude-mem\observer-sessions\110\", true)]
    [InlineData("/home/test/.claude-mem/observer-sessions", true)]
    [InlineData("/home/test/.claude-mem", false)]
    [InlineData("/home/test/Projects/claude-mem/observer-sessions", false)]
    [InlineData("/home/test/Projects/claude-mem", false)]
    public void Claude_OnlyTheBackgroundToolDirectoriesAreIgnored(string cwd, bool ignored)
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "3344.json"),
            JsonSerializer.Serialize(new { pid = 3344, sessionId = "sess-x", cwd, status = "busy" }));

        var sessions = SessionMonitor.ReadClaude(_root, pid => pid == 3344).ToList();
        Assert.Equal(ignored ? 0 : 1, sessions.Count);
    }

    [Fact]
    public void Codex_EmptyOrPartialInitialRolloutRecoversProjectOnceSessionMetaFlushes()
    {
        var day = DateTime.UtcNow;
        var dir = Path.Combine(_root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "rollout-2026-11-17-0000-12345678.jsonl");
        File.WriteAllText(file, "");
        _ = _monitor.ReadCodex(_root).ToList();

        File.AppendAllText(file, Line("session_meta", new { cwd = "/work/recovered-app" }) + Line("event_msg", new { type = "task_started" }));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
        var s = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal("recovered-app", s.Project);
        Assert.Equal("recovered-app-1234", s.Name);
    }

    [Fact]
    public void Cursor_RecentAssistantTurn_IsIdleWithoutExposingMessageText()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var slug = "Users-test-Projects-phonebox";
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var dir = Path.Combine(_root, "projects", slug, "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file,
            """{"role":"user","message":"PRIVATE PROMPT"}""" + "\n" +
            """{"role":"assistant","message":"PRIVATE REPLY"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));

        var ws = Path.Combine(_root, "workspaceStorage", "abc");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "workspace.json"),
            """{"folder":"file:///Users/test/Projects/phonebox"}""");

        var s = Assert.Single(monitor.ReadCursor(Path.Combine(_root, "projects"), Path.Combine(_root, "workspaceStorage")));
        Assert.Equal("cursor", s.Tool);
        Assert.Equal("phonebox", s.Project);
        Assert.Equal("idle", s.Status); // last role assistant → turn done
        Assert.Null(s.WaitingFor);
        Assert.Equal("cursor:" + id, s.Id);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(s));
    }

    [Fact]
    public void Cursor_LastUserMessageWithinBusyWindow_IsBusy()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "11111111-2222-3333-4444-555555555555";
        var dir = Path.Combine(_root, "projects", "my-app", "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, """{"role":"user","message":"hi"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-30));

        var s = Assert.Single(monitor.ReadCursor(Path.Combine(_root, "projects"), Path.Combine(_root, "missing-ws")));
        Assert.Equal("busy", s.Status); // last role user → agent working on a reply
        Assert.Null(s.WaitingFor);
        Assert.Equal("app", s.Project); // fallback from slug segment
    }

    [Fact]
    public void Cursor_FinalUserRecordLargerThanTailWindow_IsBusy()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "22222222-3333-4444-5555-666666666666";
        var dir = Path.Combine(_root, "projects", "big-app", "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        // A short assistant line, then a user record larger than the 8 KiB initial tail window.
        var huge = new string('x', 12_000);
        File.WriteAllText(file,
            """{"role":"assistant","message":"earlier"}""" + "\n" +
            """{"role":"user","message":""" + "\"" + huge + "\"}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-20));

        Assert.Equal("user", SessionMonitor.ReadCursorLastRole(file));
        var s = Assert.Single(monitor.ReadCursor(Path.Combine(_root, "projects")));
        Assert.Equal("busy", s.Status);
        Assert.Null(s.WaitingFor);
        Assert.DoesNotContain(huge[..32], JsonSerializer.Serialize(s));
    }

    [Fact]
    public void Cursor_StaleTranscript_IsIdle()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "99999999-aaaa-bbbb-cccc-dddddddddddd";
        var dir = Path.Combine(_root, "projects", "demo", "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, """{"role":"assistant","message":"done"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-20));

        var s = Assert.Single(monitor.ReadCursor(Path.Combine(_root, "projects")));
        Assert.Equal("idle", s.Status);
        Assert.Null(s.WaitingFor);
    }

    [Theory]
    [InlineData("Users-test-Projects-phonebox", "phonebox")]
    [InlineData("1794465413728", "Cursor")]
    [InlineData("", "Cursor")]
    public void Cursor_ProjectFallback_UsesLastSlugSegment(string slug, string expected)
        => Assert.Equal(expected, SessionMonitor.CursorProjectFallback(slug));

    [Theory]
    [InlineData("/Users/test/Projects/phonebox", "Users-test-Projects-phonebox")]
    [InlineData("C:/Users/test/Projects/phonebox-agent", "C-Users-test-Projects-phonebox-agent")]
    [InlineData(@"C:\Users\test\Projects\phonebox-agent", "C-Users-test-Projects-phonebox-agent")]
    public void Cursor_ProjectSlug_StripsDriveColon(string path, string expected)
        => Assert.Equal(expected, SessionMonitor.CursorProjectSlug(path));

    [Fact]
    public void Cursor_UnchangedTranscript_ReusesCachedLastRole()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "33333333-4444-5555-6666-777777777777";
        var dir = Path.Combine(_root, "projects", "cache-app", "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, """{"role":"user","message":"go"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-15));

        var projects = Path.Combine(_root, "projects");
        Assert.Equal("busy", Assert.Single(monitor.ReadCursor(projects)).Status);
        var fi = new FileInfo(file);
        Assert.True(monitor.HasCachedCursorRole(file, fi.LastWriteTimeUtc, fi.Length));

        // Unchanged file: cache still matches; status stays busy without needing a re-read of the body.
        Assert.Equal("busy", Assert.Single(monitor.ReadCursor(projects)).Status);
        Assert.True(monitor.HasCachedCursorRole(file, fi.LastWriteTimeUtc, fi.Length));

        File.AppendAllText(file, """{"role":"assistant","message":"done"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal("idle", Assert.Single(monitor.ReadCursor(projects)).Status);
        fi.Refresh();
        Assert.True(monitor.HasCachedCursorRole(file, fi.LastWriteTimeUtc, fi.Length));
    }

    private string WriteAntigravityTranscript(string id, string lines, bool isCli = false, DateTime? writeUtc = null)
    {
        var root = Path.Combine(_root, ".gemini", isCli ? "antigravity-cli" : "antigravity");
        var logs = Path.Combine(root, "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(logs);
        var file = Path.Combine(logs, "transcript.jsonl");
        File.WriteAllText(file, lines);
        if (writeUtc is { } wt) File.SetLastWriteTimeUtc(file, wt);
        return file;
    }

    [Fact]
    public void Antigravity_ConfigDefaultsOff_NormalizedAndHiddenFromToPublic()
    {
        var cfg = new AgentConfig();
        Assert.False(cfg.Antigravity.Sessions);
        Assert.False(cfg.Antigravity.ShareSessionTitles);

        var path = Path.Combine(_root, "agy-cfg.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """{"antigravity":null}""");
        var store = new ConfigStore(path);
        var loaded = store.Get();
        Assert.NotNull(loaded.Antigravity);
        Assert.False(loaded.Antigravity.Sessions);
        Assert.False(loaded.Antigravity.ShareSessionTitles);

        var pubJson = JsonSerializer.Serialize(loaded.ToPublic(), ConfigStore.WriteOpts);
        using var pubDoc = JsonDocument.Parse(pubJson);
        Assert.False(pubDoc.RootElement.TryGetProperty("antigravity", out _));
        Assert.False(pubDoc.RootElement.TryGetProperty("Antigravity", out _));
    }

    [Fact]
    public void Antigravity_CompletedTurn_ReportsMetadataWithoutLeakingPromptsOrArgs()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "aaaaaaaa-1111-2222-3333-444444444444";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var t2 = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("O");
        WriteAntigravityTranscript(id,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{t0}}}","content":"PRIVATE PROMPT"}""" + "\n" +
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{t1}}}","model":"gemini-4-carbon-medium","input_tokens":1000,"cache_read_tokens":500,"output_tokens":200,"thinking":"PRIVATE THINKING","tool_calls":[{"name":"run_command","args":{"CommandLine":"SECRET_CMD"}}]}""" + "\n" +
            $$$"""{"step_index":2,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{t2}}}","model":"gemini-4-carbon-medium","input_tokens":250,"cache_read_tokens":1000,"output_tokens":150,"content":"PRIVATE REPLY"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-20));

        var s = Assert.Single(monitor.ReadAntigravity(_root));
        Assert.Equal("antigravity:" + id, s.Id);
        Assert.Equal("antigravity", s.Tool);
        Assert.Equal("Antigravity", s.Project);
        Assert.Equal("antigravity-aaaa", s.Name);
        Assert.Equal("idle", s.Status);
        Assert.Equal("completed", s.Outcome);
        Assert.Equal("gemini-4-carbon-medium", s.Model);
        Assert.Equal("run_command", s.LastTool);
        Assert.Equal(1250, s.InputTokens);
        Assert.Equal(350, s.OutputTokens);
        Assert.Equal(1500, s.CachedInputTokens);
        Assert.Equal(3100, s.TotalTokens);
        Assert.Null(s.Title);

        var serialized = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("PRIVATE", serialized);
        Assert.DoesNotContain("SECRET_CMD", serialized);
    }

    [Fact]
    public void Antigravity_ActiveToolTurnAndStaleInterruptedTurn_TrackBusyVsIdle()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var activeId = "bbbbbbbb-1111-2222-3333-444444444444";
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-40).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O");
        WriteAntigravityTranscript(activeId,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{t0}}}","content":"go"}""" + "\n" +
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{t1}}}","tool_calls":[{"name":"read_file","args":{}}]}""" + "\n" +
            $$$"""{"step_index":2,"source":"MODEL","type":"GENERIC","status":"DONE","created_at":"{{{t1}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-10));

        var staleId = "cccccccc-1111-2222-3333-444444444444";
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        WriteAntigravityTranscript(staleId,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{staleTs}}}","content":"old"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddMinutes(-10));

        var sessions = monitor.ReadAntigravity(_root).ToList();
        var active = Assert.Single(sessions, x => x.Id == "antigravity:" + activeId);
        Assert.Equal("busy", active.Status);
        Assert.Null(active.Outcome);
        Assert.NotNull(active.TurnStartedAt);
        Assert.Equal("read_file", active.LastTool);

        var stale = Assert.Single(sessions, x => x.Id == "antigravity:" + staleId);
        Assert.Equal("idle", stale.Status);
        Assert.Null(stale.TurnStartedAt);
    }

    [Fact]
    public void Antigravity_ErrorStatusAndErrorMessage_ReportFailedOutcome()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id1 = "dddddddd-1111-2222-3333-444444444444";
        var ts = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("O");
        WriteAntigravityTranscript(id1,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{ts}}}"}""" + "\n" +
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"ERROR","created_at":"{{{ts}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-20));

        var id2 = "eeeeeeee-1111-2222-3333-444444444444";
        WriteAntigravityTranscript(id2,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{ts}}}"}""" + "\n" +
            $$$"""{"step_index":1,"source":"SYSTEM","type":"ERROR_MESSAGE","status":"ERROR","created_at":"{{{ts}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-15));

        var sessions = monitor.ReadAntigravity(_root).ToList();
        Assert.Equal("failed", Assert.Single(sessions, x => x.Id == "antigravity:" + id1).Outcome);
        Assert.Equal("failed", Assert.Single(sessions, x => x.Id == "antigravity:" + id2).Outcome);
    }

    [Fact]
    public void Antigravity_BomEmojiTitleAndFailedResponseWithToolCalls()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "44444444-aaaa-bbbb-cccc-dddddddddddd";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O");
        var file = WriteAntigravityTranscript(id,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{t0}}}"}""" + "\n" +
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"ERROR","created_at":"{{{t1}}}","model":"gemini-4-carbon-medium","tool_calls":[{"name":"run_command"}]}""" + "\n");
        // a transcript that starts with a UTF-8 byte order mark is still read
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(file)]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-10));
        var annDir = Path.Combine(_root, ".gemini", "antigravity", "annotations");
        Directory.CreateDirectory(annDir);
        File.WriteAllText(Path.Combine(annDir, id + ".pbtxt"), "title:\"Release \U0001F680 notes\"\n");

        var s = Assert.Single(monitor.ReadAntigravity(_root, titles: true));
        Assert.Equal("idle", s.Status);       // the failed response ended the turn although it carried a tool call
        Assert.Equal("failed", s.Outcome);
        Assert.Equal("run_command", s.LastTool);
        Assert.Equal("Release \U0001F680 notes", s.Title); // the emoji is a surrogate pair and survives whole
    }

    [Fact]
    public void Antigravity_SubagentAndArchivedAreIgnored_QuotedArchivedInTitleIsNotArchived()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var ts = DateTimeOffset.UtcNow.AddSeconds(-15).ToString("O");
        var subId = "11111111-aaaa-bbbb-cccc-dddddddddddd";
        WriteAntigravityTranscript(subId,
            $$$"""{"step_index":0,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","created_at":"{{{ts}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-15));

        var archId = "22222222-aaaa-bbbb-cccc-dddddddddddd";
        WriteAntigravityTranscript(archId,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{ts}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-15));
        var annDir = Path.Combine(_root, ".gemini", "antigravity", "annotations");
        Directory.CreateDirectory(annDir);
        File.WriteAllText(Path.Combine(annDir, archId + ".pbtxt"), "title:\"Old\" archived:true\n");

        var activeId = "33333333-aaaa-bbbb-cccc-dddddddddddd";
        WriteAntigravityTranscript(activeId,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{ts}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-15));
        File.WriteAllText(Path.Combine(annDir, activeId + ".pbtxt"),
            "title:\"why does archived:true fail in \\\"parser\\\"\" last_user_view_time:{seconds:1}\n");

        var s = Assert.Single(monitor.ReadAntigravity(_root, titles: true));
        Assert.Equal("antigravity:" + activeId, s.Id);
        Assert.Equal("why does archived:true fail in \"parser\"", s.Title);

        var offMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var sNoTitle = Assert.Single(offMonitor.ReadAntigravity(_root, titles: false));
        Assert.Null(sNoTitle.Title);
    }

    [Fact]
    public void Antigravity_CliHistoryProjectAndChunkedTranscript_KeepStartAndOmitPartialTokens()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "44444444-aaaa-bbbb-cccc-dddddddddddd";
        var tStart = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var tStartIso = DateTimeOffset.FromUnixTimeMilliseconds(tStart).ToString("O");
        var tNowIso = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O");

        var file = WriteAntigravityTranscript(id,
            $$$"""{"step_index":42,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{tNowIso}}}","input_tokens":50,"output_tokens":10}""" + "\n",
            isCli: true, writeUtc: DateTime.UtcNow.AddSeconds(-10));
        var chunkDir = Path.Combine(Path.GetDirectoryName(file)!, "chunks", "transcript");
        Directory.CreateDirectory(chunkDir);
        File.WriteAllText(Path.Combine(chunkDir, "00000000.jsonl"),
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{tStartIso}}}"}""" + "\n");

        var cliRoot = Path.Combine(_root, ".gemini", "antigravity-cli");
        File.WriteAllText(Path.Combine(cliRoot, "history.jsonl"),
            $$$"""{"conversationId":"{{{id}}}","display":"PRIVATE DISPLAY TEXT","workspace":"C:\\Users\\test\\Projects\\phonebox"}""" + "\n");

        var s = Assert.Single(monitor.ReadAntigravity(_root));
        Assert.Equal("phonebox", s.Project);
        Assert.Equal("phonebox-4444", s.Name);
        Assert.Equal(tStart, s.StartedAt);
        Assert.Null(s.TotalTokens);
        Assert.Null(s.InputTokens);
        Assert.DoesNotContain("PRIVATE DISPLAY TEXT", JsonSerializer.Serialize(s));
    }

    [Fact]
    public void Antigravity_ChunksThatRepeatTheWholeTranscript_KeepTheTokenCounts()
    {
        // agy 1.5.x (#658): chunks/transcript/00000000.jsonl is written from the first turn, and transcript.jsonl still
        // holds the whole conversation, so the counts in it are complete
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "66666666-aaaa-bbbb-cccc-dddddddddddd";
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O");
        var lines =
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{t0}}}"}""" + "\n" +
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{t1}}}","model":"gemini-4-hydrogen-high","input_tokens":10572,"cache_read_tokens":0,"output_tokens":49}""" + "\n";
        var file = WriteAntigravityTranscript(id, lines, isCli: true, writeUtc: DateTime.UtcNow.AddSeconds(-10));
        var chunkDir = Path.Combine(Path.GetDirectoryName(file)!, "chunks", "transcript");
        Directory.CreateDirectory(chunkDir);
        File.WriteAllText(Path.Combine(chunkDir, "00000000.jsonl"), lines);

        var s = Assert.Single(monitor.ReadAntigravity(_root));
        Assert.Equal("gemini-4-hydrogen-high", s.Model);
        Assert.Equal(10572, s.InputTokens);
        Assert.Equal(49, s.OutputTokens);
        Assert.Equal(0, s.CachedInputTokens);
        Assert.Equal(10621, s.TotalTokens);
    }

    [Fact]
    public void Antigravity_UnchangedTranscript_ReusesCachedDetailsAndUpdatesOnAppend()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "55555555-aaaa-bbbb-cccc-dddddddddddd";
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("O");
        var file = WriteAntigravityTranscript(id,
            $$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{t0}}}"}""" + "\n",
            writeUtc: DateTime.UtcNow.AddSeconds(-20));

        Assert.Equal("busy", Assert.Single(monitor.ReadAntigravity(_root)).Status);
        var fi = new FileInfo(file);
        Assert.True(monitor.HasCachedAntigravityDetails(file, fi.LastWriteTimeUtc, fi.Length));

        Assert.Equal("busy", Assert.Single(monitor.ReadAntigravity(_root)).Status);
        Assert.True(monitor.HasCachedAntigravityDetails(file, fi.LastWriteTimeUtc, fi.Length));

        var t1 = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O");
        File.AppendAllText(file,
            $$$"""{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"{{{t1}}}","input_tokens":100,"output_tokens":20}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-5));

        var updated = Assert.Single(monitor.ReadAntigravity(_root));
        Assert.Equal("idle", updated.Status);
        Assert.Equal("completed", updated.Outcome);
        Assert.Equal(120, updated.TotalTokens);
        fi.Refresh();
        Assert.True(monitor.HasCachedAntigravityDetails(file, fi.LastWriteTimeUtc, fi.Length));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
