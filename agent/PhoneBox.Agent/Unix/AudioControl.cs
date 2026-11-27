using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Master volume on Linux (PipeWire's wpctl, or PulseAudio's pactl) and the selected macOS output.
/// macOS falls back to CoreAudio channels and, for a matching display, optional DDC/CI readback.
/// </summary>
public sealed partial class AudioControl
{
    private const int MaxAgeMs = 900;
    // An output macOS cannot control in-process (a display's or soundbar's own volume over HDMI) is read by starting
    // osascript, and every start registers a new app with WindowServer. At one reading a second, WindowServer's Mach port
    // count grew until it hit its limit after a few days; WindowServer then exited and the user was logged out. Such an
    // output cannot be set from the phone anyway, so it is read again after 10 s, not every second.
    private const int SlowReadMaxAgeMs = 10_000;
    // An output with no reading at all (on HDMI osascript says "missing value" every time) waits twice as long after each
    // empty reading, up to 5 minutes; choosing another output starts over at once.
    private const long FailedReadCapMs = 300_000;
    private readonly Func<uint?>? _outputId; // the selected output, read in-process (no helper)
    private uint? _lastOutput;
    private int _failures;     // readings in a row that came back empty
    private readonly Lock _lock = new();
    private readonly Func<AudioSnapshot?> _read;
    private AudioSnapshot? _cached;
    private long _at;          // when _cached was read; 0 = never read or invalidated
    private long _gen;         // bumped by Invalidate, so a read from before a volume change cannot land after it
    private long _seq, _written; // read tickets: a reading only lands if no later-started read has landed
    // the generation the running background read belongs to, -1 while none runs: a read for a newer generation (another
    // output was chosen) starts at once instead of waiting for one of the old output, which can take seconds
    private long _refreshingGen = -1; // guarded by _lock

    private const int SetTimeoutMs = 8000; // a busy computer can need seconds; giving up kills the helper and nothing is set
    private readonly Func<int, int?>? _set; // the level reached, or null when the write failed
    private readonly Func<bool>? _toggleMute;

    // only macOS reads through osascript; on Linux a missing reading is a sink that is briefly gone and is retried at once
    private readonly bool _slowReadsForUncontrollable;

    public AudioControl() : this(Read, null, null, OperatingSystem.IsMacOS(), OperatingSystem.IsMacOS() ? () => MacAudioProbe.SelectedOutput()?.Id : null) { }

    /// <summary>For tests: a fake reader (and optionally setter and mute toggle) instead of osascript / wpctl / pactl.</summary>
    internal AudioControl(Func<AudioSnapshot?> read, Func<int, int?>? set = null, Func<bool>? toggleMute = null, bool slowReadsForUncontrollable = false,
        Func<uint?>? outputId = null)
    { _read = read; _set = set; _toggleMute = toggleMute; _slowReadsForUncontrollable = slowReadsForUncontrollable; _outputId = outputId; }

    /// <summary>How long an empty reading is kept after this many empty readings in a row: 10 s, 20 s, 40 s ... up to 5 minutes.</summary>
    internal static long FailedReadMaxAgeMs(int failures) =>
        Math.Min((long)SlowReadMaxAgeMs << Math.Clamp(failures - 1, 0, 16), FailedReadCapMs);

    internal bool Refreshing { get { lock (_lock) return _refreshingGen != -1; } }

    /// <summary>
    /// The last reading, refreshed in the background. Never waits for osascript / wpctl / pactl: the live stream
    /// calls this every second for every phone, and under heavy CPU load those helpers can take seconds each.
    /// </summary>
    public AudioSnapshot? Snapshot()
    {
        long gen, ticket = 0; AudioSnapshot? cached; var start = false;
        var output = _outputId?.Invoke();
        // the generation is read and its gate claimed in one hold of the lock: a caller that saw an older generation must
        // not take a newer read's gate (that would start a second helper for the same output)
        lock (_lock)
        {
            // another output was chosen: what the old one did says nothing about it, so read it now and drop reads in flight
            if (output != _lastOutput) { _lastOutput = output; _failures = 0; _at = 0; _gen++; }
            cached = _cached; gen = _gen;
            var maxAge = !_slowReadsForUncontrollable || cached is { CanControl: true } ? MaxAgeMs
                : cached is null ? FailedReadMaxAgeMs(_failures)
                : SlowReadMaxAgeMs;
            // _at == 0 is "never read or invalidated", not a time: soon after boot the uptime clock is below maxAge
            if ((_at == 0 || Environment.TickCount64 - _at >= maxAge) && _refreshingGen != gen)
            {
                _refreshingGen = gen; ticket = ++_seq; start = true;
            }
        }
        if (start)
            _ = Task.Run(() =>
            {
                // the read belongs to the generation whose gate it holds; if that moved on, Store drops it
                try { Store(ticket, gen, _read()); }
                catch { }
                // only its own gate: a read for a newer generation may have started meanwhile and still be running
                finally { lock (_lock) if (_refreshingGen == gen) _refreshingGen = -1; }
            });
        return cached;
    }

    /// <summary>A reading for a control action: the cached one while fresh, otherwise read now (may wait for the helper).</summary>
    public AudioSnapshot? Current()
    {
        lock (_lock)
            if (_at != 0 && Environment.TickCount64 - _at < MaxAgeMs) return _cached;
        var (ticket, gen) = BeginRead();
        var s = _read();
        Store(ticket, gen, s);
        return s;
    }

    private (long Ticket, long Gen) BeginRead() { lock (_lock) return (++_seq, _gen); }

    private void Store(long ticket, long gen, AudioSnapshot? s)
    {
        bool changed;
        lock (_lock)
        {
            if (_gen != gen || ticket <= _written) return;
            // the volume moved from outside (the keyboard's volume keys, another app): the phone should see it now
            changed = _cached is not null && s is not null && (_cached.Volume != s.Volume || _cached.Muted != s.Muted);
            _written = ticket; _cached = s; _at = Environment.TickCount64;
            _failures = s is null ? _failures + 1 : 0;
        }
        if (changed) StateSignal.Poke();
    }

    // control actions run one at a time, each with its cache update inside: two requests at once (a dragged slider) must
    // leave the cache with the volume the device really ended on, not the one whose write happened to finish last
    private readonly object _controlGate = new();

    public bool SetVolume(int level)
    {
        if (OperatingSystem.IsMacOS() && !CanControlNow()) return false;
        level = Math.Clamp(level, 0, 100);
        lock (_controlGate)
        {
            // the level the write reached: the request, unless the output could not get there (channels that cannot all go
            // as high), in which case it is what the output will report, not what was asked for
            var reached = _set is not null ? _set(level) : SetOnThisSystem(level);
            // On success the new volume is what the next state shows: the old reading would make the slider snap back until
            // the background read (seconds, on a busy computer) catches up, which then looks like "nothing changed".
            var before = KnownReading();
            // a write that leaves mute alone with no earlier reading says nothing about mute: do not cache a guess, read it
            if (reached is { } now && MutedAfterSet(_set is not null || ClearsMute(level), before) is { } muted) Remember(new AudioSnapshot(now, muted, true));
            else Invalidate();
            return reached is not null;
        }
    }

    /// <summary>
    /// The mute state a volume write leaves behind. A write that also clears mute leaves the sink unmuted; one that only sets
    /// the volume (pactl, wpctl at 0) leaves it as it was, which is unknown (null) when there was no earlier reading.
    /// </summary>
    internal static bool? MutedAfterSet(bool clearsMute, AudioSnapshot? before) => clearsMute ? false : before?.Muted;

    /// <summary>Whether the write that SetOnThisSystem makes for this level also unmutes: macOS always does, wpctl above 0.</summary>
    private static bool ClearsMute(int level) => OperatingSystem.IsMacOS() || (Sh.Which("wpctl") is not null && level > 0);

    /// <summary>Sets the volume on this system; the level reached (null when it failed).</summary>
    private static int? SetOnThisSystem(int level)
    {
        if (OperatingSystem.IsMacOS())
        {
            // in-process; osascript only for an output CoreAudio cannot control
            if (MacAudioProbe.SetVolume(level) is { } reached) return reached;
            return Sh.RunWithTimeout(SetTimeoutMs, "osascript", "-e", $"set volume output volume {level} without output muted") is not null ? level : null;
        }
        var ok = Sh.Which("wpctl") is not null
            ? Sh.RunWithTimeout(SetTimeoutMs, "wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", (level / 100.0).ToString("0.00", CultureInfo.InvariantCulture)) is not null
              && (level == 0 || Sh.RunWithTimeout(SetTimeoutMs, "wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0") is not null)
            : Sh.RunWithTimeout(SetTimeoutMs, "pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{level}%") is not null;
        return ok ? level : null;
    }

    /// <summary>
    /// Whether the output can be controlled, from the last reading however old: a fresh read first would put another
    /// slow helper call in front of the set. Only a missing or negative reading is read again.
    /// </summary>
    private bool CanControlNow()
    {
        AudioSnapshot? known;
        lock (_lock) known = _cached;
        return known is { CanControl: true } || Current() is { CanControl: true };
    }

    /// <summary>
    /// The last reading, or null when there is none or it was invalidated (after a failed action, or one that changed
    /// something the reading does not say): an invalidated reading is unknown, not a baseline to build on.
    /// </summary>
    internal AudioSnapshot? KnownReading() { lock (_lock) return _at != 0 ? _cached : null; }

    /// <summary>Takes a known volume as the current reading and drops any read that started before it.</summary>
    private void Remember(AudioSnapshot s) { lock (_lock) { _cached = s; _at = Environment.TickCount64; _gen++; } }

    public bool Step(int delta) => Current() is { } s && SetVolume(s.Volume + delta);

    public bool ToggleMute()
    {
        if (OperatingSystem.IsMacOS() && Current() is not { CanControl: true }) return false;
        lock (_controlGate)
        {
            // the reading the flip starts from is taken before the toggle: a read that lands while the toggle is in progress
            // may already include it, and flipping that again would show the opposite of the truth
            var before = KnownReading();
            var ok = _toggleMute?.Invoke() ?? ToggleOnThisSystem();
            // the flipped state is what the next state shows (and what a later volume-only write must start from); after a
            // failure, with no valid earlier reading, or if a read landed meanwhile, the next read decides. The comparison and
            // the publish are one hold of the lock: a read cannot land between them and then be overwritten as if it were stale
            lock (_lock)
            {
                if (ok && before is not null && _at != 0 && ReferenceEquals(_cached, before)) Remember(before with { Muted = !before.Muted });
                else Invalidate();
            }
            return ok;
        }
    }

    private static bool ToggleOnThisSystem() => OperatingSystem.IsMacOS()
        ? MacAudioProbe.ToggleMute()
          || Sh.RunWithTimeout(SetTimeoutMs, "osascript", "-e", "set volume output muted (not (output muted of (get volume settings)))") is not null
        : Sh.Which("wpctl") is not null
            ? Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle") is not null
            : Sh.Run("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle") is not null;

    private void Invalidate() { lock (_lock) { _at = 0; _gen++; } }

    private static AudioSnapshot? Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = MacAudioProbe.SelectedOutput();
            // a controllable output is read in-process (microseconds); osascript is for the rest (a display's own volume...)
            if (output is { Volume: { } nativeVolume, Muted: { } nativeOutputMuted } native && MacAudioProbe.CanControl(native.Id))
                return new AudioSnapshot(nativeVolume, nativeOutputMuted, true);
            var s = Sh.Run("osascript", "-e", "set v to get volume settings\nreturn ((output volume of v) as string) & \",\" & ((output muted of v) as string)")?.Trim();
            var p = s?.Split(',');
            if (p is { Length: 2 } && int.TryParse(p[0], out var vol) && bool.TryParse(p[1], out var nativeMuted))
                return new AudioSnapshot(vol, nativeMuted, output is { } selected && MacAudioProbe.CanControl(selected.Id));
            if (output is null) return null;
            var level = output.Value.Volume ?? MacAudioProbe.DisplayVolume(output.Value.Id, output.Value.Name);
            return level is { } measured ? new AudioSnapshot(measured, output.Value.Muted ?? false, false) : null;
        }
        if (Sh.Which("wpctl") is not null)
        {
            // "Volume: 0.45 [MUTED]"
            var m = WpctlRe().Match(Sh.Run("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@") ?? "");
            if (m.Success) return new AudioSnapshot((int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 100), m.Groups[2].Success);
        }
        var vol2 = PercentRe().Match(Sh.Run("pactl", "get-sink-volume", "@DEFAULT_SINK@") ?? "");
        if (!vol2.Success) return null;
        var muted = (Sh.Run("pactl", "get-sink-mute", "@DEFAULT_SINK@") ?? "").Contains("yes");
        return new AudioSnapshot(int.Parse(vol2.Groups[1].Value), muted);
    }

    [GeneratedRegex(@"Volume:\s*([\d.]+)(\s*\[MUTED\])?")] private static partial Regex WpctlRe();
    [GeneratedRegex(@"(\d+)%")] private static partial Regex PercentRe();
}
