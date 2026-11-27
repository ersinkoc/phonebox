using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Sends key combinations and text to the computer for shortcut keys and macros: "ctrl+shift+m", "win+d", "f13",
/// "playpause"; several combos separated by commas are pressed one after another.
///  - Windows: SendInput.
///  - Linux: xdotool (X11), or wtype then ydotool (Wayland; GNOME needs ydotool), xdotool only when neither is installed.
///  - macOS: key combinations through System Events via osascript; text and the mouse (Trackpad widget) as Quartz
///    events in-process, see Unix/MacKeyboard.cs and Unix/MacMouse.cs. All need the Accessibility permission.
/// </summary>
public static class KeySender
{
    /// <summary>
    /// What to do when macOS refuses to send keys. Fixed text, so the phone may show it as it is. An entry granted to an
    /// older, differently signed build (ad hoc before 0.9.22) stays in the list, even switched on, but no longer applies:
    /// only removing it and adding the agent again helps (#269).
    /// </summary>
    internal const string AccessibilityHint =
        "macOS refused: allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility. " +
        "If it is already listed, remove it there with − and add it again";

    internal record struct MouseCall(string Action, int Dx, int Dy, int Delta);
    internal static Action<MouseCall>? TestMouseSink;

    private static readonly string[] Modifiers = ["ctrl", "shift", "alt", "win"];

    /// <summary>Splits "Ctrl+Shift+M, Win+D" into combos of normalized key names; throws on unknown keys.</summary>
    public static List<List<string>> Parse(string spec)
    {
        var combos = new List<List<string>>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var keys = part.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).ToList();
            if (keys.Count == 0) continue;
            if (keys.Count(k => !Modifiers.Contains(k)) > 1) throw new ArgumentException($"\"{part}\" has more than one key besides Ctrl / Shift / Alt / Win");
            foreach (var k in keys) if (!Modifiers.Contains(k) && !IsKnown(k)) throw new ArgumentException($"unknown key \"{k}\"");
            combos.Add(keys);
        }
        if (combos.Count == 0) throw new ArgumentException("no keys given");
        return combos;
    }

    private static string Normalize(string k) => Numpad(k.ToLowerInvariant()) ?? k.ToLowerInvariant() switch
    {
        "control" or "ctl" => "ctrl",
        "cmd" or "command" or "super" or "meta" or "windows" or "⌘" => "win",
        "option" or "opt" or "⌥" => "alt",
        "return" => "enter",
        "escape" => "esc",
        "del" => "delete",
        "bksp" => "backspace",
        "pgup" => "pageup",
        "pgdn" or "pagedn" => "pagedown",
        "prtsc" or "print" => "printscreen",
        "play" or "pause" or "play/pause" => "playpause",
        "next" => "nexttrack",
        "prev" or "previous" => "prevtrack",
        "volup" => "volumeup",
        "[" or "openbracket" => "bracketleft",
        "]" or "closebracket" => "bracketright",
        "voldown" => "volumedown",
        var x => x,
    };

    // The keypad, under the names other tools use: numpad3, num3, kp3, KP_3 (xdotool), numpad_3; numpadadd, kpplus,
    // KP_Add... "numpad+" cannot be written, as + joins the keys of a combo (#270).
    private static readonly string[] NumpadPrefixes = ["numpad_", "numpad", "num_", "num", "kp_", "kp"];

    private static string? Numpad(string k)
    {
        var prefix = NumpadPrefixes.FirstOrDefault(k.StartsWith);
        if (prefix is null) return null;
        var rest = k[prefix.Length..];
        var name = rest switch
        {
            [var d] when char.IsAsciiDigit(d) => rest,
            "enter" or "return" => "enter",
            "add" or "plus" => "add",
            "subtract" or "sub" or "minus" => "subtract",
            "multiply" or "mul" or "times" or "*" => "multiply",
            "divide" or "div" or "slash" or "/" => "divide",
            "decimal" or "dec" or "period" or "dot" or "." or "comma" or "separator" => "decimal",
            _ => null,
        };
        return name is null ? null : "numpad" + name;
    }

    private static bool IsKnown(string k) =>
        (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) || Special.ContainsKey(k) || (k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24);

    // name -> (Windows virtual key, xdotool name, macOS key code)
    private static readonly Dictionary<string, (byte Vk, string X, int Mac)> Special = new()
    {
        ["enter"] = (0x0D, "Return", 36), ["tab"] = (0x09, "Tab", 48), ["esc"] = (0x1B, "Escape", 53), ["space"] = (0x20, "space", 49),
        ["backspace"] = (0x08, "BackSpace", 51), ["delete"] = (0x2E, "Delete", 117), ["insert"] = (0x2D, "Insert", 114),
        ["home"] = (0x24, "Home", 115), ["end"] = (0x23, "End", 119), ["pageup"] = (0x21, "Prior", 116), ["pagedown"] = (0x22, "Next", 121),
        ["up"] = (0x26, "Up", 126), ["down"] = (0x28, "Down", 125), ["left"] = (0x25, "Left", 123), ["right"] = (0x27, "Right", 124),
        ["printscreen"] = (0x2C, "Print", 105), ["minus"] = (0xBD, "minus", 27), ["equals"] = (0xBB, "equal", 24),
        ["comma"] = (0xBC, "comma", 43), ["period"] = (0xBE, "period", 47), ["slash"] = (0xBF, "slash", 44), ["semicolon"] = (0xBA, "semicolon", 41),
        ["bracketleft"] = (0xDB, "bracketleft", 33), ["bracketright"] = (0xDD, "bracketright", 30),
        ["playpause"] = (0xB3, "XF86AudioPlay", -1), ["nexttrack"] = (0xB0, "XF86AudioNext", -1), ["prevtrack"] = (0xB1, "XF86AudioPrev", -1),
        ["mute"] = (0xAD, "XF86AudioMute", -1), ["volumeup"] = (0xAF, "XF86AudioRaiseVolume", -1), ["volumedown"] = (0xAE, "XF86AudioLowerVolume", -1),
        // the keypad (#270): its own keys, not the number row, so a shortcut bound to Numpad3 (Discord, games) is matched
        ["numpad0"] = (0x60, "KP_0", 82), ["numpad1"] = (0x61, "KP_1", 83), ["numpad2"] = (0x62, "KP_2", 84), ["numpad3"] = (0x63, "KP_3", 85),
        ["numpad4"] = (0x64, "KP_4", 86), ["numpad5"] = (0x65, "KP_5", 87), ["numpad6"] = (0x66, "KP_6", 88), ["numpad7"] = (0x67, "KP_7", 89),
        ["numpad8"] = (0x68, "KP_8", 91), ["numpad9"] = (0x69, "KP_9", 92),
        ["numpadmultiply"] = (0x6A, "KP_Multiply", 67), ["numpadadd"] = (0x6B, "KP_Add", 69), ["numpadsubtract"] = (0x6D, "KP_Subtract", 78),
        ["numpaddecimal"] = (0x6E, "KP_Decimal", 65), ["numpaddivide"] = (0x6F, "KP_Divide", 75),
        // Windows has no key code of its own for the keypad's Enter: it is VK_RETURN with the extended flag (WinExtended)
        ["numpadenter"] = (0x0D, "KP_Enter", 76),
    };

    /// <summary>
    /// Keys that Windows tells apart from another key only by the "extended" flag: the keypad's Enter (VK_RETURN, like the
    /// main Enter) and its / (whose scan code is the main / key's).
    /// </summary>
    internal static bool WinExtended(string k) => k is "numpadenter" or "numpaddivide";

    public static void SendKeys(string spec)
    {
        var combos = Parse(spec);
        foreach (var combo in combos)
        {
            Send(combo);
            if (combos.Count > 1) Thread.Sleep(60);
        }
    }

    /// <summary>
    /// The Windows virtual-key code for a normalized key name. Modifiers are the left-hand keys (VK_LSHIFT, not the
    /// generic VK_SHIFT): apps that read raw input, Discord among them, could not tell which Shift went down (#98).
    /// </summary>
    internal static byte WinVk(string k) => k switch
    {
        "ctrl" => 0xA2, "shift" => 0xA0, "alt" => 0xA4, "win" => 0x5B,
        _ when Special.TryGetValue(k, out var s) => s.Vk,
        _ when k[0] == 'f' && k.Length > 1 => (byte)(0x70 + int.Parse(k[1..]) - 1),
        _ => (byte)char.ToUpperInvariant(k[0]),
    };

#if WINDOWS
    private static void Send(List<string> combo) => SendCombo(combo);

    /// <summary>
    /// A mouse "move" of zero pixels: nothing on screen changes, but this process becomes the one that received the last
    /// input, which Windows requires before it lets a process bring a window to the front.
    /// </summary>
    internal static bool NudgeInput() =>
        SendInput(1, [new INPUT { type = 0 /* MOUSE */, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 1 /* MOVE, by 0 */ } } }], Marshal.SizeOf<INPUT>()) == 1;

    /// <summary>Presses one parsed combo; false when Windows took none or only part of the input.</summary>
    public static bool SendCombo(List<string> combo)
    {
        var keys = combo.Select(k => (Vk: WinVk(k), Extended: WinExtended(k))).ToList();
        var inputs = new List<INPUT>();
        foreach (var (vk, ext) in keys) inputs.Add(Key(vk, false, ext));
        foreach (var (vk, ext) in Enumerable.Reverse(keys)) inputs.Add(Key(vk, true, ext));
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count;
    }

    public static void TypeText(string text) => TryTypeText(text, CancellationToken.None);

    /// <summary>Types the text; false when Windows took none or only part of the input (SendInput doesn't wait).</summary>
    public static bool TryTypeText(string text, CancellationToken ct)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count;
    }

    // arrows, Home/End, Insert/Delete, PageUp/PageDown and the right-hand keys need the "extended" flag
    private static bool Extended(byte vk) => vk is >= 0x21 and <= 0x2E or 0x5B;

    private static INPUT Key(byte vk, bool up, bool forceExtended = false)
    {
        // the scan code too: raw-input apps tell keys apart (and left from right) by it, and 0 is no key at all.
        // MAPVK_VK_TO_VSC_EX marks the media keys with E0; the arrows and the Home/End block share their codes with
        // the keypad, so they still come from the list above.
        var sc = MapVirtualKeyW(vk, 4);
        var extended = forceExtended || Extended(vk) || (sc >> 8) is 0xE0 or 0xE1;
        return new()
        {
            type = 1,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)(sc & 0xFF), dwFlags = (up ? 2u : 0u) | (extended ? 1u : 0u) } },
        };
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 4u /* UNICODE */ | (up ? 2u : 0u) } },
    };

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x01000;

    private static INPUT MouseInput(int dx, int dy, uint mouseData, uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags } },
    };

    public static void MouseMove(int dx, int dy)
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("move", dx, dy, 0)); return; }
        INPUT[] inputs = [MouseInput(dx, dy, 0, MOUSEEVENTF_MOVE)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseClick()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("click", 0, 0, 0)); return; }
        INPUT[] inputs = [MouseInput(0, 0, 0, MOUSEEVENTF_LEFTDOWN), MouseInput(0, 0, 0, MOUSEEVENTF_LEFTUP)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseRightClick()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("rightClick", 0, 0, 0)); return; }
        INPUT[] inputs = [MouseInput(0, 0, 0, MOUSEEVENTF_RIGHTDOWN), MouseInput(0, 0, 0, MOUSEEVENTF_RIGHTUP)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseScroll(int delta)
    {
        if (delta == 0) return;
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("scroll", 0, 0, delta)); return; }
        INPUT[] inputs = [MouseInput(0, 0, (uint)delta, MOUSEEVENTF_WHEEL)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseHorizontalScroll(int delta)
    {
        if (delta == 0) return;
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("hscroll", 0, 0, delta)); return; }
        INPUT[] inputs = [MouseInput(0, 0, (uint)delta, MOUSEEVENTF_HWHEEL)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseDown()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("mouseDown", 0, 0, 0)); return; }
        INPUT[] inputs = [MouseInput(0, 0, 0, MOUSEEVENTF_LEFTDOWN)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void MouseUp()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("mouseUp", 0, 0, 0)); return; }
        INPUT[] inputs = [MouseInput(0, 0, 0, MOUSEEVENTF_LEFTUP)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
    /// <summary>The Trackpad widget's multi-finger gestures are a macOS feature; elsewhere they are ignored.</summary>
    public static bool MouseGesture(string type, Action<Exception>? onFailure = null)
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall(type, 0, 0, 0)); return true; }
        return false;
    }
#else
    /// <summary>Presses one parsed combo; false when xdotool, wtype or ydotool failed (macOS throws when it refuses).</summary>
    public static bool SendCombo(List<string> combo)
    {
        if (OperatingSystem.IsMacOS()) { MacKeystroke(combo); return true; }
        var x = string.Join("+", combo.Select(k => k switch
        {
            "win" => "super", "ctrl" or "shift" or "alt" => k,
            _ when Special.TryGetValue(k, out var s) => s.X,
            _ when k[0] == 'f' && k.Length > 1 => k.ToUpperInvariant(),
            _ => k,
        }));
        ToolRun Run(string tool, params string[] args) => RunTool(tool, args, TimeSpan.FromSeconds(2), CancellationToken.None).Run;
        if (!OnWayland() && FindTool("xdotool") is not null) return Run("xdotool", "key", "--clearmodifiers", x) == ToolRun.Ok;
        var y = YdotoolCombo(combo);
        return RunOnWayland(() => Run("wtype", WtypeArgs(combo)), y is null ? null : () => Run("ydotool", "key", y),
            () => Run("xdotool", "key", "--clearmodifiers", x), "sending keys",
            "ydotool can't press media keys, F13-F24 or keypad keys");
    }

    private static void Send(List<string> combo)
    {
        if (!SendCombo(combo)) throw new InvalidOperationException("the computer refused the key press");
    }

    internal enum ToolRun { Ok, Failed, TimedOut, Missing }

    // Seams for the tests of which tool runs and in what order
    internal static Func<string, string?> FindTool = Sh.Which;
    internal static Func<bool> OnWayland = () => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null;
    internal static Func<string, string[], TimeSpan, CancellationToken, (ToolRun Run, string Output)> RunTool = RunProcess;

    private static (ToolRun, string) RunProcess(string tool, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        if (FindTool(tool) is not { } path) return (ToolRun.Missing, "");
        var psi = new System.Diagnostics.ProcessStartInfo(path);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            var r = Task.Run(() => ToolProcess.RunAsync(psi, timeout, ct)).GetAwaiter().GetResult();
            return (r.ExitCode == 0 ? ToolRun.Ok : ToolRun.Failed, r.Output + r.Error);
        }
        catch (TimeoutException) { return (ToolRun.TimedOut, ""); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return (ToolRun.Failed, ""); }
    }

    /// <summary>
    /// Wayland (or X11 without xdotool): wtype (wlroots, KDE), then ydotool (GNOME, which has neither wtype's protocol
    /// nor a way for xdotool in). A tool that timed out may have typed already, so nothing runs after it. xdotool runs
    /// only when neither wtype nor ydotool is installed: on GNOME it exits 0 having typed nothing.
    /// <paramref name="ydotool"/> is null when ydotool can't do this input, <paramref name="ydotoolCannot"/> says why.
    /// </summary>
    private static bool RunOnWayland(Func<ToolRun> wtype, Func<ToolRun>? ydotool, Func<ToolRun> xdotool, string what, string ydotoolCannot)
    {
        var hasWtype = FindTool("wtype") is not null;
        var hasYdotool = FindTool("ydotool") is not null;
        if (hasWtype)
        {
            var w = wtype();
            if (w == ToolRun.Ok) return true;
            if (w == ToolRun.TimedOut || !hasYdotool) return false;
        }
        if (hasYdotool)
        {
            if (!YdotoolUsable()) throw new InvalidOperationException(YdotoolUnusable);
            if (ydotool is null) throw new InvalidOperationException(ydotoolCannot);
            return ydotool() == ToolRun.Ok;
        }
        if (FindTool("xdotool") is not null) return xdotool() == ToolRun.Ok;
        throw new InvalidOperationException(what + " needs xdotool (X11), or wtype or ydotool (Wayland)");
    }

    private const string YdotoolUnusable = "ydotool doesn't work here: PhoneBox uses ydotool 0.1.x, with write access to /dev/uinput";

    // ydotool sends key positions of a US keyboard: on other layouts some keys differ (AZERTY a/q; UK @ " # ~ \ |).
    // Keys are not mapped through the active layout; letters and digits match on QWERTY layouts such as US and UK.
    // shortcut: only ydotool 0.1.x (Ubuntu 24.04, tested) is used. 1.x takes numeric key codes and reads escapes in
    // text, so it is skipped until someone can test it; its "type --help" is the one that lists --escape.
    internal static bool YdotoolOk;

    private static bool YdotoolUsable()
    {
        if (YdotoolOk) return true;
        var (_, help) = RunTool("ydotool", ["type", "--help"], TimeSpan.FromSeconds(3), CancellationToken.None);
        // only a yes is kept: right after login /dev/uinput may not be ready yet
        return YdotoolOk = help.Contains("--key-delay") && !help.Contains("--escape");
    }

    /// <summary>ydotool 0.1.x types only these: it stops at any other character and still exits 0.</summary>
    internal static bool YdotoolCanType(string text) => text.All(c => c is '\n' or '\t' or (>= ' ' and <= '~'));

    private static string[] WtypeArgs(List<string> combo)
    {
        var args = new List<string>();
        var mods = combo.Where(Modifiers.Contains).Select(m => m == "win" ? "logo" : m).ToList();
        foreach (var m in mods) args.AddRange(["-M", m]);
        var key = combo.FirstOrDefault(k => !Modifiers.Contains(k));
        if (key is not null) args.AddRange(["-k", Special.TryGetValue(key, out var s) ? s.X : key]);
        foreach (var m in Enumerable.Reverse(mods)) args.AddRange(["-m", m]);
        return [.. args];
    }

    /// <summary>
    /// A combo in ydotool 0.1.x's key names ("ctrl+c"), or null when it has no name for one of the keys. ydotool 0.1.x
    /// reads a name it does not know as its first letter and exits 0, so a wrong name would press the wrong key.
    /// </summary>
    internal static string? YdotoolCombo(List<string> combo)
    {
        var names = combo.Select(k => k switch
        {
            "ctrl" or "shift" or "alt" => k, "win" => "super",
            "enter" or "tab" or "esc" or "backspace" or "delete" or "insert" or "home" or "end" or "pageup" or "pagedown"
                or "up" or "down" or "left" or "right" => k,
            "printscreen" => "sysrq",
            "space" => " ", "minus" => "-", "equals" => "=", "comma" => ",", "period" => ".", "slash" => "/", "semicolon" => ";",
            "bracketleft" => "[", "bracketright" => "]",
            _ when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]) => k,
            _ when k[0] == 'f' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 12 => k,
            _ => null, // media keys, F13-F24 and the keypad have no name in 0.1.x
        }).ToList();
        return names.Contains(null) ? null : string.Join("+", names);
    }

    public static void TypeText(string text)
    {
        if (!TryTypeText(text, CancellationToken.None)) throw new InvalidOperationException("the computer refused the typing");
    }

    /// <summary>
    /// Types the text; false when the tool failed or <paramref name="ct"/> stopped it (macOS throws when it refuses). The
    /// tool gets time for the whole text (xdotool types a character every 8 ms, ydotool 0.1.x every 12 ms) and is killed
    /// when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static bool TryTypeText(string text, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(3000 + 20L * text.Length);
        ToolRun Run(string tool, params string[] args) => RunTool(tool, args, timeout, ct).Run;
        // Quartz events carrying the text itself: independent of the keyboard layout, and no helper process (#706)
        if (OperatingSystem.IsMacOS()) return MacKeyboard.TypeText(text, ct);
        if (!OnWayland() && FindTool("xdotool") is not null) return Run("xdotool", "type", "--delay", "8", "--", text) == ToolRun.Ok;
        var lf = text.Replace("\r\n", "\n"); // clipboard text can have Windows line endings, and ydotool can't type \r
        return RunOnWayland(() => Run("wtype", "--", text), YdotoolCanType(lf) ? () => Run("ydotool", "type", "--", lf) : null,
            () => Run("xdotool", "type", "--delay", "8", "--", text), "typing",
            "ydotool types only ASCII: letters, digits, punctuation, tab and new lines");
    }

    private static void MacKeystroke(List<string> combo)
    {
        var mods = combo.Where(Modifiers.Contains).Select(m => m switch { "win" => "command down", "ctrl" => "control down", "alt" => "option down", _ => "shift down" }).ToList();
        var key = combo.FirstOrDefault(k => !Modifiers.Contains(k));
        if (key is not null && Special.TryGetValue(key, out var s) && s.Mac < 0)
        {
            // media keys have no key code in System Events; use the media remote instead where we can
            if (key == "playpause") { Sh.Run("osascript", "-e", "tell application \"Music\" to playpause"); return; }
            throw new InvalidOperationException($"{key} cannot be sent on macOS");
        }
        var using_ = mods.Count > 0 ? $" using {{{string.Join(", ", mods)}}}" : "";
        string action;
        if (key is null) throw new InvalidOperationException("only modifier keys given");
        else if (Special.TryGetValue(key, out var sp)) action = $"key code {sp.Mac}";
        else if (key[0] == 'f' && key.Length > 1) action = $"key code {MacF(int.Parse(key[1..]))}";
        else action = $"keystroke \"{key}\"";
        Osa($"tell application \"System Events\" to {action}{using_}");
    }

    /// <summary>Presses a key by its macOS key code, with modifiers named the way System Events does ("control down").</summary>
    internal static void MacKeyCode(int code, IReadOnlyList<string> modifiers)
    {
        var using_ = modifiers.Count > 0 ? $" using {{{string.Join(", ", modifiers)}}}" : "";
        Osa($"tell application \"System Events\" to key code {code}{using_}");
    }

    private static int MacF(int n) => n switch
    {
        1 => 122, 2 => 120, 3 => 99, 4 => 118, 5 => 96, 6 => 97, 7 => 98, 8 => 100, 9 => 101, 10 => 109, 11 => 103, 12 => 111,
        13 => 105, 14 => 107, 15 => 113, 16 => 106, 17 => 64, 18 => 79, 19 => 80, 20 => 90, _ => throw new InvalidOperationException($"F{n} cannot be sent on macOS"),
    };

    private static void Osa(string script)
    {
        if (Sh.RunWithTimeout(5000, "osascript", "-e", script) is null)
            throw new InvalidOperationException(AccessibilityHint);
    }

    private static readonly object UnixMoveLock = new();
    private static readonly object UnixSendLock = new();
    private static int _unixPendingDx;
    private static int _unixPendingDy;
    private static bool _unixMoveScheduled;

    private static void FlushUnixMove()
    {
        int dx, dy;
        lock (UnixMoveLock)
        {
            dx = _unixPendingDx;
            dy = _unixPendingDy;
            _unixPendingDx = 0;
            _unixPendingDy = 0;
        }
        if (dx != 0 || dy != 0)
            UnixMouse("mousemove_relative", "--", dx.ToString(System.Globalization.CultureInfo.InvariantCulture), dy.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void UnixAction(params string[] args)
    {
        lock (UnixSendLock)
        {
            FlushUnixMove();
            UnixMouse(args);
        }
    }

    private static void UnixMouse(params string[] args)
    {
        var xdotool = Sh.Which("xdotool");
        if (xdotool is null)
            throw new InvalidOperationException("controlling the mouse needs xdotool (X11)");
        var psi = new System.Diagnostics.ProcessStartInfo(xdotool);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("xdotool failed: " + ex.Message, ex);
        }
    }

    public static void MouseMove(int dx, int dy)
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("move", dx, dy, 0)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.Move(dx, dy); return; }
        lock (UnixMoveLock)
        {
            _unixPendingDx += dx;
            _unixPendingDy += dy;
            if (_unixMoveScheduled) return;
            _unixMoveScheduled = true;
        }
        Task.Run(async () =>
        {
            await Task.Delay(16);
            lock (UnixSendLock)
            {
                lock (UnixMoveLock) { _unixMoveScheduled = false; }
                FlushUnixMove();
            }
        });
    }

    public static void MouseClick()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("click", 0, 0, 0)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.Click(); return; }
        UnixAction("click", "1");
    }

    public static void MouseRightClick()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("rightClick", 0, 0, 0)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.RightClick(); return; }
        UnixAction("click", "3");
    }

    private static int _unixVScrollAcc;
    private static int _unixHScrollAcc;

    public static void MouseScroll(int delta)
    {
        if (delta == 0) return;
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("scroll", 0, 0, delta)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.Scroll(delta); return; }
        _unixVScrollAcc += delta;
        var n = Math.Abs(_unixVScrollAcc) / 120;
        if (n == 0) return;
        var dir = _unixVScrollAcc > 0 ? "4" : "5";
        _unixVScrollAcc -= Math.Sign(_unixVScrollAcc) * n * 120;
        UnixAction("click", "--repeat", n.ToString(System.Globalization.CultureInfo.InvariantCulture), dir);
    }

    public static void MouseHorizontalScroll(int delta)
    {
        if (delta == 0) return;
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("hscroll", 0, 0, delta)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.HorizontalScroll(delta); return; }
        _unixHScrollAcc += delta;
        var n = Math.Abs(_unixHScrollAcc) / 120;
        if (n == 0) return;
        var dir = _unixHScrollAcc > 0 ? "7" : "6";
        _unixHScrollAcc -= Math.Sign(_unixHScrollAcc) * n * 120;
        UnixAction("click", "--repeat", n.ToString(System.Globalization.CultureInfo.InvariantCulture), dir);
    }

    public static void MouseDown()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("mouseDown", 0, 0, 0)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.Down(); return; }
        UnixAction("mousedown", "1");
    }

    public static void MouseUp()
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall("mouseUp", 0, 0, 0)); return; }
        if (OperatingSystem.IsMacOS()) { MacMouse.Up(); return; }
        UnixAction("mouseup", "1");
    }
    /// <summary>
    /// A three- or four-finger swipe, or a spread or pinch, from the Trackpad widget. Only macOS has these gestures,
    /// and what they do follows the Mac's own Trackpad settings (see Unix/MacGestures.cs). The action starts in the
    /// background: it can take a moment and the phone's next movement should not wait for it.
    /// </summary>
    public static bool MouseGesture(string type, Action<Exception>? onFailure = null)
    {
        if (TestMouseSink is not null) { TestMouseSink(new MouseCall(type, 0, 0, 0)); return true; }
        if (!OperatingSystem.IsMacOS() || !MacGestures.IsGesture(type)) return false;
        // a gesture that macOS refuses (no Accessibility permission, say) is reported to the caller, which has the logger
        Task.Run(() => { try { MacGestures.Run(type); } catch (Exception ex) { onFailure?.Invoke(ex); } });
        return true;
    }

#endif
}
