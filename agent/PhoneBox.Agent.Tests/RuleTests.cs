using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Rule conditions (days, hours, pushed value comparisons) and the charge guard's decisions.</summary>
public class RuleTests
{
    [Theory]
    [InlineData(null, DayOfWeek.Sunday, true)]
    [InlineData("mon,tue,wed,thu,fri", DayOfWeek.Friday, true)]
    [InlineData("mon,tue,wed,thu,fri", DayOfWeek.Saturday, false)]
    [InlineData("sat,sun", DayOfWeek.Sunday, true)]
    public void Days(string? days, DayOfWeek day, bool expected) => Assert.Equal(expected, RuleEngine.OnDay(days, day));

    [Theory]
    [InlineData("weekdays", "mon,tue,wed,thu,fri")]
    [InlineData("Sun, sat", "sat,sun")]
    [InlineData("weekdays weekends", null)]   // every day
    [InlineData("funday", null)]
    [InlineData("", null)]
    public void DayListsAreCleaned(string input, string? expected) => Assert.Equal(expected, RuleEngine.NormalizeDays(input));

    [Fact]
    public void DaysAndHoursCombine()
    {
        var r = new RuleConfig { Days = "mon,tue,wed,thu,fri", During = "09:00-18:00" };
        Assert.True(RuleEngine.Allowed(r, new DateTime(2026, 11, 9, 10, 0, 0)));   // Monday 10:00
        Assert.False(RuleEngine.Allowed(r, new DateTime(2026, 11, 9, 19, 0, 0)));  // Monday 19:00
        Assert.False(RuleEngine.Allowed(r, new DateTime(2026, 11, 8, 10, 0, 0)));  // Sunday
        Assert.True(RuleEngine.Allowed(new RuleConfig(), new DateTime(2026, 11, 8, 3, 0, 0)));
    }

    [Theory]
    [InlineData("29.5", ">", "28", true)]
    [InlineData("27", ">", "28", false)]
    [InlineData("\"12\"", "<", "20", true)]          // a number sent as text
    [InlineData("\"open\"", "=", "OPEN", true)]
    [InlineData("\"closed\"", "!=", "open", true)]
    [InlineData("{\"value\":31,\"sub\":\"living room\"}", ">", "30", true)]
    [InlineData("\"warm\"", ">", "20", false)]       // not a number
    public void PushedValuesCompare(string json, string op, string arg, bool expected) =>
        Assert.Equal(expected, RuleEngine.Compare(JsonDocument.Parse(json).RootElement, op, arg));

    [Theory]
    [InlineData("08:30-typo", 8, 30, false)]
    [InlineData("08:30-09:30-extra", 8, 30, false)]
    [InlineData("08:30-1.01:00", 8, 30, false)]
    [InlineData("08:30", 8, 30, true)]
    [InlineData("22:00-07:00", 0, 0, true)]
    [InlineData("22:00-24:00", 23, 0, true)]
    [InlineData("24:00-07:00", 0, 0, true)]
    public void TimeWindowsRequireValidEndpoints(string window, int hour, int minute, bool expected) =>
        Assert.Equal(expected, RuleEngine.InWindow(window, new TimeSpan(hour, minute, 0)));

    [Fact]
    public void RuleOptionsAreClamped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "rules": [ { "when": "always", "then": "shortcut", "priority": 42, "forSec": -5, "cooldownMin": 99999, "countdownSec": 99, "days": "weekdays", "op": "~", "during": "  " } ] }""");
        try
        {
            var r = new ConfigStore(path).Get().Rules.Single();
            Assert.Equal(9, r.Priority);
            Assert.Equal(0, r.ForSec);
            Assert.Equal(1440, r.CooldownMin);
            Assert.Equal(30, r.CountdownSec);
            Assert.Equal("mon,tue,wed,thu,fri", r.Days);
            Assert.Null(r.Op);
            Assert.Null(r.During);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WhileActiveShortcutRulesKeepAlreadyOnTogglesOnAndReviewRequestedCountsOnlyMatchingNonDrafts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"phonebox-rule-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        PushStore? push = null;
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
            {
              "shortcuts": [{ "id": "lamp", "label": "Lamp", "cmd": "builtin:macro", "toggle": true }],
              "rules": [
                { "id": "call", "name": "Call", "when": "push", "match": "mic", "then": "shortcut", "shortcut": "lamp", "whileActive": true },
                { "id": "rev", "name": "Rev", "when": "review-requested", "match": "org/target", "then": "notify", "text": "{detail}" }
              ]
            }
            """);
            var cfg = new ConfigStore(cfgPath);
            push = new PushStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<PushStore>.Instance, Path.Combine(dir, "push.json"));
            var gh = new GitHubMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubMonitor>.Instance);
            typeof(GitHubMonitor).GetField("_snap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(gh, new GhSnapshot("me", 0,
                [
                    new GhPr("org/target", 42, "Ready", "alice", Draft: false, null, null, 0),
                    new GhPr("org/target", 43, "Draft", "bob", Draft: true, null, null, 0),
                    new GhPr("org/other", 99, "Other", "eve", Draft: false, null, null, 0),
                ], [], 0, null));

            var engine = new RuleEngine(cfg, push,
                new SessionMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionMonitor>.Instance),
                new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance),
                new SystemMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemMonitor>.Instance),
                new PrivacyMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<PrivacyMonitor>.Instance),
                new CalendarMonitor(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarMonitor>.Instance),
                new ObsMonitor(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<ObsMonitor>.Instance),
                new QuotaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<QuotaMonitor>.Instance),
                gh,
                new BatteryMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<BatteryMonitor>.Instance),
                new PingMonitor(), new AudioControl(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleEngine>.Instance);

            var lamp = cfg.Get().Shortcuts.Single();
            Assert.Null(Shortcuts.Run(cfg.Get(), "lamp", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, push));
            Assert.Equal(true, Shortcuts.State(lamp, push, null));

            push.Set("mic", JsonSerializer.SerializeToElement(true), null);
            var seq = push.Seq;
            engine.Evaluate();
            Assert.Equal(true, Shortcuts.State(lamp, push, null));
            Assert.Equal("org/target#42", push.Since(seq).Single(e => e.Kind == "notify").Data!.Value.GetProperty("text").GetString());

            engine.Pause(5);
            engine.Evaluate();
            Assert.Equal(false, Shortcuts.State(lamp, push, null));
        }
        finally
        {
            push?.Dispose();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MacroShortcutStepsUpdateToggleStateAndRunOffCmd()
    {
        var lampId = "macro-lamp-" + Guid.NewGuid().ToString("N");
        var lamp = new Shortcut { Id = lampId, Label = "Lamp", Toggle = true, Cmd = "builtin:macro", OffCmd = "builtin:keys", OffArgs = "bad_off_key" };
        var macro = new Shortcut { Id = "scene", Label = "Scene", Cmd = "builtin:macro", Steps = [$"shortcut:{lampId}"] };
        var cfg = new AgentConfig { Shortcuts = [lamp, macro] };

        Assert.Null(Shortcuts.Run(cfg, "scene", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        Assert.Equal(true, Shortcuts.State(lamp, null, null));
        var second = Shortcuts.Run(cfg, "scene", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.NotNull(second);
        Assert.Contains("bad_off_key", second);
    }
}

public class ChargeGuardTests
{
    private static readonly ChargeConfig C = new() { Enabled = true, Min = 50, Max = 80 };
    private const long Now = 1_000_000_000;
    private static PhoneBatteryReport At(int level, bool plugged, long ageMs = 0) => new(level, plugged, null, Now - ageMs);

    [Fact]
    public void StopsChargingAtTheTop() => Assert.Equal("off", ChargeGuard.Decide(C, At(80, true), Now, null, 0));

    [Fact]
    public void StartsChargingAtTheBottom() => Assert.Equal("on", ChargeGuard.Decide(C, At(50, false), Now, "off", Now - 60_000));

    [Fact]
    public void LeavesThePowerAloneInBetween()
    {
        Assert.Null(ChargeGuard.Decide(C, At(65, true), Now, "on", Now - 60_000));
        Assert.Null(ChargeGuard.Decide(C, At(65, false), Now, "off", Now - 60_000));
    }

    [Fact]
    public void RepeatsASwitchThatDidNotTakeOnlyAfterAWhile()
    {
        Assert.Null(ChargeGuard.Decide(C, At(85, true), Now, "off", Now - 60_000));             // just switched: wait
        Assert.Equal("off", ChargeGuard.Decide(C, At(85, true), Now, "off", Now - 11 * 60_000)); // still charging later: again
    }

    [Fact]
    public void NeverLeavesASilentPhoneWithoutPower()
    {
        Assert.Equal("on", ChargeGuard.Decide(C, At(70, false, ageMs: 11 * 60_000), Now, "off", Now - 20 * 60_000));
        Assert.Equal("on", ChargeGuard.Decide(C, null, Now, "off", 0));
        Assert.Null(ChargeGuard.Decide(C, null, Now, "on", 0));
    }

    [Fact]
    public void LimitsAreKeptApart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "charge": { "enabled": true, "min": 70, "max": 60, "method": "magic" } }""");
        try
        {
            var c = new ConfigStore(path).Get().Charge;
            Assert.Equal(70, c.Min);
            Assert.Equal(75, c.Max);
            Assert.Equal("ha", c.Method);
        }
        finally { File.Delete(path); }
    }
}

public class UsbDoctorTests
{
    private const long Now = 1_000_000_000;
    private static PhoneBatteryReport R(bool plugged, string? usb, long ageMs = 0, string? source = null) => new(60, plugged, null, Now - ageMs, source, usb);

    [Fact]
    public void ChargingWithoutDataBlamesTheCable() =>
        Assert.Contains("only carries power", UsbDoctor.Diagnose(R(true, "none", source: "ac"), false, Now));

    [Fact]
    public void WirelessChargingWithoutUsbDoesNotBlameACable()
    {
        Assert.Contains("only carries power", UsbDoctor.Diagnose(R(true, "none", source: "usb"), false, Now));
        var actual = UsbDoctor.Diagnose(R(true, "none", source: "wireless"), false, Now);
        Assert.True(actual is null, $"EXPECTED: no diagnosis | ACTUAL: {actual}");
        Assert.Contains("USB debugging is off", UsbDoctor.Diagnose(R(true, "connected", source: "wireless"), false, Now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(-300_000, false)]
    public void BatteryReportsFromBeforeAClockRollbackDoNotRaiseADiagnosis(long ageMs, bool expected)
    {
        var actual = UsbDoctor.Diagnose(R(true, "none", ageMs, "ac"), false, Now) is not null;
        Assert.True(expected == actual, $"EXPECTED: diagnosis={expected} | ACTUAL: diagnosis={actual}");
    }

    [Fact]
    public void ConnectedWithAdbButUnseenBlamesThePort() =>
        Assert.Contains("adb does not see it", UsbDoctor.Diagnose(R(true, "connected,configured,adb"), false, Now));

    [Fact]
    public void ConnectedWithoutAdbAsksForDebugging() =>
        Assert.Contains("USB debugging is off", UsbDoctor.Diagnose(R(true, "connected,configured"), false, Now));

    [Theory]
    [InlineData(true, "none", 0, true)]                 // adb sees the phone: nothing to explain
    [InlineData(false, "none", 0, false)]               // unplugged
    [InlineData(true, null, 0, false)]                  // an older app without the USB report
    [InlineData(true, "none", 3 * 60_000, false)]       // stale report
    public void StaysQuiet(bool plugged, string? usb, long ageMs, bool adbSees) =>
        Assert.Null(UsbDoctor.Diagnose(R(plugged, usb, ageMs), adbSees, Now));

    [Fact]
    public void NoReportNoDiagnosis() => Assert.Null(UsbDoctor.Diagnose(null, false, Now));
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/ersinkoc/phonebox/releases/latest", true)]
    [InlineData("https://API.github.com/repos/x/y/releases/assets/1", true)]
    [InlineData("http://api.github.com/repos/x/y", false)]                      // never over plain HTTP
    [InlineData("https://github.com/x/y/releases/download/v1/a.zip", false)]
    [InlineData("https://api.github.com.evil.example/repos", false)]
    [InlineData("https://release-assets.githubusercontent.com/x", false)]
    [InlineData("not a url", false)]
    public void TokenOnlyGoesToTheGitHubApi(string url, bool expected) => Assert.Equal(expected, UpdateService.IsGitHubApi(url));

    [Fact]
    public void TimeoutsAndHttpErrorsAreTransientButOurCancellationIsNot()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.True(UpdateService.Transient(new HttpRequestException("504"), CancellationToken.None));
        Assert.True(UpdateService.Transient(new TaskCanceledException("HttpClient.Timeout"), CancellationToken.None));
        Assert.False(UpdateService.Transient(new TaskCanceledException(), cancelled.Token));
        Assert.False(UpdateService.Transient(new InvalidOperationException(), CancellationToken.None));
    }
}

/// <summary>F1 (audit 2026-11-08): other sites must not reach the local API through GET requests without an Origin.</summary>
public class BrowserGuardTests
{
    [Theory]
    [InlineData("cross-site", "no-cors", "GET", "/api/widget/devops/command", true)]   // <img src> on another site
    [InlineData("same-site", "cors", "GET", "/api/settings", true)]                     // another port on localhost
    [InlineData("cross-site", "navigate", "GET", "/api/widget/p/w", true)]             // a link straight to the API
    [InlineData("cross-site", "navigate", "POST", "/", true)]                          // a form posted from another site
    [InlineData("cross-site", "navigate", "GET", "/", false)]                          // a link to the settings page
    [InlineData("same-origin", "cors", "GET", "/api/settings", false)]                 // the settings page itself
    [InlineData("none", "navigate", "GET", "/", false)]                                // typed into the address bar
    [InlineData(null, null, "GET", "/api/state", false)]                               // the phone's USB proxy, curl, hooks
    public void ForeignBrowserRequestsAreRefused(string? site, string? mode, string method, string path, bool foreign) =>
        Assert.Equal(foreign, BrowserGuard.IsForeign(site, mode, method, new Microsoft.AspNetCore.Http.PathString(path)));
}
