using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Panic mute wins over push-to-talk: no hold reopens a microphone panic closed, or meant to close.</summary>
public sealed class ControlDeckPanicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-panic-" + Guid.NewGuid().ToString("N"));
    private readonly string _cfg;

    public ControlDeckPanicTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = Path.Combine(_dir, "config.json");
        File.WriteAllText(_cfg, "{}");
    }

    [Fact]
    public async Task AHoldThatBeginsDuringPanicCannotReopenTheMicrophone()
    {
        var native = new PanicNative(micMuted: false) { HoldSpeakerMute = true };
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance);
        try
        {
            var panic = Task.Run(() => deck.Act("panic", null, null, null, null, null, default));
            Assert.True(native.SpeakerMuteEntered.Wait(TimeSpan.FromSeconds(5))); // panic is between its first and last mute
            var begin = Task.Run(() => deck.Act("ptt", "mic-1", "begin", null, null, null, default));
            await Task.WhenAny(begin, Task.Delay(300));
            native.ReleaseSpeakerMute.Set();
            Assert.True((await panic.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            var hold = await begin.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(hold.Ok);
            Assert.True((await deck.Act("ptt", "mic-1", "end", null, null, Assert.IsType<string>(hold.Lease), default)).Ok);
            Assert.True(native.MicMuted);
        }
        finally { native.ReleaseSpeakerMute.Set(); }
    }

    [Fact]
    public async Task AFailedPanicMuteStaysPendingAndBlocksPushToTalkUntilItLands()
    {
        var native = new PanicNative(micMuted: true);
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance);
        var hold = await deck.Act("ptt", "mic-1", "begin", null, null, null, default);
        Assert.True(hold.Ok);
        Assert.False(native.MicMuted);

        native.FailMicMute = true;
        Assert.False((await deck.Act("panic", null, null, null, null, null, default)).Ok);
        Assert.False(native.MicMuted);
        Assert.False((await deck.Act("ptt", "mic-1", "begin", null, null, null, default)).Ok);

        native.FailMicMute = false;
        // the hold ended with panic; the pending mute lands on the next push-to-talk request
        Assert.False((await deck.Act("ptt", "mic-1", "end", null, null, Assert.IsType<string>(hold.Lease), default)).Ok);
        Assert.True(native.MicMuted);
    }

    [Fact]
    public async Task AFailedManualMuteKeepsThePanicMutePending()
    {
        var native = new PanicNative(micMuted: true);
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance);
        var hold = await deck.Act("ptt", "mic-1", "begin", null, null, null, default);
        Assert.True(hold.Ok);

        native.FailMicMute = true;
        Assert.False((await deck.Act("panic", null, null, null, null, null, default)).Ok);
        // the user taps mute on the panel, and that fails too: the panic mute must stay pending
        Assert.False((await deck.Act("device", "mic-1", null, null, true, null, default)).Ok);
        Assert.False((await deck.Act("ptt", "mic-1", "begin", null, null, null, default)).Ok);
        Assert.False(native.MicMuted);
    }

    // "Mics off, camera off" (#387): panic's microphone path without the outputs, plus the OBS virtual camera

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MicsCameraOffMutesTheMicrophonesLeavesTheOutputsAndStopsTheCamera(bool speakerMuted)
    {
        var native = new PanicNative(micMuted: false, speakerMuted);
        var camera = new FakeCamera(VirtualCameraStop.Stopped);
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance, camera: camera);
        var result = await deck.Act("mics-camera-off", null, null, null, null, null, default);
        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal("muted", result.Mics);
        Assert.Equal("stopped", result.Camera);
        Assert.True(native.MicMuted);
        Assert.Equal(speakerMuted, native.SpeakerMuted);
        Assert.DoesNotContain("spk-1", native.Calls); // the outputs are not even asked
        Assert.Equal(1, camera.Calls);
    }

    [Fact]
    public async Task MicsCameraOffKeepsARefusedMicrophonePendingAndClosedToPushToTalk()
    {
        var native = new PanicNative(micMuted: true);
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance, camera: new FakeCamera(VirtualCameraStop.Stopped));
        var hold = await deck.Act("ptt", "mic-1", "begin", null, null, null, default);
        Assert.True(hold.Ok);
        Assert.False(native.MicMuted);

        native.FailMicMute = true;
        var result = await deck.Act("mics-camera-off", null, null, null, null, null, default);
        Assert.False(result.Ok);
        Assert.Equal("pending", result.Mics);
        Assert.Equal("stopped", result.Camera);
        Assert.Contains("could not be muted", result.Error);
        Assert.False(native.MicMuted);
        // the held talk key can neither keep the microphone nor take it again
        Assert.False((await deck.Act("ptt", "mic-1", "begin", null, null, null, default)).Ok);

        native.FailMicMute = false;
        // the hold was dropped; the pending mute lands on the next push-to-talk request
        Assert.False((await deck.Act("ptt", "mic-1", "end", null, null, Assert.IsType<string>(hold.Lease), default)).Ok);
        Assert.True(native.MicMuted);
        Assert.DoesNotContain("spk-1", native.Calls);
    }

    [Theory]
    [InlineData(VirtualCameraStop.NotConnected, "not-connected", "OBS is not connected")]
    [InlineData(VirtualCameraStop.Failed, "failed", "OBS did not stop the virtual camera")]
    public async Task MicsCameraOffNeverReportsACameraObsDidNotStop(VirtualCameraStop outcome, string code, string reason)
    {
        var native = new PanicNative(micMuted: false);
        var camera = new FakeCamera(outcome);
        using var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance, camera: camera);
        var result = await deck.Act("mics-camera-off", null, null, null, null, null, default);
        Assert.False(result.Ok);
        Assert.Equal("muted", result.Mics);
        Assert.Equal(code, result.Camera);
        Assert.Contains("Camera not turned off: " + reason, result.Error);
        Assert.True(native.MicMuted); // the microphones are muted all the same
        Assert.Equal(1, camera.Calls);
    }

    [Fact]
    public async Task MicsCameraOffWithoutObsOrMicrophonesSaysSo()
    {
        // no OBS at all: the camera is not reported as off
        var native = new PanicNative(micMuted: false);
        using (var deck = new ControlDeck(new ConfigStore(_cfg), native, NullLogger<ControlDeck>.Instance))
        {
            var result = await deck.Act("mics-camera-off", null, null, null, null, null, default);
            Assert.False(result.Ok);
            Assert.Equal("not-connected", result.Camera);
            Assert.True(native.MicMuted);
        }

        // no audio: the camera is still stopped, and the microphones are not reported as muted
        var silent = new PanicNative(micMuted: false) { AudioAvailable = false };
        var camera = new FakeCamera(VirtualCameraStop.AlreadyOff);
        using (var deck = new ControlDeck(new ConfigStore(_cfg), silent, NullLogger<ControlDeck>.Instance, camera: camera))
        {
            var result = await deck.Act("mics-camera-off", null, null, null, null, null, default);
            Assert.False(result.Ok);
            Assert.Equal("unavailable", result.Mics);
            Assert.Equal("off", result.Camera);
            Assert.Equal(1, camera.Calls);
            Assert.Empty(silent.Calls);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeCamera(VirtualCameraStop outcome) : IVirtualCamera
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<VirtualCameraStop> StopVirtualCamAsync(CancellationToken ct) { Interlocked.Increment(ref _calls); return Task.FromResult(outcome); }
    }

    private sealed class PanicNative(bool micMuted, bool speakerMuted = false) : IDeckNative
    {
        private volatile bool _micMuted = micMuted;
        private volatile bool _speakerMuted = speakerMuted;
        public bool MicMuted => _micMuted;
        public bool SpeakerMuted => _speakerMuted;
        public bool AudioAvailable { get; init; } = true;
        // every device the deck asked to change, in order
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
        public volatile bool FailMicMute;
        public bool HoldSpeakerMute { get; init; }
        public ManualResetEventSlim SpeakerMuteEntered { get; } = new();
        public ManualResetEventSlim ReleaseSpeakerMute { get; } = new();

        public DeckAudio Audio() => new(AudioAvailable, null,
        [
            new("spk-1", "Speakers", "output", true, 60, _speakerMuted, 0, 2, CanControl: true),
            new("mic-1", "Desk Mic", "input", true, 80, _micMuted, 0, 1, CanControl: true),
        ], []);

        public bool Device(string id, int? volume, bool? muted, int? channel = null)
        {
            Calls.Enqueue(id);
            if (id == "spk-1")
            {
                if (muted == true && HoldSpeakerMute && !SpeakerMuteEntered.IsSet)
                {
                    SpeakerMuteEntered.Set();
                    ReleaseSpeakerMute.Wait(TimeSpan.FromSeconds(10));
                }
                if (muted is { } m) _speakerMuted = m;
                return true;
            }
            if (id == "mic-1")
            {
                if (muted == true && FailMicMute) return false;
                if (muted is { } m) _micMuted = m;
                return true;
            }
            return false;
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
