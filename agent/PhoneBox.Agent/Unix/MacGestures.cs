using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// The Trackpad widget's multi-finger gestures on macOS: three or four fingers swiping, and four or five fingers
/// spreading or pinching. What a gesture does is decided by the Mac's own Trackpad settings, as on a real trackpad:
/// a gesture that is off in System Settings does nothing, and "swipe up" is Mission Control only when Mission Control
/// is set to three (or four) fingers.
/// </summary>
internal static class MacGestures
{
    internal enum Action { None, MissionControl, AppExpose, ShowDesktop, Apps, SpaceLeft, SpaceRight, Back, Forward }

    private static Regex Name => TrackpadHandler.GestureName;

    internal static bool IsGesture(string? type) => type is not null && Name.IsMatch(type);

    /// <summary>
    /// What a gesture does under the given settings. <paramref name="trackpad"/> is a value of the Trackpad settings
    /// (null when the Mac never wrote it) and <paramref name="dock"/> one of the Dock's gesture switches (null: on).
    /// </summary>
    internal static Action Resolve(string type, Func<string, int?> trackpad, Func<string, bool?> dock)
    {
        var m = Name.Match(type);
        if (!m.Success) return Action.None;
        bool On(string key, bool whenMissing) => (trackpad(key) ?? (whenMissing ? 2 : 0)) == 2;
        bool Switch(string key) => dock(key) ?? true;

        if (m.Groups["pinch"].Success)
        {
            // Launchpad (Apps, from macOS 26) and Show Desktop: pinch or spread with the thumb and three fingers
            var enabled = On("TrackpadFourFingerPinchGesture", true) || On("TrackpadFiveFingerPinchGesture", true);
            if (!enabled) return Action.None;
            return m.Groups["pinch"].Value == "spread"
                ? Switch("showDesktopGestureEnabled") ? Action.ShowDesktop : Action.None
                : Switch("showLaunchpadGestureEnabled") ? Action.Apps : Action.None;
        }

        var three = m.Groups["n"].Value == "3";
        switch (m.Groups["dir"].Value)
        {
            case "up":
                return On(three ? "TrackpadThreeFingerVertSwipeGesture" : "TrackpadFourFingerVertSwipeGesture", !three) && Switch("showMissionControlGestureEnabled")
                    ? Action.MissionControl : Action.None;
            case "down":
                return On(three ? "TrackpadThreeFingerVertSwipeGesture" : "TrackpadFourFingerVertSwipeGesture", !three) && Switch("showAppExposeGestureEnabled")
                    ? Action.AppExpose : Action.None;
        }

        // Left and right: between full-screen apps and desktops (2), or, on three fingers, between pages (1).
        // The content follows the fingers, so swiping left goes to the space on the right.
        var left = m.Groups["dir"].Value == "left";
        var horizontal = trackpad(three ? "TrackpadThreeFingerHorizSwipeGesture" : "TrackpadFourFingerHorizSwipeGesture") ?? (three ? 0 : 2);
        return horizontal switch
        {
            2 => left ? Action.SpaceRight : Action.SpaceLeft,
            1 when three => left ? Action.Forward : Action.Back,
            _ => Action.None,
        };
    }

    private static readonly string[] Domains = ["com.apple.AppleMultitouchTrackpad", "com.apple.driver.AppleBluetoothMultitouch.trackpad"];
    // "79 = { enabled = 1; value = { parameters = ( 65535, 123, 8650752 ); type = standard; }; };" (the value is there once it was changed)
    private static readonly Regex Hotkey = new(@"(?<id>\d+)\s*=\s*\{\s*enabled\s*=\s*(?<on>\d)\s*;(?:\s*value\s*=\s*\{\s*parameters\s*=\s*\(\s*-?\d+\s*,\s*(?<key>\d+)\s*,\s*(?<mods>\d+)\s*\))?", RegexOptions.Compiled);
    private static readonly Regex Entry = new(@"^\s*""?(?<key>\w+)""?\s*=\s*(?<value>\d+);", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly object Gate = new();
    private static readonly Preferences Cache = new();
    private static long _readAt;
    private static long _lastRunAt;

    /// <summary>Does what a real trackpad would do for this gesture; false when the Mac's settings give it no action.</summary>
    public static bool Run(string type)
    {
        lock (Gate)
        {
            // one touch makes one gesture; a repeat that follows at once is the same swipe, not a second one
            var now = Environment.TickCount64;
            if (now - _lastRunAt < 350) return false;
            _lastRunAt = now;
            if (_readAt == 0 || now - _readAt >= 2000)
            {
                _readAt = now;
                Cache.Read(args => Sh.Run("defaults", args));
            }
        }
        var action = Resolve(type, key => Cache.Trackpad.TryGetValue(key, out var v) ? v : null, key => Cache.Dock.TryGetValue(key, out var v) ? v != 0 : null);
        // moving between spaces is the keyboard shortcut "Move left / right a space": pressed as the user set it, and not at all when turned off
        if (action is Action.SpaceLeft or Action.SpaceRight)
        {
            var shortcut = SpaceShortcut(Cache.Hotkeys, action == Action.SpaceLeft ? SpaceLeftShortcut : SpaceRightShortcut);
            if (!shortcut.On) return false;
            KeySender.MacKeyCode(shortcut.KeyCode ?? 0, ModifierNames(shortcut.Modifiers));
            return true;
        }
        return Perform(action);
    }

    // ids in com.apple.symbolichotkeys
    private const int SpaceLeftShortcut = 79, SpaceRightShortcut = 81;

    /// <summary>
    /// A keyboard shortcut as the Mac keeps it: on or off, with the key code and modifier flags (NSEvent's) it is bound to.
    /// The key code is null for a shortcut that was never changed (0 is a key: the A key).
    /// </summary>
    internal readonly record struct Shortcut(bool On, int? KeyCode, long Modifiers);

    /// <summary>The shortcuts in <c>defaults read com.apple.symbolichotkeys AppleSymbolicHotKeys</c>.</summary>
    internal static Dictionary<int, Shortcut> ParseShortcuts(string text)
    {
        var shortcuts = new Dictionary<int, Shortcut>();
        foreach (Match m in Hotkey.Matches(text))
            shortcuts[int.Parse(m.Groups["id"].Value)] = new Shortcut(m.Groups["on"].Value != "0",
                m.Groups["key"].Success ? int.Parse(m.Groups["key"].Value) : null, m.Groups["mods"].Success ? long.Parse(m.Groups["mods"].Value) : 0);
        return shortcuts;
    }

    /// <summary>
    /// What "Move left / right a space" is on this Mac: what it was changed to, or Ctrl+Left / Ctrl+Right as it comes from the
    /// factory (a shortcut the Mac has not written down, or one that was never changed).
    /// </summary>
    internal static Shortcut SpaceShortcut(IReadOnlyDictionary<int, Shortcut> shortcuts, int id)
    {
        var factory = new Shortcut(true, id == SpaceLeftShortcut ? 123 : 124, 0x40000);
        if (!shortcuts.TryGetValue(id, out var saved)) return factory;
        return saved.KeyCode is null ? factory with { On = saved.On } : saved;
    }

    /// <summary>The modifier flags of a shortcut as System Events names them; the fn flag arrow keys carry is left out.</summary>
    internal static string[] ModifierNames(long flags) =>
        new[] { (0x40000L, "control down"), (0x20000L, "shift down"), (0x80000L, "option down"), (0x100000L, "command down") }
            .Where(m => (flags & m.Item1) != 0).Select(m => m.Item2).ToArray();

    /// <summary>
    /// What the Mac's settings said the last time they were read. A read that fails or times out keeps what that same source said
    /// before, so a gesture the user turned off does not come back on; the Trackpad settings are kept per domain for that reason.
    /// </summary>
    internal sealed class Preferences
    {
        private readonly Dictionary<string, Dictionary<string, int>> _domains = new();

        public IReadOnlyDictionary<string, int> Trackpad { get; private set; } = new Dictionary<string, int>();
        public IReadOnlyDictionary<string, int> Dock { get; private set; } = new Dictionary<string, int>();
        public IReadOnlyDictionary<int, Shortcut> Hotkeys { get; private set; } = new Dictionary<int, Shortcut>();

        /// <param name="defaults">Runs <c>defaults</c> with the arguments and returns its output, or null when it failed.</param>
        public void Read(Func<string[], string?> defaults)
        {
            foreach (var domain in Domains)
                if (defaults(["read", domain]) is { } text) _domains[domain] = Values(text, _ => true);
            // the built-in trackpad's settings win over a Magic Trackpad's where both have the key
            var merged = new Dictionary<string, int>();
            foreach (var domain in Domains)
                if (_domains.TryGetValue(domain, out var values))
                    foreach (var (key, value) in values) merged.TryAdd(key, value);
            Trackpad = merged;
            if (defaults(["read", "com.apple.dock"]) is { } dock) Dock = Values(dock, key => key.EndsWith("GestureEnabled", StringComparison.Ordinal));
            if (defaults(["read", "com.apple.symbolichotkeys", "AppleSymbolicHotKeys"]) is { } hotkeys) Hotkeys = ParseShortcuts(hotkeys);
        }

        private static Dictionary<string, int> Values(string text, Func<string, bool> wanted)
        {
            var values = new Dictionary<string, int>();
            foreach (Match m in Entry.Matches(text))
                if (wanted(m.Groups["key"].Value)) values[m.Groups["key"].Value] = int.Parse(m.Groups["value"].Value);
            return values;
        }
    }

    private static bool Perform(Action action)
    {
        switch (action)
        {
            case Action.MissionControl: return Open("Mission Control");
            case Action.Apps:
                // macOS 26 replaced Launchpad with Apps
                return Open(File.Exists("/System/Applications/Apps.app/Contents/Info.plist") ? "Apps" : "Launchpad");
            case Action.AppExpose: return DockNotification("com.apple.expose.front.awake") || Keys("ctrl+down");
            case Action.ShowDesktop: return DockNotification("com.apple.showdesktop.awake") || Keys("f11");
            case Action.Back: return Keys("win+[");
            case Action.Forward: return Keys("win+]");
            default: return false;
        }
    }

    private static bool Open(string app) => Sh.Run("open", "-a", app) is not null;

    private static bool Keys(string spec)
    {
        KeySender.SendKeys(spec);
        return true;
    }

    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    // The second argument is a pointer-sized value the Dock ignores for these notifications (0).
    [DllImport(ApplicationServices)] private static extern void CoreDockSendNotification(nint notification, nint unused);

    /// <summary>
    /// The notification the Dock itself listens for when its gesture or shortcut fires. Mission Control and Apps are apps that can
    /// be opened; App Expose and Show Desktop are not, and nothing else runs them without a keyboard shortcut, which the user may
    /// have changed. This is not documented by Apple. It returns true as soon as the notification has been sent, because nothing
    /// says whether the Dock acted on it; the default shortcut is pressed only when the symbol cannot be found (a macOS without
    /// it). If a later macOS keeps the symbol but the Dock stops reacting, App Expose and Show Desktop do nothing.
    /// </summary>
    private static bool DockNotification(string name)
    {
        try
        {
            var text = CFStringCreateWithCString(0, name, 0x08000100);
            if (text == 0) return false;
            try { CoreDockSendNotification(text, 0); }
            finally { CFRelease(text); }
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }
}
