using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A phone that stops answering adb mid-round (against a fake adb that logs its calls; never the real adb or a phone):
/// it sits out the rest of that round, so the phones after it still get keep-awake, and the next round tries it again.
/// </summary>
[Collection("adb session")] // SessionEndingTests flips the static session-ending window
public sealed class AdbUnreachablePhoneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-unreachable-" + Guid.NewGuid().ToString("N"));
    public AdbUnreachablePhoneTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CallLog => Path.Combine(_dir, "calls.log");

    /// <summary>FAKE1 and FAKE2 attached, the app installed, no reverse rule yet; <paramref name="broken"/> fails every
    /// "reverse --list" and every "settings" command (exit code 1).</summary>
    private string FakeAdb(string? broken)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached& echo FAKE1\tdevice& echo FAKE2\tdevice& exit /b 0)",
                "if \"%2\"==\"" + broken + "\" if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (exit /b 1)",
                "if \"%2\"==\"" + broken + "\" if \"%4\"==\"settings\" (exit /b 1)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (exit /b 0)",
                "if \"%4\"==\"pm\" (echo package:/data/app/base.apk& exit /b 0)",
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\nFAKE1\\tdevice\\nFAKE2\\tdevice\\n'; exit 0; fi",
            "if [ \"$2\" = '" + broken + "' ] && [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then exit 1; fi",
            "if [ \"$2\" = '" + broken + "' ] && [ \"$4\" = settings ]; then exit 1; fi",
            "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then exit 0; fi",
            "if [ \"$4\" = pm ]; then echo package:/data/app/base.apk; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

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
    public async Task APhoneThatFailedItsReverseQuerySitsOutTheRestOfTheRound()
    {
        const string awake = "shell settings put global stay_on_while_plugged_in 7";
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(broken: "FAKE1");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, display = new { homeScreen = false, keepAwake = true, followComputerLock = false } }));
        var store = new ConfigStore(path);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath)); // never reach the real adb
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Pass() => (Task)ensure.Invoke(adb, [CancellationToken.None])!;

        var error = await Record.ExceptionAsync(Pass);
        var calls = Calls();
        Assert.True(error is null, $"the round failed: {error?.Message}");
        Assert.Contains("-s FAKE2 " + awake, calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("-s FAKE1 shell settings", StringComparison.Ordinal));

        File.Delete(CallLog);
        FakeAdb(broken: null); // FAKE1 answers again: the next round sets it up
        await Pass();
        calls = Calls();
        Assert.Contains("-s FAKE1 reverse tcp:8765 tcp:8765", calls);
        Assert.Contains("-s FAKE1 " + awake, calls);
    }
}
