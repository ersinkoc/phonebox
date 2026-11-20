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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
