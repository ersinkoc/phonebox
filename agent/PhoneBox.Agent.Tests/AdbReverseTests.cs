using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// AdbReverse against a fake adb that logs its calls (never the real adb or a real phone). Every test checks first
/// that the agent's own lookup lands on the fake, then runs the background loop and reads the call log.
/// </summary>
[Collection("adb session")] // SessionEndingTests flips the static session-ending window
public sealed class AdbReverseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-" + Guid.NewGuid().ToString("N"));
    public AdbReverseTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CallLog => Path.Combine(_dir, "calls.log");

    /// <summary>A fake adb: <paramref name="devices"/> attached, "reverse --list" prints <paramref name="reverseList"/>,
    /// the app is installed, and "cmd package set-home-activity" fails on <paramref name="homeRefused"/> the way Android
    /// refuses it (an error line, exit code 1).</summary>
    private string FakeAdb(string[] devices, string reverseList, string? homeRefused, bool installed = true,
        string? reverseRefused = null, string? usbKey = null, string? appVersion = null, bool ownsProvider = true, int? sdk = null,
        string? transportId = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" if \"%2\"==\"-l\" (echo List of devices attached" + string.Concat(devices.Select(d => "& echo " + d + "    device usb:1-1 product:x model:y device:z" + (transportId is null ? "" : " transport_id:" + transportId))) + "& exit /b 0)",
                "if \"%1\"==\"devices\" (echo List of devices attached" + string.Concat(devices.Select(d => "& echo " + d + "\tdevice")) + "& exit /b 0)",
                "if \"%2\"==\"" + reverseRefused + "\" if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (exit /b 1)",
                "if \"%4\"==\"getprop\" (" + (sdk is null ? "exit /b 0)" : "echo " + sdk + "& exit /b 0)"),
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (" + (reverseList.Length == 0 ? "exit /b 0)" : "echo " + reverseList + "& exit /b 0)"),
                "if \"%4\"==\"pm\" (" + (installed ? "echo package:/data/app/base.apk& exit /b 0)" : "exit /b 1)"),
                "if \"%4\"==\"content\" (" + (usbKey is null ? "echo No result found.& exit /b 0)" : "echo Row: 0 key=" + usbKey + "& exit /b 0)"),
                "if \"%4\"==\"dumpsys\" (" + (appVersion is null ? "" : "echo     versionName=" + appVersion + "& ")
                    + (usbKey is not null && ownsProvider ? "echo   [net.oxog.phonebox.deskpanel.usb]:& echo     Provider{3ec2f24 net.oxog.phonebox.deskpanel/.UsbKeyProvider}& " : "") + "exit /b 0)",
                "if \"%6\"==\"set-home-activity\" if \"%2\"==\"" + homeRefused + "\" (echo Error: Failed to set default home.& exit /b 1)",
                "if \"%6\"==\"set-home-activity\" (echo Success& exit /b 0)",
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ] && [ \"$2\" = -l ]; then printf 'List of devices attached\\n" + string.Concat(devices.Select(d => d + "    device usb:1-1 product:x model:y device:z" + (transportId is null ? "" : " transport_id:" + transportId) + "\\n")) + "'; exit 0; fi",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\n" + string.Concat(devices.Select(d => d + "\\tdevice\\n")) + "'; exit 0; fi",
            "if [ \"$2\" = '" + reverseRefused + "' ] && [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then exit 1; fi",
            "if [ \"$4\" = getprop ]; then " + (sdk is null ? "" : "echo " + sdk + "; ") + "exit 0; fi",
            "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then " + (reverseList.Length == 0 ? "" : "echo '" + reverseList + "'; ") + "exit 0; fi",
            "if [ \"$4\" = pm ]; then " + (installed ? "echo package:/data/app/base.apk; exit 0; fi" : "exit 1; fi"),
            "if [ \"$4\" = content ]; then " + (usbKey is null ? "echo 'No result found.'; exit 0; fi" : "echo 'Row: 0 key=" + usbKey + "'; exit 0; fi"),
            "if [ \"$4\" = dumpsys ]; then " + (appVersion is null ? "" : "echo '    versionName=" + appVersion + "'; ")
                + (usbKey is not null && ownsProvider ? "printf '  [net.oxog.phonebox.deskpanel.usb]:\\n    Provider{3ec2f24 net.oxog.phonebox.deskpanel/.UsbKeyProvider}\\n'; " : "") + "exit 0; fi",
            "if [ \"$6\" = set-home-activity ] && [ \"$2\" = '" + homeRefused + "' ]; then echo 'Error: Failed to set default home.'; exit 1; fi",
            "if [ \"$6\" = set-home-activity ]; then echo Success; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

    /// <summary>
    /// What the fake adb was called with. The agent runs adb for several phones at once, so a fake adb may still be
    /// appending while this reads; on Windows cmd.exe holds the file without sharing it, so a read then retries
    /// instead of failing the test (this was a flake on the Windows runners).
    /// </summary>
    private string[] Calls()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(CallLog)) return [];
                using var stream = new FileStream(CallLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    [Fact]
    public void CommandLogCanBeReadWhileTheFakeAdbWriterIsOpen()
    {
        File.WriteAllText(CallLog, "devices\n");
        Assert.Equal(["devices"], Calls());
        using var writer = new FileStream(CallLog, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Equal(["devices"], Calls());
    }

    [Fact]
    public async Task UnpluggingAllPhonesRemovesMissingAppInstallTargets()
    {
        var fake = FakeAdb(["FAKE1"], "", null, installed: false);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false } }));
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        await Pass();
        Assert.Equal(["FAKE1"], adb.MissingApp);
        FakeAdb([], "", null, installed: false);
        await Pass();
        Assert.Empty(adb.MissingApp);
        Assert.False(adb.Connected);
        await Pass();
        Assert.Empty(adb.MissingApp);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeepAwakeIsReappliedOnlyAfterDisconnect(bool disconnect, bool secondPhone)
    {
        const string rule = "UsbFfs tcp:8765 tcp:8765";
        const string awake = "shell settings put global stay_on_while_plugged_in 7";
        string[] devices = secondPhone ? ["FAKE1", "FAKE2"] : ["FAKE1"];
        var fake = FakeAdb(devices, rule, null);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = true } }));
        var store = new ConfigStore(path);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath));
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;

        await Pass();
        Assert.Single(Calls(), call => call == "-s FAKE1 " + awake);
        if (disconnect)
        {
            FakeAdb(secondPhone ? ["FAKE2"] : [], rule, null);
            await Pass();
            Assert.Equal(secondPhone, adb.Connected);
            FakeAdb(devices, rule, null);
        }
        await Pass();
        Assert.True(adb.Connected);
        var calls = Calls();
        Assert.Equal(disconnect ? 2 : 1, calls.Count(call => call == "-s FAKE1 " + awake));
        Assert.Equal(secondPhone ? 1 : 0, calls.Count(call => call == "-s FAKE2 " + awake));
    }

    [Fact]
    public async Task FailedReverseQueryDoesNotBlockNextPhone()
    {
        var devices = new[] { "FAKE1", "FAKE2" };
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(devices, "", homeRefused: null);
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false, followComputerLock = false } }));
        var store = new ConfigStore(path);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath));
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        const string first = "-s FAKE1 reverse tcp:8765 tcp:8765";
        const string second = "-s FAKE2 reverse tcp:8765 tcp:8765";

        await Pass();
        Assert.Contains(first, Calls());
        Assert.Contains(second, Calls());
        File.Delete(CallLog);

        FakeAdb(devices, "", homeRefused: null, reverseRefused: "FAKE1");
        var error = await Record.ExceptionAsync(Pass);
        var calls = Calls();
        var secondConfigured = calls.Contains(second);
        Assert.True(error is null && secondConfigured,
            $"CONTROL: both phones configured normally. EXPECTED: next phone configured, round succeeds | ACTUAL: next phone configured={secondConfigured}, error={error?.Message}");
        Assert.DoesNotContain(first, calls);
    }

    [Theory]
    [InlineData(new[] { "FAKE1" }, "FAKE1", null)]
    [InlineData(new[] { "FAKE2", "FAKE1" }, "FAKE1", "FAKE2")]
    public async Task RefusedReverseQueriesPreserveTheOtherPhones(string[] devices, string refused, string? working)
    {
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(devices, "", homeRefused: null, reverseRefused: refused);
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false, followComputerLock = false } }));
        var store = new ConfigStore(path);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath));
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        Assert.DoesNotContain($"-s {refused} reverse tcp:8765 tcp:8765", Calls());
        if (working is not null) Assert.Contains($"-s {working} reverse tcp:8765 tcp:8765", Calls());
    }

    /// <summary>Runs the loop until <paramref name="done"/> holds or three rounds ("adb devices" calls) went by.</summary>
    private async Task<string[]> RunUntil(string fake, object settings, AgentEndpoint? endpoint, Func<string[], bool> done, UsbLink? usb = null)
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(settings));
        var store = new ConfigStore(cfgPath);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath)); // never reach the real adb
        var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance, endpoint, usb);
        using var cts = new CancellationTokenSource();
        await adb.StartAsync(cts.Token);
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var calls = Calls();
                if (done(calls) || calls.Count(c => c == "devices") >= 3) break;
                await Task.Delay(100);
            }
            return Calls();
        }
        finally { cts.Cancel(); try { await adb.StopAsync(CancellationToken.None); } catch { } }
    }

    /// <summary>
    /// Android refuses "set-home-activity" (exit 1) while the app's home entry is still off or the app is missing. That
    /// must not end the round: the other phone still becomes PhoneBox's home and keep-awake reaches both phones.
    /// </summary>
    [Fact]
    public async Task ARefusedHomeScreenOnOnePhoneDoesNotHoldBackTheOthersOrKeepAwake()
    {
        var fake = FakeAdb(["FAKE1", "FAKE2"], "UsbFfs tcp:8765 tcp:8765", homeRefused: "FAKE1");
        const string awake = "shell settings put global stay_on_while_plugged_in 7";
        var calls = await RunUntil(fake, new { adbPath = fake, display = new { homeScreen = true, keepAwake = true, followComputerLock = false } }, null,
            c => c.Contains("-s FAKE2 " + awake) && c.Contains("-s FAKE1 " + awake));
        Assert.Contains("-s FAKE2 shell cmd package set-home-activity net.oxog.phonebox.deskpanel/.HomeAlias", calls);
        Assert.Contains("-s FAKE1 " + awake, calls);
        Assert.Contains("-s FAKE2 " + awake, calls);
    }

    /// <summary>
    /// A rule left by an agent on another port (--port) points the phone at a port nobody listens on; the agent must
    /// point it back at itself instead of taking any "tcp:8765" in the list for its own rule.
    /// </summary>
    [Theory]
    [InlineData("UsbFfs tcp:8765 tcp:18765", 8765)]   // a development agent ran before
    [InlineData("UsbFfs tcp:8765 tcp:8765", 18765)]   // the development agent itself, after the installed one
    [InlineData("UsbFfs tcp:9000 tcp:8765", 8765)]    // some other rule onto the computer's 8765
    public async Task AReverseRuleToAnotherPortIsPointedAtThisAgent(string existing, int port)
    {
        var fake = FakeAdb(["FAKE1"], existing, homeRefused: null);
        var want = $"-s FAKE1 reverse tcp:8765 tcp:{port}";
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false } },
            new AgentEndpoint(port), c => c.Contains(want));
        Assert.Contains(want, calls);
    }

    /// <summary>The agent's own rule is left alone: no re-binding and no panel opening every round.</summary>
    [Fact]
    public async Task TheAgentsOwnRuleIsLeftAlone()
    {
        var fake = FakeAdb(["FAKE1"], "UsbFfs tcp:8765 tcp:8765", homeRefused: null);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false } },
            null, _ => false);
        Assert.True(calls.Count(c => c == "devices") >= 2, "the loop did not run twice");
        Assert.DoesNotContain(calls, c => c.StartsWith("-s FAKE1 reverse tcp:") || c.Contains("am start"));
    }

    /// <summary>
    /// A newly plugged-in phone is woken and shows the panel, unless Settings › Display › open on connect is off (a phone
    /// that is also used for app testing): then only the reverse rule is set and the phone is left as it is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewPhoneOpensThePanelOnlyWhenOpenOnConnectIsOn(bool openOnConnect)
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect } },
            null, c => c.Count(x => x == "devices") >= 2);
        Assert.Contains("-s FAKE1 reverse tcp:8765 tcp:8765", calls);
        Assert.Equal(openOnConnect, calls.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));
        Assert.Equal(openOnConnect, calls.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP"));
    }

    /// <summary>
    /// Android 5 and 6 (API 21-23) cannot list adb reverse rules: "reverse --list" fails with "protocol fault" and adb
    /// re-attaches the phone, and later it lists nothing although the rule is there. The rule then looked missing every
    /// round and the panel was reopened every round, which restarted its data stream: such a phone is never asked, and
    /// its rule is set up once per connection.
    /// </summary>
    [Fact]
    public async Task APanelIsNotReopenedEveryRoundWhenThePhoneCannotListReverseRules()
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, reverseRefused: "FAKE1", sdk: 22);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = true } },
            null, c => c.Count(x => x == "devices") >= 3);
        Assert.Equal(1, calls.Count(x => x == "-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));
        // and the rule is set once: setting it again drops the panel's open connection
        Assert.Equal(1, calls.Count(x => x == "-s FAKE1 reverse tcp:8765 tcp:8765"));
        Assert.DoesNotContain("-s FAKE1 reverse --list", calls);
        Assert.Equal(1, calls.Count(x => x == "-s FAKE1 shell getprop ro.build.version.sdk"));
    }

    /// <summary>
    /// An Android 5 or 6 phone unplugged and plugged in again between two rounds keeps its serial, so it never leaves the
    /// device list, but adb dropped its rule with the old connection: a new transport id gets the rule set again.
    /// </summary>
    [Fact]
    public async Task AnOldPhonePluggedInAgainBetweenRoundsGetsItsRuleBack()
    {
        var devices = new[] { "FAKE1" };
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(devices, "", homeRefused: null, reverseRefused: "FAKE1", sdk: 22, transportId: "3");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = false } }));
        var store = new ConfigStore(path);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath));
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        const string rule = "-s FAKE1 reverse tcp:8765 tcp:8765";

        await Pass();
        await Pass();
        Assert.Equal(1, Calls().Count(c => c == rule)); // same connection: set once

        FakeAdb(devices, "", homeRefused: null, reverseRefused: "FAKE1", sdk: 22, transportId: "4");
        await Pass();
        await Pass();
        Assert.Equal(2, Calls().Count(c => c == rule)); // new connection: set again, once
        Assert.DoesNotContain("-s FAKE1 reverse --list", Calls());
    }

    /// <summary>A newer phone that lists no rule for this agent (the adb server restarted, or another agent took tcp:8765)
    /// gets the rule set again: only Android 5 and 6 rely on what the agent remembers.</summary>
    [Fact]
    public async Task ANewerPhoneWhoseRuleIsGoneGetsItBack()
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, sdk: 34);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = false } },
            null, c => c.Count(x => x == "-s FAKE1 reverse tcp:8765 tcp:8765") >= 2);
        Assert.Contains("-s FAKE1 reverse --list", calls);
        Assert.True(calls.Count(x => x == "-s FAKE1 reverse tcp:8765 tcp:8765") >= 2, string.Join(" | ", calls));
    }

    [Fact]
    public void OpenOnConnectIsOnByDefault()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        Assert.True(new ConfigStore(path).Get().Display.OpenOnConnect);
    }

    private const string UsbKey = "k3Y_0123456789abcdefghijklmnopqrstuvwxyzABCD";
    private const string KeyQuery = "-s FAKE1 shell content query --uri content://net.oxog.phonebox.deskpanel.usb/key";
    private const string Pin = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PinWrite = "-s FAKE1 shell content insert --uri content://net.oxog.phonebox.deskpanel.usb/pin --bind fp:s:" + Pin;
    private const string Dumpsys = "-s FAKE1 shell dumpsys package net.oxog.phonebox.deskpanel";

    /// <summary>
    /// The tunnel ends on the agent's USB port, not on the local port the settings page and the hooks use, and the agent
    /// reads the key the phone's app made, so that only that app gets through.
    /// </summary>
    [Fact]
    public async Task ANewPhoneGetsTheTunnelToTheUsbPortAndItsAppsKeyIsRead()
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, usbKey: UsbKey);
        var usb = new UsbLink(45678, Pin);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false, followComputerLock = false } },
            new AgentEndpoint(8765), c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"), usb);
        Assert.Contains("-s FAKE1 reverse tcp:8765 tcp:45678", calls);
        Assert.DoesNotContain("-s FAKE1 reverse tcp:8765 tcp:8765", calls);
        Assert.Contains(KeyQuery, calls);
        Assert.Contains(PinWrite, calls); // the app pins the tunnel's certificate
        Assert.True(Array.IndexOf(calls, Dumpsys) < Array.IndexOf(calls, KeyQuery), "the provider's owner is checked first");
        Assert.True(usb.HasKey("FAKE1"));
        Assert.Equal(UsbLink.Access.Allowed, usb.Authorize("GET", "/api/state", "", "Bearer " + UsbKey));
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>
    /// The USB port changes when the agent restarts. The phone still has the old rule: it is pointed at the new port,
    /// but the phone was not just plugged in, so it is neither woken nor brought to the panel.
    /// </summary>
    [Theory]
    [InlineData("UsbFfs tcp:8765 tcp:8765")]  // an agent from before the USB port
    [InlineData("UsbFfs tcp:8765 tcp:51234")] // this agent's previous run
    public async Task AnAgentRestartMovesTheTunnelWithoutWakingThePhone(string existing)
    {
        var fake = FakeAdb(["FAKE1"], existing, homeRefused: null, usbKey: UsbKey);
        var usb = new UsbLink(45678, Pin);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false, followComputerLock = false } },
            new AgentEndpoint(8765), c => c.Count(x => x == "devices") >= 2, usb);
        Assert.Contains("-s FAKE1 reverse tcp:8765 tcp:45678", calls);
        Assert.DoesNotContain(calls, c => c.Contains("am start") || c.Contains("KEYCODE_WAKEUP"));
        Assert.True(usb.HasKey("FAKE1"));
    }

    /// <summary>
    /// An app from before the key has nothing to answer with: the agent reads its version for the update offer and asks
    /// again only now and then, not every round.
    /// </summary>
    [Fact]
    public async Task AnAppWithoutAKeyIsAskedRarelyAndItsVersionIsRead()
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, usbKey: null, appVersion: "0.9.23");
        var usb = new UsbLink(45678, Pin);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false, followComputerLock = false } },
            new AgentEndpoint(8765), c => c.Count(x => x == "devices") >= 3, usb);
        Assert.True(calls.Count(c => c == "devices") >= 3, "the loop did not run three times");
        Assert.Equal(1, calls.Count(c => c == Dumpsys));
        Assert.DoesNotContain(KeyQuery, calls);
        Assert.DoesNotContain(PinWrite, calls);
        Assert.False(usb.HasKey("FAKE1"));
        Assert.Contains("-s FAKE1 reverse tcp:8765 tcp:45678", calls);
    }

    /// <summary>
    /// Another app took the provider name while an older PhoneBox was installed and answers with a key of its own: the
    /// app's own listing doesn't have the provider, so that key is never asked for, and the agent's pin is not written.
    /// </summary>
    [Fact]
    public async Task AKeyFromAProviderTheAppDoesNotOwnIsNeverUsed()
    {
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, usbKey: UsbKey, appVersion: "0.9.23", ownsProvider: false);
        var usb = new UsbLink(45678, Pin);
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1", display = new { homeScreen = false, keepAwake = false, followComputerLock = false } },
            new AgentEndpoint(8765), c => c.Count(x => x == "devices") >= 2, usb);
        Assert.Contains(Dumpsys, calls);
        Assert.DoesNotContain(KeyQuery, calls);
        Assert.DoesNotContain(PinWrite, calls);
        Assert.False(usb.HasKey("FAKE1"));
        Assert.Equal(UsbLink.Access.Unauthorized, usb.Authorize("GET", "/api/state", "", "Bearer " + UsbKey));
    }

    [Theory]
    [InlineData("UsbFfs tcp:8765 tcp:8765", true)]
    [InlineData("UsbFfs tcp:9000 tcp:8765", false)]
    [InlineData("", false)]
    public void AnyRuleForThePhonesPortCounts(string list, bool expected) => Assert.Equal(expected, AdbReverse.HasAnyRule(list));

    /// <summary>
    /// The agent builds AdbReverse through dependency injection: it must get the USB link there, or the tunnel would
    /// end on the local port again.
    /// </summary>
    [Fact]
    public void TheAgentsAdbReverseGetsTheUsbLink()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new ConfigStore(path));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new AgentEndpoint(8765));
        var usb = new UsbLink(45678, Pin);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, usb);
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<AdbReverse>(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var adb = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AdbReverse>(provider);
        var target = typeof(AdbReverse).GetProperty("TargetRule", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal("tcp:45678", target.GetValue(adb));
    }
}
