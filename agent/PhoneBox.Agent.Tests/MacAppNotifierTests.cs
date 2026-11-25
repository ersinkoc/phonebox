using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The PhoneBox notifier program: when it is built, how macOS permission is handled, and that every failure falls back to
/// osascript. A fake runner stands in for swiftc, sips, iconutil, codesign and the program itself.
/// </summary>
public sealed class MacAppNotifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-notifier-" + Guid.NewGuid());
    private readonly List<(string File, string[] Args, string? Stdin)> _calls = [];
    private long _now = 1_000_000;
    private readonly List<string> _modes = [];
    private int _authorizeExit = 0;          // what "--authorize" and "--status" answer
    private int _showExit = 0;               // what a notification request answers
    private int _swiftcExit = 0;
    private bool _throwOnShow;
    private string? _swiftc = "/Library/Developer/CommandLineTools/usr/bin/swiftc";

    public MacAppNotifierTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Task<(int, string)> Run(string file, IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        _calls.Add((file, [.. args], stdin));
        if (file == "/usr/bin/xcrun" && args.Count > 2 && args[2] == "swiftc")
        {
            if (_swiftcExit == 0) { Directory.CreateDirectory(Path.GetDirectoryName(args[^1])!); File.WriteAllText(args[^1], "binary"); }
            return Task.FromResult((_swiftcExit, ""));
        }
        if (file == "/usr/bin/open")
        {
            // the permission step, started through LaunchServices: its answer goes to the file named after --result
            _modes.Add("--authorize");
            File.WriteAllText(args[^1], _authorizeExit.ToString());
            return Task.FromResult((0, ""));
        }
        if (file.EndsWith("/iconutil")) { File.WriteAllText(args[^1], "icns"); return Task.FromResult((0, "")); }
        if (file.EndsWith("PhoneBoxNotifier"))
        {
            if (args.Count > 0 && args[0] == "--status") { _modes.Add(args[0]); return Task.FromResult((_authorizeExit, "")); }
            if (_throwOnShow) throw new IOException("could not start");
            return Task.FromResult((_showExit, ""));
        }
        return Task.FromResult((0, ""));
    }

    private MacAppNotifier Notifier() => new(_dir, Run, () => _swiftc, () => ("// swift", [1, 2, 3]), () => _now, NullLogger.Instance);

    [Fact]
    public async Task TheFirstNotificationStartsPreparingAndUsesTheFallback()
    {
        var n = Notifier();
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        await WaitFor(() => n.State is NotifierState.Ready);
        Assert.Contains(_calls, c => c.File == "/usr/bin/xcrun" && c.Args.Contains("swiftc"));
    }

    [Fact]
    public async Task OnceReadyANotificationIsHandedToTheProgramAsJsonOnItsInput()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.Ready, n.State);
        Assert.True(await n.PostAsync("Anna \"A\"", "WhatsApp", "line1\nline2 Καλημέρα 🌍", [9, 9], null, CancellationToken.None));
        var request = _calls.Last(c => c.File.EndsWith("PhoneBoxNotifier") && c.Args.Length == 0);
        using var doc = JsonDocument.Parse(request.Stdin!);
        Assert.Equal("Anna \"A\"", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("line1\nline2 Καλημέρα 🌍", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal(Convert.ToBase64String([9, 9]), doc.RootElement.GetProperty("image").GetString());
    }

    [Fact]
    public async Task TheTextIsNeverAnArgument()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        await n.PostAsync("-e do shell script", "--authorize", "x", null, null, CancellationToken.None);
        Assert.All(_calls.Where(c => c.File.EndsWith("PhoneBoxNotifier") && c.Stdin is not null), c => Assert.Empty(c.Args));
    }

    [Fact]
    public async Task ANotificationWithoutAnIconHasNoImageKey()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        await n.PostAsync("t", "s", "b", null, null, CancellationToken.None);
        using var doc = JsonDocument.Parse(_calls.Last(c => c.Stdin is not null).Stdin!);
        Assert.False(doc.RootElement.TryGetProperty("image", out _));
    }

    [Fact]
    public async Task WithoutASwiftCompilerItIsUnavailableAndNothingIsBuilt()
    {
        _swiftc = null;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.Unavailable, n.State);
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.DoesNotContain(_calls, c => c.File.EndsWith("PhoneBoxNotifier"));
    }

    [Fact]
    public async Task AFailedBuildIsUnavailable()
    {
        _swiftcExit = 1;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.Unavailable, n.State);
        Assert.False(File.Exists(Path.Combine(_dir, "version")));
    }

    [Fact]
    public async Task APermissionTheOwnerRefusedMeansNoNotificationFromTheProgram()
    {
        _authorizeExit = 3;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.NotAllowed, n.State);
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.DoesNotContain(_calls, c => c.Stdin is not null);
    }

    [Fact]
    public async Task ARefusalIsLookedAtAgainAfterTenMinutesAndAllowedThenWorks()
    {
        _authorizeExit = 3;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _authorizeExit = 0;                                  // the owner allowed it in System Settings
        _now += 50_000;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));   // looked at once a minute, not at every notification
        _now += 20_000;
        Assert.True(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(NotifierState.Ready, n.State);
    }

    [Fact]
    public async Task AQuestionLeftOpenIsUndecidedNotReadyAndNotGivenUpOn()
    {
        _authorizeExit = 5;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.Undecided, n.State);
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.DoesNotContain(_calls, c => c.Stdin is not null);          // nothing is handed to a program that may not show it
        Assert.Single(_modes);
        _authorizeExit = 0;                                                // the owner answered meanwhile
        _now += 11 * 60_000;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));  // this one starts the new look
        await WaitFor(() => n.State == NotifierState.Ready);
        Assert.Equal(2, _modes.Count(m => m == "--authorize"));
    }

    [Fact]
    public async Task ARefusalIsRecheckedWithStatusNeverWithTheQuestion()
    {
        _authorizeExit = 3;
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _now += 61_000;
        await n.PostAsync("t", "s", "b", null, null, CancellationToken.None);
        Assert.Equal(["--authorize", "--status"], _modes);
    }

    [Fact]
    public async Task AnExitOfFiveWhileShowingMeansTheQuestionIsOpen()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _showExit = 5;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(NotifierState.Undecided, n.State);
    }

    [Fact]
    public async Task ABuildWhoseStampIsRightButWhoseAppIsIncompleteIsBuiltAgain()
    {
        await Notifier().PrepareAsync(CancellationToken.None);
        File.Delete(Path.Combine(_dir, "PhoneBox.app", "Contents", "Info.plist"));   // cut short, or damaged
        await Notifier().PrepareAsync(CancellationToken.None);
        Assert.Equal(2, _calls.Count(c => c.File == "/usr/bin/xcrun" && c.Args.Contains("swiftc")));
    }

    [Fact]
    public async Task ARebuildThatFailsLeavesNoStampBehind()
    {
        await Notifier().PrepareAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "version"), "old");
        _swiftcExit = 1;
        await Notifier().PrepareAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_dir, "version")));
    }

    [Fact]
    public async Task AFolderOthersCanWriteIsNotTrusted()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierState.Unavailable, n.State);
        Assert.DoesNotContain(_calls, c => c.File == "/usr/bin/xcrun");
    }

    [Fact]
    public async Task AnExitOfThreeWhileShowingMeansNotAllowedAndTheFallbackIsUsed()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _showExit = 3;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(NotifierState.NotAllowed, n.State);
    }

    [Fact]
    public async Task AnotherFailureIsFalseButTheProgramStaysReady()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _showExit = 4;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(NotifierState.Ready, n.State);
        _showExit = 0;
        Assert.True(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task AProgramThatCannotStartIsFalseNotAnException()
    {
        var n = Notifier();
        await n.PrepareAsync(CancellationToken.None);
        _throwOnShow = true;
        Assert.False(await n.PostAsync("t", "s", "b", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task AnUpToDateBuildIsNotBuiltAgain()
    {
        var first = Notifier();
        await first.PrepareAsync(CancellationToken.None);
        var builds = _calls.Count(c => c.File == "/usr/bin/xcrun" && c.Args.Contains("swiftc"));
        var second = Notifier();
        await second.PrepareAsync(CancellationToken.None);
        Assert.Equal(1, builds);
        Assert.Equal(1, _calls.Count(c => c.File == "/usr/bin/xcrun" && c.Args.Contains("swiftc")));
        Assert.Equal(NotifierState.Ready, second.State);
    }

    [Fact]
    public async Task ABuildWithAnotherVersionIsReplaced()
    {
        await Notifier().PrepareAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "version"), "old");
        await Notifier().PrepareAsync(CancellationToken.None);
        Assert.Equal(2, _calls.Count(c => c.File == "/usr/bin/xcrun" && c.Args.Contains("swiftc")));
    }

    [Fact]
    public async Task TheBuildMakesTheIconsetSignsTheAppAndNamesItPhoneBox()
    {
        await Notifier().PrepareAsync(CancellationToken.None);
        Assert.Equal(NotifierBundle.IconsetSizes.Count, _calls.Count(c => c.File == "/usr/bin/sips"));
        Assert.Contains(_calls, c => c.File == "/usr/bin/codesign" && c.Args.Contains("--force"));
        var plist = File.ReadAllText(Path.Combine(_dir, "PhoneBox.app", "Contents", "Info.plist"));
        Assert.Contains(NotifierBundle.BundleId, plist);
        Assert.Contains("<string>PhoneBox</string>", plist);
    }

    [Fact]
    public async Task TheFallbackNotifierUsesTheOtherOneWhenTheFirstSaysNoOrThrows()
    {
        var calls = new List<string>();
        Func<bool, bool, IMacNotifier> make = (first, throws) => new FallbackNotifier(new Stub("primary", first, throws, calls), new Stub("fallback", true, false, calls));
        Assert.True(await make(true, false).PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(["primary"], calls); calls.Clear();
        Assert.True(await make(false, false).PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(["primary", "fallback"], calls); calls.Clear();
        Assert.True(await make(false, true).PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(["primary", "fallback"], calls);
    }

    private sealed class Stub(string name, bool result, bool throws, List<string> calls) : IMacNotifier
    {
        public Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
        {
            calls.Add(name);
            if (throws) throw new InvalidOperationException();
            return Task.FromResult(result);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}

/// <summary>The real thing, on a Mac with Xcode or the command line tools: the Swift program compiles, is signed, and refuses bad input. Nothing is shown and no permission is asked.</summary>
public sealed class MacAppNotifierRealBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-notifier-real-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task TheSwiftProgramBuildsSignsAndRefusesABadRequest()
    {
        if (!OperatingSystem.IsMacOS() || MacAppNotifier.FindSwiftc() is null) return; // nothing to build with on this machine
        Directory.CreateDirectory(_dir);
        var notifier = MacAppNotifier.Create(_dir, NullLogger.Instance);
        var inputs = MacAppNotifier.EmbeddedInputs()!.Value;
        Assert.True(await notifier.BuildAsync(inputs.Source, inputs.Icon, NotifierBundle.Version(inputs.Source, inputs.Icon), CancellationToken.None));
        Assert.True(File.Exists(notifier.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(_dir, "PhoneBox.app", "Contents", "Resources", "PhoneBox.icns")));
        Assert.Equal("0", (Sh.Run("/usr/bin/codesign", "-v", Path.Combine(_dir, "PhoneBox.app")) is not null ? "0" : "1"));
        // a request that is not JSON ends the program with 2 before it asks macOS for anything
        var psi = new System.Diagnostics.ProcessStartInfo(notifier.ExecutablePath);
        var result = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(20), CancellationToken.None, "this is not json");
        Assert.Equal(2, result.ExitCode);
    }
}

/// <summary>The files the notifier is made of.</summary>
public sealed class NotifierBundleTests
{
    private static byte[] AgentIco()
    {
        using var s = typeof(NotificationBridge).Assembly.GetManifestResourceStream("phonebox.ico")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    [Fact]
    public void TheAgentsOwnIconHasA256PixelPng()
    {
        var png = NotifierBundle.LargestPng(AgentIco());
        Assert.NotNull(png);
        Assert.True(NotificationIcon.IsSmallPng(png!) || png!.Length > NotificationIcon.MaxBytes); // a real PNG either way
        Assert.Equal(0x89, png![0]);
        Assert.Equal(256, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
    }

    [Fact]
    public void NotAnIconGivesNothing()
    {
        Assert.Null(NotifierBundle.LargestPng([1, 2, 3]));
        Assert.Null(NotifierBundle.LargestPng(new byte[100]));
        Assert.Null(NotifierBundle.LargestPng([]));
    }

    [Fact]
    public void ACutShortIconFileGivesNothingInsteadOfThrowing()
    {
        var ico = AgentIco();
        Assert.Null(NotifierBundle.LargestPng(ico[..40]));
    }

    [Fact]
    public void TheVersionChangesWithTheSourceAndWithTheIcon()
    {
        var a = NotifierBundle.Version("src", [1]);
        Assert.NotEqual(a, NotifierBundle.Version("src2", [1]));
        Assert.NotEqual(a, NotifierBundle.Version("src", [2]));
        Assert.Equal(a, NotifierBundle.Version("src", [1]));
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void TheSwiftCompilerIsLookedForOnlyUnderTheDeveloperFolder()
    {
        Assert.Null(NotifierBundle.FindSwiftc(() => null, _ => true));
        Assert.Null(NotifierBundle.FindSwiftc(() => "", _ => true));
        Assert.Null(NotifierBundle.FindSwiftc(() => "/Library/Developer/CommandLineTools", _ => false));
        Assert.Equal("/Library/Developer/CommandLineTools/usr/bin/swiftc",
            NotifierBundle.FindSwiftc(() => "/Library/Developer/CommandLineTools", _ => true)?.Replace('\\', '/'));
        Assert.Equal("/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/swiftc",
            NotifierBundle.FindSwiftc(() => "/Applications/Xcode.app/Contents/Developer", p => p.Contains("XcodeDefault"))?.Replace('\\', '/'));
    }

    [Fact]
    public void TheRequestKeepsAwkwardTextIntact()
    {
        var text = "quote \" backslash \\ newline\n tab\t Καλημέρα 🌍 </script> -e";
        using var doc = JsonDocument.Parse(NotifierBundle.RequestJson(text, text, text, null));
        Assert.Equal(text, doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(text, doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void TheEmbeddedSwiftProgramHasTheTwoModesAndTheExitCodesTheAgentReads()
    {
        using var s = typeof(NotificationBridge).Assembly.GetManifestResourceStream("notifier.swift")!;
        var source = new StreamReader(s).ReadToEnd();
        Assert.Contains("--authorize", source);
        Assert.Contains("--status", source);
        Assert.Contains("code = granted ? 0 : 3", source);       // refused is 3
        Assert.Contains("var code: Int32 = 5", source);          // no answer in time is 5, never 0
        Assert.Contains("code = 4", source);                     // macOS refusing the notification is 4
        Assert.Contains("finish(2,", source);                    // a bad request is 2
        Assert.DoesNotContain("requestAuthorization", source[source.IndexOf("getNotificationSettings", StringComparison.Ordinal)..]); // showing never asks
        Assert.Contains("UNUserNotificationCenter", source);
        Assert.DoesNotContain("Process(", source);      // the program runs nothing else
    }
}
