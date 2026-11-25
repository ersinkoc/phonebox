using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class GrokSessionTests : IDisposable
{
    private const string Id = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-grok-" + Guid.NewGuid());
    private readonly SessionMonitor _monitor = new(NullLogger<SessionMonitor>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void RecentSessionReportsFolderModelAndTokensWithoutTheChat()
    {
        var dir = SessionDir();
        Write(dir, "summary.json", Summary());
        Write(dir, "usage.json", Usage());
        Write(dir, "signals.json", Signals());
        Write(dir, "events.jsonl", Event("turn_started", "note", "SECRET PROMPT") + Event("tool_completed", "tool_name", "read_file"));
        Write(dir, "chat_history.jsonl", "{\"text\":\"SECRET CHAT\"}\n");

        var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => false));
        Assert.Equal("grok:" + Id, session.Id);
        Assert.Equal("grok", session.Tool);
        Assert.Equal("phonebox", session.Project);
        Assert.Equal("phonebox-1111", session.Name);
        Assert.Equal("busy", session.Status);
        Assert.Equal("grok-4.7-build", session.Model);
        Assert.Equal("high", session.Effort);
        Assert.Equal(1000, session.TotalTokens);
        Assert.Equal(700, session.InputTokens);
        Assert.Equal(300, session.OutputTokens);
        Assert.Equal(50, session.CachedInputTokens);
        Assert.Equal(20, session.ReasoningTokens);
        Assert.Equal(40, session.LastInputTokens);
        Assert.Equal(256000, session.ContextWindow);
        Assert.Equal("read_file", session.LastTool);
        Assert.Null(session.Title);
        Assert.Null(session.Outcome);
        var json = JsonSerializer.Serialize(session);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("424242", json);
        Assert.DoesNotContain("cost", json);
        Assert.DoesNotContain("/work", json);
        Assert.DoesNotContain("chat_history", json);
    }

    [Fact]
    public void ACompletedTurnIsIdleAndACancelledTurnIsAborted()
    {
        var dir = SessionDir();
        Write(dir, "summary.json", Summary());
        var stamp = DateTime.UtcNow.AddMinutes(-1);
        Write(dir, "events.jsonl", Event("turn_started") + Event("turn_ended", "outcome", "completed"), stamp);
        var done = Assert.Single(_monitor.ReadGrok(_root, (_, _) => false));
        Assert.Equal("idle", done.Status);
        Assert.Equal("completed", done.Outcome);

        // Both outcomes have the same length, and the file can keep the same modification time.
        Write(dir, "events.jsonl", Event("turn_started") + Event("turn_ended", "outcome", "cancelled"), stamp);
        var stopped = Assert.Single(_monitor.ReadGrok(_root, (_, _) => true));
        Assert.Equal("idle", stopped.Status);
        Assert.Equal("aborted", stopped.Outcome);
    }

    [Fact]
    public void PermissionWaitsOnlyWhileThatProcessIsAlive()
    {
        var dir = SessionDir();
        var when = DateTime.UtcNow.AddMinutes(-10);
        Write(dir, "summary.json", Summary(), when);
        Write(dir, "events.jsonl", Event("turn_started") + Event("phase_changed", "phase", "permission_prompt"), when);
        File.WriteAllText(Path.Combine(_root, "active_sessions.json"),
            "[{\"session_id\":\"" + Id + "\",\"pid\":4242,\"pid_start\":1000,\"cwd\":\"/work/phonebox\"}]");

        var waiting = Assert.Single(_monitor.ReadGrok(_root, (pid, start) => pid == 4242 && start == 1000));
        Assert.Equal("waiting", waiting.Status);
        Assert.Equal("permission", waiting.WaitingFor);
        Assert.Equal(4242, waiting.Pid);

        var other = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var quiet = Assert.Single(other.ReadGrok(_root, (_, _) => false));
        Assert.Equal("idle", quiet.Status);
        Assert.Null(quiet.WaitingFor);
        Assert.Null(quiet.Pid);
    }

    [Fact]
    public void AToolAfterPermissionIsWorking()
    {
        var dir = SessionDir();
        Write(dir, "events.jsonl", Event("turn_started") + Event("phase_changed", "phase", "permission_prompt")
            + Event("tool_completed", "tool_name", "bash"));
        var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => true));
        Assert.Equal("busy", session.Status);
        Assert.Null(session.WaitingFor);
        Assert.Equal("bash", session.LastTool);
    }

    [Fact]
    public void AnOpenTurnOlderThanThreeMinutesIsIdle()
    {
        var dir = SessionDir();
        var when = DateTime.UtcNow.AddMinutes(-10);
        Write(dir, "events.jsonl", Event("turn_started"), when);
        var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => true));
        Assert.Equal("idle", session.Status);
        Assert.Equal("phonebox", session.Project);
    }

    [Fact]
    public void SessionsQuietForThreeHoursAreSkipped()
    {
        var dir = SessionDir();
        Write(dir, "summary.json", Summary(), DateTime.UtcNow.AddHours(-4));
        Write(dir, "events.jsonl", Event("turn_started"), DateTime.UtcNow.AddHours(-4));
        Assert.Empty(_monitor.ReadGrok(_root, (_, _) => true));
    }

    [Fact]
    public void TheFolderNameDecodesWhenSummaryIsMissingOrBroken()
    {
        var dir = SessionDir();
        Write(dir, "usage.json", "{");
        Write(dir, "events.jsonl", Event("turn_started"));
        var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => false));
        Assert.Equal("phonebox", session.Project);
        Assert.Equal("busy", session.Status);
        Assert.Null(session.TotalTokens);
    }

    [Fact]
    public void AnUnreadableProjectFolderDoesNotHideTheOthers()
    {
        if (OperatingSystem.IsWindows()) return; // folder permissions are tested where chmod applies
        var dir = SessionDir();
        Write(dir, "summary.json", Summary());
        Write(dir, "events.jsonl", Event("turn_started"));
        var locked = Path.Combine(_root, "sessions", "%2Fwork%2Flocked");
        Directory.CreateDirectory(Path.Combine(locked, "22222222-3333-4444-5555-666666666666"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => false));
            Assert.Equal("grok:" + Id, session.Id);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void APidStartThatIsNotUnixTimeOnlyChecksThePid()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var startMicros = new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() * 1000;
        Assert.True(SessionMonitor.GrokSameProcess(self.Id, startMicros)); // macOS: Unix microseconds that match
        Assert.False(SessionMonitor.GrokSameProcess(self.Id, startMicros - 3_600_000_000)); // a reused pid
        Assert.True(SessionMonitor.GrokSameProcess(self.Id, 123_456_789)); // Linux-style clock ticks: not Unix time
    }

    [Fact]
    public void AMalformedActiveSessionsFileDoesNotHideSessions()
    {
        var dir = SessionDir();
        Write(dir, "summary.json", Summary());
        Write(dir, "events.jsonl", Event("turn_started") + Event("phase_changed", "phase", "permission_prompt"));
        File.WriteAllText(Path.Combine(_root, "active_sessions.json"),
            "[\"oops\", null, {\"session_id\":\"" + Id + "\",\"pid\":\"123\"}, {\"session_id\":\"" + Id + "\",\"pid\":null}]");
        var session = Assert.Single(_monitor.ReadGrok(_root, (_, _) => true));
        Assert.Equal("grok:" + Id, session.Id);
        Assert.Null(session.Pid);
    }

    [Fact]
    public void AnUnchangedSessionIsNotParsedAgain()
    {
        var dir = SessionDir();
        Write(dir, "summary.json", Summary());
        Write(dir, "usage.json", Usage());
        Write(dir, "events.jsonl", Event("turn_started"));
        Assert.Equal(1000, Assert.Single(_monitor.ReadGrok(_root, (_, _) => false)).TotalTokens);

        // Same size and write time: the cached reading is kept, even though the bytes differ.
        var usage = Path.Combine(dir, "usage.json");
        var when = File.GetLastWriteTimeUtc(usage);
        File.WriteAllText(usage, Usage().Replace("1000", "9999", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(usage, when);
        Assert.Equal(1000, Assert.Single(_monitor.ReadGrok(_root, (_, _) => false)).TotalTokens);

        File.SetLastWriteTimeUtc(usage, when.AddSeconds(1));
        Assert.Equal(9999, Assert.Single(_monitor.ReadGrok(_root, (_, _) => false)).TotalTokens);
    }

    private string SessionDir()
    {
        var dir = Path.Combine(_root, "sessions", "%2Fwork%2Fphonebox", Id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name, string content, DateTime? when = null)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        if (when is { } at) File.SetLastWriteTimeUtc(path, at);
    }

    private static string Summary() => JsonSerializer.Serialize(new
    {
        generated_title = "SECRET TITLE",
        last_turn_summary = "SECRET SUMMARY",
        session_summary = "SECRET SESSION",
        info = new { cwd = "/work/phonebox", id = Id },
        reasoning_effort = "high",
        current_model_id = "grok-4.7",
        created_at = "2026-11-24T16:00:00.000Z",
        last_active_at = "2026-11-24T16:05:00.000Z",
    });

    private static string Usage() => JsonSerializer.Serialize(new
    {
        sessionId = Id,
        updatedAt = "2026-11-24T16:05:00.0000000Z",
        session = new
        {
            inputTokens = 700, outputTokens = 300, cachedReadTokens = 50, reasoningTokens = 20,
            totalTokens = 1000, costUsdTicks = 424242, primaryModelId = "grok-4.7-build",
        },
        turns = new[] { new { inputTokens = 40, costUsdTicks = 424242 } },
    });

    private static string Signals() => JsonSerializer.Serialize(new { primaryModelId = "grok-4.7", contextWindowTokens = 256000 });

    private static string Event(string type, string? extraName = null, string? extra = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var tail = extraName is null ? "" : ",\"" + extraName + "\":" + JsonSerializer.Serialize(extra);
        return "{\"type\":\"" + type + "\",\"ts\":\"" + now + "\"" + tail + "}\n";
    }
}
