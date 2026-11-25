using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class WorkBuddySessionsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("phonebox-workbuddy-").FullName;

    [Fact]
    public void ReadsOnlyLiveInteractiveStatusFiles()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "37.json"), """
            {"pid":37,"kind":"interactive","sessionId":"fixture","cwd":"/work/project","startedAt":100,
             "lastHeartbeat":200,"model":"fixture-model","title":"private title"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "38.json"), """
            {"pid":38,"kind":"prewarm","sessionId":"prewarm"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "49.json"), """
            {"pid":49,"kind":"interactive","sessionId":"dead-process"}
            """);

        var live = Assert.Single(SessionMonitor.ReadWorkBuddy(_home, pid => pid != 49, nowMs: 201));
        Assert.Equal("workbuddy:fixture", live.Id);
        Assert.Equal("project", live.Project);
        Assert.Equal("busy", live.Status);
        Assert.Equal("fixture-model", live.Model);
        Assert.Null(live.Title);
        Assert.Equal("idle", Assert.Single(SessionMonitor.ReadWorkBuddy(_home, pid => pid != 49, nowMs: 90_201)).Status);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void ApprovalRequiresBothOptInsOnAnEnabledPage(bool setting, bool page, bool widget, bool expected)
    {
        var sessions = new WidgetConfig { Type = "sessions" };
        sessions.Set("workBuddyApprovals", widget);
        var cfg = new AgentConfig
        {
            WorkBuddy = new WorkBuddyConfig { PhoneApprovals = setting },
            Pages = [new PageConfig { Enabled = page, Widgets = [sessions] }],
        };
        Assert.Equal(expected, WorkBuddyHook.Allowed(cfg));
    }

    [Fact]
    public async Task WorkBuddyApprovalsDefaultOffEvenWhenClaudeApprovalsAreOn()
    {
        var store = new ConfigStore(Path.Combine(_home, "config.json"));
        Assert.True(store.Get().Claude.PhoneApprovals);
        Assert.False(store.Get().WorkBuddy.PhoneApprovals);
        Assert.False(store.Get().WorkBuddy.Sessions);
        var logger = NullLogger<SessionMonitor>.Instance;
        var monitor = new SessionMonitor(logger, store);
        var pushes = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_home, "push.json"));
        var approvals = new ApprovalStore(pushes, NullLogger<ApprovalStore>.Instance);
        using var input = JsonDocument.Parse("""{"tool_name":"Read","session_id":"test","cwd":"/work/project"}""");
        Assert.Null(await ClaudeHook.HandleWorkBuddyAsync(input.RootElement, store, approvals, monitor, CancellationToken.None));
        Assert.Empty(approvals.Snapshot());
    }

    [Fact]
    public void InstallsTheWorkBuddyEndpointWithoutChangingOtherHooks()
    {
        var path = Path.Combine(_home, "settings.json");
        File.WriteAllText(path, """{"hooks":{"SessionStart":[]},"other":"keep"}""");

        Assert.Null(ClaudeHook.Configure(true, 8765, 90, path, WorkBuddyHook.Endpoint));
        Assert.True(ClaudeHook.Installed(path, WorkBuddyHook.Endpoint));
        Assert.False(ClaudeHook.Installed(path));
        Assert.Contains("SessionStart", File.ReadAllText(path));
        Assert.Contains("other", File.ReadAllText(path));
        Assert.Null(ClaudeHook.Configure(false, 8765, 90, path, WorkBuddyHook.Endpoint));
        Assert.False(ClaudeHook.Installed(path, WorkBuddyHook.Endpoint));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
