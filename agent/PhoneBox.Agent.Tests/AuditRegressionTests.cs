using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Regression tests from the 2026-11-08 audit (F3/F4 pushed values, F5 rules, F8 command widgets). Every test works on
/// its own temporary files; nothing touches the real config.json or data.json.
/// </summary>
// Starts real processes against a deadline: run apart from other collections, so a busy two-core runner cannot
// push a child's start past it (see ShTests).
[Collection("Process deadline")]
public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<PushStore> _stores = [];

    public AuditRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // a store saves 2 s after its last change; disposing it saves now, so the folder isn't recreated after it's gone
        foreach (var s in _stores) s.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private PushStore Push()
    {
        var s = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "data.json"));
        _stores.Add(s);
        return s;
    }
    private static readonly JsonElement Open = JsonSerializer.SerializeToElement("open");

    [Fact]
    public void PushStoreStillComesFromTheAgentsServices()
    {
        using var sp = new ServiceCollection().AddLogging().AddSingleton<PushStore>().BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<PushStore>());
    }

    // ---------------------------------------------------------------- F3 / F4: pushed values with a TTL

    [Fact]
    public void AnExpiredValueChangesTheVersionOnceSoThePhoneDropsIt()
    {
        var s = Push();
        s.Set("door", Open, 1);
        s.Set("keep", Open, null);
        s.Set("renewed", Open, 1);
        Thread.Sleep(300);
        s.Set("renewed", JsonSerializer.SerializeToElement("still here"), null);
        var before = s.Version;
        Thread.Sleep(1100);
        var after = s.Version;
        Assert.NotEqual(before, after);
        Assert.Equal(after, s.Version);
        var snap = s.Snapshot();
        Assert.False(snap.ContainsKey("door"));
        Assert.True(snap.ContainsKey("keep"));
        Assert.Equal("still here", snap["renewed"].Data.GetString());
    }

    [Fact]
    public void ExpiredValuesDoNotCountAgainstTheKeyLimit()
    {
        var s = Push();
        for (var i = 0; i < 200; i++) Assert.Null(s.Set($"tmp-{i}", Open, 1));
        Assert.NotNull(s.Set("new", Open, null));
        Thread.Sleep(1100);
        Assert.Null(s.Set("new", Open, null));
    }

    // ---------------------------------------------------------------- F5: rules after an edit or a pause

    private (RuleEngine Engine, PushStore Push, Action<string> Write) Rules()
    {
        var path = Path.Combine(_dir, "config.json");
        void Write(string rules) { File.WriteAllText(path, "{\"rules\":[" + rules + "]}"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddTicks(Random.Shared.Next(1, 1_000_000))); }
        Write("");
        var cfg = new ConfigStore(path);
        var push = Push();
        var engine = new RuleEngine(cfg, push, new SessionMonitor(NullLogger<SessionMonitor>.Instance), new MediaMonitor(NullLogger<MediaMonitor>.Instance),
            new SystemMonitor(NullLogger<SystemMonitor>.Instance), new PrivacyMonitor(NullLogger<PrivacyMonitor>.Instance), new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance),
            new ObsMonitor(cfg, NullLogger<ObsMonitor>.Instance), new QuotaMonitor(NullLogger<QuotaMonitor>.Instance), new GitHubMonitor(NullLogger<GitHubMonitor>.Instance),
            new BatteryMonitor(NullLogger<BatteryMonitor>.Instance), new PingMonitor(), new AudioControl(), NullLogger<RuleEngine>.Instance);
        return (engine, push, Write);
    }

    private static string OnAir(string name, string when = "always", string? match = null) =>
        $$"""{"id":"meeting","name":"{{name}}","when":"{{when}}"{{(match is null ? "" : $",\"match\":\"{match}\"")}},"then":"push","key":"onair","pushValue":"live","whileActive":true,"cooldownMin":30}""";

    [Fact]
    public void AnEditOrAPauseDoesNotLeaveAWhileActiveRuleOffForItsRestTime()
    {
        var (engine, push, write) = Rules();
        write(OnAir("Meeting"));
        engine.Evaluate();
        Assert.True(push.Snapshot().ContainsKey("onair"));
        write(OnAir("Meeting (renamed)"));
        engine.Evaluate();
        Assert.True(push.Snapshot().ContainsKey("onair"));
        engine.Pause(1); engine.Evaluate();
        Assert.False(push.Snapshot().ContainsKey("onair"));
        engine.Pause(0); engine.Evaluate();
        Assert.True(push.Snapshot().ContainsKey("onair"));
    }

    [Fact]
    public void TheRestTimeStillHoldsWhenTheConditionReallyEndsAndComesBack()
    {
        var (engine, push, write) = Rules();
        write(OnAir("Meeting", "push", "trigger"));
        var on = JsonSerializer.SerializeToElement(true);
        push.Set("trigger", on, null); engine.Evaluate();
        Assert.True(push.Snapshot().ContainsKey("onair"));
        push.Remove("trigger"); engine.Evaluate();
        Thread.Sleep(RuleEngine.ReleaseSec * 1000 + 300);
        engine.Evaluate();
        Assert.False(push.Snapshot().ContainsKey("onair"));
        push.Set("trigger", on, null); engine.Evaluate();
        Assert.False(push.Snapshot().ContainsKey("onair"));
    }

    [Fact]
    public void AnAppInFrontRuleFollowsTheForegroundApp()
    {
        var (engine, push, write) = Rules();
        write("""{"id":"focus","name":"Focus","when":"app-in-front","match":"code","then":"goto","page":"work","whileActive":true}""");
        engine.FrontProbe = () => "Visual Studio Code";
        engine.Evaluate();
        Assert.Contains(push.Since(0), e => e.Kind == "goto");
        Assert.Contains("focus", engine.Active());
        engine.FrontProbe = () => "Safari";
        engine.Evaluate();
        Thread.Sleep(RuleEngine.ReleaseSec * 1000 + 300);
        engine.Evaluate();
        Assert.Contains(push.Since(0), e => e.Kind == "back");
        Assert.DoesNotContain("focus", engine.Active());
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]   // MQTT / Home Assistant style
    [InlineData("False", false)] // PowerShell's $false as text
    [InlineData("No", false)]
    public void APushedOffValueDoesNotTurnAPushRuleOnInAnyCase(string body, bool on)
    {
        var (engine, push, write) = Rules();
        write(OnAir("TV", "push", "tv"));
        push.Set("tv", PushStore.ParseValue(body), null);
        engine.Evaluate();
        Assert.Equal(on, engine.Active().Contains("meeting"));
        // the same key read by a toggle shortcut agrees
        Assert.Equal(on, Shortcuts.State(new Shortcut { Id = "s", StateKey = "tv" }, push, null));
    }

    [Theory]
    [InlineData("""{"value":"on","sub":"living room"}""", true)]
    [InlineData("""{"title":"Sensor"}""", true)]                    // no value: an object still counts as on
    [InlineData("""{"value":"off","sub":"living room"}""", false)]  // README › Webhooks payload shape
    [InlineData("""{"value":false}""", false)]
    [InlineData("""{"value":0,"unit":"W"}""", false)]
    public void APushRuleJudgesAJsonPayloadByItsValue(string json, bool on)
    {
        var (engine, push, write) = Rules();
        write(OnAir("TV", "push", "tv"));
        push.Set("tv", JsonDocument.Parse(json).RootElement, null);
        engine.Evaluate();
        Assert.Equal(on, engine.Active().Contains("meeting"));
    }

    // ---------------------------------------------------------------- F8: command widgets and background processes

    [Fact]
    public void ABackgroundProcessLeftByTheCommandDoesNotBlockTheWidget()
    {
        var path = Path.Combine(_dir, "config.json");
        var pidPath = Path.Combine(_dir, "background.pid");
        var workerPath = Path.Combine(_dir, "background.ps1");
        File.WriteAllText(workerPath, $"[IO.File]::WriteAllText('{pidPath.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30");
        var command = $"cmd /c start /b powershell.exe -NoProfile -NonInteractive -File \"{workerPath}\"";
        var commandUnix = $"/bin/sleep 30 & printf '%s' $! > '{pidPath.Replace("'", "'\\''")}'; echo hi";
        File.WriteAllText(path, JsonSerializer.Serialize(new { pages = new[] { new { id = "p", title = "P", widgets = new[] {
            new { id = "bg", type = "command", x = 0, y = 0, w = 6, h = 3, options = new { command, commandUnix, interval = 60 } }
        } } } }));
        var runner = new CommandRunner(new ConfigStore(path), NullLogger<CommandRunner>.Instance);
        var sw = Stopwatch.StartNew();
        runner.Get("p", "bg");
        CommandResult? r = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            r = runner.Get("p", "bg");
            if (r is { At: > 0, Running: false }) break;
            Thread.Sleep(100);
        }
        try
        {
            Assert.True(r is { At: > 0, Running: false }, $"no result after {sw.Elapsed.TotalSeconds:0.0} s (the background process holds the output pipe)");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12), $"took {sw.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var ownedPid))
                try
                {
                    using var child = Process.GetProcessById(ownedPid);
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
                }
                catch (ArgumentException) { /* the fixture already exited */ }
        }
    }
}
