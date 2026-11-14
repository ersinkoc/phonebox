using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Configured control actions. Phones never supply executable paths, credentials or shell commands.</summary>
public sealed partial class ControlDeck : BackgroundService
{
    private readonly ConfigStore _config;
    private readonly ILogger<ControlDeck> _log;
    private readonly IDeckNative _native;
    private readonly Func<long> _now;
    private readonly SpotifyControl? _spotify;
    private readonly ControlTasks _tasks;
    private readonly MicrophoneLeases _ptt;
    private readonly PowerConfirmations _power = new(() => Environment.TickCount64);
    private readonly SemaphoreSlim _profiles = new(1, 1);
    private readonly SemaphoreSlim _listeners = new(2, 2);
    private readonly Lock _state = new();
    private DeckAudio? _audio;
    private long _audioAt;
    private readonly Dictionary<string, (int Original, int Applied)> _duck = [];
    private int? _duckPercent;
    private readonly Dictionary<string, long> _sleep = [];
    private Task? _hardwareJob;
    private long _hardwareAt;
    private IReadOnlyList<DeckMonitor> _monitors = [];
    private IReadOnlyList<DeckItem> _plans = [], _midi = [];

    private readonly PushStore? _push;
    private readonly AudioControl? _master;
    public ControlDeck(ConfigStore config, AudioControl audio, MediaMonitor media, ILogger<ControlDeck> log, SpotifyControl spotify, PushStore push)
        : this(config, CreateNative(audio, media), log, null, push, audio) { _spotify = spotify; }

    private static IDeckNative CreateNative(AudioControl audio, MediaMonitor media)
    {
#if WINDOWS
        return new WindowsDeckNative();
#else
        return new PortableDeckNative(audio, media);
#endif
    }
    internal ControlDeck(ConfigStore config, IDeckNative native, ILogger<ControlDeck> log, Func<long>? now = null, PushStore? push = null, AudioControl? master = null)
    {
        _config = config; _log = log; _push = push; _master = master;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _tasks = new(config, log);
        _native = native;
        _ptt = new(id => Audio().Devices.FirstOrDefault(d => d.Id == id && d.Direction == "input")?.Muted,
            (id, muted) => { try { var ok = _native.Device(id, null, muted); Invalidate(); return ok; } catch { return false; } }, () => Environment.TickCount64);
    }
    private DeckAudio Audio()
    {
        lock (_state)
        {
            if (_audio is not null && Environment.TickCount64 - _audioAt < 400) return _audio;
            _audioAt = Environment.TickCount64;
            return _audio = _native.Audio();
        }
    }
    internal DeckAudio CurrentAudio() => Audio();
    internal IReadOnlyList<DeckMonitor> CurrentMonitors() { RefreshHardware(); return _monitors; }
    private void Invalidate() { lock (_state) _audio = null; }

    public async Task<object> Snapshot(CancellationToken ct)
    {
        RefreshHardware();
        var c = _config.Get();
        IReadOnlyList<DeckMedia> media = [];
        string? mediaError = null;
        try { media = await _native.Media(ct); } catch { mediaError = "Media sessions are unavailable"; }
        object[] sleep;
        lock (_state) sleep = _sleep.Select(p => (object)new { id = p.Key, at = p.Value }).ToArray();
        return new
        {
            platform = AppPaths.Os, audio = Audio(), media, mediaError,
            monitors = _monitors, powerPlans = _plans, midi = _midi, hardwareLoading = _hardwareJob?.IsCompleted == false,
            actions = c.Controls.Actions.Select(Describe).ToArray(),
            shortcuts = c.Shortcuts.Select(DescribeShortcut),
            profiles = c.Controls.Profiles.Select(p => new DeckItem(p.Id, p.Label, "profile")),
            allowListening = c.Controls.AllowListening, allowPower = c.Controls.AllowPower,
            ducking = _duckPercent is not null,
            sleep,
        };
    }

    private void RefreshHardware()
    {
        lock (_state)
        {
            if (_hardwareJob?.IsCompleted == false || Environment.TickCount64 - _hardwareAt < 30_000) return;
            _hardwareAt = Environment.TickCount64;
            _hardwareJob = Task.Run(() =>
            {
                try { _monitors = _native.Monitors(); _plans = _native.PowerPlans(); _midi = _native.MidiPorts(); }
                catch (Exception ex) { _log.LogDebug(ex, "Control hardware discovery failed"); }
            });
        }
    }

    /// <summary>Returns a draft for the desktop editor; saving still uses the ordinary config version check.</summary>
    public DeckProfile CaptureProfile()
    {
        var audio = _native.Audio();
        if (!audio.Available) throw new InvalidOperationException("Audio is unavailable");
        return new DeckProfile { Id = "profile-" + Guid.NewGuid().ToString("N")[..12], Label = "Audio profile",
            Defaults = audio.Defaults?.ToDictionary(p => p.Key, p => p.Value) ?? [],
            Levels = audio.Devices.Where(d => d.CanControl).Select(d => new DeckLevel("device", d.Id, d.Volume, d.Muted))
                .Concat(audio.Sessions.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => new DeckLevel("application", g.Key, g.First().Volume, g.First().Muted))).ToList() };
    }

    public string TaskOutput(string id) => _tasks.Output(id);

    public async Task<DeckResult> Act(string kind, string? id, string? op, int? value, bool? muted, string? lease, CancellationToken ct)
    {
        id ??= ""; op ??= "";
        if (id.Length > 2048 || op.Length > 64 || lease?.Length > 64) return new(false, "Invalid control request");
        try
        {
            bool ok;
            switch (kind)
            {
                case "device":
                    if (value is null && muted is null) return new(false, "Choose a volume or mute state");
                    if (!Audio().Devices.Any(d => d.Id == id && d.CanControl) || value is < 0 or > 100) return new(false, "Audio device is unavailable");
                    ok = _native.Device(id, value, muted, op is "left" ? 0 : op is "right" ? 1 : null); break;
                case "session":
                    if (value is null && muted is null) return new(false, "Choose a volume or mute state");
                    if (!Audio().Sessions.Any(s => s.Id == id) || value is < 0 or > 100) return new(false, "Application audio session ended");
                    ok = _native.Session(id, value, muted); break;
                case "default":
                    if (op is not ("" or "all" or "media" or "communications" or "console")) return new(false, "Unknown audio role");
                    ok = _native.DefaultDevice(id, op switch { "media" => 1, "communications" => 2, "console" => 0, _ => null }); break;
                case "media": ok = await _native.MediaAction(id, op, value ?? 0, ct); break;
                case "ptt":
                    if (op == "begin") { var token = _ptt.Begin(id); return new(token is not null, token is null ? "Microphone is unavailable" : null, token); }
                    ok = lease is not null && (op == "renew" ? _ptt.Renew(lease) : op == "end" && _ptt.End(lease)); break;
                case "profile": return await Profile(id, ct);
                case "duck":
                    lock (_state) { _duckPercent = op == "off" ? null : Math.Clamp(value ?? 25, 0, 100); }
                    ok = true; break;
                case "sleep":
                    if (value is > 0 && !(await _native.Media(ct)).Any(p => p.Id == id)) return new(false, "Select an active media player");
                    lock (_state) { RestoreFade(id); if (value is null or <= 0) _sleep.Remove(id); else _sleep[id] = _now() + Math.Clamp(value.Value, 1, 360) * 60_000L; }
                    ok = true; break;
                case "panic":
                    if (!Audio().Available || Audio().Devices.Count == 0) return new(false, "Audio is unavailable");
                    ok = true;
                    foreach (var d in Audio().Devices) ok &= _native.Device(d.Id, null, true);
                    break;
                case "sound-stop": _native.StopSound(string.IsNullOrEmpty(id) ? null : id); ok = true; break;
                case "action": return await Action(id, op, value, ct);
                case "brightness": ok = value is >= 0 and <= 100 && _native.Brightness(id, value.Value); break;
                case "desktop": ok = value is -1 or 1 && await _native.PlatformAction("desktop", "", null, value.Value, ct); break;
                case "power-plan": ok = _native.PowerPlans().Any(p => p.Id == id) && await _native.PlatformAction("power-plan", id, null, 0, ct); break;
                case "power-arm":
                    if (!_config.Get().Controls.AllowPower || id is not ("sleep" or "restart" or "shutdown")) return new(false, "Enable power controls in desktop settings");
                    return new(true, Lease: _power.Arm(id));
                case "power":
                    if (!_config.Get().Controls.AllowPower || lease is null || !_power.Consume(lease, id)) return new(false, "Hold the power button to confirm");
                    ok = await _native.PlatformAction("power", id, null, 0, ct); break;
                case "lock": case "monitor-off":
                    ok = Shortcuts.Execute(new Shortcut { Cmd = "builtin:" + kind, Label = kind }, _log) is null; break;
                default: return new(false, "Unknown control action");
            }
            Invalidate();
            return new(ok, ok ? null : "This control is unavailable on the selected device");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "Control {Kind} failed", kind); return new(false, "Control failed; check the computer's integration settings"); }
    }

    private async Task<DeckResult> Profile(string id, CancellationToken ct)
    {
        var profile = _config.Get().Controls.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return new(false, "No such profile");
        await _profiles.WaitAsync(ct);
        try
        {
            var before = Audio();
            foreach (var level in profile.Levels)
                if (level.Kind is not ("device" or "session" or "application") || (level.Kind == "device" ? !before.Devices.Any(d => d.Id == level.Id && d.CanControl) : level.Kind == "session" && !before.Sessions.Any(s => s.Id == level.Id)) || level.Volume is < 0 or > 100)
                    return new(false, "A profile's audio target is unavailable; refresh the profile in desktop settings");
            if (profile.Output is { Length: > 0 } selectedOutput && !before.Devices.Any(d => d.Id == selectedOutput && d.Direction == "output")
                || profile.Input is { Length: > 0 } selectedInput && !before.Devices.Any(d => d.Id == selectedInput && d.Direction == "input"))
                return new(false, "A profile's selected device is unavailable");
            foreach (var pair in profile.Defaults)
                if (!audioRole(pair.Key, out _) || !before.Devices.Any(d => d.Id == pair.Value && pair.Key.StartsWith(d.Direction + ":", StringComparison.Ordinal)))
                    return new(false, "A profile's default device is unavailable");
            foreach (var level in profile.Levels)
            {
                if (level.Kind == "application")
                {
                    foreach (var session in before.Sessions.Where(s => string.Equals(s.Name, level.Id, StringComparison.OrdinalIgnoreCase)))
                        if (!_native.Session(session.Id, level.Volume, level.Muted)) return new(false, "An application level could not be restored");
                    continue;
                }
                var ok = level.Kind == "device" ? _native.Device(level.Id, level.Volume, level.Muted) : _native.Session(level.Id, level.Volume, level.Muted);
                if (!ok) return new(false, "Profile could not be applied completely");
            }
            foreach (var pair in profile.Defaults) { audioRole(pair.Key, out var role); if (!_native.DefaultDevice(pair.Value, role)) return new(false, "A profile's audio role could not be restored"); }
            if (profile.Output is { Length: > 0 } output && !_native.DefaultDevice(output)) return new(false, "Profile output is unavailable");
            if (profile.Input is { Length: > 0 } input && !_native.DefaultDevice(input)) return new(false, "Profile microphone is unavailable");
            foreach (var action in profile.Actions) { var result = await Action(action, "", null, ct); if (!result.Ok) return result; }
            Invalidate(); return new(true);
        }
        finally { _profiles.Release(); }
        static bool audioRole(string text, out int role)
        {
            var pieces = text.Split(':'); role = -1;
            return pieces.Length == 2 && pieces[0] is "input" or "output" && int.TryParse(pieces[1], out role) && role is >= 0 and <= 2;
        }
    }

    private async Task<DeckResult> Action(string id, string op, int? value, CancellationToken ct)
    {
        var c = _config.Get();
        if (id.StartsWith("shortcut:", StringComparison.Ordinal))
            return await ShortcutAction(c, id[9..], op, ct);
        var action = c.Controls.Actions.FirstOrDefault(a => a.Id == id);
        if (action is null) return new(false, "Choose a configured control in desktop settings");
        switch (action.Kind)
        {
            case "task": case "server": return _tasks.Start(action, op == "stop");
            case "shortcut": case "macro": case "snippet": case "meeting": case "discord": case "project": case "app": case "focus":
                return await ShortcutAction(c, action.Target, op, ct);
            case "keys": KeySender.SendKeys(action.Target); return new(true);
            case "text": KeySender.TypeText(action.Target); return new(true);
            case "sound": case "radio":
                if (op == "stop") _native.StopSound(action.Id);
                else _native.Play(action.Id, action.Target, action.Device, Math.Clamp(action.Value, 0, 100));
                return new(true);
            case "midi":
                if (!int.TryParse(action.Device, out var port)) return new(false, "Choose a MIDI output port");
                _native.Midi(port, action.Target, value ?? action.Value); return new(true);
            case "osc":
                await SendOsc(action.Device ?? "", action.Target, value ?? action.Value, ct); return new(true);
            case "workflow": return await Workflow(action, ct);
            case "playlist": return _spotify is null ? new(false, "Spotify is unavailable") : await _spotify.Act("playlist", action.Id, ct);
            case "audio-route": return await RouteAudio(action, ct);
            case "mixer": case "mixer-mute": return MixerAction(action, value);
            default:
                var ok = await _native.PlatformAction(action.Kind, action.Target, action.Argument, value ?? action.Value, ct);
                return new(ok, ok ? null : "Integration is unavailable or requires setup on the computer");
        }
    }

    private static bool ShortcutRunning(Shortcut shortcut)
    {
        if (shortcut.Cmd.StartsWith("builtin:", StringComparison.Ordinal) || Uri.TryCreate(shortcut.Cmd, UriKind.Absolute, out var uri) && !uri.IsFile) return false;
        try
        {
            var name = Path.GetFileNameWithoutExtension(shortcut.Cmd);
            if (name.Length == 0) return false; // a folder: an empty name would match every process
            var processes = Process.GetProcessesByName(name);
            try { return processes.Any(p => p.MainWindowHandle != 0); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return false; }
    }
    private static DeckItem DescribeShortcut(Shortcut s) => new(s.Id, s.Label, s.Cmd switch
    { "builtin:keys" => "keys", "builtin:text" => "text", "builtin:macro" => "macro", _ when s.Cmd.StartsWith("builtin:", StringComparison.Ordinal) => "shortcut", _ => "app" }, ShortcutRunning(s) ? "Running" : null);
    private async Task<DeckResult> ShortcutAction(AgentConfig config, string id, string op, CancellationToken ct)
    {
        var shortcut = config.Shortcuts.FirstOrDefault(s => s.Id == id);
        if (shortcut is null) return new(false, "Configured shortcut is missing");
        if (op == "focus" && ShortcutRunning(shortcut))
        {
            var focused = await _native.PlatformAction("focus", shortcut.Cmd, null, 0, ct);
            return new(focused, focused ? null : "Windows could not focus the running application");
        }
        var error = Shortcuts.Run(config, id, _log, _push, _master);
        return new(error is null, error is null ? null : "Configured shortcut failed");
    }

    internal static byte[] OscPacket(string address, float value)
    {
        if (!address.StartsWith('/') || address.Length > 256 || address.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid OSC address");
        static byte[] Text(string s) { var raw = Encoding.UTF8.GetBytes(s); var padded = new byte[(raw.Length + 4) / 4 * 4]; raw.CopyTo(padded, 0); return padded; }
        var data = new List<byte>(Text(address)); data.AddRange(Text(",f"));
        var number = BitConverter.GetBytes(value); if (BitConverter.IsLittleEndian) Array.Reverse(number); data.AddRange(number); return data.ToArray();
    }
    private static async Task SendOsc(string endpoint, string address, int value, CancellationToken ct)
    {
        if (!Uri.TryCreate("udp://" + endpoint, UriKind.Absolute, out var uri) || uri.Port is <= 0 or > 65535) throw new ArgumentException("Configure an OSC host and port");
        using var client = new UdpClient();
        var packet = OscPacket(address, value);
        await client.SendAsync(packet, uri.Host, uri.Port, ct);
    }

    public async Task Listen(HttpResponse response, CancellationToken ct)
    {
        if (!_config.Get().Controls.AllowListening || !OperatingSystem.IsWindows()) { response.StatusCode = 403; return; }
        if (!await _listeners.WaitAsync(0, ct)) { response.StatusCode = 429; return; }
        try
        {
            response.ContentType = "application/x-ndjson";
            await foreach (var packet in _native.Listen(ct))
            {
                if (!_config.Get().Controls.AllowListening) break;
                await response.WriteAsync(JsonSerializer.Serialize(packet, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        finally { _listeners.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _ptt.Sweep();
                try { await Tick(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogDebug(ex, "Control Deck polling failed; retrying"); }
                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _ptt.Sweep(all: true); RestoreDucking(); lock (_state) foreach (var player in _fades.Keys.ToArray()) RestoreFade(player); await _tasks.StopAsync(); DisposeMixer(); _native.Dispose(); }
    }
    private async Task Tick(CancellationToken ct)
    {
        await TickSleep(ct);
        lock (_state)
        {
            if (_duckPercent is null) { RestoreDucking(); return; }
            var audio = Audio();
            if (!audio.Devices.Any(d => d.Direction == "input" && !d.Muted && d.Peak > 0.035)) { RestoreDucking(); return; }
            foreach (var s in audio.Sessions.Where(s => s.Pid != Environment.ProcessId))
            {
                if (_fades.Values.Any(levels => levels.ContainsKey(s.Id))) continue;
                if (_duck.ContainsKey(s.Id)) continue;
                var applied = (int)Math.Round(s.Volume * _duckPercent.Value / 100.0);
                if (_native.Session(s.Id, applied, null)) _duck[s.Id] = (s.Volume, applied);
            }
        }
    }
    private void RestoreDucking()
    {
        lock (_state)
        {
            if (_duck.Count == 0) return;
            var audio = _native.Audio();
            foreach (var (id, saved) in _duck.ToArray())
            {
                var session = audio.Sessions.FirstOrDefault(s => s.Id == id);
                if (session is not null && session.Volume == saved.Applied) _native.Session(id, saved.Original, null);
                _duck.Remove(id);
            }
            Invalidate();
        }
    }
}
