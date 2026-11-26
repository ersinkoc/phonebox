using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Runs the shortcuts defined in config.json. The phone only sends a shortcut id, never a command.
/// A shortcut starts a program, presses keys, types text or runs a macro; toggles remember or read their on / off state.
/// </summary>
public static class Shortcuts
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Remembered = new();

    /// <summary>
    /// Invoked whenever the builtin:lock shortcut runs. The agent hooks this to sleep connected phones
    /// when <see cref="DisplayConfig.LockPhoneWithShortcut"/> is enabled.
    /// </summary>
    public static Action<AgentConfig>? OnLocked { get; set; }

    /// <summary>Locks the computer for builtin:lock. A test replaces it, so running the tests never locks the machine.</summary>
    internal static Action LockComputer { get; set; } = PlatformLock;

    private static void PlatformLock()
    {
#if WINDOWS
        LockWorkStation();
#else
        if (OperatingSystem.IsMacOS()) Unix("pmset", "displaysleepnow");
        else if (Sh.Run("loginctl", "lock-session") is null) Unix("xdg-screensaver", "lock");
#endif
    }

    /// <returns>An error message, or null on success.</returns>
    public static string? Run(AgentConfig cfg, string id, ILogger log, PushStore? push = null, AudioControl? audio = null, bool hold = false)
    {
        var sc = cfg.Shortcuts.FirstOrDefault(s => s.Id == id);
        if (sc is null) return $"no such shortcut: {id}";
        // a long press runs the key's own LongPress target (one hop: that key's own long press is not followed)
        if (hold)
        {
            if (sc.LongPress is null || cfg.Shortcuts.FirstOrDefault(s => s.Id == sc.LongPress) is not { } held) return $"shortcut {id} has no long press";
            sc = held;
        }
        return RunCore(cfg, sc, log, push, audio, 0);
    }

    private static string? RunCore(AgentConfig? cfg, Shortcut sc, ILogger log, PushStore? push, AudioControl? audio, int depth)
    {
        if (!sc.Toggle) return Execute(sc, log, cfg, depth, push, audio);
        var on = State(sc, push, audio) ?? false;
        var target = on && sc.OffCmd is not null ? new Shortcut { Label = sc.Label, Cmd = sc.OffCmd, Args = sc.OffArgs, Cwd = sc.Cwd } : sc;
        var err = Execute(target, log, cfg, depth, push, audio);
        if (err is null && sc.StateKey is null && sc.Cmd is not ("builtin:mute" or PhoneDevices.MicCmd)) Remembered[sc.Id] = !on;
        return err;
    }

    /// <summary>On / off state of the toggle keys, for the phone.</summary>
    public static Dictionary<string, bool>? States(AgentConfig cfg, PushStore push, AudioControl audio)
    {
        Dictionary<string, bool>? map = null;
        foreach (var sc in cfg.Shortcuts.Where(s => s.Toggle || s.Cmd is "builtin:mute" or PhoneDevices.MicCmd))
            if (State(sc, push, audio) is { } on) (map ??= [])[sc.Id] = on;
        return map;
    }

    public static bool? State(Shortcut sc, PushStore? push, AudioControl? audio)
    {
        if (sc.Cmd == "builtin:mute" && sc.StateKey is null) return audio?.Snapshot()?.Muted;
        // the phone mic key is lit while the mic is live and not muted
        if (sc.Cmd == PhoneDevices.MicCmd && sc.StateKey is null) return PhoneDevices.MicOn;
        if (sc.StateKey is not null) return PushIsOn(push, sc.StateKey);
        return Remembered.TryGetValue(sc.Id, out var r) && r;
    }

    // builtin:cycle: where each cycle key stands; lost when the agent restarts, like a toggle's state. A state belongs to the
    // list it was made for: after the list is edited it is dropped, since its positions would point at other keys.
    private sealed class CycleState(string list)
    {
        public readonly string List = list;
        public int Cursor = -1;     // the slot the last tap claimed
        public int? Retry;          // a slot whose key failed: the next tap tries it again
        public int? Shown;          // the slot of the key that last ran successfully
    }
    private static readonly Dictionary<string, CycleState> Cycles = [];
    private static readonly object CycleLock = new();
    private static string ListOf(Shortcut sc) => string.Join('\n', sc.Cycle ?? []);

    /// <summary>
    /// Where each cycle key stands, for the phone: the id of the key that last ran successfully (not one still running or
    /// failing). An id, not a position, so the phone shows the right face even when it has not yet loaded a list that was
    /// edited. Keys never tapped, or never successful, are left out.
    /// </summary>
    public static Dictionary<string, string>? CycleStates(AgentConfig cfg)
    {
        Dictionary<string, string>? map = null;
        lock (CycleLock)
            foreach (var sc in cfg.Shortcuts.Where(s => s.Cmd == "builtin:cycle"))
                if (Cycles.TryGetValue(sc.Id, out var st) && st.List == ListOf(sc) && st.Shown is { } at && at < (sc.Cycle?.Count ?? 0)) (map ??= [])[sc.Id] = sc.Cycle![at];
        return map;
    }

    /// <summary>
    /// A tap on a cycle key: run the next key of its list (the first one after the last). The slot is claimed before the
    /// key runs, so two taps at the same moment (the phone and a rule, say) run two different keys; the lock covers only
    /// the claim and the outcome, never the run, so cycles that call each other on two threads cannot deadlock. A key that
    /// fails is tried again by the next tap. A list with one key runs that key, and an empty one does nothing.
    /// </summary>
    private static string? RunCycle(AgentConfig? cfg, Shortcut sc, ILogger log, PushStore? push, AudioControl? audio, int depth)
    {
        if (depth > 3) return "keys call each other too deeply";
        var ids = sc.Cycle ?? [];
        if (cfg is null || ids.Count == 0) return null;
        var list = ListOf(sc);
        CycleState state;
        int slot;
        lock (CycleLock)
        {
            if (!Cycles.TryGetValue(sc.Id, out state!) || state.List != list) Cycles[sc.Id] = state = new CycleState(list);
            slot = (state.Retry ?? (state.Cursor + 1)) % ids.Count;
            state.Retry = null;
            state.Cursor = slot;
        }
        var target = cfg.Shortcuts.FirstOrDefault(s => s.Id == ids[slot]);
        var err = target is null ? $"no such shortcut: {ids[slot]}" : RunCore(cfg, target, log, push, audio, depth + 1);
        lock (CycleLock)
        {
            // the outcome counts only for the state it was claimed from (not one made after the list was edited)
            if (Cycles.TryGetValue(sc.Id, out var now) && ReferenceEquals(now, state))
            {
                if (err is null) state.Shown = slot;
                else state.Retry ??= slot;
            }
        }
        return err;
    }

    /// <summary>
    /// Whether the value pushed to /api/push/{key} is on: true, a number other than 0, or text other than 0 / false / off / no.
    /// "key=value" asks instead whether the key holds that value (letter case and surrounding spaces aside), so one pushed
    /// value, such as a keyboard layout, can light one of several keys. Push keys cannot contain "=", so a plain key keeps
    /// its meaning.
    /// </summary>
    internal static bool PushIsOn(PushStore? push, string key)
    {
        string? want = null;
        if (key.IndexOf('=') is var eq and >= 0) { want = key[(eq + 1)..].Trim(); key = key[..eq]; }
        if (push?.Snapshot().TryGetValue(key.Trim(), out var item) != true) return false;
        var d = item!.Data;
        if (d.ValueKind == System.Text.Json.JsonValueKind.Object && d.TryGetProperty("value", out var v)) d = v;
        if (want is not null) return PushEquals(d, want);
        return d.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.Number => d.GetDouble() != 0,
            System.Text.Json.JsonValueKind.String => d.GetString() is { Length: > 0 } t && t.ToLowerInvariant() is not ("0" or "false" or "off" or "no"),
            _ => false,
        };
    }

    /// <summary>A pushed text, number or true / false compared with the wanted value: "RU" matches "ru", "2" matches 2.0.</summary>
    private static bool PushEquals(System.Text.Json.JsonElement d, string want) => d.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => string.Equals(d.GetString()?.Trim(), want, StringComparison.OrdinalIgnoreCase),
        System.Text.Json.JsonValueKind.Number => JsonNumber(want) is { } n && System.Text.Json.JsonElement.DeepEquals(d, n),
        System.Text.Json.JsonValueKind.True => want.Equals("true", StringComparison.OrdinalIgnoreCase),
        System.Text.Json.JsonValueKind.False => want.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The wanted value as a JSON number, compared digit for digit (no rounding through double); null if it is none.</summary>
    private static System.Text.Json.JsonElement? JsonNumber(string want)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(want);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Number ? doc.RootElement.Clone() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Tests record what a shortcut would press or type instead of sending it to the computer.</summary>
    internal static Action<string>? TestKeys, TestType;

    private static void PressKeys(string spec) { if (TestKeys is { } t) t(spec); else KeySender.SendKeys(spec); }
    private static void TypeOut(string text) { if (TestType is { } t) t(text); else KeySender.TypeText(text); }

    private const string ClipboardTag = "{clipboard}";
    private const int MaxClipboardChars = 10_000;

    /// <summary>How long a paste: step leaves its text on the clipboard after the paste keys, so the program has read it.</summary>
    internal static int PasteSettleMs = 400;

    /// <summary>
    /// Replaces {clipboard} with the text on the clipboard (at most 10,000 characters, so a copied page is not typed out
    /// for minutes). The clipboard is read only when the text asks for it, and its content never leaves the agent.
    /// </summary>
    internal static string WithClipboard(string text)
    {
        if (!text.Contains(ClipboardTag, StringComparison.Ordinal)) return text;
        var clip = Clipboard.GetText();
        if (clip.Length > MaxClipboardChars)
        {
            // not between the two halves of a character outside the BMP (an emoji): a lone half cannot be typed
            var cut = char.IsHighSurrogate(clip[MaxClipboardChars - 1]) ? MaxClipboardChars - 1 : MaxClipboardChars;
            clip = clip[..cut];
        }
        return text.Replace(ClipboardTag, clip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pastes the text through the clipboard (instant for long text, and right for any character) and puts back what was
    /// on the clipboard, unless something else has been copied in the meantime. The text is kept out of Windows'
    /// clipboard history and cloud clipboard. When the clipboard cannot be read, the pasted text stays on it.
    /// </summary>
    // one paste at a time: two overlapping runs (the phone, a rule, the Control Deck) would each save the other's
    // pasted text as "what was copied" and put the wrong text back
    private static readonly object PasteGate = new();

    private static void PasteThroughClipboard(string text)
    {
        lock (PasteGate)
        {
            string? before;
            try { before = Clipboard.GetText(); } catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { before = null; }
            Clipboard.SetText(text, sensitive: true);
            var pasted = false;
            try
            {
                Thread.Sleep(40);
                PressKeys(OperatingSystem.IsMacOS() ? "win+v" : "ctrl+v");
                pasted = true;
            }
            finally
            {
                // also when the paste keys failed: the pasted text (a token, say) must not stay on the clipboard
                if (before is not null)
                {
                    if (pasted) Thread.Sleep(PasteSettleMs);
                    // the text that was copied comes back; a picture or files read as "" and cannot be put back, so the
                    // clipboard is emptied instead of keeping the pasted text. Something copied in the meantime is left alone.
                    try { if (Clipboard.GetText() == text) Clipboard.SetText(before, sensitive: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
                }
            }
        }
    }

    /// <returns>An error message, or null on success.</returns>
    public static string? Execute(Shortcut sc, ILogger log, AgentConfig? cfg = null, int depth = 0, PushStore? push = null, AudioControl? audio = null)
    {
        try
        {
            switch (sc.Cmd)
            {
                case "builtin:cycle":
                    return RunCycle(cfg, sc, log, push, audio, depth);
                case "builtin:keys":
                    PressKeys(sc.Args ?? "");
                    break;
                case "builtin:text":
                    TypeOut(WithClipboard(sc.Args ?? ""));
                    break;
                case "builtin:macro":
                    if (depth > 3) throw new InvalidOperationException("macros call each other too deeply");
                    MacroFlow.Check(sc.Steps ?? [], cfg);
                    // one entry per open if:, with the state outside it; "active" is whether the steps here run
                    var open = new Stack<(bool Outer, bool Holds)>();
                    var active = true;
                    foreach (var step in sc.Steps ?? [])
                    {
                        switch (MacroFlow.Classify(step, out var condition))
                        {
                            case MacroFlow.Word.If:
                                // a condition inside a block that is skipped is not even looked at
                                var holds = active && MacroFlow.Evaluate(condition, cfg, push, audio);
                                open.Push((active, holds));
                                active = holds;
                                continue;
                            case MacroFlow.Word.Else:
                                var (outerOfElse, heldIf) = open.Peek();
                                active = outerOfElse && !heldIf;
                                continue;
                            case MacroFlow.Word.EndIf:
                                active = open.Pop().Outer;
                                continue;
                        }
                        if (!active) continue;
                        var i = step.IndexOf(':');
                        var (kind, value) = i > 0 ? (step[..i].Trim().ToLowerInvariant(), step[(i + 1)..].Trim()) : ("keys", step.Trim());
                        switch (kind)
                        {
                            case "keys": PressKeys(value); break;
                            case "text": TypeOut(WithClipboard(value)); break;
                            case "clipboard": Clipboard.SetText(WithClipboard(value)); break;
                            case "paste": PasteThroughClipboard(WithClipboard(value)); break;
                            case "wait": Thread.Sleep(Math.Clamp(int.TryParse(value, out var ms) ? ms : 0, 0, 10_000)); break;
                            case "shortcut":
                                var other = cfg?.Shortcuts.FirstOrDefault(s => s.Id == value) ?? throw new InvalidOperationException($"no such shortcut: {value}");
                                if (RunCore(cfg, other, log, push, audio, depth + 1) is { } err) throw new InvalidOperationException(err);
                                break;
                            default: throw new InvalidOperationException($"unknown macro step \"{step}\" (use keys:, text:, clipboard:, paste:, wait:, shortcut: or if: … else … endif)");
                        }
                        Thread.Sleep(40);
                    }
                    break;
                case "builtin:wake":
                    // "packets sent", never "the machine is up"
                    if (WakeOnLan.Wake(sc.Args) is { } wakeError) throw new InvalidOperationException(wakeError);
                    break;
                case PhoneDevices.MicCmd:
                    PhoneDevices.Run(sc.Cmd, sc.Args);
                    break;
                case "builtin:lock":
                    LockComputer();
                    if (cfg is not null) OnLocked?.Invoke(cfg);
                    break;
#if WINDOWS
                case "builtin:monitor-off":
                    MonitorOff();
                    break;
                case "builtin:mute":
                    Keys.Tap(Keys.VK_VOLUME_MUTE);
                    break;
#else
                case "builtin:monitor-off":
                    if (OperatingSystem.IsMacOS()) Unix("pmset", "displaysleepnow");
                    else Unix("xset", "dpms", "force", "off");
                    break;
                case "builtin:mute":
                    // false: the output cannot be muted (macOS) or wpctl / pactl failed; a caller must not count it as done
                    if (!(audio ?? new AudioControl()).ToggleMute()) throw new InvalidOperationException("could not toggle mute on the current output");
                    break;
#endif
                default:
                    Start(sc);
                    break;
            }
            log.LogInformation("shortcut: {Label}", sc.Label);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("shortcut {Label} failed: {Msg}", sc.Label, ex.Message);
            return ex.Message;
        }
    }

    private static void Start(Shortcut sc)
    {
        var psi = new ProcessStartInfo(Home(sc.Cmd), Home(sc.Args ?? "")) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(sc.Cwd)) psi.WorkingDirectory = Home(sc.Cwd);
#if WINDOWS
        LetTheNewWindowComeForward();
        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 /* not found */ && TerminalStandIns(sc.Cmd)
            .Select(t => Path.Combine(Environment.SystemDirectory, t == "powershell.exe" ? @"WindowsPowerShell\v1.0\powershell.exe" : t))
            .FirstOrDefault(File.Exists) is { } alt)
        {
            StartStandIn(alt, sc);
        }
#else
        // a missing program does not throw here: .NET hands the name to xdg-open, which fails on its own
        if (TerminalStandIns(sc.Cmd, Environment.GetEnvironmentVariable("TERMINAL")) is { Length: > 0 } alts && Sh.Which(sc.Cmd) is null
            && alts.Select(Sh.Which).FirstOrDefault(p => p is not null) is { } alt)
        {
            StartStandIn(alt, sc);
            return;
        }
        Process.Start(psi);
#endif
    }

    // A stand-in is a fixed program at a full path on disk: start it directly, without the shell and without arguments.
    // (The agent has no console on Windows, so a console program started this way gets its own window.)
    private static void StartStandIn(string path, Shortcut sc)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(sc.Cwd)) start.WorkingDirectory = Home(sc.Cwd);
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// What to start instead when a default shortcut's program is missing: Windows Terminal (wt.exe) is not part of
    /// Windows 10, x-terminal-emulator only exists on Debian-family Linux (#73), and the default Calculator, Settings and
    /// System Monitor shortcuts name GNOME's apps, which a KDE, Xfce or MATE desktop usually doesn't have (#238). Without
    /// this the missing name goes to xdg-open, which opens it as a web address. Empty for other commands.
    /// </summary>
    internal static string[] TerminalStandIns(string cmd, string? terminalEnv = null) => cmd.Trim().ToLowerInvariant() switch
    {
        "wt.exe" or "wt" => ["powershell.exe", "cmd.exe"],
        "x-terminal-emulator" =>
        [
            .. string.IsNullOrWhiteSpace(terminalEnv) ? [] : new[] { terminalEnv.Trim() },
            "kgx", "gnome-terminal", "konsole", "xfce4-terminal", "mate-terminal", "lxterminal", "tilix", "alacritty", "kitty", "foot", "wezterm", "xterm",
        ],
        "gnome-calculator" => ["kcalc", "mate-calc", "galculator", "qalculate-gtk", "xfce4-calculator"],
        "gnome-control-center" => ["systemsettings", "systemsettings5", "xfce4-settings-manager", "mate-control-center", "cinnamon-settings", "budgie-control-center", "lxqt-config"],
        "gnome-system-monitor" => ["plasma-systemmonitor", "ksysguard", "xfce4-taskmanager", "mate-system-monitor", "lxtask"],
        _ => [],
    };

    /// <summary>"~" at the start of a path means the home folder (Linux / macOS style).</summary>
    private static string Home(string s) =>
        s == "~" || s.StartsWith("~/") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..] : s.Replace(" ~/", " " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/");

#if WINDOWS
    /// <summary>
    /// Windows keeps a program started by a background process (the agent answering the phone) behind the window in use,
    /// flashing in the taskbar (#322). A zero-pixel mouse move makes the agent the process that received the last input,
    /// which lets it pass the right to come forward to any process; the app, or the launcher it starts through, then
    /// takes it when its window opens. The right ends with the user's next input.
    /// </summary>
    private static void LetTheNewWindowComeForward()
    {
        try { if (KeySender.NudgeInput()) AllowSetForegroundWindow(-1 /* ASFW_ANY */); } catch { }
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    /// <summary>
    /// Turns the display off with one WM_SYSCOMMAND / SC_MONITORPOWER, sent to a hidden window of the agent's own.
    /// Broadcasting it (HWND_BROADCAST) put a copy in every top-level window's queue: on a Modern Standby PC the first
    /// copy sent the PC to standby while the others were still queued, and after the wake each window handled its copy
    /// and turned the display off again, on and off for about a minute (#335).
    /// </summary>
    private static void MonitorOff()
    {
        var window = CreateWindowEx(0, "STATIC", "", 0x80000000 /* WS_POPUP, never shown */, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException($"could not turn the display off (error {Marshal.GetLastPInvokeError()})");
        try { SendMessage(window, 0x0112 /* WM_SYSCOMMAND */, 0xF170 /* SC_MONITORPOWER */, 2 /* off */); }
        finally { DestroyWindow(window); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hWnd);
#else
    private static void Unix(string tool, params string[] args)
    {
        if (Sh.Run(tool, args) is null && Sh.Which(tool) is null) throw new InvalidOperationException($"{tool} is not installed");
    }
#endif
}

#if WINDOWS
public static class Keys
{
    public const byte VK_VOLUME_MUTE = 0xAD;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 2 /* KEYEVENTF_KEYUP */, 0);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}
#endif
