using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The wake and unlock a newly plugged-in phone gets when Settings > Display > wake and unlock is on: after the phone
/// is woken, the agent asks it whether a keyguard is up and only then steps over it, so nothing that is not a lock
/// screen ever receives the swipe. A secure PIN, pattern or password is never passed: "wm dismiss-keyguard" refuses
/// those by itself, which is Android's rule and not one this agent enforces. On Windows this is the only unlock the
/// agent does, since following the computer's lock (next door, in the apply-screen test) is macOS-only.
///
/// Like the other ADB tests, this runs the real AdbReverse against a fake adb that logs its calls and answers with
/// canned text, never the real adb or a real phone. The dumps fed to the parser were captured over USB in both the
/// locked and the unlocked state from two phones that are here: a Lenovo A9210 (Android 14, API 34, its keyguard
/// secured by a credential) and a Redmi Note 7 (MIUI V12.5.3.0, Android 10, API 29, a swipe-only keyguard). MIUI
/// prints neither of the two policy flags, which is why the parser reads MIUI's own "showing=" line as well. The
/// Galaxy Tab A 2016 (API 22) fixture is the exception: no such build is reachable here, so the flag name in it comes
/// from documentation. Nothing here depends on the language of the interface, because "dumpsys" answers the same way
/// on every build regardless of it.
///
/// The file is kept ASCII, as the other ADB tests are: what a Windows console mangles on an old codepage makes a diff
/// hard to read, so the dumps are quoted with the lines that carry the flags and nothing else.
/// </summary>
[Collection("adb session")] // SessionEndingTests flips the static session-ending window
public sealed class AdbWakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-wake-" + Guid.NewGuid().ToString("N"));
    public AdbWakeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CallLog => Path.Combine(_dir, "calls.log");

    /// <summary>
    /// A fake adb with <paramref name="devices"/> attached and the PhoneBox panel installed. Its "dumpsys window"
    /// answer is <paramref name="windowDump"/>, which is what decides whether the keyguard gets stepped over.
    /// </summary>
    private string FakeAdb(string windowDump, string[]? devices = null)
    {
        devices ??= ["FAKE1"];
        // The captured dumps carry quotes, brackets and more than one line, so they are not pasted into the scripts:
        // the answer is read back from a file next to the fake adb.
        var dump = Path.Combine(_dir, "window-dump.txt");
        File.WriteAllText(dump, windowDump);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached" + string.Concat(devices.Select(d => "& echo " + d + "\tdevice")) + "& exit /b 0)",
                "if \"%4\"==\"getprop\" (exit /b 0)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (exit /b 0)",
                "if \"%4\"==\"pm\" (echo package:/data/app/base.apk& exit /b 0)",
                "if \"%4\"==\"dumpsys\" if \"%5\"==\"window\" (type \"%~dp0window-dump.txt\"& exit /b 0)",
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\n" + string.Concat(devices.Select(d => d + "\\tdevice\\n")) + "'; exit 0; fi",
            "if [ \"$4\" = getprop ]; then exit 0; fi",
            "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then exit 0; fi",
            "if [ \"$4\" = pm ]; then echo package:/data/app/base.apk; exit 0; fi",
            "if [ \"$4\" = dumpsys ] && [ \"$5\" = window ]; then cat '" + dump + "'; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

    /// <summary>
    /// What the fake adb was called with, read the same retrying way as in the reverse test: cmd.exe holds the log
    /// without sharing it while a call is being logged, and a test that read it then used to fail on the runners.
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

    /// <summary>Runs the loop until <paramref name="done"/> holds or three rounds ("adb devices" calls) went by.</summary>
    private async Task<string[]> RunUntil(string fake, object settings, Func<string[], bool> done)
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(settings));
        var store = new ConfigStore(cfgPath);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath)); // never reach the real adb
        var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        using var cts = new CancellationTokenSource();
        await adb.StartAsync(cts.Token);
        try
        {
            for (var round = 0; round < 120; round++)
            {
                var calls = Calls();
                if (done(calls) || calls.Count(c => c == "devices") >= 3) break;
                await Task.Delay(100);
            }
            return Calls();
        }
        finally { cts.Cancel(); try { await adb.StopAsync(CancellationToken.None); } catch { } }
    }

    /// <summary>The settings the Kiosk card writes, with the wake and unlock switch left as asked.</summary>
    private static object Kiosk(bool wakeOnUnlock, bool openOnConnect = true, bool followComputerLock = false) =>
        new { homeScreen = false, keepAwake = false, openOnConnect, followComputerLock, wakeOnUnlock };

    /// <summary>The whole configuration the agent is started with: one fake phone and the switches above.</summary>
    private static object Settings(string fake, bool wakeOnUnlock, bool openOnConnect = true, bool followComputerLock = false) =>
        new { adbPath = fake, deviceSerial = "FAKE1", display = Kiosk(wakeOnUnlock, openOnConnect, followComputerLock) };

    /// <summary>Wakes the phone and brings the panel up the way the follow-the-computer-lock path does.</summary>
    private static async Task ApplyScreenAsync(AdbReverse adb, string device, bool locked)
    {
        var apply = typeof(AdbReverse).GetMethod("ApplyScreenAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)apply.Invoke(adb, [device, locked, CancellationToken.None])!;
    }

    /// <summary>
    /// Android 12 and newer: the Lenovo A9210 (Android 14, API 34) captured over USB while its keyguard was up. This
    /// build answers both names, "mShowingLockscreen" next to the new one, so the older name is not dead on a current
    /// build either.
    /// </summary>
    private const string AndroidNewLocked = "\nPolicy Control values (from shell):\n  mShowingLockscreen=true\n  mShowingTrustAgentLocked=false\n  isKeyguardShowing=true\n  isKeyguardPending=false\n";

    /// <summary>
    /// The same phone with its keyguard gone, which is where "wm dismiss-keyguard" left it: the swipe lock was stepped
    /// over, and the credential its owner set is still what stands between them and the phone.
    /// </summary>
    private const string AndroidNewUnlocked = "\nPolicy Control values (from shell):\n  mShowingLockscreen=false\n  isKeyguardShowing=false\n  isKeyguardPending=false\n\nKeyguardServiceDelegate state (from context):\n  showing=false\n  secure=true\n";

    /// <summary>
    /// Android 11 and older: the Galaxy Tab A 2016 (API 22), which knows "mShowingLockscreen" and not the new name. No
    /// such build is reachable here, so this is the one fixture whose flag name comes from documentation.
    /// </summary>
    private const string AndroidOldLocked = "\nPolicy Control values (from shell):\n  mShowingLockscreen=true\n  mCurrentFocus=Window{9781989 u0 com.android.settings}\n";

    /// <summary>
    /// A skin that prints neither policy flag: the Redmi Note 7 (MIUI V12.5.3.0, Android 10, API 29) captured over USB
    /// while locked, which is the phone this feature was asked for. Its keyguard is the swipe-only kind, which is why
    /// "secure" reads false. A parser that only knew the two policy names would never unlock this phone.
    /// </summary>
    private const string MiuiLocked = "\nKeyguardServiceDelegate state (from context):\n  showing=true\n  showingAndNotOccluded=true\n  secure=false\n  mShowingDream=false mDreamingLockscreen=true mDreamingSleepToken=null\n  mStatusBar=Window{be0e0e9 u0 StatusBar} isStatusBarKeyguard=true\n  mCurrentFocus=Window{be0e0e9 u0 StatusBar}\n";

    /// <summary>The same phone with the panel on screen: MIUI writes "showing=false" and "mDreamingLockscreen" goes false too.</summary>
    private const string MiuiUnlocked = "\nKeyguardServiceDelegate state (from context):\n  showing=false\n  secure=false\n  mShowingDream=false mDreamingLockscreen=false mDreamingSleepToken=null\n  mStatusBar=Window{be0e0e9 u0 StatusBar} isStatusBarKeyguard=false\n  mCurrentFocus=Window{3a94cfc u0 net.oxog.phonebox.deskpanel/net.oxog.phonebox.deskpanel.MainActivity}\n";

    /// <summary>
    /// A window service that names no keyguard state at all, which the parser reads as "not locked": a phone that
    /// cannot say whether it is locked is left alone rather than stepped over blindly.
    /// </summary>
    private const string NoKeyguardStateNamed = "\n  mCurrentFocus=null\n  mHoldScreenWindow=null\n";

    [Theory] // the flag each build is known for, in either order, and the answer when it cannot be had
    [InlineData(AndroidNewLocked, true)]        // Android 14 (API 34), captured
    [InlineData(AndroidOldLocked, true)]        // API 22, per the Android docs for that release
    [InlineData(MiuiLocked, true)]              // MIUI 12.5 (API 29), captured: neither policy flag, only "showing="
    [InlineData(MiuiUnlocked, false)]           // the same phone with its panel showing
    [InlineData(AndroidNewUnlocked, false)]     // the Android 14 phone with its keyguard gone
    [InlineData(NoKeyguardStateNamed, false)]   // nothing to read: the safe reading is that no keyguard is up
    [InlineData("", false)]
    [InlineData("\n", false)]
    // a dump the phone never answered, which must not be taken for a keyguard
    [InlineData("error: device offline", false)]
    [InlineData("error: closed", false)]
    public void KeyguardShowingReadsTheFlagOfEachBuild(string dump, bool expected) =>
        Assert.Equal(expected, AdbReverse.KeyguardShowing(dump));

    [Fact]
    public void TheFlagsAreReadInWhateverOrderTheyCome()
    {
        // The order the two names print in was seen to differ between builds; neither one may depend on the other.
        Assert.True(AdbReverse.KeyguardShowing("\n  isKeyguardShowing=true\n  mShowingLockscreen=false\n"));
        Assert.True(AdbReverse.KeyguardShowing("\n  mShowingLockscreen=true\n  isKeyguardShowing=false\n"));
    }

    [Fact]
    public void MiuisOwnShowingFlagIsNotAKeyguardState()
    {
        // MIUI prints this next to "showing=" when locked, and it stays when the keyguard is gone, so reading it as a
        // keyguard would have the agent step over a lock screen that is not there.
        Assert.True(AdbReverse.KeyguardShowing(MiuiLocked));
        Assert.False(AdbReverse.KeyguardShowing("\n  showingAndNotOccluded=true\n  secure=false\n"));
        Assert.False(AdbReverse.KeyguardShowing("\n  showing=false\n  showingAndNotOccluded=false\n"));
    }

    [Fact]
    public void WakeOnUnlockIsOffByDefault()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        Assert.False(new ConfigStore(path).Get().Display.WakeOnUnlock);
    }

    /// <summary>
    /// A phone that sleeps on its keyguard needs the keyguard stepped over, not just woken: waking alone leaves the
    /// panel behind the lock screen, which is the whole complaint this answers. The agent asks the phone first, so a
    /// phone that is not on a keyguard never gets a swipe that would land on whatever it is showing instead.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APluggedInPhoneIsUnlockedOnlyWhenTheSettingIsOn(bool wakeOnUnlock)
    {
        var fake = FakeAdb(MiuiLocked); // the phone the feature was asked for, locked, MIUI: only "showing=" names the keyguard
        var calls = await RunUntil(fake, Settings(fake, wakeOnUnlock),
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        var opened = Array.IndexOf(calls, "-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity");
        Assert.True(opened >= 0, "the panel is opened:\n" + string.Join("\n", calls));
        Assert.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", calls);
        var asked = Array.IndexOf(calls, "-s FAKE1 shell dumpsys window");
        var dismissed = Array.IndexOf(calls, "-s FAKE1 shell wm dismiss-keyguard");
        if (!wakeOnUnlock)
        {
            Assert.False(dismissed >= 0, "nothing is stepped over without the setting:\n" + string.Join("\n", calls));
            // the phone is not even asked, since an agent that will not unlock has no use for the answer
            Assert.False(asked >= 0, "the phone is asked while the setting is off:\n" + string.Join("\n", calls));
            return;
        }
        Assert.True(asked >= 0, "the phone is asked whether a keyguard is up:\n" + string.Join("\n", calls));
        Assert.True(dismissed >= 0, "the keyguard is stepped over while the setting is on:\n" + string.Join("\n", calls));
        Assert.True(asked < dismissed, "the phone answers before anything is stepped over:\n" + string.Join("\n", calls));
        Assert.True(dismissed < opened, "the lock screen is gone before the panel is brought up:\n" + string.Join("\n", calls));
    }

    /// <summary>
    /// The same plug-in with the phone already unlocked: the panel still comes up, and nothing is stepped over, which
    /// is what keeps the swipe away from the app the phone was showing.
    /// </summary>
    [Fact]
    public async Task APluggedInPhoneWithNoKeyguardKeepsWhatItIsShowing()
    {
        var fake = FakeAdb(MiuiUnlocked);
        var calls = await RunUntil(fake, Settings(fake, true),
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        Assert.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", calls);
        Assert.Contains("-s FAKE1 shell dumpsys window", calls);
        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>
    /// A phone that cannot answer about its keyguard still gets the panel: the answer only decides whether the swipe
    /// is sent, never whether the panel opens, so an agent that cannot read the screen leaves the phone as it is.
    /// </summary>
    [Fact]
    public async Task APhoneThatCannotAnswerAboutItsKeyguardStillGetsThePanel()
    {
        var fake = FakeAdb("error: device offline");
        var calls = await RunUntil(fake, Settings(fake, true),
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        Assert.Contains("-s FAKE1 shell dumpsys window", calls);
        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>
    /// "Open PhoneBox when the phone is plugged in" is the switch that wakes the phone, and the unlock next door is a
    /// step inside that path: with the panel not being opened at all, the agent has no reason to touch the screen, and
    /// the phone keeps sleeping.
    /// </summary>
    [Fact]
    public async Task APhoneThatIsNotOpenedIsNotWokenOrUnlocked()
    {
        var fake = FakeAdb(MiuiLocked);
        var calls = await RunUntil(fake, Settings(fake, true, openOnConnect: false),
            c => c.Count(x => x == "devices") >= 3);

        Assert.DoesNotContain("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", calls);
        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.DoesNotContain("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>
    /// The other path to an unlocked screen: the follow-the-computer-lock setting, which exists on macOS only (the
    /// code asks OperatingSystem.IsMacOS()), so on Windows it cannot wake anything and the wake-and-unlock switch
    /// above is the only unlock there is here. What that means for a computer that sleeps with the phone locked is the
    /// question the review asked, and the answer for the phone this was written for is the test above.
    /// </summary>
    [Fact]
    public async Task WakeOnUnlockIsNotTiedToFollowingTheComputerLock()
    {
        var fake = FakeAdb(MiuiLocked);
        var calls = await RunUntil(fake, Settings(fake, false, followComputerLock: true),
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        Assert.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", calls);
        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>
    /// The same "wm dismiss-keyguard" the follow-the-computer-lock path has always sent, now asked about first: the
    /// phone on its keyguard is stepped over, a phone that is not on one is left showing what it was showing.
    /// </summary>
    [Theory]
    [InlineData(MiuiLocked, true)]      // locked: the swipe-only keyguard is stepped over
    [InlineData(AndroidNewLocked, true)]// the same on a build that answers with the policy flags
    [InlineData(MiuiUnlocked, false)]   // already unlocked: no swipe, nothing else disturbed
    [InlineData(NoKeyguardStateNamed, false)] // a phone that cannot say is left alone
    public async Task ThePhoneIsOnlySteppedOverWhenItIsOnItsKeyguard(string windowDump, bool expectDismissed)
    {
        var fake = FakeAdb(windowDump);
        var cfgPath = Path.Combine(_dir, "config.json");
        // both switches that could unlock on their own are off, so any swipe in the log came from the path called below
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(Settings(fake, wakeOnUnlock: false, openOnConnect: false, followComputerLock: false)));
        var store = new ConfigStore(cfgPath);
        var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        using var cts = new CancellationTokenSource();
        await adb.StartAsync(cts.Token);
        try
        {
            for (var round = 0; round < 120; round++)
            {
                var calls = Calls();
                if (calls.Count(c => c == "devices") >= 2) break; // the fake has answered at least one full round
                await Task.Delay(100);
            }
            await ApplyScreenAsync(adb, "FAKE1", locked: false); // the computer is awake: this is the path that wakes and unlocks the phone
        }
        finally { cts.Cancel(); try { await adb.StopAsync(CancellationToken.None); } catch { } }

        var calls2 = Calls();
        Assert.Contains("-s FAKE1 shell dumpsys window", calls2);
        Assert.Equal(expectDismissed, calls2.Contains("-s FAKE1 shell wm dismiss-keyguard"));
    }
}
