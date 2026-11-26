using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// "Unmute microphones" (#645): opens again only the microphones the emergency mute or "Mics off, camera off" closed
/// while they were open, never one the user keeps muted or one only a push-to-talk hold had opened.
/// </summary>
public sealed class ControlDeckUnmuteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-unmute-" + Guid.NewGuid().ToString("N"));
    private readonly string _cfg;

    public ControlDeckUnmuteTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = Path.Combine(_dir, "config.json");
        File.WriteAllText(_cfg, "{}");
    }

    private ControlDeck Deck(MicsNative native) =>
        new(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance, camera: new StoppedCamera());

    private static Task<DeckResult> Act(ControlDeck deck, string kind, string? id = null, string? op = null, bool? muted = null, string? lease = null) =>
        deck.Act(kind, id, op, null, muted, lease, default);

    private static async Task<int> ToUnmute(ControlDeck deck) =>
        JsonSerializer.SerializeToElement(await deck.Snapshot(default)).GetProperty("micsToUnmute").GetInt32();

    [Theory]
    [InlineData("panic")]
    [InlineData("mics-camera-off")]
    public async Task OpensOnlyTheMicrophonesThatWereOpen(string mute)
    {
        // the desk microphone is open, the headset's is muted on purpose
        var native = new MicsNative(("desk", false), ("headset", true));
        using var deck = Deck(native);
        Assert.Equal(0, await ToUnmute(deck));

        await Act(deck, mute);
        Assert.True(native.Muted("desk"));
        Assert.True(native.Muted("headset"));
        Assert.Equal(1, await ToUnmute(deck));

        var result = await Act(deck, "mics-on");
        Assert.True(result.Ok);
        Assert.Equal("unmuted", result.Mics);
        Assert.False(native.Muted("desk"));
        Assert.True(native.Muted("headset"));
        if (mute == "panic") Assert.True(native.Muted("spk")); // the outputs are not this button's business
        Assert.Equal(0, await ToUnmute(deck));

        // a second tap has nothing left to do
        var again = await Act(deck, "mics-on");
        Assert.False(again.Ok);
        Assert.Equal("none", again.Mics);
    }

    [Fact]
    public async Task NothingToUnmuteBeforeAnyMute()
    {
        var native = new MicsNative(("desk", true));
        using var deck = Deck(native);
        var result = await Act(deck, "mics-on");
        Assert.False(result.Ok);
        Assert.Equal("none", result.Mics);
        Assert.True(native.Muted("desk"));
        Assert.DoesNotContain(native.Calls, c => c.Muted == false);
    }

    [Fact]
    public async Task AMicrophoneOnlyAHoldOpenedStaysMuted()
    {
        var native = new MicsNative(("desk", true));
        using var deck = Deck(native);
        var hold = await Act(deck, "ptt", "desk", "begin");
        Assert.True(hold.Ok);
        Assert.False(native.Muted("desk")); // open only while the talk key is held

        await Act(deck, "panic");
        Assert.True(native.Muted("desk"));
        Assert.Equal(0, await ToUnmute(deck));
        Assert.Equal("none", (await Act(deck, "mics-on")).Mics);
        Assert.True(native.Muted("desk"));
    }

    [Fact]
    public async Task TheUsersOwnMuteChoiceWins()
    {
        var native = new MicsNative(("desk", false));
        using var deck = Deck(native);
        await Act(deck, "panic");
        // the user opens it and mutes it again by hand: that is now the state they chose
        Assert.True((await Act(deck, "device", "desk", muted: false)).Ok);
        Assert.True((await Act(deck, "device", "desk", muted: true)).Ok);
        Assert.Equal(0, await ToUnmute(deck));
        Assert.Equal("none", (await Act(deck, "mics-on")).Mics);
        Assert.True(native.Muted("desk"));
    }

    [Fact]
    public async Task UnmutingDropsAPendingPanicMuteSoTheRetryCannotCloseItAgain()
    {
        var native = new MicsNative(("desk", false)) { FailMute = true };
        using var deck = Deck(native);
        Assert.False((await Act(deck, "panic")).Ok); // the microphone refused the mute: it stays pending
        Assert.False(native.Muted("desk"));

        native.FailMute = false;
        Assert.True((await Act(deck, "mics-on")).Ok);
        // a push-to-talk request retries pending mutes; there is none left, so the microphone stays open
        var hold = await Act(deck, "ptt", "desk", "begin");
        Assert.True(hold.Ok);
        Assert.True((await Act(deck, "ptt", "desk", "end", lease: hold.Lease)).Ok);
        Assert.False(native.Muted("desk"));
    }

    [Fact]
    public async Task AFailedUnmuteIsKeptForTheNextTap()
    {
        var native = new MicsNative(("desk", false));
        using var deck = Deck(native);
        await Act(deck, "panic");

        native.FailUnmute = true;
        var failed = await Act(deck, "mics-on");
        Assert.False(failed.Ok);
        Assert.Equal("failed", failed.Mics);
        Assert.True(native.Muted("desk"));
        Assert.Equal(1, await ToUnmute(deck));

        native.FailUnmute = false;
        Assert.True((await Act(deck, "mics-on")).Ok);
        Assert.False(native.Muted("desk"));
    }

    [Fact]
    public async Task AMicrophoneThatIsGoneIsForgotten()
    {
        var native = new MicsNative(("desk", false));
        using var deck = Deck(native);
        await Act(deck, "panic");
        native.Unplug("desk");
        Assert.Equal(0, await ToUnmute(deck));
        Assert.Equal("none", (await Act(deck, "mics-on")).Mics);

        // plugged in again later, muted: no longer this button's to open
        native.Plug("desk", muted: true);
        Assert.Equal(0, await ToUnmute(deck));
        Assert.True(native.Muted("desk"));
    }

    [Fact]
    public async Task AHoldThatEndsJustBeforeTheMuteDoesNotCountAsOpen()
    {
        // the first reading still shows the microphone open, but by the time the mute runs a hold that just expired has
        // closed it again (MicrophoneLeases.Sweep): the reading taken under the gate is the one that counts
        var native = new MicsNative(("desk", false));
        var reads = 0;
        native.OnAudio = () => { if (++reads == 2) native.Plug("desk", muted: true); };
        using var deck = Deck(native);
        await Act(deck, "panic");
        Assert.True(native.Muted("desk"));
        Assert.Equal(0, await ToUnmute(deck));
    }

    [Fact]
    public async Task TheListIsKeptWhileTheDevicesCannotBeRead()
    {
        var native = new MicsNative(("desk", false));
        using var deck = Deck(native);
        await Act(deck, "panic");

        native.Available = false;
        Assert.Equal(0, await ToUnmute(deck));
        var failed = await Act(deck, "mics-on");
        Assert.False(failed.Ok);
        Assert.Equal("failed", failed.Mics);

        // the devices are back (the deck's own reading is cached for 400 ms, so the tap is what reads them again)
        native.Available = true;
        Assert.True((await Act(deck, "mics-on")).Ok);
        Assert.False(native.Muted("desk"));
    }

    [Fact]
    public async Task AHeldTalkKeyDoesNotCloseAMicrophoneUnmutedWhileHeld()
    {
        var native = new MicsNative(("desk", false));
        using var deck = Deck(native);
        await Act(deck, "panic");
        var hold = await Act(deck, "ptt", "desk", "begin"); // saves "muted" as the state to go back to
        Assert.True(hold.Ok);
        Assert.True((await Act(deck, "mics-on")).Ok);
        Assert.True((await Act(deck, "ptt", "desk", "end", lease: hold.Lease)).Ok);
        Assert.False(native.Muted("desk"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StoppedCamera : IVirtualCamera
    {
        public Task<VirtualCameraStop> StopVirtualCamAsync(CancellationToken ct) => Task.FromResult(VirtualCameraStop.Stopped);
    }

    /// <summary>One speaker and the given microphones (id, muted).</summary>
    private sealed class MicsNative : IDeckNative
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, bool> _mics = [];
        private bool _speakerMuted;
        public volatile bool FailMute, FailUnmute;
        public volatile bool Available = true;
        public Action? OnAudio;
        public System.Collections.Concurrent.ConcurrentQueue<(string Id, bool? Muted)> Calls { get; } = new();

        public MicsNative(params (string Id, bool Muted)[] mics)
        {
            foreach (var (id, muted) in mics) _mics[id] = muted;
        }

        public bool Muted(string id) { lock (_gate) return id == "spk" ? _speakerMuted : _mics[id]; }
        public void Unplug(string id) { lock (_gate) _mics.Remove(id); }
        public void Plug(string id, bool muted) { lock (_gate) _mics[id] = muted; }

        public DeckAudio Audio()
        {
            OnAudio?.Invoke();
            lock (_gate)
                return new(Available, null,
                    [new("spk", "Speakers", "output", true, 60, _speakerMuted, 0, 2, CanControl: true),
                     .. _mics.Select(m => new DeckDevice(m.Key, m.Key, "input", false, 80, m.Value, 0, 1, CanControl: true))], []);
        }

        public bool Device(string id, int? volume, bool? muted, int? channel = null)
        {
            Calls.Enqueue((id, muted));
            lock (_gate)
            {
                if (id == "spk") { if (muted is { } s) _speakerMuted = s; return true; }
                if (!_mics.ContainsKey(id)) return false;
                if (muted == true && FailMute || muted == false && FailUnmute) return false;
                if (muted is { } m) _mics[id] = m;
                return true;
            }
        }

        public bool Session(string id, int? volume, bool? muted) => false;
        public bool DefaultDevice(string id, int? role = null) => false;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(false);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }
}
