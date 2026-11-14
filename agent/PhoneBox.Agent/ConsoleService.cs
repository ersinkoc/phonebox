using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public interface IWindowPlatform
{
    ConsoleWindowInfo? GetForeground(bool readTitle);
    IReadOnlyList<ConsoleWindowInfo> ListWindows();
    bool FocusWindow(string processMatch, string? titleMatch);
}

public sealed record ConsoleActResult(bool Ok, string? Error = null, ConsoleSnapshot? State = null);

/// <summary>
/// Tracks the active desktop application on demand and executes profile key, macro, deck, and rotary axis actions.
/// </summary>
public sealed class ConsoleService : BackgroundService
{
    private const long PollWindowMs = 12_000;
    private const int ActivePollMs = 350;
    private const int IdlePollMs = 1_000;
    internal const long DebounceMs = 450;

    private readonly ConfigStore _config;
    private readonly ILogger<ConsoleService> _log;
    private readonly AudioControl _audio;
    private readonly MediaMonitor _media;
    private readonly ObsMonitor _obs;
    private readonly ControlDeck _deck;
    private readonly PushStore _push;
    private readonly IWindowPlatform _platform;
    private readonly Func<long> _now;
    private readonly Action<string> _sendKeys;
    private readonly Action<string> _typeText;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, bool> _toggles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _brightnessCache = new(StringComparer.Ordinal);

    private long _lastTouchMs = -PollWindowMs;
    private string? _activeProcess;
    private string? _activeTitle;
    private string? _matchedProfileId;
    private string? _pinnedProfileId;
    private long? _unmatchedSince;
    private long _updatedAt;

    public ConsoleService(
        ConfigStore config,
        ILogger<ConsoleService> log,
        AudioControl audio,
        MediaMonitor media,
        ObsMonitor obs,
        ControlDeck deck,
        PushStore push)
        : this(config, log, audio, media, obs, deck, push, new NativeWindowPlatform(), () => Environment.TickCount64)
    {
    }

    internal ConsoleService(
        ConfigStore config,
        ILogger<ConsoleService> log,
        AudioControl audio,
        MediaMonitor media,
        ObsMonitor obs,
        ControlDeck deck,
        PushStore push,
        IWindowPlatform platform,
        Func<long> now,
        Action<string>? sendKeys = null,
        Action<string>? typeText = null)
    {
        _config = config;
        _log = log;
        _audio = audio;
        _media = media;
        _obs = obs;
        _deck = deck;
        _push = push;
        _platform = platform;
        _now = now;
        _sendKeys = sendKeys ?? KeySender.SendKeys;
        _typeText = typeText ?? KeySender.TypeText;
    }

    public void Touch()
    {
        var wasIdle = _now() - Interlocked.Read(ref _lastTouchMs) >= PollWindowMs;
        Interlocked.Exchange(ref _lastTouchMs, _now());
        if (wasIdle && _wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public IReadOnlyList<ConsoleWindowInfo> ListWindows() => _platform.ListWindows();

    public ConsoleSnapshot Snapshot(bool touch = true)
    {
        if (touch) Touch();
        var cfg = _config.Get().Console;
        return BuildSnapshot(cfg);
    }

    private ConsoleSnapshot BuildSnapshot(AppConsoleConfig cfg)
    {
        var profiles = (cfg.Profiles ?? []).Where(p => p.Enabled).ToList();
        string? proc, title, matched, pinned;
        long updatedAt;
        Dictionary<string, bool> states;
        lock (_gate)
        {
            proc = cfg.AutoFollow ? _activeProcess : null;
            title = cfg.AutoFollow ? _activeTitle : null;
            matched = cfg.AutoFollow && _matchedProfileId is { } m && profiles.Any(p => p.Id == m) ? m : null;
            pinned = _pinnedProfileId is { } pin && profiles.Any(p => p.Id == pin) ? pin : null;
            updatedAt = _updatedAt;
            states = new Dictionary<string, bool>(_toggles, StringComparer.Ordinal);
        }

        MergeLiveStates(states, profiles);
        var effective = pinned
            ?? matched
            ?? profiles.FirstOrDefault(p => p.IsDefault)?.Id
            ?? profiles.FirstOrDefault()?.Id;

        return new ConsoleSnapshot(cfg.AutoFollow, proc, title, matched, pinned, effective, states, updatedAt);
    }

    private void MergeLiveStates(Dictionary<string, bool> states, List<ConsoleProfile> profiles)
    {
        var wantsAudioMute = profiles.Any(p => (p.SubPages ?? []).Any(sp => (sp.Slots ?? []).Any(s => s.Toggle && s.StateKey == "audio:muted")));
        if (wantsAudioMute)
        {
            try
            {
                if (_audio.Snapshot() is { } a) states["audio:muted"] = a.Muted;
            }
            catch { }
        }
        try
        {
            if (_media.Snapshot() is { } m) states["media:playing"] = m.Playing;
        }
        catch { }
        try
        {
            var obs = _obs.Snapshot();
            if (obs.Connected)
            {
                states["obs:recording"] = obs.Recording && !obs.RecordPaused;
                states["obs:paused"] = obs.RecordPaused;
                states["obs:streaming"] = obs.Streaming;
                if (obs.MicMuted is { } mm) states["obs:mic"] = mm;
                if (!string.IsNullOrEmpty(obs.Scene)) states[$"obs:scene:{obs.Scene}"] = true;
            }
        }
        catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var active = _now() - Interlocked.Read(ref _lastTouchMs) < PollWindowMs;
                if (active && _config.Get().Console.AutoFollow)
                {
                    StepPoll();
                    await Task.Delay(ActivePollMs, ct);
                }
                else
                {
                    await _wake.WaitAsync(IdlePollMs, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogDebug("console poll: {Msg}", ex.Message); await Task.Delay(IdlePollMs, ct); }
        }
    }

    internal void StepPoll()
    {
        var cfg = _config.Get().Console;
        if (!cfg.AutoFollow) return;

        var profiles = cfg.Profiles ?? [];
        var needTitle = profiles.Any(p => p.Enabled && !p.IsDefault && !string.IsNullOrWhiteSpace(p.TitleMatch));
        var win = _platform.GetForeground(needTitle);

        lock (_gate)
        {
            _activeProcess = win?.Process;
            _activeTitle = needTitle ? win?.Title : null;
            _updatedAt = _now();

            var match = MatchProfile(profiles, win);
            if (match is not null)
            {
                _matchedProfileId = match.Id;
                _unmatchedSince = null;
            }
            else if (_matchedProfileId is not null)
            {
                _unmatchedSince ??= _now();
                if (_now() - _unmatchedSince.Value >= DebounceMs)
                {
                    _matchedProfileId = null;
                    _unmatchedSince = null;
                }
            }
        }
    }

    public async Task<ConsoleActResult> ActAsync(ConsoleActRequest req, CancellationToken ct)
    {
        Touch();
        var rootCfg = _config.Get();
        var cfg = rootCfg.Console;
        var profiles = (cfg.Profiles ?? []).Where(p => p.Enabled).ToList();

        switch (req.Kind)
        {
            case "pin":
                if (!string.IsNullOrWhiteSpace(req.Pinned) && !profiles.Any(p => p.Id == req.Pinned))
                    return new(false, "Selected profile was not found");
                lock (_gate) _pinnedProfileId = string.IsNullOrWhiteSpace(req.Pinned) ? null : req.Pinned;
                return new(true, null, BuildSnapshot(cfg));

            case "slot":
            {
                var profile = ResolveProfile(cfg, profiles, req.ProfileId);
                if (profile is null) return new(false, "No active console profile");
                var sub = (profile.SubPages ?? []).FirstOrDefault(sp => sp.Id == req.SubPageId)
                    ?? (profile.SubPages ?? []).FirstOrDefault();
                var slots = sub?.Slots ?? [];
                if (req.Slot is null || req.Slot < 0 || req.Slot >= slots.Count)
                    return new(false, "Slot out of range");

                var slot = slots[req.Slot.Value];
                if (slot.Kind == "empty") return new(true, null, BuildSnapshot(cfg));

                var err = await ExecuteSlotAsync(rootCfg, cfg, profile, slot, ct);
                if (err is not null) return new(false, err, BuildSnapshot(cfg));

                if (slot.Toggle && sub is not null)
                {
                    var isLiveKey = slot.StateKey is { } sk
                        && (sk == "audio:muted" || sk == "media:playing" || sk.StartsWith("obs:", StringComparison.Ordinal));
                    if (!isLiveKey)
                    {
                        var key = slot.StateKey ?? $"{profile.Id}:{sub.Id}:{req.Slot.Value}";
                        lock (_gate) _toggles[key] = !_toggles.GetValueOrDefault(key);
                    }
                }
                return new(true, null, BuildSnapshot(cfg));
            }

            case "axis-tap":
            {
                var profile = ResolveProfile(cfg, profiles, req.ProfileId);
                var axis = (profile?.Axes ?? []).FirstOrDefault(a => a.Index == (req.Axis ?? 1));
                if (profile is null || axis is null) return new(false, "Axis is not configured for this profile");
                if (string.IsNullOrWhiteSpace(axis.TapAction)) return new(false, "No tap action configured for this axis");
                var err = await ExecuteTapAsync(rootCfg, cfg, profile, axis.TapAction, ct);
                return new(err is null, err, BuildSnapshot(cfg));
            }

            case "axis":
            {
                var profile = ResolveProfile(cfg, profiles, req.ProfileId);
                var axis = (profile?.Axes ?? []).FirstOrDefault(a => a.Index == (req.Axis ?? 1));
                if (profile is null || axis is null) return new(false, "Axis is not configured for this profile");
                var delta = Math.Clamp(req.Delta ?? 0, -10, 10);
                if (delta == 0) return new(true, null, BuildSnapshot(cfg));
                var err = await ExecuteAxisAsync(cfg, profile, axis, delta, ct);
                return new(err is null, err, BuildSnapshot(cfg));
            }

            default:
                return new(false, "Unknown console action");
        }
    }

    private ConsoleProfile? ResolveProfile(AppConsoleConfig cfg, List<ConsoleProfile> profiles, string? requestedId)
    {
        if (!string.IsNullOrWhiteSpace(requestedId) && profiles.FirstOrDefault(p => p.Id == requestedId) is { } explicitProfile)
            return explicitProfile;
        string? pinned, matched;
        lock (_gate) { pinned = _pinnedProfileId; matched = cfg.AutoFollow ? _matchedProfileId : null; }
        return profiles.FirstOrDefault(p => p.Id == pinned)
            ?? profiles.FirstOrDefault(p => p.Id == matched)
            ?? profiles.FirstOrDefault(p => p.IsDefault)
            ?? profiles.FirstOrDefault();
    }

    private async Task<string?> EnsureFocusAsync(AppConsoleConfig cfg, ConsoleProfile profile, CancellationToken ct)
    {
        if (!cfg.FocusBeforeKeys || profile.IsDefault || string.IsNullOrWhiteSpace(profile.AppMatch))
            return null;

        var needTitle = !string.IsNullOrWhiteSpace(profile.TitleMatch);
        var fg = _platform.GetForeground(needTitle);
        if (MatchesProfile(profile, fg)) return null;

        if (!_platform.FocusWindow(profile.AppMatch, profile.TitleMatch))
            return $"Start {profile.Name} on the computer first";

        await Task.Delay(80, ct);
        return null;
    }

    private async Task<string?> ExecuteSlotAsync(AgentConfig rootCfg, AppConsoleConfig cfg, ConsoleProfile profile, ConsoleSlot slot, CancellationToken ct)
    {
        try
        {
            switch (slot.Kind)
            {
                case "keys":
                {
                    if (string.IsNullOrWhiteSpace(slot.Keys)) return "No shortcut keys assigned";
                    if (await EnsureFocusAsync(cfg, profile, ct) is { } focusErr) return focusErr;
                    _sendKeys(slot.Keys);
                    return null;
                }
                case "text":
                {
                    if (string.IsNullOrEmpty(slot.Text)) return "No text configured";
                    if (await EnsureFocusAsync(cfg, profile, ct) is { } focusErr) return focusErr;
                    _typeText(slot.Text);
                    return null;
                }
                case "macro":
                {
                    if (slot.Steps is not { Count: > 0 }) return "No macro steps configured";
                    if (await EnsureFocusAsync(cfg, profile, ct) is { } focusErr) return focusErr;
                    foreach (var step in slot.Steps)
                    {
                        var i = step.IndexOf(':');
                        if (i > 0)
                        {
                            var op = step[..i].Trim().ToLowerInvariant();
                            var arg = step[(i + 1)..].Trim();
                            switch (op)
                            {
                                case "keys": _sendKeys(arg); break;
                                case "text": _typeText(arg); break;
                                case "wait": await Task.Delay(Math.Clamp(int.TryParse(arg, out var ms) ? ms : 50, 0, 5000), ct); break;
                                case "shortcut":
                                    if (Shortcuts.Run(rootCfg, arg, _log, _push, _audio) is { } sErr) return sErr;
                                    break;
                                default: _sendKeys(step); break;
                            }
                        }
                        else
                        {
                            _sendKeys(step);
                        }
                        await Task.Delay(35, ct);
                    }
                    return null;
                }
                case "shortcut":
                    return string.IsNullOrWhiteSpace(slot.Target)
                        ? "No shortcut selected"
                        : Shortcuts.Run(rootCfg, slot.Target, _log, _push, _audio);
                case "deck":
                    return await ExecuteDeckAsync(slot.Target ?? "", ct);
                default:
                    return "Unsupported slot kind";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task<string?> ExecuteTapAsync(AgentConfig rootCfg, AppConsoleConfig cfg, ConsoleProfile profile, string tap, CancellationToken ct)
    {
        if (tap.StartsWith("deck:", StringComparison.OrdinalIgnoreCase))
            return await ExecuteDeckAsync(tap[5..], ct);
        if (tap.StartsWith("shortcut:", StringComparison.OrdinalIgnoreCase))
            return Shortcuts.Run(rootCfg, tap[9..], _log, _push, _audio);
        if (await EnsureFocusAsync(cfg, profile, ct) is { } focusErr) return focusErr;
        try { _sendKeys(tap); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task<string?> ExecuteDeckAsync(string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target)) return "No deck target configured";
        switch (target)
        {
            case "audio:mute":
                return _audio.ToggleMute() ? null : "Audio backend is unavailable";
            case "media:playpause":
                return await _media.Control("playpause", null) ? null : "No active media player";
            case "media:next":
                return await _media.Control("next", null) ? null : "No active media player";
            case "media:prev":
                return await _media.Control("prev", null) ? null : "No active media player";
            case "obs:record":
                return await _obs.ActAsync("record", null);
            case "obs:pause":
                return await _obs.ActAsync("pause", null);
            case "obs:stream":
                return await _obs.ActAsync("stream", null);
            case "obs:mic":
                return await _obs.ActAsync("mic", null);
            default:
                if (target.StartsWith("obs:scene:", StringComparison.Ordinal))
                    return await _obs.ActAsync("scene", target["obs:scene:".Length..]);
                var actionId = target.StartsWith("action:", StringComparison.Ordinal) ? target[7..] : target;
                var res = await _deck.Act("action", actionId, null, null, null, null, ct);
                return res.Ok ? null : (res.Error ?? "Control action failed");
        }
    }

    private async Task<string?> ExecuteAxisAsync(AppConsoleConfig cfg, ConsoleProfile profile, ConsoleAxis axis, int delta, CancellationToken ct)
    {
        var steps = Math.Max(1, Math.Abs(delta));
        var dir = Math.Sign(delta);
        var volStep = axis.Sensitivity switch { "fine" => 1, "coarse" => 6, _ => 3 };
        var seekMs = axis.Sensitivity switch { "fine" => 2_000L, "coarse" => 15_000L, _ => 5_000L };

        try
        {
            switch (axis.Kind)
            {
                case "keys":
                {
                    var spec = dir < 0 ? axis.PrevKeys : axis.NextKeys;
                    if (string.IsNullOrWhiteSpace(spec)) return "No key binding configured for this dial direction";
                    if (await EnsureFocusAsync(cfg, profile, ct) is { } focusErr) return focusErr;
                    for (var i = 0; i < Math.Min(steps, 5); i++) _sendKeys(spec);
                    return null;
                }
                case "system-volume":
                    return _audio.Step(dir * steps * volStep) ? null : "System volume control is unavailable";
                case "app-volume":
                {
                    var sessions = _deck.CurrentAudio().Sessions;
                    var session = !string.IsNullOrWhiteSpace(axis.AppTarget)
                        ? sessions.FirstOrDefault(s => MatchesPattern(s.Name, axis.AppTarget))
                        : sessions.FirstOrDefault(s => _activeProcess is not null && MatchesPattern(s.Name, _activeProcess));
                    if (session is null) return "Start the target application and play audio";
                    var next = Math.Clamp(session.Volume + dir * steps * volStep, 0, 100);
                    var res = await _deck.Act("session", session.Id, null, next, null, null, ct);
                    return res.Ok ? null : (res.Error ?? "App volume control is unavailable");
                }
                case "mic-gain":
                {
                    var inputs = _deck.CurrentAudio().Devices.Where(d => d.Direction == "input" && d.CanControl).ToList();
                    var mic = !string.IsNullOrWhiteSpace(axis.AppTarget)
                        ? inputs.FirstOrDefault(d => MatchesPattern(d.Name, axis.AppTarget) || d.Id == axis.AppTarget)
                        : inputs.FirstOrDefault(d => d.Default) ?? inputs.FirstOrDefault();
                    if (mic is null) return "Microphone input is unavailable on this computer";
                    var next = Math.Clamp(mic.Volume + dir * steps * volStep, 0, 100);
                    var res = await _deck.Act("device", mic.Id, null, next, null, null, ct);
                    return res.Ok ? null : (res.Error ?? "Microphone gain control failed");
                }
                case "media-seek":
                {
                    var snap = _media.Snapshot();
                    if (snap is null || !snap.CanSeek) return "Current media player does not support seeking";
                    var max = snap.DurMs > 0 ? snap.DurMs : long.MaxValue;
                    var target = Math.Clamp(snap.PosMs + dir * steps * seekMs, 0, max);
                    return await _media.Control("seek", target) ? null : "Media seek failed";
                }
                case "brightness":
                {
                    var monitors = _deck.CurrentMonitors();
                    var mon = !string.IsNullOrWhiteSpace(axis.AppTarget)
                        ? monitors.FirstOrDefault(m => m.Id == axis.AppTarget || MatchesPattern(m.Name, axis.AppTarget))
                        : monitors.FirstOrDefault();
                    if (mon is null) return "Monitor brightness control is unavailable on this computer";
                    var current = mon.Brightness ?? _brightnessCache.GetValueOrDefault(mon.Id, 50);
                    var next = Math.Clamp(current + dir * steps * volStep, 0, 100);
                    var res = await _deck.Act("brightness", mon.Id, null, next, null, null, ct);
                    if (res.Ok) _brightnessCache[mon.Id] = next;
                    return res.Ok ? null : (res.Error ?? "Brightness control failed");
                }
                default:
                    return "Unsupported axis kind";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    internal static ConsoleProfile? MatchProfile(IReadOnlyList<ConsoleProfile> profiles, ConsoleWindowInfo? win)
    {
        if (win is null) return null;
        var active = profiles.Where(p => p.Enabled && !p.IsDefault).ToList();
        return active.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.AppMatch) && !string.IsNullOrWhiteSpace(p.TitleMatch)
                && MatchesPattern(win.Process, p.AppMatch) && MatchesPattern(win.Title, p.TitleMatch))
            ?? active.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.AppMatch) && !string.IsNullOrWhiteSpace(p.TitleMatch)
                && MatchesPattern(win.Title, p.TitleMatch))
            ?? active.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.AppMatch) && string.IsNullOrWhiteSpace(p.TitleMatch)
                && MatchesPattern(win.Process, p.AppMatch));
    }

    private static bool MatchesProfile(ConsoleProfile profile, ConsoleWindowInfo? win)
    {
        if (win is null) return false;
        var hasApp = !string.IsNullOrWhiteSpace(profile.AppMatch);
        var hasTitle = !string.IsNullOrWhiteSpace(profile.TitleMatch);
        if (!hasApp && !hasTitle) return false;
        if (hasApp && !MatchesPattern(win.Process, profile.AppMatch)) return false;
        if (hasTitle && !MatchesPattern(win.Title, profile.TitleMatch)) return false;
        return true;
    }

    internal static bool MatchesPattern(string haystack, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(pattern)) return false;
        var normalized = StripExe(haystack.Trim());
        foreach (var raw in pattern.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = StripExe(raw);
            if (token.Length > 0 && normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string StripExe(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}

internal sealed partial class NativeWindowPlatform : IWindowPlatform
{
#if WINDOWS
    public ConsoleWindowInfo? GetForeground(bool readTitle)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var proc = ProcessName((int)pid);
        var title = readTitle ? WindowTitle(hwnd) : "";
        return string.IsNullOrEmpty(proc) ? null : new ConsoleWindowInfo(proc, title);
    }

    public IReadOnlyList<ConsoleWindowInfo> ListWindows()
    {
        var list = new List<ConsoleWindowInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var proc = ProcessName((int)pid);
            var title = WindowTitle(hwnd);
            if (!string.IsNullOrWhiteSpace(proc) && !string.IsNullOrWhiteSpace(title)
                && seen.Add($"{proc}\n{title}"))
            {
                list.Add(new ConsoleWindowInfo(proc, title));
            }
            return list.Count < 60;
        }, 0);
        return list;
    }

    public bool FocusWindow(string processMatch, string? titleMatch)
    {
        nint target = 0;
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var proc = ProcessName((int)pid);
            if (!ConsoleService.MatchesPattern(proc, processMatch)) return true;
            var title = WindowTitle(hwnd);
            if (!string.IsNullOrWhiteSpace(titleMatch) && !ConsoleService.MatchesPattern(title, titleMatch)) return true;
            target = hwnd;
            return false;
        }, 0);

        if (target == 0 && string.IsNullOrWhiteSpace(titleMatch))
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != 0 && ConsoleService.MatchesPattern(p.ProcessName, processMatch))
                    { target = p.MainWindowHandle; break; }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        if (target == 0) return false;
        if (IsIconic(target)) ShowWindow(target, 9);
        return SetForegroundWindow(target) || GetForegroundWindow() == target;
    }

    private static string ProcessName(int pid)
    {
        if (pid <= 0) return "";
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    private static string WindowTitle(nint hwnd)
    {
        var buf = new char[512];
        var len = GetWindowText(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, [Out] char[] lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
#else
    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex MacNameRe();

    [GeneratedRegex("ASN:[^\\s\"]+-(?:\"([^\"]+)\"|([^:\\s]+))")]
    private static partial Regex MacVisibleRe();

    public ConsoleWindowInfo? GetForeground(bool readTitle)
    {
        if (OperatingSystem.IsMacOS())
        {
            var front = Sh.RunWithTimeout(600, "lsappinfo", "front")?.Trim();
            if (string.IsNullOrWhiteSpace(front)) return null;
            var info = Sh.RunWithTimeout(600, "lsappinfo", "info", "-only", "name", front);
            var proc = info is not null && MacNameRe().Match(info) is { Success: true } m ? m.Groups[1].Value : "";
            if (string.IsNullOrWhiteSpace(proc)) return null;
            var title = "";
            if (readTitle)
            {
                title = Sh.RunWithTimeout(600, "osascript", "-e",
                    "tell application \"System Events\" to get name of front window of first application process whose frontmost is true")?.Trim() ?? "";
            }
            return new ConsoleWindowInfo(proc, title);
        }

        var pidText = Sh.RunWithTimeout(500, "xdotool", "getactivewindow", "getwindowpid")?.Trim();
        if (!int.TryParse(pidText, out var pid) || pid <= 0) return null;
        var linuxProc = Sh.ReadFile($"/proc/{pid}/comm")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(linuxProc)) return null;
        var linuxTitle = readTitle ? (Sh.RunWithTimeout(500, "xdotool", "getactivewindow", "getwindowname")?.Trim() ?? "") : "";
        return new ConsoleWindowInfo(linuxProc, linuxTitle);
    }

    public IReadOnlyList<ConsoleWindowInfo> ListWindows()
    {
        if (OperatingSystem.IsMacOS())
        {
            var raw = Sh.RunWithTimeout(800, "lsappinfo", "visibleProcessList") ?? "";
            var list = new List<ConsoleWindowInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in MacVisibleRe().Matches(raw))
            {
                var name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace('_', ' ').Trim();
                if (name.Length > 0 && seen.Add(name))
                    list.Add(new ConsoleWindowInfo(name, ""));
            }
            return list;
        }

        var wmctrl = Sh.RunWithTimeout(800, "wmctrl", "-lp");
        if (wmctrl is null) return [];
        var linux = new List<ConsoleWindowInfo>();
        var lseen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in wmctrl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[2], out var pid) || pid <= 0) continue;
            var proc = Sh.ReadFile($"/proc/{pid}/comm")?.Trim() ?? "";
            var title = parts[4].Trim();
            if (proc.Length > 0 && lseen.Add($"{proc}\n{title}"))
                linux.Add(new ConsoleWindowInfo(proc, title));
        }
        return linux;
    }

    public bool FocusWindow(string processMatch, string? titleMatch)
    {
        if (OperatingSystem.IsMacOS())
        {
            var running = ListWindows().FirstOrDefault(w => ConsoleService.MatchesPattern(w.Process, processMatch));
            if (running is null) return false;
            var res = Sh.RunWithTimeout(1200, "osascript",
                "-e", "on run argv",
                "-e", "tell application (item 1 of argv) to activate",
                "-e", "end run",
                running.Process);
            return res is not null;
        }

        var wmctrl = Sh.RunWithTimeout(800, "wmctrl", "-lp");
        if (wmctrl is not null)
        {
            foreach (var line in wmctrl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[2], out var pid) || pid <= 0) continue;
                var proc = Sh.ReadFile($"/proc/{pid}/comm")?.Trim() ?? "";
                var title = parts[4].Trim();
                if (!ConsoleService.MatchesPattern(proc, processMatch)) continue;
                if (!string.IsNullOrWhiteSpace(titleMatch) && !ConsoleService.MatchesPattern(title, titleMatch)) continue;
                if (Sh.RunWithTimeout(800, "wmctrl", "-ia", parts[0]) is not null) return true;
            }
        }
        return false;
    }
#endif
}
