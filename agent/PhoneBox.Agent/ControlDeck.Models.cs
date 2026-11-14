namespace PhoneBox.Agent;

public sealed class ControlDeckConfig
{
    public bool AllowListening { get; set; }
    public bool AllowPower { get; set; }
    public List<DeckAction> Actions { get; set; } = [];
    public List<DeckProfile> Profiles { get; set; } = [];
    public string? SpotifyToken { get; set; }
    public string? SpotifyDevice { get; set; }
    public string? MixerExecutable { get; set; }
    public string? MixerLibrary { get; set; }
    public string? SpotifyClientId { get; set; }
    public string? SpotifyRefreshToken { get; set; }
}

/// <summary>Computer-side bindings. Only Id/Label/Kind are sent to a phone.</summary>
public sealed class DeckAction
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "shortcut";
    public string Target { get; set; } = "";
    public string? Argument { get; set; }
    public int Value { get; set; }
    public string? Device { get; set; }
}
public sealed class DeckProfile
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<DeckLevel> Levels { get; set; } = [];
    public List<string> Actions { get; set; } = [];
    public string? Output { get; set; }
    public string? Input { get; set; }
    public Dictionary<string, string> Defaults { get; set; } = [];
}
public sealed record DeckLevel(string Kind, string Id, int Volume, bool Muted);
public sealed record DeckItem(string Id, string Name, string Kind, string? State = null, bool Available = true);
public sealed record DeckDevice(string Id, string Name, string Direction, bool Default, int Volume, bool Muted,
    double Peak, int Channels, bool CanControl = true, IReadOnlyList<int>? ChannelVolumes = null);
public sealed record DeckSession(string Id, string Name, int Pid, string DeviceId, int Volume, bool Muted, double Peak);
public sealed record DeckAudio(bool Available, string? Error, IReadOnlyList<DeckDevice> Devices, IReadOnlyList<DeckSession> Sessions,
    IReadOnlyDictionary<string, string>? Defaults = null);
public sealed record DeckMedia(string Id, string Name, string Title, bool Playing, long Position, long Duration,
    bool CanSeek, bool CanShuffle, bool CanRepeat, bool Shuffle, string Repeat);
public sealed record DeckMonitor(string Id, string Name, int? Brightness, int Width, int Height, int Hz);
public sealed record DeckResult(bool Ok, string? Error = null, string? Lease = null);
public sealed record DeckAudioPacket(int Rate, int Channels, int Bits, string Encoding, string? Data);

internal interface IDeckNative : IDisposable
{
    event Action<string>? SoundStopped { add { } remove { } }
    DeckAudio Audio();
    bool Device(string id, int? volume, bool? muted, int? channel = null);
    bool Session(string id, int? volume, bool? muted);
    bool DefaultDevice(string id, int? role = null);
    IReadOnlyList<DeckMonitor> Monitors();
    bool Brightness(string id, int value);
    IReadOnlyList<DeckItem> PowerPlans();
    Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct);
    string? ActionState(DeckAction action) => null;
    Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct);
    Task<bool> MediaAction(string id, string action, long value, CancellationToken ct);
    async Task<(bool Ok, bool WasPlaying)> MediaActionWithState(string id, string action, long value, CancellationToken ct)
    {
        var wasPlaying = false;
        if (action == "playpause")
        {
            try { wasPlaying = (await Media(ct)).FirstOrDefault(p => p.Id == id)?.Playing == true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return (await MediaAction(id, action, value, ct), wasPlaying);
    }
    Task<bool> PauseCurrentMedia(CancellationToken ct) => Task.FromResult(false);
    void Play(string id, string file, string? device, int volume);
    void StopSound(string? id);
    IReadOnlyList<DeckItem> MidiPorts();
    void Midi(int port, string message, int value);
    IAsyncEnumerable<DeckAudioPacket> Listen(CancellationToken ct);
}
