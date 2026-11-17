using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Sends key combinations and text to the computer for shortcut keys and macros: "ctrl+shift+m", "win+d", "f13",
/// "playpause"; several combos separated by commas are pressed one after another.
///  - Windows: SendInput.
///  - Linux: xdotool (X11) or wtype (Wayland).
///  - macOS: System Events via osascript (needs the Accessibility permission for the agent).
/// </summary>
public static class KeySender
{
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

    private static string Normalize(string k) => k.ToLowerInvariant() switch
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
    };

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
        var vks = combo.Select(WinVk).ToList();
        var inputs = new List<INPUT>();
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        foreach (var vk in Enumerable.Reverse(vks)) inputs.Add(Key(vk, true));
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

    private static INPUT Key(byte vk, bool up)
    {
        // the scan code too: raw-input apps tell keys apart (and left from right) by it, and 0 is no key at all.
        // MAPVK_VK_TO_VSC_EX marks the media keys with E0; the arrows and the Home/End block share their codes with
        // the keypad, so they still come from the list above.
        var sc = MapVirtualKeyW(vk, 4);
        var extended = Extended(vk) || (sc >> 8) is 0xE0 or 0xE1;
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
#else
    /// <summary>Presses one parsed combo; false when xdotool or wtype failed (macOS throws when it refuses).</summary>
    public static bool SendCombo(List<string> combo) => Send(combo);

    private static bool Send(List<string> combo)
    {
        if (OperatingSystem.IsMacOS()) { MacKeystroke(combo); return true; }
        // X11: xdotool; Wayland: wtype
        var x = string.Join("+", combo.Select(k => k switch
        {
            "win" => "super", "ctrl" or "shift" or "alt" => k,
            _ when Special.TryGetValue(k, out var s) => s.X,
            _ when k[0] == 'f' && k.Length > 1 => k.ToUpperInvariant(),
            _ => k,
        }));
        if (Sh.Which("xdotool") is not null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) return Sh.Run("xdotool", "key", "--clearmodifiers", x) is not null;
        if (Sh.Which("wtype") is not null)
        {
            var args = new List<string>();
            var mods = combo.Where(Modifiers.Contains).Select(m => m == "win" ? "logo" : m).ToList();
            foreach (var m in mods) args.AddRange(["-M", m]);
            var key = combo.FirstOrDefault(k => !Modifiers.Contains(k));
            if (key is not null) args.AddRange(["-k", Special.TryGetValue(key, out var s) ? s.X : key]);
            foreach (var m in Enumerable.Reverse(mods)) args.AddRange(["-m", m]);
            return Sh.Run("wtype", [.. args]) is not null;
        }
        if (Sh.Which("xdotool") is not null) return Sh.Run("xdotool", "key", "--clearmodifiers", x) is not null;
        throw new InvalidOperationException("sending keys needs xdotool (X11) or wtype (Wayland)");
    }

    public static void TypeText(string text) => TryTypeText(text, CancellationToken.None);

    /// <summary>
    /// Types the text; false when the tool failed (macOS throws when it refuses). The tool gets time for the whole text
    /// (xdotool types a character every 8 ms) and is killed when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static bool TryTypeText(string text, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(3000 + 20L * text.Length);
        bool Run(string tool, params string[] args)
        {
            var path = Sh.Which(tool);
            if (path is null) return false;
            var psi = new System.Diagnostics.ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
            try { return ToolProcess.RunAsync(psi, timeout, ct).GetAwaiter().GetResult().ExitCode == 0; }
            catch (TimeoutException) { return false; }
        }
        if (OperatingSystem.IsMacOS())
        {
            if (!Run("osascript", "-e", $"tell application \"System Events\" to keystroke \"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""))
                throw new InvalidOperationException("macOS refused: allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility");
            return true;
        }
        if (Sh.Which("xdotool") is not null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) return Run("xdotool", "type", "--delay", "8", "--", text);
        if (Sh.Which("wtype") is not null) return Run("wtype", "--", text);
        if (Sh.Which("xdotool") is not null) return Run("xdotool", "type", "--delay", "8", "--", text);
        throw new InvalidOperationException("typing needs xdotool (X11) or wtype (Wayland)");
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

    private static int MacF(int n) => n switch
    {
        1 => 122, 2 => 120, 3 => 99, 4 => 118, 5 => 96, 6 => 97, 7 => 98, 8 => 100, 9 => 101, 10 => 109, 11 => 103, 12 => 111,
        13 => 105, 14 => 107, 15 => 113, 16 => 106, 17 => 64, 18 => 79, 19 => 80, 20 => 90, _ => throw new InvalidOperationException($"F{n} cannot be sent on macOS"),
    };

    private static void Osa(string script)
    {
        if (Sh.RunWithTimeout(5000, "osascript", "-e", script) is null)
            throw new InvalidOperationException("macOS refused: allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility");
    }
#endif
}
