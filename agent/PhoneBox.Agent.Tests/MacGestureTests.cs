using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>What the phone's multi-finger gestures do on a Mac, which its own Trackpad settings decide.</summary>
public class MacGestureTests
{
    private static MacGestures.Action Resolve(string gesture, Dictionary<string, int>? trackpad = null, Dictionary<string, bool>? dock = null) =>
        MacGestures.Resolve(gesture,
            key => trackpad is not null && trackpad.TryGetValue(key, out var v) ? v : null,
            key => dock is not null && dock.TryGetValue(key, out var v) ? v : null);

    [Theory]
    [InlineData("swipe3up")]
    [InlineData("swipe4left")]
    [InlineData("spread")]
    [InlineData("pinch")]
    public void TheNamesThePhoneSendsAreGestures(string name) => Assert.True(MacGestures.IsGesture(name));

    [Theory]
    [InlineData("swipe5up")]
    [InlineData("swipe2up")]
    [InlineData("swipe3diagonal")]
    [InlineData("click")]
    [InlineData("scroll")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherNamesAreNot(string? name) => Assert.False(MacGestures.IsGesture(name));

    [Fact]
    public void ThreeFingersSwipeUpIsMissionControlWhenTheMacIsSetToThreeFingers()
    {
        var threeFingers = new Dictionary<string, int> { ["TrackpadThreeFingerVertSwipeGesture"] = 2, ["TrackpadFourFingerVertSwipeGesture"] = 0 };
        Assert.Equal(MacGestures.Action.MissionControl, Resolve("swipe3up", threeFingers));
        Assert.Equal(MacGestures.Action.AppExpose, Resolve("swipe3down", threeFingers));
        // the same swipe with four fingers does nothing: that is how a real trackpad set this way behaves
        Assert.Equal(MacGestures.Action.None, Resolve("swipe4up", threeFingers));
    }

    [Fact]
    public void FourFingersIsTheDefaultForAMacThatNeverSavedTheSettings()
    {
        Assert.Equal(MacGestures.Action.MissionControl, Resolve("swipe4up"));
        Assert.Equal(MacGestures.Action.AppExpose, Resolve("swipe4down"));
        Assert.Equal(MacGestures.Action.None, Resolve("swipe3up"));
    }

    [Fact]
    public void ASwitchedOffGestureDoesNothing()
    {
        var off = new Dictionary<string, int> { ["TrackpadThreeFingerVertSwipeGesture"] = 0 };
        Assert.Equal(MacGestures.Action.None, Resolve("swipe3up", off));
        var dockOff = new Dictionary<string, bool> { ["showMissionControlGestureEnabled"] = false };
        var on = new Dictionary<string, int> { ["TrackpadThreeFingerVertSwipeGesture"] = 2 };
        Assert.Equal(MacGestures.Action.None, Resolve("swipe3up", on, dockOff));
        // App Exposé is its own switch in the Dock
        Assert.Equal(MacGestures.Action.AppExpose, Resolve("swipe3down", on, dockOff));
    }

    [Fact]
    public void SwipingSideToSideFollowsTheFingersBetweenSpaces()
    {
        var spaces = new Dictionary<string, int> { ["TrackpadThreeFingerHorizSwipeGesture"] = 2 };
        Assert.Equal(MacGestures.Action.SpaceRight, Resolve("swipe3left", spaces));
        Assert.Equal(MacGestures.Action.SpaceLeft, Resolve("swipe3right", spaces));
        Assert.Equal(MacGestures.Action.SpaceRight, Resolve("swipe4left"));
    }

    [Fact]
    public void ThreeFingersCanSwipeBetweenPagesInstead()
    {
        var pages = new Dictionary<string, int> { ["TrackpadThreeFingerHorizSwipeGesture"] = 1 };
        Assert.Equal(MacGestures.Action.Back, Resolve("swipe3right", pages));
        Assert.Equal(MacGestures.Action.Forward, Resolve("swipe3left", pages));
        Assert.Equal(MacGestures.Action.None, Resolve("swipe3left"));
    }

    // as `defaults read com.apple.symbolichotkeys AppleSymbolicHotKeys` prints them: 79 as it came from the factory, 80 off,
    // 81 changed to Option+Command+Right
    private const string Hotkeys = """
        {
            79 =     {
                enabled = 1;
            };
            80 =     {
                enabled = 0;
                value =         {
                    parameters =             (
                        65535,
                        123,
                        8781824
                    );
                    type = standard;
                };
            };
            81 =     {
                enabled = 1;
                value =         {
                    parameters =             (
                        65535,
                        124,
                        1572864
                    );
                    type = standard;
                };
            };
        }
        """;

    [Fact]
    public void TheSpaceShortcutsAreReadAsTheMacKeepsThem()
    {
        var shortcuts = MacGestures.ParseShortcuts(Hotkeys);
        Assert.Equal(new MacGestures.Shortcut(true, null, 0), shortcuts[79]);
        Assert.Equal(new MacGestures.Shortcut(false, 123, 8781824), shortcuts[80]);
        Assert.Equal(new MacGestures.Shortcut(true, 124, 1572864), shortcuts[81]);
    }

    [Fact]
    public void MovingBetweenSpacesPressesTheShortcutTheUserSet()
    {
        var shortcuts = MacGestures.ParseShortcuts(Hotkeys);
        // never changed: Ctrl+Left, as it comes from the factory
        Assert.Equal(new MacGestures.Shortcut(true, 123, 0x40000), MacGestures.SpaceShortcut(shortcuts, 79));
        // changed to Option+Command+Right
        Assert.Equal(new MacGestures.Shortcut(true, 124, 1572864), MacGestures.SpaceShortcut(shortcuts, 81));
        // a Mac that has not written the shortcuts down
        Assert.Equal(new MacGestures.Shortcut(true, 124, 0x40000), MacGestures.SpaceShortcut(new Dictionary<int, MacGestures.Shortcut>(), 81));
    }

    [Fact]
    public void KeyCodeZeroIsTheAKeyNotAShortcutThatWasNeverChanged()
    {
        // the A key (code 0) with Control+Command: a shortcut the user set, not the factory one
        var text = "{ 79 = { enabled = 1; value = { parameters = ( 65535, 0, 1310720 ); type = standard; }; }; 81 = { enabled = 1; }; }";
        var shortcuts = MacGestures.ParseShortcuts(text);
        Assert.Equal(new MacGestures.Shortcut(true, 0, 1310720), shortcuts[79]);
        Assert.Equal(new MacGestures.Shortcut(true, 0, 1310720), MacGestures.SpaceShortcut(shortcuts, 79));
        Assert.Null(shortcuts[81].KeyCode);
        Assert.Equal(124, MacGestures.SpaceShortcut(shortcuts, 81).KeyCode);
    }

    [Fact]
    public void ASettingsReadThatFailsKeepsWhatThatSourceSaidBefore()
    {
        // as `defaults read` prints them: one setting a line
        const string builtIn = "{\n    TrackpadThreeFingerVertSwipeGesture = 0;\n    TrackpadFourFingerVertSwipeGesture = 2;\n}";
        const string magic = "{\n    TrackpadThreeFingerVertSwipeGesture = 2;\n    TrackpadFourFingerHorizSwipeGesture = 2;\n}";
        var failing = new HashSet<string>();
        string? Defaults(string[] args) => failing.Contains(args[1]) ? null : args[1] switch
        {
            "com.apple.AppleMultitouchTrackpad" => builtIn,
            "com.apple.driver.AppleBluetoothMultitouch.trackpad" => magic,
            "com.apple.dock" => "{\n    showMissionControlGestureEnabled = 0;\n}",
            _ => "{ 79 = { enabled = 0; }; }",
        };
        var cache = new MacGestures.Preferences();
        cache.Read(Defaults);
        // the built-in trackpad wins where both have the key
        Assert.Equal(0, cache.Trackpad["TrackpadThreeFingerVertSwipeGesture"]);
        Assert.Equal(2, cache.Trackpad["TrackpadFourFingerHorizSwipeGesture"]);

        // the built-in trackpad cannot be read this time: its switched-off gesture stays off, the Magic Trackpad is still read
        failing.Add("com.apple.AppleMultitouchTrackpad");
        cache.Read(Defaults);
        Assert.Equal(0, cache.Trackpad["TrackpadThreeFingerVertSwipeGesture"]);
        Assert.Equal(2, cache.Trackpad["TrackpadFourFingerVertSwipeGesture"]);

        // nothing can be read: everything stays as it was, the Dock switch and the shortcut included
        failing.UnionWith(["com.apple.driver.AppleBluetoothMultitouch.trackpad", "com.apple.dock", "com.apple.symbolichotkeys"]);
        cache.Read(Defaults);
        Assert.Equal(0, cache.Trackpad["TrackpadThreeFingerVertSwipeGesture"]);
        Assert.Equal(0, cache.Dock["showMissionControlGestureEnabled"]);
        Assert.False(cache.Hotkeys[79].On);
    }

    [Fact]
    public void ASwitchedOffSpaceShortcutStaysOff()
    {
        var off = MacGestures.ParseShortcuts("{ 79 = { enabled = 0; }; }");
        Assert.False(MacGestures.SpaceShortcut(off, 79).On);
        Assert.Equal(123, MacGestures.SpaceShortcut(off, 79).KeyCode);
    }

    [Fact]
    public void ModifierFlagsAreNamedForSystemEvents()
    {
        Assert.Equal(["control down"], MacGestures.ModifierNames(0x40000));
        Assert.Equal(["option down", "command down"], MacGestures.ModifierNames(1572864));
        // the fn flag that arrow keys carry is not a modifier to press
        Assert.Equal(["control down"], MacGestures.ModifierNames(8650752));
        Assert.Empty(MacGestures.ModifierNames(0));
    }

    [Fact]
    public void SpreadShowsTheDesktopAndPinchOpensTheApps()
    {
        Assert.Equal(MacGestures.Action.ShowDesktop, Resolve("spread"));
        Assert.Equal(MacGestures.Action.Apps, Resolve("pinch"));
        var fiveOnly = new Dictionary<string, int> { ["TrackpadFourFingerPinchGesture"] = 0, ["TrackpadFiveFingerPinchGesture"] = 2 };
        Assert.Equal(MacGestures.Action.ShowDesktop, Resolve("spread", fiveOnly));
        var off = new Dictionary<string, int> { ["TrackpadFourFingerPinchGesture"] = 0, ["TrackpadFiveFingerPinchGesture"] = 0 };
        Assert.Equal(MacGestures.Action.None, Resolve("spread", off));
        var noDesktop = new Dictionary<string, bool> { ["showDesktopGestureEnabled"] = false };
        Assert.Equal(MacGestures.Action.None, Resolve("spread", null, noDesktop));
        Assert.Equal(MacGestures.Action.Apps, Resolve("pinch", null, noDesktop));
    }
}
