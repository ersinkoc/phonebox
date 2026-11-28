#if !WINDOWS
using System.Diagnostics;
using System.Globalization;

namespace PhoneBox.Agent;

/// <summary>Existing master/media controls remain available; portable radio/sound uses installed CLI players and native-only features report unavailable explicitly.</summary>
internal sealed class PortableDeckNative(
    AudioControl audio,
    MediaMonitor media,
    Func<bool, (string Kind, string Path)?>? findPlayer = null,
    PulseDeckAudio? pulse = null) : IDeckNative
{
    private const long PlayerCacheTtlMs = 15_000;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Process> _sounds = [];
    private (long At, (string Kind, string Path)? Player)? _filePlayerCache;
    private (long At, (string Kind, string Path)? Player)? _streamPlayerCache;

    /// <summary>
    /// Every device and application stream on Linux (pactl); without that backend (macOS, Linux without pactl) the system
    /// output's master volume only. Until pactl has answered once it may have no Pulse server to talk to (PipeWire without
    /// pipewire-pulse), so the system output stays. Once it has, a failed listing is reported unavailable rather than
    /// falling back to the system output: panic and "Mics off" must not take a list without microphones for the full one.
    /// </summary>
    public DeckAudio Audio() => pulse?.Read() is { } listed ? listed
        : pulse is { HasAnswered: true } ? new(false, "The sound server did not answer (pactl)", [], [])
        : audio.Snapshot() is { } a ? new(true, null,
            [new("default", "System output", "output", true, a.Volume, a.Muted, 0, 1, a.CanControl)], [])
        : new(false, "Audio backend is unavailable", [], []);
    /// <summary>Push-to-talk's restore target: read fresh, not from the listing pactl keeps for a moment, so a mute another
    /// application made just before the hold is not lost.</summary>
    public bool? MicrophoneMuted(string id) =>
        (pulse?.Read(fresh: true) ?? Audio()).Devices.FirstOrDefault(d => d.Id == id && d.Direction == "input")?.Muted;
    public bool Device(string id, int? volume, bool? muted, int? channel = null)
    {
        // "default" is the synthetic system output only while pactl has not listed anything: a Pulse sink or source can
        // itself be named "default", and once pactl has answered every id is one of its devices
        if (pulse is not null && (id != "default" || pulse.Read() is not null || pulse.HasAnswered))
            return pulse.Device(id, volume, muted, channel);
        if (id != "default" || channel is not null || audio.Current() is not { CanControl: true } current) return false;
        if (volume is { } v && !audio.SetVolume(v)) return false;
        return muted is not { } m || m == current.Muted || audio.ToggleMute();
    }
    public bool Session(string id, int? volume, bool? muted) => pulse?.Session(id, volume, muted) ?? false;
    public bool DefaultDevice(string id, int? role = null) => pulse?.DefaultDevice(id, role) ?? false;
    public IReadOnlyList<DeckMonitor> Monitors() => [];
    public bool Brightness(string id, int value) => false;
    public IReadOnlyList<DeckItem> PowerPlans() => [];
    public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(media.Snapshot() is { } m
        ? [new("current", m.App, m.Title, m.Playing, m.PosMs, m.DurMs, m.CanSeek, false, false, false, "None")] : []);
    public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) =>
        id != "current" ? Task.FromResult(false) : action == "pause" ? media.PauseAsync() : media.Control(action, value);
    public Task<bool> PauseCurrentMedia(CancellationToken ct) => media.PauseAsync();

    /// <summary>A player that ended by itself (stream dropped, file finished), so a radio shows as stopped at once.</summary>
    public event Action<string>? SoundStopped;

    public string? ActionState(DeckAction action)
    {
        if (action.Kind is not ("sound" or "radio")) return null;
        if (ResolvePlayer(isStream: action.Kind == "radio") is null) return null;
        lock (_gate) return _sounds.ContainsKey(action.Id) ? "Playing" : "Stopped";
    }

    public void Play(string id, string file, string? device, int volume)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Playback target is empty", nameof(file));
        var isStream = Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!isStream)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Sound file not found", file);
            file = Path.GetFullPath(file);
        }
        var player = ResolvePlayer(isStream) ?? throw new NotSupportedException("Install ffplay, mpv, or vlc for audio playback");
        var vol = Math.Clamp(volume, 0, 100);
        var start = BuildStartInfo(player.Kind, player.Path, file, vol);

        lock (_gate)
        {
            StopSoundLocked(id);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) => _ = Task.Run(() =>
            {
                var ended = false;
                lock (_gate)
                    if (_sounds.TryGetValue(id, out var current) && ReferenceEquals(current, process))
                    {
                        StopSoundLocked(id);
                        ended = true;
                    }
                if (ended) SoundStopped?.Invoke(id);
            });
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new IOException("Could not start audio player");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
            _ = DrainPipeAsync(process.StandardOutput.BaseStream);
            _ = DrainPipeAsync(process.StandardError.BaseStream);
            _sounds[id] = process;
        }
    }

    public void StopSound(string? id)
    {
        lock (_gate) StopSoundLocked(id);
    }

    private void StopSoundLocked(string? id)
    {
        foreach (var key in _sounds.Keys.Where(k => id is null || k == id).ToArray())
        {
            var process = _sounds[key];
            _sounds.Remove(key);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    private (string Kind, string Path)? ResolvePlayer(bool isStream)
    {
        if (findPlayer is not null) return findPlayer(isStream);
        var now = Environment.TickCount64;
        lock (_gate)
        {
            var cached = isStream ? _streamPlayerCache : _filePlayerCache;
            if (cached is { } hit && now - hit.At < PlayerCacheTtlMs) return hit.Player;
            var found = FindInstalledPlayer(isStream);
            if (isStream) _streamPlayerCache = (now, found);
            else _filePlayerCache = (now, found);
            return found;
        }
    }

    private static (string Kind, string Path)? FindInstalledPlayer(bool isStream)
    {
        foreach (var tool in new[] { "ffplay", "mpv", "cvlc", "vlc" })
            if (ToolProcess.Find(tool) is { } found) return (tool, found);
        if (!isStream && OperatingSystem.IsMacOS() && ToolProcess.Find("afplay") is { } afplay)
            return ("afplay", afplay);
        return null;
    }

    internal static ProcessStartInfo BuildStartInfo(string kind, string executable, string target, int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        var normalizedVolume = (clamped / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        string[] args = kind switch
        {
            "ffplay" => ["-nodisp", "-autoexit", "-vn", "-sn", "-loglevel", "error", "-volume", clamped.ToString(CultureInfo.InvariantCulture), "-i", target],
            "mpv" => ["--no-config", "--no-terminal", "--no-video", "--really-quiet", $"--volume={clamped.ToString(CultureInfo.InvariantCulture)}", "--", target],
            "afplay" => ["-v", normalizedVolume, target],
            _ => ["--intf", "dummy", "--no-one-instance", "--play-and-exit", "--no-video", "--quiet", $"--gain={normalizedVolume}", "--", target],
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task DrainPipeAsync(Stream stream)
    {
        var buffer = new byte[2048];
        try { while (await stream.ReadAsync(buffer) > 0) { } } catch { }
    }

    public IReadOnlyList<DeckItem> MidiPorts() => [];
    public void Midi(int port, string message, int value) => throw new NotSupportedException("Native MIDI requires Windows");
    public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.CompletedTask; yield break; }
    public void Dispose() => StopSound(null);
}
#endif
