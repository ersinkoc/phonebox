#if !WINDOWS
namespace PhoneBox.Agent;

/// <summary>Existing master/media controls remain available; native-only features report unavailable explicitly.</summary>
internal sealed class PortableDeckNative(AudioControl audio, MediaMonitor media) : IDeckNative
{
    public DeckAudio Audio() => audio.Snapshot() is { } a ? new(true, null,
        [new("default", "System output", "output", true, a.Volume, a.Muted, 0, 1, a.CanControl)], [])
        : new(false, "Audio backend is unavailable", [], []);
    public bool Device(string id, int? volume, bool? muted, int? channel = null)
    {
        if (id != "default" || channel is not null || audio.Snapshot() is not { CanControl: true } current) return false;
        if (volume is { } v && !audio.SetVolume(v)) return false;
        return muted is not { } m || m == current.Muted || audio.ToggleMute();
    }
    public bool Session(string id, int? volume, bool? muted) => false;
    public bool DefaultDevice(string id, int? role = null) => false;
    public IReadOnlyList<DeckMonitor> Monitors() => [];
    public bool Brightness(string id, int value) => false;
    public IReadOnlyList<DeckItem> PowerPlans() => [];
    public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(media.Snapshot() is { } m
        ? [new("current", m.App, m.Title, m.Playing, m.PosMs, m.DurMs, m.CanSeek, false, false, false, "None")] : []);
    public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) =>
        id != "current" ? Task.FromResult(false) : action == "pause" ? media.PauseAsync() : media.Control(action, value);
    public Task<bool> PauseCurrentMedia(CancellationToken ct) => media.PauseAsync();
    public void Play(string id, string file, string? device, int volume) => throw new NotSupportedException("Native sound playback requires Windows");
    public void StopSound(string? id) { }
    public IReadOnlyList<DeckItem> MidiPorts() => [];
    public void Midi(int port, string message, int value) => throw new NotSupportedException("Native MIDI requires Windows");
    public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.CompletedTask; yield break; }
    public void Dispose() { }
}
#endif
