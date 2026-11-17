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
    private string FakeAdb(string[] devices, string reverseList, string? homeRefused)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached" + string.Concat(devices.Select(d => "& echo " + d + "\tdevice")) + "& exit /b 0)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (" + (reverseList.Length == 0 ? "exit /b 0)" : "echo " + reverseList + "& exit /b 0)"),
                "if \"%4\"==\"pm\" (echo package:/data/app/base.apk& exit /b 0)",
                "if \"%6\"==\"set-home-activity\" if \"%2\"==\"" + homeRefused + "\" (echo Error: Failed to set default home.& exit /b 1)",
                "if \"%6\"==\"set-home-activity\" (echo Success& exit /b 0)",
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\n" + string.Concat(devices.Select(d => d + "\\tdevice\\n")) + "'; exit 0; fi",
            "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then " + (reverseList.Length == 0 ? "" : "echo '" + reverseList + "'; ") + "exit 0; fi",
            "if [ \"$4\" = pm ]; then echo package:/data/app/base.apk; exit 0; fi",
            "if [ \"$6\" = set-home-activity ] && [ \"$2\" = '" + homeRefused + "' ]; then echo 'Error: Failed to set default home.'; exit 1; fi",
            "if [ \"$6\" = set-home-activity ]; then echo Success; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

    private string[] Calls() => File.Exists(CallLog) ? File.ReadAllLines(CallLog).Select(l => l.Trim()).ToArray() : [];

    /// <summary>Runs the loop until <paramref name="done"/> holds or three rounds ("adb devices" calls) went by.</summary>
    private async Task<string[]> RunUntil(string fake, object settings, AgentEndpoint? endpoint, Func<string[], bool> done)
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(settings));
        var store = new ConfigStore(cfgPath);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath)); // never reach the real adb
        var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance, endpoint);
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
}
