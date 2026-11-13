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
    public void Claude_OnlyTheExactCodexBarProbeDirectoryIsIgnored(string cwd, bool ignored)
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "3344.json"),
            JsonSerializer.Serialize(new { pid = 3344, sessionId = "sess-x", cwd, status = "busy" }));

        var sessions = SessionMonitor.ReadClaude(_root, pid => pid == 3344).ToList();
        Assert.Equal(ignored ? 0 : 1, sessions.Count);
    }

    [Fact]
    public void Antigravity_DaemonAndWal_ReportsActiveProjectAndIgnoresDeadPid()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        var convDir = Path.Combine(agyDir, "conversations");
        Directory.CreateDirectory(daemonDir);
        Directory.CreateDirectory(convDir);
        File.WriteAllText(Path.Combine(daemonDir, "daemon.json"), """{"pid":4455}""");
        var wal = Path.Combine(agyDir, "conversation_summaries.db-wal");
        File.WriteAllText(wal, "file:///Users/test/Projects/phonebox ");
        File.SetLastWriteTimeUtc(wal, DateTime.UtcNow);

        Assert.Empty(SessionMonitor.ReadAntigravity(_root, _ => [], pid => pid == 9999));

        var session = Assert.Single(SessionMonitor.ReadAntigravity(_root, _ => [], pid => pid == 4455));
        Assert.Equal("antigravity", session.Tool);
        Assert.Equal("phonebox", session.Project);
        Assert.Equal("busy", session.Status);
        Assert.Equal(4455, session.Pid);
    }

    [Fact]
    public void OpenClaw_ActiveProcessAndDirectory_ReportsStatus()
    {
        var clawDir = Path.Combine(_root, ".openclaw");
        Directory.CreateDirectory(clawDir);
        var stateFile = Path.Combine(clawDir, "state.json");
        File.WriteAllText(stateFile, "{}");
        File.SetLastWriteTimeUtc(stateFile, DateTime.UtcNow.AddMinutes(-5));

        var session = Assert.Single(SessionMonitor.ReadOpenClaw(_root, name => name == "openclaw" ? [5566] : [], pid => pid == 5566));
        Assert.Equal("openclaw", session.Tool);
        Assert.Equal("openclaw", session.Project);
        Assert.Equal("idle", session.Status);
        Assert.Equal(5566, session.Pid);

        Assert.Empty(SessionMonitor.ReadOpenClaw(_root, name => name == "claw" ? [7788] : [], pid => pid == 7788));
    }

    [Fact]
    public void ChatReadersExtractLastPromptAndReplySafelyAndTruncateLongText()
    {
        var claudeProjects = Path.Combine(_root, ".claude", "projects", "work");
        Directory.CreateDirectory(claudeProjects);
        var longPrompt = new string('p', 1700);
        File.WriteAllText(Path.Combine(claudeProjects, "sess-1.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "user", message = new { content = longPrompt } }),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "All checks green." } } } })));

        var (claudePrompt, claudeReply) = SessionMonitor.ReadClaudeChat(_root, "sess-1");
        Assert.NotNull(claudePrompt);
        Assert.Equal(1501, claudePrompt!.Length);
        Assert.EndsWith("…", claudePrompt);
        Assert.Equal("All checks green.", claudeReply);
        Assert.Equal((null, null), SessionMonitor.ReadClaudeChat(_root, "../etc/passwd"));

        var codexFile = Rollout();
        File.AppendAllText(codexFile,
            Line("event_msg", new { type = "user_message", message = "Fix the build warning" }) +
            Line("event_msg", new { type = "agent_message", message = "Updated the nullable annotation." }));
        var codexHome = Path.Combine(_root, "codex-home");
        var codexSessDir = Path.Combine(codexHome, ".codex", "sessions", "2026", "11", "13");
        Directory.CreateDirectory(codexSessDir);
        File.Copy(codexFile, Path.Combine(codexSessDir, "rollout-test-1.jsonl"));
        var (cPrompt, cReply) = SessionMonitor.ReadCodexChat(codexHome, "rollout-test-1.jsonl");
        Assert.Equal("Fix the build warning", cPrompt);
        Assert.Equal("Updated the nullable annotation.", cReply);
        Assert.Equal((null, null), SessionMonitor.ReadCodexChat(codexHome, "../*.jsonl"));

        var agyLogDir = Path.Combine(_root, ".gemini", "antigravity", "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(agyLogDir);
        File.WriteAllText(Path.Combine(agyLogDir, "transcript.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "USER_INPUT", content = "Open PRs for remaining features" }),
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", content = "Prepared PR branch cleanly." })));
        var (agyPrompt, agyReply) = SessionMonitor.ReadAntigravityChat(_root);
        Assert.Equal("Open PRs for remaining features", agyPrompt);
        Assert.Equal("Prepared PR branch cleanly.", agyReply);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
