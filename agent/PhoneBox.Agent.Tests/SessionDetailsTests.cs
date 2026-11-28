using System.Text;
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
    // ---------------------------------------------------------------- Pi sessions
    private string WritePiSession(string id, string lines, DateTime? writeUtc = null)
    {
        var dir = Path.Combine(_root, ".pi", "agent", "sessions", "--home-test-work-phonebox--");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "2026-11-01T00-00-00-000Z_" + id + ".jsonl");
        File.WriteAllText(file, lines);
        if (writeUtc is { } wt) File.SetLastWriteTimeUtc(file, wt);
        return file;
    }

    private static string PiMsg(string role, object? msg = null, string? ts = null)
        => JsonSerializer.Serialize(new { type = "message", timestamp = ts ?? DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O"), message = msg ?? new { role } });

    [Fact]
    public void Pi_ConfigDefaultsOff_AreNormalized()
    {
        var cfg = new AgentConfig();
        Assert.False(cfg.Pi.Sessions);
        Assert.False(cfg.Pi.ShareSessionTitles);

        var path = Path.Combine(_root, "pi-cfg.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """{"pi":null}""");
        var store = new ConfigStore(path);
        var loaded = store.Get();
        Assert.NotNull(loaded.Pi);
        Assert.False(loaded.Pi.Sessions);
        Assert.False(loaded.Pi.ShareSessionTitles);
    }

    [Fact]
    public void Pi_CompletedTurn_ReportsStatsWithoutLeakingPromptsOrArgs()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "11111111-aaaa-bbbb-cccc-222222222222";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-6).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var t2 = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/phonebox"}""" + "\n" +
            $$$"""{"type":"model_change","id":"a1","timestamp":"{{{t0}}}","provider":"llama-swap","modelId":"local-7b"}""" + "\n" +
            $$$"""{"type":"thinking_level_change","id":"a2","timestamp":"{{{t0}}}","thinkingLevel":"medium"}""" + "\n" +
            $$$"""{"type":"message","id":"a3","timestamp":"{{{t1}}}","message":{"role":"user","content":"PRIVATE PROMPT"}}""" + "\n" +
            $$$"""{"type":"message","id":"a4","timestamp":"{{{t2}}}","message":{"role":"assistant","model":"local-7b","thinkingLevel":"medium","usage":{"input":100,"output":40,"cacheRead":500,"cacheWrite":10,"reasoning":20,"totalTokens":670},"stopReason":"toolUse","content":[{"type":"toolCall","name":"read","arguments":{"path":"PRIVATE_ARG"}}]}}""" + "\n" +
            $$$"""{"type":"message","id":"a5","timestamp":"{{{t2}}}","message":{"role":"toolResult","toolName":"read","content":"PRIVATE RESULT"}}""" + "\n" +
            $$$"""{"type":"message","id":"a6","timestamp":"{{{t2}}}","message":{"role":"assistant","model":"local-7b","thinkingLevel":"medium","usage":{"input":2,"output":30,"cacheRead":660,"cacheWrite":0,"reasoning":0,"totalTokens":692},"stopReason":"stop","content":[{"type":"text","text":"PRIVATE REPLY"}]}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("pi:" + id, s.Id);
        Assert.Equal("pi", s.Tool);
        Assert.Equal("phonebox", s.Project);
        Assert.Equal("phonebox-2222", s.Name);
        Assert.Equal("idle", s.Status);
        Assert.Equal("completed", s.Outcome);
        Assert.Equal("local-7b", s.Model);
        Assert.Equal("medium", s.Effort);
        Assert.Equal(662, s.LastInputTokens);      // latest request only: 2 input + 660 cache reads + 0 cache writes
        Assert.Equal(112, s.InputTokens);          // 100 + 2 + 10 cache writes
        Assert.Equal(70, s.OutputTokens);
        Assert.Equal(1160, s.CachedInputTokens);
        Assert.Equal(20, s.ReasoningTokens);
        Assert.Equal(1342, s.TotalTokens);          // reasoning is already part of Pi's output count
        Assert.Equal("read", s.LastTool);
        Assert.Null(s.TurnStartedAt);              // the turn is over
        Assert.Null(s.ContextWindow);              // the provider is not in the model store
        Assert.Null(s.Title);

        var serialized = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("PRIVATE", serialized);
    }

    [Fact]
    public void Pi_OpenTurn_IsBusyAndFollowsAppendsUntilTheStopReasonClosesIt()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "33333333-aaaa-bbbb-cccc-444444444444";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("O");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/phonebox"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t1}}}","message":{"role":"user","content":"go"}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddSeconds(-20).UtcDateTime);

        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var open = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("busy", open.Status);
        Assert.Null(open.Outcome);
        Assert.NotNull(open.TurnStartedAt);
        var fi = new FileInfo(file);
        Assert.True(monitor.HasCachedPiDetails(file, fi.LastWriteTimeUtc, fi.Length));

        File.AppendAllText(file, PiMsg("assistant", new { role = "assistant", model = "m", usage = new { input = 5, output = 7, cacheRead = 0, cacheWrite = 0, reasoning = 0, totalTokens = 12 }, stopReason = "stop" }) + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-5));

        var closed = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("idle", closed.Status);
        Assert.Equal("completed", closed.Outcome);
        Assert.Equal(12, closed.TotalTokens);
    }

    [Fact]
    public void Pi_AbortedAndErrorStops_ReportTheOutcome()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-12).ToString("O");
        var oldWrite = DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime;

        var abortedId = "55555555-aaaa-bbbb-cccc-666666666666";
        WritePiSession(abortedId,
            $$$"""{"type":"session","version":3,"id":"{{{abortedId}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/a"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"aborted","content":[]}}""" + "\n",
            writeUtc: oldWrite);

        var failedId = "77777777-aaaa-bbbb-cccc-888888888888";
        WritePiSession(failedId,
            $$$"""{"type":"session","version":3,"id":"{{{failedId}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/b"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"error","content":[]}}""" + "\n",
            writeUtc: oldWrite);

        var sessions = monitor.ReadPi(root).ToList();
        Assert.Equal("aborted", Assert.Single(sessions, x => x.Id == "pi:" + abortedId).Outcome);
        Assert.Equal("failed", Assert.Single(sessions, x => x.Id == "pi:" + failedId).Outcome);
    }

    [Fact]
    public void Pi_AnErrorFinishesAtOnceAndItsAutoRetryReopensTheTurn()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "abababab-aaaa-bbbb-cccc-babababababa";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/retry"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"user","content":"go"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"error","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":0},"content":[]}}""" + "\n");

        // an error is heard at once: it must not read as a running turn for minutes (a final error has no retry)
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var failed = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("idle", failed.Status);
        Assert.Equal("failed", failed.Outcome);

        // auto-retry: Pi omits the failed attempt with a context_edit, then writes nothing until the retry ends
        File.AppendAllText(file,
            $$$"""{"type":"context_edit","id":"c1","timestamp":"{{{t0}}}","targetId":"a4","replacement":null}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var retrying = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("busy", retrying.Status);
        Assert.Null(retrying.Outcome);

        // the retried reply ends it
        File.AppendAllText(file, PiMsg("assistant", new { role = "assistant", model = "m", usage = new { input = 7, output = 2, cacheRead = 3, cacheWrite = 0, reasoning = 0, totalTokens = 9 }, stopReason = "stop" }) + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var done = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("completed", done.Outcome);
        Assert.Equal(10, done.LastInputTokens);
    }

    [Fact]
    public void Pi_LengthStop_FinishesAtOnceAndTheCompactingRecoveryReopensIt()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "b0b0b0b0-aaaa-bbbb-cccc-b0b0b0b0b0b0";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/length"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"length","usage":{"input":9,"output":8,"cacheRead":0,"cacheWrite":0,"reasoning":0},"content":[]}}""" + "\n");

        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        Assert.Equal("failed", Assert.Single(monitor.ReadPi(root)).Outcome);

        // the truncation recovery compacts and retries, which is a running turn again
        File.AppendAllText(file,
            $$$"""{"type":"compaction","id":"c2","timestamp":"{{{t0}}}","tokensBefore":9000,"summary":"PRIVATE SUMMARY"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        Assert.Equal("busy", Assert.Single(monitor.ReadPi(root)).Status);

        // the recovered reply ends it, with no second "failed"
        File.AppendAllText(file, PiMsg("assistant", new { role = "assistant", model = "m", stopReason = "stop", content = Array.Empty<object>() }) + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        Assert.Equal("completed", Assert.Single(monitor.ReadPi(root)).Outcome);
    }

    [Fact]
    public void Pi_AFailureIsNotReopenedByARenameOrAnotherRecord()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "e0e0e0e0-aaaa-bbbb-cccc-e0e0e0e0e0e0";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O");
        var failed = DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O");
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/gone"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{failed}}}","message":{"role":"user","content":"go"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{failed}}}","message":{"role":"assistant","stopReason":"error","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":0},"content":[]}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddMinutes(-20).UtcDateTime);

        var settled = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("idle", settled.Status);
        Assert.Equal("failed", settled.Outcome);

        // a title rename rewrites the file without retrying anything: the turn stays finished
        File.AppendAllText(file,
            $$$"""{"type":"session_info","timestamp":"{{{DateTimeOffset.UtcNow:O}}}","name":"renamed after a dead retry"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var renamed = Assert.Single(monitor.ReadPi(root, titles: true));
        Assert.Equal("renamed after a dead retry", renamed.Title);
        Assert.Equal("idle", renamed.Status);
        Assert.Equal("failed", renamed.Outcome);

        // nor does a record that carries no retry
        File.AppendAllText(file, PiMsg("bashExecution", null) + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        Assert.Equal("failed", Assert.Single(monitor.ReadPi(root)).Outcome);
    }

    [Fact]
    public void Pi_AnOpenTurnWithNoWritesForHalfAnHourSettlesIdle()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var id = "f1f1f1f1-aaaa-bbbb-cccc-f1f1f1f1f1f1";

        // Pi was killed mid-tool 40 minutes ago: nothing more is coming, so the row must not read "working"
        var old = DateTimeOffset.UtcNow.AddMinutes(-40).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{old}}}","cwd":"/home/test/work/killed"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{old}}}","message":{"role":"user","content":"go"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{old}}}","message":{"role":"toolResult","content":"x"}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddMinutes(-40).UtcDateTime);

        var settled = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("idle", settled.Status);
        Assert.Null(settled.Outcome);            // nothing finished: no "finished" toast for a session that died
    }

    [Fact]
    public void Pi_LongRunningTurn_StaysBusyPastThreeMinutes()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "cdcdcdcd-aaaa-bbbb-cccc-dcdcdcdcdcdc";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/slow"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"user","content":"long task"}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddMinutes(-9).UtcDateTime);

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("busy", s.Status);
        Assert.Null(s.Outcome);
    }

    [Fact]
    public void Pi_ErrorUsage_DoesNotResetTheLastContextSize()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "efefefef-aaaa-bbbb-cccc-efefefefefef";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-12).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/ctx"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"stop","usage":{"input":12,"output":4,"cacheRead":30,"cacheWrite":0,"reasoning":0,"totalTokens":46},"content":[]}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"error","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":0},"content":[]}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime);

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("failed", s.Outcome);
        Assert.Equal(42, s.LastInputTokens);       // the error's EMPTY_USAGE did not replace 12 + 30
    }

    [Fact]
    public void Pi_BadUsageNumbers_DoNotHideTheSession()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "fafafafa-aaaa-bbbb-cccc-fafafafafafa";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/numbers"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"stop","usage":{"input":1.5,"output":999999999999999999999,"cacheRead":"7","cacheWrite":null,"reasoning":2},"content":[]}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("completed", s.Outcome);
        Assert.Equal(2, s.ReasoningTokens);        // the valid counter survives the bad ones
    }

    [Fact]
    public void Pi_AppendedTurn_InsideAHugeToolResult_KeepsTheSessionBusy()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "a2a2a2a2-aaaa-bbbb-cccc-a2a2a2a2a2a2";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-6).ToString("O");
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/appended"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"stop","usage":{"input":3,"output":2,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":5},"content":[]}}""" + "\n",
            writeUtc: DateTimeOffset.UtcNow.AddMinutes(-6).UtcDateTime);

        Assert.Equal("completed", Assert.Single(monitor.ReadPi(root)).Outcome);

        // the next turn ends in a tool result bigger than the tail window, so the window starts inside it
        var huge = new string('x', 2 * 1024 * 1024 + 1024);
        File.AppendAllText(file,
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"user","content":"go"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"toolUse","content":[{"type":"toolCall","name":"bash"}]}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"toolResult","toolName":"bash","content":"{{{huge}}}"}}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var busy = Assert.Single(monitor.ReadPi(root));
        Assert.Equal("busy", busy.Status);
        Assert.Null(busy.Outcome);
    }

    [Fact]
    public void Pi_InitialRead_InsideAHugeToolResult_IsBusy()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "a3a3a3a3-aaaa-bbbb-cccc-a3a3a3a3a3a3";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var huge = new string('x', 2 * 1024 * 1024 + 1024);
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/first"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"toolUse","content":[{"type":"toolCall","name":"bash"}]}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"toolResult","toolName":"bash","content":"{{{huge}}}"}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("first", s.Project);
        Assert.Equal("busy", s.Status);
        Assert.Null(s.Outcome);
    }

    [Fact]
    public void Pi_LargeFile_IsReadFromItsTailAndStillFindsTheSessionHeader()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "a1a1a1a1-aaaa-bbbb-cccc-a1a1a1a1a1a1";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var huge = new string('x', 2 * 1024 * 1024 + 1024);
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/big"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"toolUse","usage":{"input":10,"output":5,"cacheRead":0,"cacheWrite":0,"reasoning":0},"content":[{"type":"toolCall","name":"read"}]}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"toolResult","content":"{{{huge}}}"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"tail-model","stopReason":"stop","usage":{"input":4,"output":2,"cacheRead":0,"cacheWrite":0,"reasoning":0},"content":[]}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("big", s.Project);
        Assert.Equal("tail-model", s.Model);
        Assert.Equal("completed", s.Outcome);
        Assert.Null(s.TotalTokens);                // the middle was not read, so the counters are not claimed as complete
    }

    [Fact]
    public void Pi_RoleLikeToolArguments_DoNotSkipOrRerouteRecords()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "b1b1b1b1-aaaa-bbbb-cccc-b1b1b1b1b1b1";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/roles"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"user","content":"go"}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"toolUse","usage":{"input":11,"output":3,"cacheRead":0,"cacheWrite":0,"reasoning":0},"content":[{"type":"toolCall","name":"write","arguments":{"role":"system","path":"x"}}]}}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"stop","usage":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"reasoning":0},"content":[{"type":"text","text":"done"},{"type":"toolCall","name":"read","arguments":{"role":"toolResult"}}]}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        // an argument that mentions "system" must not skip the record it belongs to: its usage still counts
        Assert.Equal(12, s.InputTokens);
        Assert.Equal(5, s.OutputTokens);
        Assert.Equal(1, s.LastInputTokens);
        Assert.Equal("read", s.LastTool);
        // an argument that mentions "toolResult" must not turn a finished reply into a running turn
        Assert.Equal("idle", s.Status);
        Assert.Equal("completed", s.Outcome);
    }

    [Fact]
    public void Pi_BashExecutionAfterTheStop_DoesNotReopenTheTurn()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "99999999-aaaa-bbbb-cccc-000000000000";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/c"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","stopReason":"stop","usage":{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":2},"content":[]}}""" + "\n" +
            PiMsg("bashExecution", null) + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("idle", s.Status);
        Assert.Equal("completed", s.Outcome);
    }

    [Fact]
    public void Pi_Title_IsSharedOnlyWhenAskedFor()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var offMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        string Content(string name) =>
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/d"}""" + "\n" +
            $$$"""{"type":"session_info","timestamp":"{{{t0}}}","name":"{{{name}}}"}""" + "\n";
        var file = WritePiSession(id, Content("Fix the flaky test"));

        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        Assert.Equal("Fix the flaky test", Assert.Single(monitor.ReadPi(root, titles: true)).Title);
        Assert.Null(Assert.Single(offMonitor.ReadPi(root)).Title);

        // with sharing off, the title the monitor already read is dropped and the file is read again: a rename that
        // leaves the file's write time and length alone (same length) is still picked up when sharing comes back on
        Assert.Null(Assert.Single(monitor.ReadPi(root)).Title);
        var written = new FileInfo(file).LastWriteTimeUtc;
        File.WriteAllText(file, Content("Fix the other test"));
        File.SetLastWriteTimeUtc(file, written);
        Assert.Equal("Fix the other test", Assert.Single(monitor.ReadPi(root, titles: true)).Title);
    }

    [Fact]
    public void Pi_TailWindowThatStartsAtARecordBoundary_KeepsThatRecord()
    {
        const int window = 2 * 1024 * 1024;
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "e1e1e1e1-aaaa-bbbb-cccc-e1e1e1e1e1e1";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        string Stop(int pad) => $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"m","stopReason":"stop","usage":{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":2},"content":[{"type":"text","text":"{{{new string('x', pad)}}}"}]}}""" + "\n";
        var stop = Stop(window - Encoding.UTF8.GetByteCount(Stop(0)));
        Assert.Equal(window, Encoding.UTF8.GetByteCount(stop));        // the window begins exactly at this record

        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/boundary"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"toolResult","content":"{{{new string('y', 4096)}}}"}}""" + "\n" +
            stop);

        // the cut lands on a line break, so its first line is a whole record and has to be read, not dropped
        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("boundary", s.Project);
        Assert.Equal("completed", s.Outcome);
        Assert.Equal("idle", s.Status);
    }

    [Fact]
    public void Pi_AgentDirExpandsATildeLikePiDoes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".pi-work"), SessionMonitor.PiAgentDir("~/.pi-work"));
        Assert.Equal(Path.Combine(home, ".pi-work"), SessionMonitor.PiAgentDir("~\\.pi-work"));
        Assert.Equal(home, SessionMonitor.PiAgentDir("~"));
        Assert.Equal(home, SessionMonitor.PiAgentDir("~/"));
        Assert.Equal(Path.Combine(home, ".pi", "agent"), SessionMonitor.PiAgentDir(null));
        Assert.Equal(Path.Combine(home, ".pi", "agent"), SessionMonitor.PiAgentDir("  "));
        Assert.Equal("/opt/pi", SessionMonitor.PiAgentDir("/opt/pi"));
    }

    [Fact]
    public void Pi_CacheIsForgottenWhenTheSessionsAreTurnedOff()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "c1c1c1c1-aaaa-bbbb-cccc-c1c1c1c1c1c1";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/cache"}""" + "\n");

        Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        var info = new FileInfo(file);
        Assert.True(monitor.HasCachedPiDetails(file, info.LastWriteTimeUtc, info.Length));

        monitor.ClearPiCache();
        Assert.False(monitor.HasCachedPiDetails(file, info.LastWriteTimeUtc, info.Length));
    }

    [Fact]
    public void Pi_SessionIdIsCleanedAndCapped()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var longId = new string('q', 100);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(longId,
            $$$"""{"type":"session","version":3,"id":"{{{longId}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/capped"}""" + "\n");

        var capped = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal("pi:" + new string('q', 80), capped.Id);
        Assert.Equal("capped-qqqq", capped.Name);
    }

    [Fact]
    public void Pi_ContextWindow_ComesFromPiOwnModelStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".pi", "agent"));
        File.WriteAllText(Path.Combine(_root, ".pi", "agent", "models-store.json"),
            """{"llama-swap":{"models":[{"id":"local-7b","contextWindow":32768}],"etag":"x","checkedAt":"2026-11-27T00:00:00Z"}}""");
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/e"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"local-7b","provider":"llama-swap","stopReason":"stop","content":[]}}""" + "\n");

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal(32768, s.ContextWindow);

        // a provider the store does not know keeps the context size without a window
        Assert.True(SessionMonitor.ContextWindows(Path.Combine(_root, "no-such-store.json")).Count == 0);
    }

    [Fact]
    public void Pi_InvalidContextWindow_IsSkippedAndValidModelsKept()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".pi", "agent"));
        var store = Path.Combine(_root, ".pi", "agent", "models-store.json");
        File.WriteAllText(store,
            """{"llama-swap":{"models":[{"id":"bad-model","contextWindow":null},{"id":"string-model","contextWindow":"32768"},{"id":"fraction-model","contextWindow":1.5},{"id":"local-7b","contextWindow":32768}]},"other":{"models":[{"id":"other-model","contextWindow":65536}]}}""");
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "dddddddd-eeee-ffff-aaaa-bbbbbbbbbbbb";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/g"}""" + "\n" +
            $$$"""{"type":"message","timestamp":"{{{t0}}}","message":{"role":"assistant","model":"local-7b","provider":"llama-swap","stopReason":"stop","content":[]}}""" + "\n");

        // the null / string entries are skipped, the valid windows survive, and the session is still shown
        var windows = SessionMonitor.ContextWindows(store);
        Assert.Equal(32768, windows["llama-swap"]["local-7b"]);
        Assert.Equal(65536, windows["other"]["other-model"]);
        Assert.False(windows["llama-swap"].ContainsKey("bad-model"));
        Assert.False(windows["llama-swap"].ContainsKey("string-model"));
        Assert.False(windows["llama-swap"].ContainsKey("fraction-model"));

        var s = Assert.Single(monitor.ReadPi(Path.Combine(_root, ".pi", "agent", "sessions")));
        Assert.Equal(32768, s.ContextWindow);
    }

    [Fact]
    public void Pi_TrailingPartialRecord_IsFinishedOnTheNextRead()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var id = "cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        var root = Path.Combine(_root, ".pi", "agent", "sessions");
        var file = WritePiSession(id,
            $$$"""{"type":"session","version":3,"id":"{{{id}}}","timestamp":"{{{t0}}}","cwd":"/home/test/work/f"}""" + "\n");

        var line = PiMsg("assistant", new { role = "assistant", model = "m", stopReason = "stop", content = Array.Empty<object>() });
        var half = line.Length / 2;
        File.AppendAllText(file, line[..half]);
        var before = monitor.ReadPi(root).ToList();
        Assert.Single(before);
        Assert.Null(before[0].Outcome);              // the assistant record is not complete yet

        File.AppendAllText(file, line[half..] + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        Assert.Equal("completed", Assert.Single(monitor.ReadPi(root)).Outcome);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
