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

    /// <summary>The Windows fake adb's call log line. The stop runs adb for several phones at once, and cmd.exe opens
    /// the log without sharing it: a second fake that finds it open tries again instead of losing its line.</summary>
    private const string WinLog = ":log\r\n>>\"%~dp0calls.log\" echo %* || (ping -n 1 127.0.0.1 >nul& goto log)";

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
                WinLog,
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

    [Theory]
    [InlineData("UsbFfs tcp:8765 tcp:8765", true)]   // this agent's rule (no USB port in the test: 8765)
    [InlineData("UsbFfs tcp:8765 tcp:9999", false)]  // another agent's: it took the phone over, so it stays
    public async Task StoppingTakesOnlyThisAgentsRuleOffThePhone(string rule, bool removed)
    {
        var fake = FakeAdb(["FAKE1"], rule, null);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false } }));
        // which rule comes off, not how fast: a busy test machine may take more than the 3 s a real stop has
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance) { StopBudget = TimeSpan.FromSeconds(60) };
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        Assert.True(adb.Connected);

        await adb.RemoveOwnRulesAsync(CancellationToken.None);
        Assert.Equal(removed, Calls().Contains("-s FAKE1 reverse --remove tcp:8765"));
    }

    [Fact]
    public async Task StoppingAlsoTakesTheRuleOffAPhoneNoLongerSelected()
    {
        var fake = FakeAdb(["FAKE1", "FAKE2"], "", null); // no rule yet: the agent sets it on both
        var path = Path.Combine(_dir, "config.json");
        object Settings(string? serial) => new { adbPath = fake, deviceSerial = serial, display = new { homeScreen = false, keepAwake = false, openOnConnect = false } };
        File.WriteAllText(path, JsonSerializer.Serialize(Settings(null)));
        var store = new ConfigStore(path);
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance) { StopBudget = TimeSpan.FromSeconds(60) };
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        await Pass();
        Assert.Contains("-s FAKE2 reverse tcp:8765 tcp:8765", Calls());

        // Settings › Phone connection: only FAKE1 from now on; FAKE2 stays plugged in with the agent's rule on it
        Assert.Null(store.Save(JsonSerializer.Serialize(Settings("FAKE1"))));
        FakeAdb(["FAKE1", "FAKE2"], "UsbFfs tcp:8765 tcp:8765", null);
        await Pass();
        Assert.Equal("FAKE1", adb.Devices);

        await adb.RemoveOwnRulesAsync(CancellationToken.None);
        Assert.Contains("-s FAKE1 reverse --remove tcp:8765", Calls());
        Assert.Contains("-s FAKE2 reverse --remove tcp:8765", Calls());
    }

    [Fact]
    public async Task AStopDuringASlowRoundStillTakesTheRuleOffBeforeThePortCloses()
    {
        // a fake adb whose Android version read hangs: the round is busy in it when the stop begins
        string fake;
        if (OperatingSystem.IsWindows())
        {
            fake = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(fake, string.Join("\r\n",
                "@echo off",
                WinLog,
                "if \"%1\"==\"devices\" (echo List of devices attached& echo FAKE1\tdevice& exit /b 0)",
                "if \"%4\"==\"getprop\" (ping -n 30 127.0.0.1 >nul& exit /b 0)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (echo UsbFfs tcp:8765 tcp:8765& exit /b 0)",
                "exit /b 0", ""));
        }
        else
        {
            fake = Path.Combine(_dir, "adb");
            File.WriteAllText(fake, string.Join("\n",
                "#!/bin/sh",
                "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
                "if [ \"$1\" = devices ]; then printf 'List of devices attached\\nFAKE1\\tdevice\\n'; exit 0; fi",
                "if [ \"$4\" = getprop ]; then exec sleep 30; fi",
                "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then echo 'UsbFfs tcp:8765 tcp:8765'; exit 0; fi",
                "exit 0", ""));
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        }
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false } }));
        var life = new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(
            NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance);
        // The stop waits up to 7 s for the stuck round: less than adb's own 8 s limit, so the round must have been
        // cancelled for the rule to come off in time, and still plenty for a busy machine to end the fake adb.
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance, lifetime: life)
        {
            RoundStopWait = TimeSpan.FromSeconds(7),
            StopBudget = TimeSpan.FromSeconds(60),
        };
        await adb.StartAsync(CancellationToken.None);
        for (var i = 0; i < 400 && !Calls().Any(c => c.Contains("getprop")); i++) await Task.Delay(50);
        Assert.Contains(Calls(), c => c.Contains("getprop")); // the round is stuck in it now

        life.StopApplication(); // returns before the services (and the agent's port) are stopped
        Assert.Contains("-s FAKE1 reverse --remove tcp:8765", Calls());
        await adb.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenTheAgentBeginsToStopTheRuleComesOffAndTheLoopEnds()
    {
        var fake = FakeAdb(["FAKE1"], "UsbFfs tcp:8765 tcp:8765", null);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false } }));
        var life = new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(
            NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance);
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance, lifetime: life)
        {
            RoundStopWait = TimeSpan.FromSeconds(30),
            StopBudget = TimeSpan.FromSeconds(60),
        };
        await adb.StartAsync(CancellationToken.None);
        for (var i = 0; i < 400 && !adb.Connected; i++) await Task.Delay(50);
        Assert.True(adb.Connected);

        life.StopApplication(); // before any service is stopped, and before the agent's port closes
        Assert.Contains("-s FAKE1 reverse --remove tcp:8765", Calls());
        var calls = Calls().Length;
        // the loop ends by itself (StopAsync has not cancelled it): no round runs, or sets the rule, after the removal
        await adb.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(calls, Calls().Length);
        await adb.StopAsync(CancellationToken.None);
        Assert.Equal(calls, Calls().Length); // and the stop itself doesn't remove it a second time
    }

    [Fact]
    public async Task OnStopASlowPhoneDoesNotKeepTheNextOneFromLosingItsRule()
    {
        // FAKE1 answers "reverse --list" only once the gate file is there; FAKE2 at once
        string fake;
        if (OperatingSystem.IsWindows())
        {
            fake = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(fake, string.Join("\r\n",
                "@echo off",
                WinLog,
                "if \"%2\"==\"FAKE1\" if \"%4\"==\"--list\" goto hold",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (echo UsbFfs tcp:8765 tcp:8765& exit /b 0)",
                "exit /b 0",
                ":hold",
                "if not exist \"%~dp0gate\" (ping -n 2 127.0.0.1 >nul& goto hold)",
                "echo UsbFfs tcp:8765 tcp:8765& exit /b 0", ""));
        }
        else
        {
            fake = Path.Combine(_dir, "adb");
            File.WriteAllText(fake, string.Join("\n",
                "#!/bin/sh",
                "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
                "if [ \"$2\" = FAKE1 ] && [ \"$4\" = --list ]; then",
                "  while [ ! -e \"$(dirname \"$0\")/gate\" ]; do sleep 0.1; done",
                "  echo 'UsbFfs tcp:8765 tcp:8765'; exit 0",
                "fi",
                "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then echo 'UsbFfs tcp:8765 tcp:8765'; exit 0; fi",
                "exit 0", ""));
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        }
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake }));
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance) { StopBudget = TimeSpan.FromSeconds(60) };
        typeof(AdbReverse).GetField("_devices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(adb, new[] { "FAKE1", "FAKE2" });

        var stop = adb.RemoveOwnRulesAsync(CancellationToken.None);
        for (var i = 0; i < 400 && !Calls().Contains("-s FAKE2 reverse --remove tcp:8765"); i++) await Task.Delay(50);
        Assert.Contains("-s FAKE2 reverse --remove tcp:8765", Calls()); // while FAKE1 is still held
        Assert.DoesNotContain("-s FAKE1 reverse --remove tcp:8765", Calls());

        // FAKE1 answers now, within adb's 8 s limit. One phone after the other, it would have timed out by the time
        // FAKE2 got its turn, and kept its rule.
        File.WriteAllText(Path.Combine(_dir, "gate"), "");
        await stop.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("-s FAKE1 reverse --remove tcp:8765", Calls());
    }

    [Fact]
    public async Task ARestartThatCannotFindTheAdbServerProcessDoesNotClaimARestart()
    {
        var fake = FakeAdb(["FAKE1"], "", null);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake }));
        var log = new Capture<AdbReverse>();
        using var adb = new AdbReverse(new ConfigStore(path), log);

        await WithoutTheRealAdbServer(null, () => adb.RestartAdbServerAsync(fake, CancellationToken.None));
        Assert.Contains("kill-server", Calls());
        Assert.Contains(log.Lines, l => l.Contains("could not be found") && l.Contains("only asking it to stop"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("restarting it"));

        // found (this test's own process, which is not adb and so is left running): the restart is announced
        await WithoutTheRealAdbServer(Environment.ProcessId, () => adb.RestartAdbServerAsync(fake, CancellationToken.None));
        Assert.Contains(log.Lines, l => l.Contains($"restarting it (process {Environment.ProcessId})"));
    }

    [Fact]
    public void TheAdbServerIsNotRestartedWhileAnAppInstallRuns()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = FakeAdb(["FAKE1"], "", null) }));
        var log = new Capture<AdbReverse>();
        using var adb = new AdbReverse(new ConfigStore(path), log);

        var install = adb.Installing();
        Assert.True(adb.InstallRunning);
        Assert.False(adb.RestartDue(0));
        Assert.False(adb.RestartDue(70_000));   // a minute without an answer, but the install would be cut short
        Assert.False(adb.RestartDue(140_000));
        Assert.Single(log.Lines, l => l.Contains("an app install is running")); // said once
        install.Dispose();
        install.Dispose();                      // a second dispose doesn't count another install off
        Assert.False(adb.InstallRunning);

        Assert.False(adb.RestartDue(150_000));  // the minute starts when the install has ended
        Assert.True(adb.RestartDue(210_000));
    }

    /// <summary>Runs <paramref name="restart"/> with the adb server's process lookup replaced and the machine's adb
    /// server settings out of the way, so it never touches a real adb server.</summary>
    private static async Task WithoutTheRealAdbServer(int? pid, Func<Task> restart)
    {
        var find = AdbReverse.FindServerPid;
        var socket = Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
        var port = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT");
        try
        {
            AdbReverse.FindServerPid = (_, _) => Task.FromResult(pid);
            Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", null);
            Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_PORT", null);
            await restart();
        }
        finally
        {
            AdbReverse.FindServerPid = find;
            Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", socket);
            Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_PORT", port);
        }
    }

    private sealed class Capture<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? ex, Func<TState, Exception?, string> format) { lock (Lines) Lines.Add(format(state, ex)); }
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

    /// <summary>A restarted adb server has no rules and can give the phone the same transport id again: an old phone,
    /// which can't list its rules, must still get its rule back. It was set up before, so it is not woken again.</summary>
    [Fact]
    public async Task AfterTheAgentRestartsTheAdbServerAnOldPhoneGetsItsRuleBack()
    {
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(["FAKE1"], "", homeRefused: null, reverseRefused: "FAKE1", sdk: 22, transportId: "1");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = true } }));
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;
        const string rule = "-s FAKE1 reverse tcp:8765 tcp:8765";
        const string wake = "-s FAKE1 shell input keyevent KEYCODE_WAKEUP";

        await Pass();
        await Pass();
        Assert.Equal(1, Calls().Count(c => c == rule)); // same connection: set once
        Assert.Equal(1, Calls().Count(c => c == wake)); // newly plugged in: woken, the panel opened

        // the restart itself, without the machine's real adb server or its environment
        var find = AdbReverse.FindServerPid;
        var socket = Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
        var port = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT");
        try
        {
            AdbReverse.FindServerPid = (_, _) => Task.FromResult<int?>(null);
            Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", null);
            Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_PORT", null);
            await adb.RestartAdbServerAsync(fake, CancellationToken.None);
        }
        finally
        {
            AdbReverse.FindServerPid = find;
            Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", socket);
            Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_PORT", port);
        }
        Assert.Contains("kill-server", Calls());

        await Pass(); // the same transport id "1" as before the restart
        Assert.Equal(2, Calls().Count(c => c == rule));
        Assert.Equal(1, Calls().Count(c => c == wake)); // not again: an automatic restart at night must not light it up
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
