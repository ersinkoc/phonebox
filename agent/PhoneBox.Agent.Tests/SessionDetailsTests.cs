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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
