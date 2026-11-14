#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Midi;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Media;
using Windows.Media.Control;
using System.Windows.Forms;

namespace PhoneBox.Agent;

internal sealed partial class WindowsDeckNative(MediaMonitor? mediaMonitor = null) : IDeckNative
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (WasapiPlayer Output, WaveStream Reader, MMDevice Device)> _sounds = [];
    private GlobalSystemMediaTransportControlsSessionManager? _media;

    public event Action<string>? SoundStopped;

    public DeckAudio Audio()
    {
        lock (_gate)
        {
            var devices = new List<DeckDevice>();
            var sessions = new List<DeckSession>();
            var defaults = new Dictionary<string, string>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                {
                    string? defaultId = null;
                    for (var role = 0; role < 3; role++)
                    {
                        try { using var current = en.GetDefaultAudioEndpoint(flow, (Role)role);
                            defaults[$"{(flow == DataFlow.Render ? "output" : "input")}:{role}"] = current.ID;
                            if (role == 1) defaultId = current.ID;
                        } catch { }
                    }
                    using var all = en.EnumerateAudioEndPoints(flow, DeviceState.Active);
                    foreach (var d in all)
                    using (d)
                    {
                        try
                        {
                            var volume = d.AudioEndpointVolume;
                            devices.Add(new(d.ID, d.FriendlyName, flow == DataFlow.Render ? "output" : "input", d.ID == defaultId,
                                (int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute,
                                d.AudioMeterInformation.MasterPeakValue, volume.Channels.Count, true,
                                Enumerable.Range(0, volume.Channels.Count).Select(i => (int)Math.Round(volume.Channels[i].VolumeLevelScalar * 100)).ToArray()));
                            if (flow != DataFlow.Render) continue;
                            var list = d.AudioSessionManager.Sessions;
                            for (var i = 0; i < list.Count; i++)
                            using (var s = list[i])
                            {
                                try
                                {
                                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                                    var pid = (int)s.GetProcessID;
                                    var name = pid == 0 ? "System sounds" : string.IsNullOrWhiteSpace(s.DisplayName) || s.DisplayName.StartsWith('@') ? ProcessName(pid) : s.DisplayName;
                                    sessions.Add(new(s.GetSessionInstanceIdentifier, name, pid, d.ID,
                                        (int)Math.Round(s.SimpleAudioVolume.Volume * 100), s.SimpleAudioVolume.Mute,
                                        s.AudioMeterInformation.MasterPeakValue));
                                }
                                catch (COMException) { /* A session can end during enumeration; keep the other sessions. */ }
                            }
                        }
                        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException) { }
                    }
                }
                return new(true, null, devices, sessions, defaults);
            }
            catch { return new(false, "Windows audio is unavailable", devices, sessions); }
        }
    }

    private static string ProcessName(int pid)
    {
        if (pid == 0) return "System sounds";
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return "Application"; }
    }
    public bool Device(string id, int? volume, bool? muted, int? channel = null)
    {
        lock (_gate)
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDevice(id);
            if (d.State != DeviceState.Active) return false;
            if (volume is { } v)
            {
                if (v is < 0 or > 100) return false;
                if (channel is { } c)
                {
                    if (c < 0 || c >= d.AudioEndpointVolume.Channels.Count) return false;
                    d.AudioEndpointVolume.Channels[c].VolumeLevelScalar = v / 100f;
                }
                else d.AudioEndpointVolume.MasterVolumeLevelScalar = v / 100f;
            }
            if (muted is { } m) d.AudioEndpointVolume.Mute = m;
            return true;
        }
    }
    public bool Session(string id, int? volume, bool? muted)
    {
        lock (_gate)
        {
            if (volume is < 0 or > 100) return false;
            using var en = new MMDeviceEnumerator();
            using var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in all)
            using (d)
            {
                var list = d.AudioSessionManager.Sessions;
                for (var i = 0; i < list.Count; i++)
                using (var s = list[i])
                {
                    if (s.GetSessionInstanceIdentifier != id) continue;
                    if (volume is { } v) s.SimpleAudioVolume.Volume = v / 100f;
                    if (muted is { } m) s.SimpleAudioVolume.Mute = m;
                    return true;
                }
            }
            return false;
        }
    }
    public bool DefaultDevice(string id, int? role = null)
    {
        if (!Audio().Devices.Any(d => d.Id == id)) return false;
        var policy = (IPolicyConfig)new PolicyConfig();
        try
        {
            if (role is < 0 or > 2) return false;
            foreach (var selected in role is { } r ? new[] { r } : new[] { 0, 1, 2 })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, selected));
            var state = Audio();
            var direction = state.Devices.First(d => d.Id == id).Direction;
            return (role is { } selectedRole ? new[] { selectedRole } : new[] { 0, 1, 2 })
                .All(r => state.Defaults?.GetValueOrDefault($"{direction}:{r}") == id);
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    public async Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct)
    {
        _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var currentSession = _media.GetCurrentSession();
        var result = new List<(DeckMedia Item, bool IsCurrent)>();
        foreach (var session in _media.GetSessions())
        {
            ct.ThrowIfCancellationRequested();
            var properties = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var duration = Math.Max(0, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds);
            var position = (long)(timeline.Position - timeline.StartTime).TotalMilliseconds;
            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                position += (long)Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds);
            var name = session.SourceAppUserModelId;
            if (name.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) name = "Spotify";
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            result.Add((new(session.SourceAppUserModelId, name, properties.Title,
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Math.Clamp(position, 0, duration), duration,
                info.Controls.IsPlaybackPositionEnabled, info.Controls.IsShuffleEnabled, info.Controls.IsRepeatEnabled,
                info.IsShuffleActive ?? false, info.AutoRepeatMode?.ToString() ?? "None"),
                ReferenceEquals(session, currentSession)));
        }
        return result.OrderByDescending(x => x.IsCurrent).Select(x => x.Item).ToArray();
    }
    public async Task<(bool Ok, bool WasPlaying)> MediaActionWithState(string id, string action, long value, CancellationToken ct)
    {
        _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var cur = _media.GetCurrentSession();
        var s = (cur?.SourceAppUserModelId == id ? cur : null) ?? _media.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId == id);
        if (s is null) return (false, false);
        ct.ThrowIfCancellationRequested();
        var wasPlaying = action == "playpause"
            && s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var ok = action switch
        {
            "playpause" => await s.TryTogglePlayPauseAsync(), "next" => await s.TrySkipNextAsync(),
            "prev" => await s.TrySkipPreviousAsync(), "pause" => await s.TryPauseAsync(),
            "seek" when s.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled && value >= 0
                && value <= (s.GetTimelineProperties().EndTime - s.GetTimelineProperties().StartTime).TotalMilliseconds =>
                    await s.TryChangePlaybackPositionAsync(s.GetTimelineProperties().StartTime.Ticks + TimeSpan.FromMilliseconds(value).Ticks),
            "shuffle" when s.GetPlaybackInfo().Controls.IsShuffleEnabled => await s.TryChangeShuffleActiveAsync(value != 0),
            "repeat" when s.GetPlaybackInfo().Controls.IsRepeatEnabled && value is >= 0 and <= 2 =>
                await s.TryChangeAutoRepeatModeAsync((MediaPlaybackAutoRepeatMode)value),
            _ => false,
        };
        return (ok, wasPlaying);
    }
    public async Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) =>
        (await MediaActionWithState(id, action, value, ct)).Ok;

    public async Task<bool> PauseCurrentMedia(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (mediaMonitor is not null) return await mediaMonitor.PauseAsync();
        _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var s = _media.GetCurrentSession();
        if (s is null || s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return false;
        ct.ThrowIfCancellationRequested();
        return await s.TryPauseAsync();
    }

    public void Play(string id, string file, string? device, int volume)
    {
        lock (_gate)
        {
            StopSound(id);
            using var en = new MMDeviceEnumerator();
            var output = string.IsNullOrWhiteSpace(device) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(device);
            WaveStream? reader = null; WasapiPlayer? player = null;
            try
            {
                if (Uri.TryCreate(file, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                    reader = new WaveChannel32(new MediaFoundationReader(file), Math.Clamp(volume, 0, 100) / 100f, 0) { PadWithZeroes = false };
                else reader = new AudioFileReader(file) { Volume = Math.Clamp(volume, 0, 100) / 100f };
                player = new WasapiPlayerBuilder().WithDevice(output).WithSharedMode().WithLatency(100).Build();
                player.Init(reader); _sounds[id] = (player, reader, output);
                var own = player;
                player.PlaybackStopped += (_, _) => _ = Task.Run(() =>
                {
                    var ended = false;
                    lock (_gate)
                        if (_sounds.TryGetValue(id, out var sound) && ReferenceEquals(sound.Output, own))
                        {
                            StopSound(id);
                            ended = true;
                        }
                    if (ended) SoundStopped?.Invoke(id);
                });
                player.Play();
            }
            catch { _sounds.Remove(id); player?.Dispose(); reader?.Dispose(); output.Dispose(); throw; }
        }
    }
    public void StopSound(string? id)
    {
        lock (_gate)
        foreach (var key in _sounds.Keys.Where(k => id is null || k == id).ToArray())
        {
            var sound = _sounds[key]; _sounds.Remove(key);
            sound.Output.Stop(); sound.Output.Dispose(); sound.Reader.Dispose(); sound.Device.Dispose();
        }
    }
    public IReadOnlyList<DeckItem> MidiPorts() => Enumerable.Range(0, MidiOut.NumberOfDevices)
        .Select(i => new DeckItem(i.ToString(), MidiOut.DeviceInfo(i).ProductName, "midi")).ToArray();
    public void Midi(int port, string message, int value)
    {
        if (port < 0 || port >= MidiOut.NumberOfDevices) throw new ArgumentOutOfRangeException(nameof(port));
        using var output = new MidiOut(port);
        var packed = DeckMidi.Parse(message, value);
        output.Send(packed);
        if ((packed & 0xF0) == 0x90 && (packed >> 16 & 127) > 0)
        {
            // Pads are momentary: never leave a note sounding after a lost phone connection.
            try { Thread.Sleep(120); }
            finally { output.Send((packed & 0xFF0F) | 0x80); }
        }
    }

    public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var queue = Channel.CreateBounded<DeckAudioPacket>(new BoundedChannelOptions(12) { FullMode = BoundedChannelFullMode.DropOldest });
        using var capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
        var format = capture.WaveFormat;
        var encoding = format is WaveFormatExtensible ext
            ? ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? "IeeeFloat" : "Pcm"
            : format.Encoding.ToString();
        capture.DataAvailable += (buffer, flags, position, qpc) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                queue.Writer.TryWrite(new(format.SampleRate, format.Channels, format.BitsPerSample, encoding, Convert.ToBase64String(buffer)));
        };
        capture.StartRecording();
        try
        {
            var last = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                while (queue.Reader.TryRead(out var packet)) { last = Environment.TickCount64; yield return packet; }
                if (Environment.TickCount64 - last > 500)
                { last = Environment.TickCount64; yield return new(format.SampleRate, format.Channels, format.BitsPerSample, encoding, null); }
                await Task.Delay(30, ct);
            }
        }
        finally { capture.StopRecording(); }
    }

    public void Dispose() => StopSound(null);

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfig;
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
#endif
