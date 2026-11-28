using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

[Collection("WorkBuddy names")] // ReadWorkBuddy goes through the static WorkBuddyNames cache (see WorkBuddyNamesTests)
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
        // the panel's heartbeat age and stale badge judge on the heartbeat the agent itself
        // judges on, not on the status-change time that Since may carry
        Assert.Equal(200, live.Heartbeat);
        // fail-loud: a heartbeat that stopped over 90 s ago and no outcome field in the file
        // reads "unknown", not a healthy idle
        Assert.Equal("unknown", Assert.Single(SessionMonitor.ReadWorkBuddy(_home, pid => pid != 49, nowMs: 90_201)).Status);
    }

    [Fact]
    public void ReadsOutcomeStatesFromStatusFiles()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "60.json"), """
            {"pid":60,"kind":"interactive","sessionId":"done","cwd":"/work/done","startedAt":100,
             "lastHeartbeat":200,"status":"completed"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "61.json"), """
            {"pid":61,"kind":"interactive","sessionId":"boom","cwd":"/work/boom","startedAt":100,
             "lastHeartbeat":200,"status":"failed"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "62.json"), """
            {"pid":62,"kind":"interactive","sessionId":"halt","cwd":"/work/halt","startedAt":100,
             "lastHeartbeat":200,"outcome":"stopped"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "63.json"), """
            {"pid":63,"kind":"interactive","sessionId":"running","cwd":"/work/running","startedAt":100,
             "lastHeartbeat":200,"status":"working"}
            """);

        // no real status file carries a status/outcome field today (WorkBuddy 2.176):
        // these rows come to life only when a future version writes one, so the parser
        // is ready without inventing anything now
        var rows = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).ToDictionary(s => s.Id);
        Assert.Equal("completed", rows["workbuddy:done"].Status);
        Assert.Equal("failed", rows["workbuddy:boom"].Status);
        Assert.Equal("stopped", rows["workbuddy:halt"].Status);
        // "working" is a turn in progress: the row reads busy, and no outcome is carried
        Assert.Equal("busy", rows["workbuddy:running"].Status);
        Assert.Null(rows["workbuddy:running"].Outcome);
    }

    [Fact]
    public void StaleHeartbeatWithNoOutcomeReadsUnknown()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        // no real file has an outcome field: over 90 s of silence with the process
        // still alive fails loud instead of posing as idle
        File.WriteAllText(Path.Combine(dir.FullName, "70.json"), """
            {"pid":70,"kind":"interactive","sessionId":"quiet","cwd":"/work/quiet","startedAt":1000,
             "lastHeartbeat":2000}
            """);
        Assert.Equal("unknown", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 2000 + 90_001).Single().Status);
    }

    [Fact]
    public void DeadProcessFilesAreDropped()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "71.json"), """
            {"pid":71,"kind":"interactive","sessionId":"gone","cwd":"/work/gone","startedAt":1000,
             "lastHeartbeat":2000,"status":"completed"}
            """);
        // a dead process's file is dropped however fresh or final it looks
        Assert.Empty(SessionMonitor.ReadWorkBuddy(_home, pid => false, nowMs: 2001));
    }

    [Fact]
    public void LiveHeartbeatWithOutcomeReadsBusyUntilTheTurnEnds()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        // "working" is a turn in progress: the row reads busy even though the file carries a status
        File.WriteAllText(Path.Combine(dir.FullName, "80.json"), """
            {"pid":80,"kind":"interactive","sessionId":"live","cwd":"/work/live","startedAt":100,
             "lastHeartbeat":200,"status":"working"}
            """);
        Assert.Equal("busy", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).Single().Status);

        // the same file after the turn ends: the outcome wins over busy
        File.WriteAllText(Path.Combine(dir.FullName, "80.json"), """
            {"pid":80,"kind":"interactive","sessionId":"live","cwd":"/work/live","startedAt":100,
             "lastHeartbeat":200,"status":"completed"}
            """);
        Assert.Equal("completed", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).Single().Status);
    }

    [Fact]
    public void UnrecognisedStatusWordsAreJudgedByTheHeartbeat()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        // a future WorkBuddy may write states this agent has never seen ("waiting",
        // "busy", "paused"…): a word outside the known set says nothing on its own, so
        // it must not pass as healthy ("busy" would) nor ring the input bell ("waiting"
        // would) — the heartbeat's freshness decides, exactly as when no field is there
        File.WriteAllText(Path.Combine(dir.FullName, "90.json"), """
            {"pid":90,"kind":"interactive","sessionId":"odd","cwd":"/work/odd","startedAt":100,
             "lastHeartbeat":200,"status":"waiting"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "91.json"), """
            {"pid":91,"kind":"interactive","sessionId":"odd2","cwd":"/work/odd2","startedAt":100,
             "lastHeartbeat":200,"outcome":"paused"}
            """);
        var rows = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).ToDictionary(s => s.Id);
        // a fresh heartbeat with an unrecognised word: busy (a turn may well still run)
        Assert.Equal("busy", rows["workbuddy:odd"].Status);
        Assert.Equal("busy", rows["workbuddy:odd2"].Status);
        Assert.Null(rows["workbuddy:odd"].Outcome);
        Assert.Null(rows["workbuddy:odd2"].Outcome);
        // the same words over a heartbeat that went quiet: unknown, never healthy
        var stale = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 200 + 90_001).ToDictionary(s => s.Id);
        Assert.Equal("unknown", stale["workbuddy:odd"].Status);
        Assert.Equal("unknown", stale["workbuddy:odd2"].Status);
    }

    /// <summary>The sessions the phone gets carry the feed's freshness as the agent itself
    /// judges it (Recent), so the panel's stale badge never compares its own clock against
    /// the computer's timestamps; only tools that report a heartbeat carry it.</summary>
    [Fact]
    public void SessionsCarryTheFreshnessTheAgentJudged()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "95.json"), """
            {"pid":95,"kind":"interactive","sessionId":"fresh","cwd":"/work/fresh","startedAt":100,
             "lastHeartbeat":200}
            """);
        var rows = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).ToDictionary(s => s.Id);
        Assert.True(rows["workbuddy:fresh"].Recent);
        Assert.Equal(200, rows["workbuddy:fresh"].Heartbeat);
        var stale = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 200 + 90_001).Single();
        Assert.False(stale.Recent);
        Assert.Equal("unknown", stale.Status);
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

    /// <summary>The phone-safe diagnostics reach the phone inside the "work" feed: the hook's
    /// installed flag (exported by WorkBuddyHook) and the approval switch, as booleans only —
    /// the settings path names the user's home folder and never leaves this computer, so the
    /// hook status endpoint stays local and off the LAN/USB allow-list.</summary>
    [Fact]
    public void TheHookStatusStaysLocalAndTheWorkFeedCarriesTheSwitches()
    {
        var path = Path.Combine(_home, "config-hook.json");
        File.WriteAllText(path, """{"workbuddy": {"phoneApprovals": true}}""");
        var store = new ConfigStore(path);
        var cfg = store.Get();
        Assert.True(cfg.WorkBuddy.PhoneApprovals);
        // the phone's own paths (the work feed, with the switches) stay open; the hook status
        // endpoint, whose answer carries the settings path, does not
        Assert.True(Lan.AllowedOverLan("/api/work"));
        Assert.False(Lan.AllowedOverLan("/api/workbuddy/hook"));

        // the work feed the phone gets carries the hook and approval state as booleans, and no path
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance, store);
        var json = JsonSerializer.Serialize(WorkFeed.Build(monitor, new GitHubMonitor(NullLogger<GitHubMonitor>.Instance), cfg.WorkBuddy, hookInstalled: true),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var wb = JsonDocument.Parse(json).RootElement.GetProperty("workbuddy");
        Assert.Equal(JsonValueKind.True, wb.GetProperty("installed").ValueKind);
        Assert.Equal(JsonValueKind.True, wb.GetProperty("approvals").ValueKind);
        Assert.Equal(JsonValueKind.False, wb.GetProperty("sessions").ValueKind);
        Assert.False(wb.TryGetProperty("path", out _));
        Assert.DoesNotContain("settings.json", json);
        Assert.DoesNotContain(".workbuddy", json);
    }

    /// <summary>A paired phone's key opens the work feed over Wi-Fi, but never the hook's own path: a POST would
    /// install the hook in ~/.workbuddy/settings.json and a DELETE would remove it, so both are refused with the
    /// phone's key, and over USB too.</summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void ThePhonesKeyCannotReachTheHookPath(string method)
    {
        var lan = new LanConfig { Enabled = true, Port = 8766, Token = "phone-tok", WebhookToken = "hook-tok" };
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "GET", "/api/work", "", "Bearer phone-tok", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, method, "/api/workbuddy/hook", "", "Bearer phone-tok", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, method, "/api/workbuddy/hook", "", "Bearer hook-tok", null));
        Assert.False(UsbLink.Allowed(method, "/api/workbuddy/hook"));
    }

    /// <summary>The work feed asks whether the hook is installed on every tick for every stream: the settings file is
    /// read at most once every few seconds, and an Install or Remove is seen at once.</summary>
    [Fact]
    public void TheHookStateIsReadAtMostOnceEveryFewSeconds()
    {
        WorkBuddyHook.ForgetCached();
        var reads = 0;
        var installed = false;
        bool Read() { reads++; return installed; }
        Assert.False(WorkBuddyHook.InstalledCached(1_000, Read));
        installed = true;
        Assert.False(WorkBuddyHook.InstalledCached(1_000 + WorkBuddyHook.CacheMs - 1, Read));
        Assert.Equal(1, reads);
        Assert.True(WorkBuddyHook.InstalledCached(1_000 + WorkBuddyHook.CacheMs, Read));
        Assert.Equal(2, reads);
        installed = false;
        WorkBuddyHook.ForgetCached(); // what Configure does after an Install or Remove
        Assert.False(WorkBuddyHook.InstalledCached(1_000 + WorkBuddyHook.CacheMs + 1, Read));
        Assert.Equal(3, reads);
        WorkBuddyHook.ForgetCached();
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
