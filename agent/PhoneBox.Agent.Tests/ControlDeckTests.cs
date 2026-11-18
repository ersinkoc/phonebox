using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ControlDeckTests
{
    [Fact]
    public void PushToTalkExpiresAndRestoresTheOriginalState()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone");
        Assert.NotNull(id); Assert.False(muted);
        time = 2999; leases.Sweep(); Assert.False(muted);
        time = 3000; leases.Sweep(); Assert.True(muted);
        Assert.False(leases.Renew(id!));
    }
    [Fact]
    public void OverlappingHoldsDoNotRestoreTheMicrophoneUntilTheLastRelease()
    {
        var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        var a = leases.Begin("microphone"); var b = leases.Begin("microphone");
        Assert.True(leases.End(a!)); Assert.False(muted);
        Assert.True(leases.End(b!)); Assert.True(muted);
    }
    [Fact]
    public void ADisconnectDoesNotMuteAPreviouslyUnmutedMicrophone()
    {
        var muted = false;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        leases.Begin("microphone"); leases.Sweep(all: true); Assert.False(muted);
    }
    [Fact]
    public void RenewalsCannotKeepAMicrophoneOpenIndefinitely()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone")!;
        for (time = 1000; time < 60_000; time += 1000) Assert.True(leases.Renew(id));
        Assert.False(leases.Renew(id)); Assert.True(muted);
    }
    [Fact]
    public void PowerConfirmationIsDelayedBoundToTheActionAndSingleUse()
    {
        long now = 0; var power = new PowerConfirmations(() => now);
        var nonce = power.Arm("restart");
        Assert.False(power.Consume(nonce, "restart"));
        now = 2000; Assert.False(power.Consume(nonce, "shutdown"));
        Assert.True(power.Consume(nonce, "restart")); Assert.False(power.Consume(nonce, "restart"));
        nonce = power.Arm("sleep"); now += 6001; Assert.False(power.Consume(nonce, "sleep"));
    }
    [Fact]
    public void OscUsesPaddedStringsAndNetworkByteOrder()
    {
        Assert.Equal(new byte[] { 47, 102, 0, 0, 44, 102, 0, 0, 63, 128, 0, 0 }, ControlDeck.OscPacket("/f", 1));
        Assert.Throws<ArgumentException>(() => ControlDeck.OscPacket("not-an-address", 1));
    }
    [Fact]
    public void MidiBytesUseWireOrderAndRejectSystemOrOutOfRangeMessages()
    {
        Assert.Equal(0x6407B0, DeckMidi.Parse("B0 07 {value}",100));
        Assert.Equal(0x003CC0, DeckMidi.Parse("C0 3C",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("F0 00 00",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 FF 10",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 3C",0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DeckMidi.Parse("B0 07 {value}",128));
    }
    [Fact]
    public void PhoneConfigurationDoesNotExposeControlSecretsOrCommands()
    {
        var cfg = new AgentConfig { Controls = new() { SpotifyToken = "private-token", Actions = [new() { Id = "sound", Target = "private-file-path" }] } };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg.ToPublic());
        Assert.DoesNotContain("private-token", json); Assert.DoesNotContain("private-file-path", json);
        Assert.True(Lan.AllowedOverLan("/api/controls/state"));
        Assert.False(Lan.AllowedOverLan("/api/controls/capture"));
    }

    [Fact]
    public async Task NextAndPreviousSkipDisabledStationsAndADisabledStationIsNotKeptAsPaused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-a", "label": "A", "kind": "radio", "target": "https://example.com/a.mp3", "value": 80 },
                      { "id": "r-b", "label": "B", "kind": "radio", "target": "https://example.com/b.mp3", "value": 80, "enabled": false },
                      { "id": "r-c", "label": "C", "kind": "radio", "target": "https://example.com/c.mp3", "value": 80 }
                    ]
                  }
                }
                """);
            var native = new FakeRadioDeckNative();
            var store = new ConfigStore(cfgPath);
            using var deck = new ControlDeck(store, native, NullLogger<ControlDeck>.Instance);
            var idleSys = new MediaSnapshot("Spotify", "Track 1", "Artist 1", "Album 1", false, 0, 180_000, 1, true);

            Assert.True((await deck.Act("action", "r-a", "", null, null, null, default)).Ok);
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-c"], native.PlayingRadios.ToArray());   // B is disabled
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("prev", idleSys, default));
            Assert.Equal(["r-a"], native.PlayingRadios.ToArray());

            // disabling the playing station stops it and leaves nothing to resume
            var json = File.ReadAllText(cfgPath).Replace("\"id\": \"r-a\", \"label\": \"A\", \"kind\": \"radio\", \"target\": \"https://example.com/a.mp3\", \"value\": 80 }",
                "\"id\": \"r-a\", \"label\": \"A\", \"kind\": \"radio\", \"target\": \"https://example.com/a.mp3\", \"value\": 80, \"enabled\": false }");
            Assert.Null(store.Save(json));
            deck.StopDisabledControls();
            deck.ReconcileRadios(waitForGate: true);
            Assert.Empty(native.PlayingRadios);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));
            Assert.Null(await deck.TryControlRadioMediaAsync("playpause", idleSys, default));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NowPlayingShowsActiveRadioAndNextPrevPlayPauseControlsCycleThroughStations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-nowplaying-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-fenomen", "label": "Radyo Fenomen", "kind": "radio", "target": "https://live.radyofenomen.com/fenomen/128/icecast.audio", "value": 80 },
                      { "id": "r-kafa", "label": "Kafa Radyo", "kind": "radio", "target": "https://stream.radyotvonline.net/hls/play/kafaradyo.m3u8", "value": 75 },
                      { "id": "r-power", "label": "PowerTürk", "kind": "radio", "target": "https://listen.powerapp.com.tr/powerturk/mpeg/icecast.audio", "value": 85 }
                    ]
                  }
                }
                """);

            long now = 10_000;
            var native = new FakeRadioDeckNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance, () => now);

            var idleSys = new MediaSnapshot("Spotify", "Track 1", "Artist 1", "Album 1", false, 12_000, 180_000, 1, true);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));
            Assert.Null(await deck.TryControlRadioMediaAsync("next", idleSys, default));

            // 1. Start Radyo Fenomen -> EffectiveMedia reports Internet Radio with station name.
            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            now = 15_000;
            var m1 = deck.EffectiveMedia(idleSys);
            Assert.NotNull(m1);
            Assert.Equal("Internet Radio", m1!.App);
            Assert.Equal("Radyo Fenomen", m1.Title);
            Assert.Equal("Live stream", m1.Artist);
            Assert.True(m1.Playing);
            Assert.Equal(5_000, m1.PosMs);
            Assert.Equal(0, m1.DurMs);

            // 2. Next song button in Now Playing -> switches to Kafa Radyo.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.Equal("Kafa Radyo", deck.EffectiveMedia(idleSys)!.Title);

            // 3. Next song button again -> switches to PowerTürk.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-power"], native.PlayingRadios.ToArray());
            Assert.Equal("PowerTürk", deck.EffectiveMedia(idleSys)!.Title);

            // 4. Next song button at the end of list -> wraps around to Radyo Fenomen.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-fenomen"], native.PlayingRadios.ToArray());
            Assert.Equal("Radyo Fenomen", deck.EffectiveMedia(idleSys)!.Title);

            // 5. Previous song button at the first station -> wraps around backwards to PowerTürk.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("prev", idleSys, default));
            Assert.Equal(["r-power"], native.PlayingRadios.ToArray());
            Assert.Equal("PowerTürk", deck.EffectiveMedia(idleSys)!.Title);

            // 6. Previous song button again -> goes back to Kafa Radyo.
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("prev", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.Equal("Kafa Radyo", deck.EffectiveMedia(idleSys)!.Title);

            // 7. Play/Pause button in Now Playing -> pauses Kafa Radyo, keeps station visible as Paused, then resumes on second press.
            now = 22_000;
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("playpause", idleSys, default));
            Assert.Empty(native.PlayingRadios);
            var pausedMedia = deck.EffectiveMedia(idleSys);
            Assert.NotNull(pausedMedia);
            Assert.Equal("Internet Radio", pausedMedia!.App);
            Assert.Equal("Kafa Radyo", pausedMedia.Title);
            Assert.Equal("Paused", pausedMedia.Artist);
            Assert.False(pausedMedia.Playing);

            Assert.Equal(true, await deck.TryControlRadioMediaAsync("playpause", idleSys, default));
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());
            Assert.True(deck.EffectiveMedia(idleSys)!.Playing);

            // 8. If a station throws on Play, Next skips it and lands on the next working station.
            native.FailingRadios.Add("r-power");
            Assert.Equal(true, await deck.TryControlRadioMediaAsync("next", idleSys, default));
            Assert.Equal(["r-fenomen"], native.PlayingRadios.ToArray());
            Assert.Equal("Radyo Fenomen", deck.EffectiveMedia(idleSys)!.Title);

            // 9. Explicit deck stop and sound-stop clear Now Playing (clearLast: true).
            Assert.True((await deck.Act("action", "r-fenomen", "stop", null, null, null, default)).Ok);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));

            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            Assert.True((await deck.Act("sound-stop", "r-fenomen", "", null, null, null, default)).Ok);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));

            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            Assert.True((await deck.Act("sound-stop", "", "", null, null, null, default)).Ok);
            Assert.Equal(idleSys, deck.EffectiveMedia(idleSys));

            // 10. A stream that ends by itself transitions Now Playing out of Live stream (both via callback and Tick reconcile).
            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            now = 30_000;
            native.SimulateStreamEnded("r-kafa", invokeCallback: true);
            var endedByCallback = deck.EffectiveMedia(idleSys);
            Assert.NotNull(endedByCallback);
            Assert.False(endedByCallback!.Playing);
            Assert.Equal("Paused", endedByCallback.Artist);

            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            now = 35_000;
            native.SimulateStreamEnded("r-kafa", invokeCallback: false);
            deck.ReconcileRadios();
            var endedByReconcile = deck.EffectiveMedia(idleSys);
            Assert.NotNull(endedByReconcile);
            Assert.False(endedByReconcile!.Playing);
            Assert.Equal("Paused", endedByReconcile.Artist);

            // 11. Cancelled token stops station cycling before probing remaining stations.
            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            using var cancelledCts = new CancellationTokenSource();
            cancelledCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deck.TryControlRadioMediaAsync("next", idleSys, cancelledCts.Token));

            // 12. Starting a radio pauses ONLY the current Now Playing session after Play succeeds (not on failure, not background/other-window sessions).
            native.FailingRadios.Clear();
            native.FailingRadios.Add("r-power");
            native.MediaSessions =
            [
                new DeckMedia("chrome.exe", "Chrome", "Tab 1 (paused)", false, 0, 100_000, true, false, false, false, "None"),
                new DeckMedia("chrome.exe", "Chrome", "Tab 2 (active)", true, 1_000, 100_000, true, false, false, false, "None"),
                new DeckMedia("vlc.exe", "VLC", "Background", true, 2_000, 100_000, true, false, false, false, "None"),
            ];
            native.CurrentSessionIndex = 1;
            native.PausedSessionIndices.Clear();

            Assert.False((await deck.Act("action", "r-power", "", null, null, null, default)).Ok);
            Assert.Empty(native.PausedSessionIndices);

            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            Assert.Equal([1], native.PausedSessionIndices.ToArray());
            Assert.False(native.MediaSessions[1].Playing);
            Assert.True(native.MediaSessions[2].Playing);

            // 13. Unsupported "play" op is rejected without stopping radio; "playpause" on a non-playing session stops active radio.
            native.MediaSessions =
            [
                new DeckMedia("Spotify.exe", "Spotify", "Song", false, 0, 100_000, true, true, true, false, "None"),
            ];
            native.CurrentSessionIndex = 0;
            Assert.False((await deck.Act("media", "Spotify.exe", "play", null, null, null, default)).Ok);
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());

            native.MediaQueryCount = 0;
            Assert.True((await deck.Act("media", "Spotify.exe", "seek", 5_000, null, null, default)).Ok);
            Assert.True((await deck.Act("media", "Spotify.exe", "shuffle", 1, null, null, default)).Ok);
            Assert.True((await deck.Act("media", "Spotify.exe", "repeat", 1, null, null, default)).Ok);
            Assert.Equal(0, native.MediaQueryCount);
            Assert.Equal(["r-kafa"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("media", "Spotify.exe", "playpause", null, null, null, default)).Ok);
            Assert.Equal(1, native.MediaQueryCount);
            Assert.Empty(native.PlayingRadios);

            // 14. Next on an already-playing media control stops active radio without enumerating all media sessions.
            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            native.MediaQueryCount = 0;
            Assert.True((await deck.Act("media", "Spotify.exe", "next", null, null, null, default)).Ok);
            Assert.Equal(0, native.MediaQueryCount);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EffectiveMediaDoesNotStallBehindAudioLockOrStateLock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-fenomen", "label": "Radyo Fenomen", "kind": "radio", "target": "https://live.radyofenomen.com/fenomen/128/icecast.audio", "value": 80 }
                    ]
                  }
                }
                """);

            var native = new FakeRadioDeckNative();
            using var gate = new ManualResetEventSlim(false);
            native.BlockPlayGate = gate;
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);
            var idleSys = new MediaSnapshot("Spotify", "Track 1", "Artist 1", "Album 1", false, 0, 180_000, 1, true);

            // Play and Snapshot block their threads on purpose; give each its own thread so a small CI runner's
            // thread pool still has a worker left for EffectiveMedia.
            var playTask = Task.Factory.StartNew(() => deck.Act("action", "r-fenomen", "", null, null, null, default),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(native.PlayEnteredGate.Wait(TimeSpan.FromSeconds(2)));

            // Snapshot acquires ControlDeck._state inside Audio() and blocks on FakeRadioDeckNative._gate.
            var snapTask = Task.Factory.StartNew(() => deck.Snapshot(default),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await Task.Delay(50);

            var mediaTask = Task.Factory.StartNew(() => deck.EffectiveMedia(idleSys),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var completed = await Task.WhenAny(mediaTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            try
            {
                Assert.Same(mediaTask, completed);
                Assert.Equal(idleSys, await mediaTask);
            }
            finally
            {
                gate.Set();
                await Task.WhenAll(playTask, snapTask);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileRadiosStopsRemovedStationAndPreservesRestartedSameIdOnDelayedCallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Radyo Fenomen", "kind": "radio", "target": "https://example.com/fenomen.mp3", "value": 80 },
                      { "id": "r-2", "label": "Kafa Radyo", "kind": "radio", "target": "https://example.com/kafa.mp3", "value": 75 }
                    ]
                  }
                }
                """);

            var native = new FakeRadioDeckNative();
            var store = new ConfigStore(cfgPath);
            using var deck = new ControlDeck(store, native, NullLogger<ControlDeck>.Instance);

            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            Assert.Contains("r-1", native.PlayingRadios);

            // Delayed SoundStopped for r-1 arriving while r-1 is still Playing must not clear tracking
            native.FireStaleSoundStopped("r-1");
            var stillPlaying = deck.EffectiveMedia(null);
            Assert.NotNull(stillPlaying);
            Assert.True(stillPlaying!.Playing);
            Assert.Equal("Radyo Fenomen", stillPlaying.Title);

            // Removing the playing radio from saved settings stops native playback and clears EffectiveMedia
            Assert.Null(store.Save("""
                {
                  "controls": {
                    "actions": [
                      { "id": "r-2", "label": "Kafa Radyo", "kind": "radio", "target": "https://example.com/kafa.mp3", "value": 75 }
                    ]
                  }
                }
                """));
            deck.ReconcileRadios(waitForGate: true);
            Assert.Empty(native.PlayingRadios);
            Assert.Null(deck.EffectiveMedia(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SharedAumidMediaPlayPauseChecksSelectedTargetSessionBeforeStoppingRadio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-shared-aumid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Radyo Fenomen", "kind": "radio", "target": "https://example.com/fenomen.mp3", "value": 80 }
                    ]
                  }
                }
                """);

            var native = new FakeRadioDeckNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);

            // Default IDeckNative path: selected session first (paused) + playing background session under same AUMID
            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            native.MediaSessions =
            [
                new DeckMedia("chrome.exe", "Chrome", "Selected Tab (paused)", false, 0, 100_000, true, false, false, false, "None"),
                new DeckMedia("chrome.exe", "Chrome", "Background Tab (playing)", true, 1_000, 100_000, true, false, false, false, "None"),
            ];
            native.CurrentSessionIndex = 0;
            Assert.True((await deck.Act("media", "chrome.exe", "playpause", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
            Assert.True(native.MediaSessions[0].Playing);

            // Direct selected-session path (modeling WindowsDeckNative): background session index 0 playing, CurrentSessionIndex 1 paused
            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            native.UseDirectSessionMediaActionWithState = true;
            native.MediaSessions =
            [
                new DeckMedia("chrome.exe", "Chrome", "Background Tab (playing)", true, 1_000, 100_000, true, false, false, false, "None"),
                new DeckMedia("chrome.exe", "Chrome", "Selected Tab (paused)", false, 0, 100_000, true, false, false, false, "None"),
            ];
            native.CurrentSessionIndex = 1;
            Assert.True((await deck.Act("media", "chrome.exe", "playpause", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
            Assert.True(native.MediaSessions[1].Playing);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingTheRequestDoesNotStopAStationThatStarted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Radyo Fenomen", "kind": "radio", "target": "https://example.com/fenomen.mp3", "value": 80 }
                    ]
                  }
                }
                """);

            var native = new FakeRadioDeckNative();
            var pauseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            native.OnPauseCurrentMedia = async ct =>
            {
                pauseStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return false;
            };

            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance)
            {
                MediaPauseTimeout = TimeSpan.FromMilliseconds(200),
            };
            using var cts = new CancellationTokenSource();

            var actTask = deck.Act("action", "r-1", "", null, null, null, cts.Token);
            await pauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("r-1", native.PlayingRadios);

            // the panel's request gives up (a slow stream open outlasted its timeout): the station keeps playing,
            // and the stuck pause gives up on its own budget
            cts.Cancel();
            await actTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("r-1", native.PlayingRadios);
            Assert.Equal("Internet Radio", deck.EffectiveMedia(null)?.App);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentRadioStartAndMediaPlayPauseSerializeWithoutBothPlaying()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-serialize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Radyo Fenomen", "kind": "radio", "target": "https://example.com/fenomen.mp3", "value": 80 }
                    ]
                  }
                }
                """);

            var native = new FakeRadioDeckNative
            {
                MediaSessions =
                [
                    new DeckMedia("Spotify.exe", "Spotify", "Song", true, 1_000, 100_000, true, false, false, false, "None"),
                ],
                CurrentSessionIndex = 0,
            };
            var pauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            native.OnPauseCurrentMedia = async _ =>
            {
                native.MediaSessions[0] = native.MediaSessions[0] with { Playing = false };
                pauseEntered.TrySetResult();
                await releasePause.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return true;
            };

            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);
            var startRadioTask = deck.Act("action", "r-1", "", null, null, null, default);
            await pauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Concurrently resume Spotify while radio transition holds _focusGate
            var playPauseTask = deck.Act("media", "Spotify.exe", "playpause", null, null, null, default);
            await Task.Delay(50);
            Assert.False(playPauseTask.IsCompleted);

            releasePause.SetResult();
            Assert.True((await startRadioTask.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            Assert.True((await playPauseTask.WaitAsync(TimeSpan.FromSeconds(10))).Ok);

            Assert.True(native.MediaSessions[0].Playing);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PlaylistActionStopsActiveRadioOnlyWhenSpotifySucceeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-playlist-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "spotifyToken": "test-access-token",
                    "actions": [
                      { "id": "r-1", "label": "Radyo Fenomen", "kind": "radio", "target": "https://example.com/fenomen.mp3", "value": 80 },
                      { "id": "pl-1", "label": "Focus Mix", "kind": "playlist", "target": "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M" }
                    ]
                  }
                }
                """);

            var store = new ConfigStore(cfgPath);
            var native = new FakeRadioDeckNative();
            var nextStatus = HttpStatusCode.Forbidden;
            using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(nextStatus)));
            using var spotify = new SpotifyControl(store, new AgentEndpoint(), http);
            using var deck = new ControlDeck(store, native, NullLogger<ControlDeck>.Instance, spotify: spotify);

            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            Assert.Contains("r-1", native.PlayingRadios);

            Assert.False((await deck.Act("action", "pl-1", "", null, null, null, default)).Ok);
            Assert.Contains("r-1", native.PlayingRadios);

            nextStatus = HttpStatusCode.NoContent;
            Assert.True((await deck.Act("action", "pl-1", "", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DisablingOrRemovingARadioAcrossFocusAndPlayGatesNeverLeavesItPlaying()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-disable-gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1. StopDisabledControls alone clears both native playback and EffectiveMedia state.
            var cfgA = Path.Combine(dir, "a.json");
            File.WriteAllText(cfgA, """{"controls":{"actions":[{"id":"r-1","label":"S1","kind":"radio","target":"https://example.com/1.mp3","value":80}]}}""");
            var storeA = new ConfigStore(cfgA);
            var nativeA = new FakeRadioDeckNative();
            using (var deckA = new ControlDeck(storeA, nativeA, NullLogger<ControlDeck>.Instance))
            {
                Assert.True((await deckA.Act("action", "r-1", "", null, null, null, default)).Ok);
                Assert.Null(storeA.Save("""{"controls":{"actions":[{"id":"r-1","label":"S1","kind":"radio","target":"https://example.com/1.mp3","value":80,"enabled":false}]}}"""));
                deckA.StopDisabledControls();
                Assert.Empty(nativeA.PlayingRadios);
                Assert.Null(deckA.EffectiveMedia(null));
            }

            // 2. Queued behind _focusGate when disabled in settings -> rejected after gate releases.
            var cfgB = Path.Combine(dir, "b.json");
            File.WriteAllText(cfgB, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "S1", "kind": "radio", "target": "https://example.com/1.mp3", "value": 80 },
                      { "id": "r-2", "label": "S2", "kind": "radio", "target": "https://example.com/2.mp3", "value": 80 }
                    ]
                  }
                }
                """);
            var storeB = new ConfigStore(cfgB);
            var nativeB = new FakeRadioDeckNative();
            var pauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nativeB.OnPauseCurrentMedia = async _ =>
            {
                pauseEntered.TrySetResult();
                await releasePause.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return true;
            };
            using (var deckB = new ControlDeck(storeB, nativeB, NullLogger<ControlDeck>.Instance))
            {
                var t1 = deckB.Act("action", "r-1", "", null, null, null, default);
                await pauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var t2 = deckB.Act("action", "r-2", "", null, null, null, default);
                Assert.Null(storeB.Save("""
                    {
                      "controls": {
                        "actions": [
                          { "id": "r-1", "label": "S1", "kind": "radio", "target": "https://example.com/1.mp3", "value": 80 },
                          { "id": "r-2", "label": "S2", "kind": "radio", "target": "https://example.com/2.mp3", "value": 80, "enabled": false }
                        ]
                      }
                    }
                    """));
                deckB.StopDisabledControls();
                deckB.ReconcileRadios(waitForGate: true);
                releasePause.SetResult();

                Assert.True((await t1.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
                var res2 = await t2.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(res2.Ok);
                Assert.Equal("Control is disabled", res2.Error);
                Assert.Equal(["r-1"], nativeB.PlayingRadios.ToArray());
            }

            // 3. Disabled while _native.Play is mid-open inside _radioGate -> stopped before committing active state.
            var cfgC = Path.Combine(dir, "c.json");
            File.WriteAllText(cfgC, """{"controls":{"actions":[{"id":"r-3","label":"S3","kind":"radio","target":"https://example.com/3.mp3","value":80}]}}""");
            var storeC = new ConfigStore(cfgC);
            var nativeC = new FakeRadioDeckNative();
            using var blockPlay = new ManualResetEventSlim(false);
            nativeC.BlockPlayGate = blockPlay;
            using (var deckC = new ControlDeck(storeC, nativeC, NullLogger<ControlDeck>.Instance))
            {
                var playTask = Task.Factory.StartNew(
                    () => deckC.Act("action", "r-3", "", null, null, null, default),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                try
                {
                    Assert.True(nativeC.PlayEnteredGate.Wait(TimeSpan.FromSeconds(5)));
                    Assert.Null(storeC.Save("""{"controls":{"actions":[{"id":"r-3","label":"S3","kind":"radio","target":"https://example.com/3.mp3","value":80,"enabled":false}]}}"""));
                }
                finally
                {
                    // Play holds the fake's lock that disposing the deck needs: release it and let Play end first,
                    // without hiding the assertion that failed
                    blockPlay.Set();
                    await Task.WhenAny(playTask, Task.Delay(TimeSpan.FromSeconds(5)));
                }
                var res3 = await playTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(res3.Ok);
                Assert.Empty(nativeC.PlayingRadios);
                Assert.Null(deckC.EffectiveMedia(null));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeRadioDeckNative : IDeckNative
    {
        private readonly Lock _gate = new();
        public readonly HashSet<string> PlayingRadios = [];
        public readonly HashSet<string> FailingRadios = [];
        public List<DeckMedia> MediaSessions = [];
        public int CurrentSessionIndex = -1;
        public readonly List<int> PausedSessionIndices = [];
        public int MediaQueryCount;
        public bool UseDirectSessionMediaActionWithState;
        public Func<CancellationToken, Task<bool>>? OnPauseCurrentMedia;
        public ManualResetEventSlim? BlockPlayGate;
        public readonly ManualResetEventSlim PlayEnteredGate = new(false);

        public event Action<string>? SoundStopped;

        public void SimulateStreamEnded(string id, bool invokeCallback)
        {
            lock (_gate) PlayingRadios.Remove(id);
            if (invokeCallback) SoundStopped?.Invoke(id);
        }

        public void FireStaleSoundStopped(string id) => SoundStopped?.Invoke(id);

        public DeckAudio Audio() { lock (_gate) return new(true, null, [], []); }
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => true;
        public bool Session(string id, int? volume, bool? muted) => true;
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public string? ActionState(DeckAction action)
        {
            if (action.Kind is not ("sound" or "radio")) return null;
            lock (_gate) return PlayingRadios.Contains(action.Id) ? "Playing" : "Stopped";
        }
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct)
        {
            MediaQueryCount++;
            return Task.FromResult<IReadOnlyList<DeckMedia>>(MediaSessions);
        }
        private int ResolveSessionIndex(string id) =>
            CurrentSessionIndex >= 0 && CurrentSessionIndex < MediaSessions.Count && MediaSessions[CurrentSessionIndex].Id == id
                ? CurrentSessionIndex
                : MediaSessions.FindIndex(m => m.Id == id);
        public async Task<(bool Ok, bool WasPlaying)> MediaActionWithState(string id, string action, long value, CancellationToken ct)
        {
            if (!UseDirectSessionMediaActionWithState)
            {
                var wasPlaying = action == "playpause" && (await Media(ct)).FirstOrDefault(p => p.Id == id)?.Playing == true;
                return (await MediaAction(id, action, value, ct), wasPlaying);
            }
            var idx = ResolveSessionIndex(id);
            if (idx < 0) return (false, false);
            var selectedWasPlaying = action == "playpause" && MediaSessions[idx].Playing;
            return (await MediaAction(id, action, value, ct), selectedWasPlaying);
        }
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct)
        {
            var idx = ResolveSessionIndex(id);
            if (idx < 0) return Task.FromResult(false);
            switch (action)
            {
                case "pause":
                    PausedSessionIndices.Add(idx);
                    MediaSessions[idx] = MediaSessions[idx] with { Playing = false };
                    return Task.FromResult(true);
                case "playpause":
                    MediaSessions[idx] = MediaSessions[idx] with { Playing = !MediaSessions[idx].Playing };
                    return Task.FromResult(true);
                case "next" or "prev" or "seek" or "shuffle" or "repeat":
                    return Task.FromResult(true);
                default:
                    return Task.FromResult(false);
            }
        }
        public Task<bool> PauseCurrentMedia(CancellationToken ct)
        {
            if (OnPauseCurrentMedia is not null) return OnPauseCurrentMedia(ct);
            if (CurrentSessionIndex < 0 || CurrentSessionIndex >= MediaSessions.Count) return Task.FromResult(false);
            if (!MediaSessions[CurrentSessionIndex].Playing) return Task.FromResult(false);
            PausedSessionIndices.Add(CurrentSessionIndex);
            MediaSessions[CurrentSessionIndex] = MediaSessions[CurrentSessionIndex] with { Playing = false };
            return Task.FromResult(true);
        }
        public void Play(string id, string file, string? device, int volume)
        {
            lock (_gate)
            {
                PlayEnteredGate.Set();
                BlockPlayGate?.Wait(TimeSpan.FromSeconds(10));
                if (FailingRadios.Contains(id)) throw new InvalidOperationException("Stream open failed");
                PlayingRadios.Add(id);
            }
        }
        public void StopSound(string? id)
        {
            lock (_gate)
            {
                if (id is null) PlayingRadios.Clear();
                else PlayingRadios.Remove(id);
            }
        }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() { lock (_gate) PlayingRadios.Clear(); }
    }

    [Fact]
    public async Task PanicMuteDropsActivePttLeasesAndPttSamplesUncachedAudioAndSweepsOnDispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-ptt-panic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, "{}");

            var n1 = new FakePttDeckNative(initialMicMuted: false, includeReadOnly: true);
            using (var deck = new ControlDeck(new ConfigStore(cfgPath), n1, NullLogger<ControlDeck>.Instance))
            {
                var begin = await deck.Act("ptt", "mic-1", "begin", null, null, null, default);
                Assert.True(begin.Ok);
                var lease = Assert.IsType<string>(begin.Lease);
                Assert.True((await deck.Act("panic", null, null, null, null, null, default)).Ok);
                Assert.True(n1.MicMuted);
                Assert.False((await deck.Act("ptt", "mic-1", "end", null, null, lease, default)).Ok);
                Assert.True(n1.MicMuted);
            }

            var n2 = new FakePttDeckNative(initialMicMuted: true, includeReadOnly: false);
            using (var deck = new ControlDeck(new ConfigStore(cfgPath), n2, NullLogger<ControlDeck>.Instance))
            {
                _ = await deck.Snapshot(default);
                n2.MicMuted = false;
                var lease = (await deck.Act("ptt", "mic-1", "begin", null, null, null, default)).Lease!;
                Assert.True((await deck.Act("ptt", "mic-1", "end", null, null, lease, default)).Ok);
                Assert.False(n2.MicMuted);
            }

            var n3 = new FakePttDeckNative(initialMicMuted: true, includeReadOnly: false);
            using (var deck = new ControlDeck(new ConfigStore(cfgPath), n3, NullLogger<ControlDeck>.Instance))
            {
                _ = await deck.Act("ptt", "mic-1", "begin", null, null, null, default);
                Assert.False(n3.MicMuted);
            }
            Assert.True(n3.MicMuted);
            Assert.True(n3.Disposed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class FakePttDeckNative(bool initialMicMuted, bool includeReadOnly) : IDeckNative
    {
        public bool MicMuted { get; set; } = initialMicMuted;
        public bool SpeakerMuted { get; set; }
        public bool Disposed { get; private set; }
        public event Action<string>? SoundStopped { add { } remove { } }

        public DeckAudio Audio()
        {
            var devices = new List<DeckDevice>
            {
                new("spk-1", "Speakers", "output", true, 60, SpeakerMuted, 0, 2, CanControl: true),
                new("mic-1", "Desk Mic", "input", true, 80, MicMuted, 0, 1, CanControl: true),
            };
            if (includeReadOnly) devices.Add(new("hdmi-ro", "HDMI", "output", false, 100, false, 0, 2, CanControl: false));
            return new(true, null, devices, []);
        }

        public bool Device(string id, int? volume, bool? muted, int? channel = null)
        {
            if (id == "hdmi-ro") return false;
            if (id == "spk-1") { if (muted is { } m) SpeakerMuted = m; return true; }
            if (id == "mic-1") { if (muted is { } m) MicMuted = m; return true; }
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
        public Task<bool> PauseCurrentMedia(CancellationToken ct) => Task.FromResult(false);
        public string? ActionState(DeckAction action) => null;
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() => Disposed = true;
    }
}
