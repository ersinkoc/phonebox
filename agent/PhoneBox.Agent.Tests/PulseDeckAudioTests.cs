using PhoneBox.Agent;
using Xunit;

/// <summary>The Linux Control Deck audio backend, fed with real <c>LC_ALL=C pactl list</c> output (PipeWire 1.6).</summary>
public sealed class PulseDeckAudioTests
{
    private const string Sinks = """
        Sink #60
        	State: RUNNING
        	Name: alsa_output.usb-Focusrite_Scarlett_2i2-00.Direct__sink
        	Description: Scarlett 2i2 USB
        	Driver: PipeWire
        	Channel Map: front-left,front-right
        	Owner Module: 4294967295
        	Mute: no
        	Volume: front-left: 26214 /  40% / -23.88 dB,   front-right: 13107 /  20% / -41.94 dB
        	        balance -0.50
        	Base Volume: 65536 / 100% / 0.00 dB
        	Monitor Source: alsa_output.usb-Focusrite_Scarlett_2i2-00.Direct__sink.monitor
        	Properties:
        		device.description = "Scarlett 2i2 USB"
        		device.class = "sound"
        	Formats:
        		pcm
        Sink #116
        	State: IDLE
        	Name: korvex_ch_musica
        	Description: Música
        	Owner Module: 536870916
        	Mute: yes
        	Volume: front-left: 98304 / 150% / 10.57 dB,   front-right: 98304 / 150% / 10.57 dB
        	        balance 0.00
        	Properties:
        		device.description = "Música"
        		media.class = "Audio/Sink"

        """;

    private const string Sources = """
        Source #60
        	State: SUSPENDED
        	Name: alsa_output.usb-Focusrite_Scarlett_2i2-00.Direct__sink.monitor
        	Description: Monitor of Scarlett 2i2 USB
        	Monitor of Sink: alsa_output.usb-Focusrite_Scarlett_2i2-00.Direct__sink
        	Mute: no
        	Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: 65536 / 100% / 0.00 dB
        	Properties:
        		device.class = "monitor"
        Source #61
        	State: SUSPENDED
        	Name: alsa_input.usb-Focusrite_Scarlett_2i2-00.Direct__source
        	Description: Scarlett 2i2 USB
        	Monitor of Sink: n/a
        	Mute: no
        	Volume: front-left: 52429 /  80% / -5.81 dB,   front-right: 52429 /  80% / -5.81 dB
        	Properties:
        		device.class = "sound"

        """;

    private const string Inputs = """
        Sink Input #798
        	Driver: PipeWire
        	Owner Module: n/a
        	Client: 75
        	Sink: 60
        	Corked: yes
        	Mute: no
        	Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: 65536 / 100% / 0.00 dB
        	        balance 0.00
        	Properties:
        		application.name = "Firefox"
        		application.process.id = "78881"
        		application.process.binary = "firefox"
        		media.name = "Canción con tildes - YouTube"
        Sink Input #3746
        	Owner Module: n/a
        	Client: 80
        	Sink: 116
        	Corked: no
        	Mute: yes
        	Volume: mono: 45875 /  70% / -9.29 dB
        	Properties:
        		application.process.binary = "sidra"
        		application.process.id = "4242"
        Sink Input #3812
        	Owner Module: 536870917
        	Client: n/a
        	Sink: 60
        	Corked: no
        	Mute: no
        	Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: 65536 / 100% / 0.00 dB
        	Properties:
        		node.name = "output.loopback-3278-13"
        		media.name = "loopback-3278-13 output"

        """;

    private const string Scarlett = "alsa_output.usb-Focusrite_Scarlett_2i2-00.Direct__sink";
    private const string Mic = "alsa_input.usb-Focusrite_Scarlett_2i2-00.Direct__source";

    /// <summary><c>LC_ALL=C pactl info</c> against pipewire-pulse, laid out as pactl.c prints it (the user name has an
    /// accent: values stay UTF-8).</summary>
    private static string PipeWireInfo(string? sink, string? source) => $"""
        Server String: /run/user/1000/pulse/native
        Library Protocol Version: 35
        Server Protocol Version: 35
        Is Local: yes
        Client Index: 158
        Tile Size: 65472
        User Name: josé
        Host Name: tab-p11
        Server Name: PulseAudio (on PipeWire 1.6.0)
        Server Version: 15.0.0
        Default Sample Specification: float32le 2ch 48000Hz
        Default Channel Map: front-left,front-right
        Default Sink: {sink ?? "(null)"}
        Default Source: {source ?? "(null)"}
        Cookie: 3c1a:5e2f

        """;

    /// <summary>The same from PulseAudio 14 (Debian 11), which has no get-default-sink / get-default-source.</summary>
    private const string PulseAudio14Info = """
        Server String: /run/user/1000/pulse/native
        Library Protocol Version: 34
        Server Protocol Version: 34
        Is Local: yes
        Client Index: 12
        Tile Size: 65472
        User Name: ana
        Host Name: debian
        Server Name: pulseaudio
        Server Version: 14.2
        Default Sample Specification: s16le 2ch 44100Hz
        Default Channel Map: front-left,front-right
        Default Sink: alsa_output.pci-0000_00_1f.3.analog-stereo
        Default Source: alsa_input.pci-0000_00_1f.3.analog-stereo
        Cookie: 9d3b:0a71

        """;

    /// <summary>A fake pactl: answers the listings above and records every other call; <paramref name="starts"/> counts
    /// every process the backend would have started.</summary>
    private static (PulseDeckAudio Audio, List<string> Calls) Fake(string defaultSink = Scarlett, Func<string, bool>? fails = null,
        List<string>? starts = null, Func<long>? now = null)
    {
        var calls = new List<string>();
        var current = defaultSink;
        var audio = new PulseDeckAudio(args =>
        {
            var line = string.Join(' ', args);
            starts?.Add(line);
            switch (line)
            {
                case "list sinks": return Sinks;
                case "list sources": return Sources;
                case "list sink-inputs": return Inputs;
                case "info": return PipeWireInfo(current, Mic);
            }
            calls.Add(line);
            if (fails?.Invoke(line) == true) return null;
            if (args is ["set-default-sink", var name]) current = name;
            return "";
        }, now ?? (() => 0));
        return (audio, calls);
    }

    [Fact]
    public void ReadsOutputsAndMicrophonesButNotMonitors()
    {
        var state = Fake().Audio.Read()!;
        Assert.True(state.Available);
        Assert.Equal([Scarlett, "korvex_ch_musica", Mic], state.Devices.Select(d => d.Id));

        var scarlett = state.Devices[0];
        Assert.Equal(("Scarlett 2i2 USB", "output", true, 40, false, 2), (scarlett.Name, scarlett.Direction, scarlett.Default, scarlett.Volume, scarlett.Muted, scarlett.Channels));
        Assert.Equal([40, 20], scarlett.ChannelVolumes!);

        var channel = state.Devices[1];
        Assert.Equal(("Música", false, 100, true), (channel.Name, channel.Default, channel.Volume, channel.Muted)); // 150% shows as 100

        var mic = state.Devices[2];
        Assert.Equal(("input", true, 80), (mic.Direction, mic.Default, mic.Volume));
    }

    [Fact]
    public void ListsApplicationStreamsAndSkipsModuleLoopbacks()
    {
        var sessions = Fake().Audio.Read()!.Sessions;
        Assert.Equal(["798", "3746"], sessions.Select(s => s.Id));

        var firefox = sessions[0];
        Assert.Equal(("Firefox", 78881, Scarlett, 100, false, "firefox", false),
            (firefox.Name, firefox.Pid, firefox.DeviceId, firefox.Volume, firefox.Muted, firefox.ProcessName, firefox.Active));

        var sidra = sessions[1]; // no application.name: falls back to the binary
        Assert.Equal(("sidra", 4242, "korvex_ch_musica", 70, true, true), (sidra.Name, sidra.Pid, sidra.DeviceId, sidra.Volume, sidra.Muted, sidra.Active));
    }

    [Fact]
    public void KeepsPulseAudioClientStreamsThatNameTheProtocolModule()
    {
        // PulseAudio (not PipeWire) gives a client's stream the native-protocol module as its owner; a loopback has no client
        const string pulseAudio = """
            Sink Input #12
            	Driver: protocol-native.c
            	Owner Module: 10
            	Client: 7
            	Sink: 60
            	Corked: no
            	Mute: no
            	Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: 65536 / 100% / 0.00 dB
            	Properties:
            		application.name = "Firefox"
            		application.process.id = "1234"
            Sink Input #13
            	Driver: module-loopback.c
            	Owner Module: 24
            	Client: n/a
            	Sink: 60
            	Corked: no
            	Mute: no
            	Volume: mono: 65536 / 100% / 0.00 dB
            	Properties:
            		media.name = "Loopback from Monitor of Null Output"

            """;
        var state = PulseDeckAudio.Build(PulseDeckAudio.Parse(Sinks), [], PulseDeckAudio.Parse(pulseAudio), Scarlett, null);
        Assert.Equal(["12"], state.Sessions.Select(s => s.Id));
        Assert.Equal(("Firefox", 1234), (state.Sessions[0].Name, state.Sessions[0].Pid));
    }

    [Fact]
    public void ReportsTheDefaultsForEveryRole()
    {
        var defaults = Fake().Audio.Read()!.Defaults!;
        for (var role = 0; role < 3; role++)
        {
            Assert.Equal(Scarlett, defaults[$"output:{role}"]);
            Assert.Equal(Mic, defaults[$"input:{role}"]);
        }
    }

    [Fact]
    public void ReadIsNullWhenTheSoundServerDoesNotAnswer()
    {
        var audio = new PulseDeckAudio(args => args[0] == "list" && args[1] == "sink-inputs" ? null : "");
        Assert.Null(audio.Read());
    }

    [Fact]
    public void MasterVolumeKeepsTheBalance()
    {
        var (audio, calls) = Fake();
        Assert.True(audio.Device(Scarlett, 80, null));
        Assert.Equal([$"set-sink-volume {Scarlett} 80% 40%"], calls);
    }

    [Fact]
    public void ChannelVolumeChangesOnlyThatChannel()
    {
        var (audio, calls) = Fake();
        Assert.True(audio.Device(Scarlett, 55, null, channel: 1));
        Assert.Equal([$"set-sink-volume {Scarlett} 40% 55%"], calls);
        Assert.False(audio.Device(Scarlett, 55, null, channel: 2));
    }

    [Fact]
    public void MicrophoneMuteUsesTheSourceCommands()
    {
        var (audio, calls) = Fake();
        Assert.True(audio.Device(Mic, null, true));
        Assert.True(audio.Device(Mic, 30, false));
        Assert.Equal([$"set-source-mute {Mic} 1", $"set-source-volume {Mic} 30% 30%", $"set-source-mute {Mic} 0"], calls);
    }

    [Fact]
    public void RejectsUnknownDevicesAndBadLevels()
    {
        var (audio, calls) = Fake();
        Assert.False(audio.Device("no-such-sink", 50, null));
        Assert.False(audio.Device(Scarlett, 101, null));
        Assert.False(audio.Device(Scarlett, -1, null));
        Assert.False(audio.Device(Scarlett + ".monitor", null, true)); // monitors are not offered, so not controllable
        Assert.Empty(calls);
    }

    [Fact]
    public void SessionVolumeAndMute()
    {
        var (audio, calls) = Fake();
        Assert.True(audio.Session("798", 25, true));
        Assert.Equal(["set-sink-input-volume 798 25%", "set-sink-input-mute 798 1"], calls);
    }

    [Fact]
    public void SessionIdsMustBeStreamIndexes()
    {
        var (audio, calls) = Fake();
        Assert.False(audio.Session("798; rm -rf ~", 25, null));
        Assert.False(audio.Session("", 25, null));
        Assert.False(audio.Session("798", 200, null));
        Assert.Empty(calls);
    }

    [Fact]
    public void AFailedVolumeChangeStopsBeforeTheMute()
    {
        var (audio, calls) = Fake(fails: line => line.StartsWith("set-sink-input-volume", StringComparison.Ordinal));
        Assert.False(audio.Session("798", 25, true));
        Assert.Equal(["set-sink-input-volume 798 25%"], calls);
    }

    [Fact]
    public void DefaultDeviceSwitchesAndChecksTheResult()
    {
        var (audio, calls) = Fake();
        Assert.True(audio.DefaultDevice("korvex_ch_musica", 1));
        Assert.True(audio.DefaultDevice("korvex_ch_musica", 2)); // already the default: every role follows it
        Assert.True(audio.DefaultDevice(Mic)); // already the default microphone
        Assert.Equal(["set-default-sink korvex_ch_musica"], calls);
        Assert.False(audio.DefaultDevice(Scarlett, 3));
        Assert.False(audio.DefaultDevice("no-such-sink"));

        var state = audio.Read()!; // the kept listing follows the switch
        Assert.Equal(["korvex_ch_musica"], state.Devices.Where(d => d.Direction == "output" && d.Default).Select(d => d.Id));
        Assert.Equal("korvex_ch_musica", state.Defaults!["output:0"]);
        Assert.Equal(Mic, state.Defaults["input:2"]);
    }

    [Fact]
    public void DefaultDeviceFailsWhenInfoDoesNotShowTheSwitch()
    {
        // the server took the command but kept its default (pactl 14 and older have no get-default-sink to ask)
        var audio = new PulseDeckAudio(args => string.Join(' ', args) switch
        {
            "list sinks" => Sinks, "list sources" => Sources, "list sink-inputs" => Inputs,
            "info" => PipeWireInfo(Scarlett, Mic),
            _ => "",
        }, () => 0);
        Assert.False(audio.DefaultDevice("korvex_ch_musica"));
    }

    [Fact]
    public void DefaultsComeFromPactlInfoOnEveryVersion()
    {
        Assert.Equal((Scarlett, Mic), PulseDeckAudio.Defaults(PipeWireInfo(Scarlett, Mic)));
        Assert.Equal(("alsa_output.pci-0000_00_1f.3.analog-stereo", "alsa_input.pci-0000_00_1f.3.analog-stereo"),
            PulseDeckAudio.Defaults(PulseAudio14Info));
        Assert.Equal(((string?)null, (string?)null), PulseDeckAudio.Defaults(PipeWireInfo(null, null))); // "(null)": none set
        Assert.Equal(((string?)null, (string?)null), PulseDeckAudio.Defaults(PulseAudio14Info.Replace("\n", "\r\n")
            .Replace("alsa_output.pci-0000_00_1f.3.analog-stereo", "").Replace("alsa_input.pci-0000_00_1f.3.analog-stereo", "n/a")));
        Assert.Equal(((string?)null, (string?)null), PulseDeckAudio.Defaults(null));
    }

    [Fact]
    public void ReadsTheDefaultsOfPulseAudio14()
    {
        const string sink = "alsa_output.pci-0000_00_1f.3.analog-stereo";
        var sinks = Sinks.Replace(Scarlett, sink);
        var audio = new PulseDeckAudio(args => string.Join(' ', args) switch
        {
            "list sinks" => sinks, "list sources" => Sources, "list sink-inputs" => Inputs, "info" => PulseAudio14Info,
            _ => null, // get-default-sink does not exist before PulseAudio 15
        });
        var state = audio.Read()!;
        Assert.True(state.Devices.Single(d => d.Id == sink).Default);
        Assert.Equal(sink, state.Defaults!["output:1"]);
    }

    [Fact]
    public void ListingsAreSharedForAMomentAndWritesReuseThem()
    {
        var starts = new List<string>();
        long clock = 1_000;
        var (audio, _) = Fake(starts: starts, now: () => clock);

        Assert.NotNull(audio.Read());
        Assert.Equal(["info", "list sinks", "list sources", "list sink-inputs"], starts); // four processes per listing
        starts.Clear();

        // a panic: every device muted and back, and a profile restore's roles, within the kept listing
        foreach (var id in new[] { Scarlett, "korvex_ch_musica", Mic }) Assert.True(audio.Device(id, null, true));
        Assert.True(audio.Device(Scarlett, 80, false));
        for (var role = 0; role < 3; role++) Assert.True(audio.DefaultDevice(Scarlett, role));
        Assert.NotNull(audio.Read());
        Assert.Equal([$"set-sink-mute {Scarlett} 1", "set-sink-mute korvex_ch_musica 1", $"set-source-mute {Mic} 1",
            $"set-sink-volume {Scarlett} 80% 40%", $"set-sink-mute {Scarlett} 0"], starts); // one process per change, no listing
        var kept = audio.Read()!;
        Assert.Equal((80, false), (kept.Devices[0].Volume, kept.Devices[0].Muted)); // patched with what was set
        Assert.True(kept.Devices[2].Muted);
        starts.Clear();

        clock += PulseDeckAudio.CacheMs; // too old now: listed again
        Assert.NotNull(audio.Read());
        Assert.Equal(["info", "list sinks", "list sources", "list sink-inputs"], starts);
    }

    [Fact]
    public void CallersArrivingTogetherShareOneListing()
    {
        var starts = 0;
        var audio = new PulseDeckAudio(args =>
        {
            Interlocked.Increment(ref starts);
            Thread.Sleep(20);
            return string.Join(' ', args) switch
            {
                "list sinks" => Sinks, "list sources" => Sources, "list sink-inputs" => Inputs, _ => PipeWireInfo(Scarlett, Mic),
            };
        }, () => 0);
        Parallel.For(0, 8, _ => Assert.NotNull(audio.Read()));
        Assert.Equal(4, starts);
    }

    [Fact]
    public void AFailedWriteDropsTheKeptListing()
    {
        var starts = new List<string>();
        var (audio, _) = Fake(fails: line => line.StartsWith("set-sink-mute", StringComparison.Ordinal), starts: starts);
        Assert.False(audio.Device(Scarlett, null, true));
        starts.Clear();
        Assert.NotNull(audio.Read()); // what pactl holds now is unknown, so it is listed again
        Assert.Contains("list sinks", starts);
    }

    [Fact]
    public void WithoutASoundServerOnlyInfoIsStarted()
    {
        var starts = new List<string>();
        var audio = new PulseDeckAudio(args => { starts.Add(string.Join(' ', args)); return null; }, () => 0);
        Assert.Null(audio.Read());
        Assert.Null(audio.Read()); // the failure is kept for a moment too
        Assert.Equal(["info"], starts);
        Assert.False(audio.HasAnswered);
    }

    [Theory]
    [InlineData(new[] { 40, 20 }, 80, new[] { 80, 40 })]
    [InlineData(new[] { 0, 0 }, 35, new[] { 35, 35 })]
    [InlineData(new[] { 100 }, 0, new[] { 0 })]
    [InlineData(new[] { 33, 100 }, 50, new[] { 17, 50 })]
    public void ScaleKeepsChannelRatios(int[] channels, int level, int[] expected) =>
        Assert.Equal(expected, PulseDeckAudio.Scale(channels, level));

    [Fact]
    public void ParsesVolumeLinesOfEveryShape()
    {
        Assert.Equal([40, 20], PulseDeckAudio.Volumes("front-left: 26214 /  40% / -23.88 dB,   front-right: 13107 /  20% / -41.94 dB"));
        Assert.Equal([70], PulseDeckAudio.Volumes("mono: 45875 /  70% / -9.29 dB"));
        Assert.Empty(PulseDeckAudio.Volumes(null));
    }

    [Fact]
    public async Task OverlappingChannelChangesBothLand()
    {
        // a stateful fake: listing is slow, so two unserialized left/right changes would both read 40/20
        var levels = new[] { 40, 20 };
        var gate = new object();
        var audio = new PulseDeckAudio(args =>
        {
            switch (string.Join(' ', args))
            {
                case "list sinks":
                    int l, r; lock (gate) (l, r) = (levels[0], levels[1]);
                    Thread.Sleep(50);
                    return Sinks.Replace("front-left: 26214 /  40% / -23.88 dB,   front-right: 13107 /  20% / -41.94 dB",
                        $"front-left: 1 / {l}% / 0 dB,   front-right: 1 / {r}% / 0 dB");
                case "list sources": return Sources;
                case "list sink-inputs": return Inputs;
                case "info": return PipeWireInfo(Scarlett, Mic);
            }
            if (args is ["set-sink-volume", Scarlett, var left, var right])
                lock (gate) levels = [int.Parse(left.TrimEnd('%')), int.Parse(right.TrimEnd('%'))];
            return "";
        });
        var a = Task.Run(() => audio.Device(Scarlett, 70, null, channel: 0));
        var b = Task.Run(() => audio.Device(Scarlett, 60, null, channel: 1));
        var results = await Task.WhenAll(a, b);
        Assert.All(results, Assert.True);
        Assert.Equal([70, 60], levels);
    }

    [Fact]
    public void AFailedListingIsUnavailableNotTheSystemOutputAlone()
    {
        // panic and "Mics off" must not take a list without microphones for the whole list
        var answering = true;
        long clock = 0;
        var pulse = new PulseDeckAudio(args => !answering ? null : string.Join(' ', args) switch
        {
            "list sinks" => Sinks, "list sources" => Sources, "list sink-inputs" => Inputs, _ => PipeWireInfo(Scarlett, Mic),
        }, () => clock);
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(new AudioControl(() => new AudioSnapshot(30, false)), media, _ => null, pulse);
        Assert.Equal(3, native.Audio().Devices.Count);

        answering = false;
        clock += PulseDeckAudio.CacheMs;
        var state = native.Audio();
        Assert.False(state.Available);
        Assert.Empty(state.Devices);
    }

    [Fact]
    public void PactlWithoutASoundServerKeepsTheSystemOutput()
    {
        // pactl installed but no Pulse server (PipeWire without pipewire-pulse): the master volume still works
        var pulse = new PulseDeckAudio(_ => null);
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        var master = new AudioControl(() => new AudioSnapshot(30, true));
        Assert.NotNull(master.Current()); // Snapshot() never waits: read once, as the live stream would have
        using var native = new PortableDeckNative(master, media, _ => null, pulse);
        var state = native.Audio();
        Assert.True(state.Available);
        Assert.Equal(("default", 30, true), (state.Devices.Single().Id, state.Devices[0].Volume, state.Devices[0].Muted));
    }

    [Fact]
    public void APulseDeviceNamedDefaultIsNotTheSystemOutput()
    {
        // PulseAudio does not reserve "default": a microphone with that name goes through pactl, not the master volume
        var sources = Sources.Replace($"Name: {Mic}", "Name: default");
        var calls = new List<string>();
        var pulse = new PulseDeckAudio(args =>
        {
            var line = string.Join(' ', args);
            switch (line)
            {
                case "list sinks": return Sinks;
                case "list sources": return sources;
                case "list sink-inputs": return Inputs;
                case "info": return PipeWireInfo(Scarlett, "default");
            }
            calls.Add(line);
            return "";
        }, () => 0);
        var toggles = 0;
        var master = new AudioControl(() => new AudioSnapshot(30, false), _ => 30, () => { toggles++; return true; });
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(master, media, _ => null, pulse);

        Assert.Contains(native.Audio().Devices, d => d is { Id: "default", Direction: "input" });
        Assert.True(native.Device("default", null, true));
        Assert.Equal(["set-source-mute default 1"], calls);
        Assert.Equal(0, toggles);
    }

    [Fact]
    public void DefaultIsTheSystemOutputUntilPactlAnswers()
    {
        var pulse = new PulseDeckAudio(_ => null, () => 0);
        var set = new List<int>();
        var master = new AudioControl(() => new AudioSnapshot(30, false), v => { set.Add(v); return v; }, () => true);
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(master, media, _ => null, pulse);
        Assert.True(native.Device("default", 45, null));
        Assert.Equal([45], set);
    }

    [Fact]
    public void PushToTalkReadsTheMicrophoneFresh()
    {
        // another application mutes the microphone inside the kept listing's 300 ms: the restore target must see it
        var micMuted = false;
        var starts = 0;
        var pulse = new PulseDeckAudio(args =>
        {
            starts++;
            return string.Join(' ', args) switch
            {
                "list sinks" => Sinks, "list sink-inputs" => Inputs, "info" => PipeWireInfo(Scarlett, Mic),
                "list sources" => micMuted
                    ? System.Text.RegularExpressions.Regex.Replace(Sources, @"(Monitor of Sink: n/a\r?\n\tMute: )no", "$1yes") : Sources,
                _ => "",
            };
        }, () => 0);
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(new AudioControl(), media, _ => null, pulse);
        Assert.False(native.Audio().Devices.Single(d => d.Id == Mic).Muted);

        micMuted = true;
        Assert.False(native.Audio().Devices.Single(d => d.Id == Mic).Muted); // ordinary reads keep the listing
        Assert.Equal(4, starts);
        Assert.True(native.MicrophoneMuted(Mic));
        Assert.Equal(8, starts);
        Assert.True(native.Audio().Devices.Single(d => d.Id == Mic).Muted); // and the fresh listing is kept
        Assert.Null(native.MicrophoneMuted(Scarlett)); // an output is not a microphone
    }

    [Fact]
    public void PortableNativeUsesPulseWhenAvailable()
    {
        var (pulse, calls) = Fake();
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(new AudioControl(), media, _ => null, pulse);
        Assert.Equal(3, native.Audio().Devices.Count);
        Assert.True(native.Session("798", 10, null));
        Assert.True(native.DefaultDevice("korvex_ch_musica"));
        Assert.Equal(["set-sink-input-volume 798 10%", "set-default-sink korvex_ch_musica"], calls);
    }

    [Fact]
    public void AMicrophoneNamedLikeAMonitorStaysListed()
    {
        // the metadata says it is a real microphone, so the ".monitor" suffix does not hide it
        const string sources = """
            Source #7
            	State: SUSPENDED
            	Name: conference.monitor
            	Description: Conference microphone
            	Monitor of Sink: n/a
            	Mute: no
            	Volume: mono: 32768 /  50% / -18.06 dB
            	Properties:
            		device.class = "sound"
            """;
        var state = PulseDeckAudio.Build([], PulseDeckAudio.Parse(sources), [], null, null);
        var mic = Assert.Single(state.Devices);
        Assert.Equal("conference.monitor", mic.Id);
        Assert.Equal("input", mic.Direction);
    }

    [Fact]
    public void RoutesEveryStreamOfTheApplicationToTheOutput()
    {
        var (audio, calls) = Fake();
        // set up on Windows as "Firefox.exe": the same control works here
        Assert.Equal(1, audio.Route("Firefox.exe", "korvex_ch_musica"));
        Assert.Equal(["move-sink-input 798 korvex_ch_musica"], calls);
        Assert.Equal("korvex_ch_musica", audio.Read()!.Sessions.Single(s => s.Id == "798").DeviceId);
    }

    [Fact]
    public void RoutingMatchesTheProcessNameAndLeavesAStreamAlreadyThere()
    {
        var (audio, calls) = Fake();
        Assert.Equal(1, audio.Route(" SIDRA ", "korvex_ch_musica")); // sidra already plays there
        Assert.Empty(calls);
    }

    [Fact]
    public void RoutingAnApplicationThatPlaysNothingMovesNothing()
    {
        var (audio, calls) = Fake();
        Assert.Equal(0, audio.Route("discord", Scarlett));
        Assert.Equal(0, audio.Route("loopback-3278-13 output", "korvex_ch_musica")); // plumbing is not an application
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("firefox", Mic)]                // a microphone is not an output
    [InlineData("firefox", "unplugged")]        // not a listed device
    [InlineData("  ", Scarlett)]
    [InlineData(".exe", Scarlett)]
    public void RoutingRefusesAnUnknownOutputOrApplication(string application, string sink)
    {
        var (audio, calls) = Fake();
        Assert.Null(audio.Route(application, sink));
        Assert.Empty(calls);
    }

    [Fact]
    public void AFailedMoveIsReportedAndForgotten()
    {
        var (audio, calls) = Fake(fails: line => line.StartsWith("move-sink-input", StringComparison.Ordinal));
        Assert.Null(audio.Route("firefox", "korvex_ch_musica"));
        Assert.Equal(["move-sink-input 798 korvex_ch_musica"], calls);
        Assert.Equal(Scarlett, audio.Read()!.Sessions.Single(s => s.Id == "798").DeviceId);
    }

    [Fact]
    public async Task TheAudioRouteControlUsesPactlOnLinux()
    {
        var (pulse, calls) = Fake();
        var directory = Path.Combine(Path.GetTempPath(), "phonebox-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, """
                {"controls":{"actions":[
                  {"id":"music","label":"Firefox to music","kind":"audio-route","target":"firefox","device":"korvex_ch_musica","enabled":true},
                  {"id":"discord","label":"Discord to music","kind":"audio-route","target":"Discord.exe","device":"korvex_ch_musica","enabled":true},
                  {"id":"mic","label":"Firefox to mic","kind":"audio-route","target":"firefox","device":"MIC","enabled":true}]}}
                """.Replace("MIC", Mic));
            using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
            var native = new PortableDeckNative(new AudioControl(), media, _ => null, pulse);
            using var deck = new ControlDeck(new ConfigStore(path), native,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ControlDeck>.Instance, () => 1000);
            Assert.True((await deck.Act("action", "music", null, null, null, null, default)).Ok);
            var idle = await deck.Act("action", "discord", null, null, null, null, default);
            Assert.False(idle.Ok);
            Assert.Contains("not playing", idle.Error);
            Assert.False((await deck.Act("action", "mic", null, null, null, null, default)).Ok);
            Assert.Equal(["move-sink-input 798 korvex_ch_musica"], calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ACancelledRouteStartsNoFurtherMove()
    {
        var (audio, calls) = Fake();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(audio.Route("firefox", "korvex_ch_musica", cancelled.Token));
        Assert.Empty(calls);
    }

    // ---- level meters (parec) --------------------------------------------------------------------------------------

    /// <summary>A parec stand-in: hands out <paramref name="data"/>, then blocks like a live capture until stopped.</summary>
    private sealed class LiveFeed(byte[] data) : Stream
    {
        private readonly ManualResetEventSlim _stopped = new();
        private int _at;
        public bool Stopped => _stopped.IsSet;
        public void Stop() => _stopped.Set();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < data.Length) { var n = Math.Min(count, data.Length - _at); Array.Copy(data, _at, buffer, offset, n); _at += n; return n; }
            _stopped.Wait();
            return 0;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>One 50 ms chunk of s16le stereo whose loudest sample is <paramref name="peak"/>.</summary>
    private static byte[] Chunk(short peak)
    {
        var bytes = new byte[PulseMeters.ChunkFrames * PulseMeters.Channels * 2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), peak);
        return bytes;
    }

    private static (PulseMeters Meters, List<string> Started, Dictionary<string, LiveFeed> Feeds) FakeMeters(
        Func<long> now, short peak = 16384, int chunks = 1)
    {
        var started = new List<string>();
        var feeds = new Dictionary<string, LiveFeed>();
        var meters = new PulseMeters(args =>
        {
            var line = string.Join(' ', args);
            lock (started) started.Add(line);
            var feed = new LiveFeed([.. Enumerable.Range(0, chunks).SelectMany(_ => Chunk(peak))]);
            lock (feeds) feeds[line] = feed;
            return new(feed, feed.Stop);
        }, now);
        return (meters, started, feeds);
    }

    private static double Settled(Func<double> level)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        double value;
        while ((value = level()) == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        return value;
    }

    [Fact]
    public void APeakIsTheLoudestSampleOfTheChunk()
    {
        Assert.Equal(0, PulseMeters.Peak(new byte[16]));
        Assert.Equal(0.5, PulseMeters.Peak(Chunk(16384)));
        Assert.Equal(1.0, PulseMeters.Peak(Chunk(short.MinValue))); // -32768 is full scale, not over it
        Assert.Equal(32767 / 32768.0, PulseMeters.Peak(Chunk(short.MaxValue)));
    }

    [Fact]
    public void AMeterStartsWhenAskedAndReportsTheLastSecond()
    {
        using var meters = FakeMeters(() => 0).Meters;
        meters.Level("output:a", () => PulseMeters.OutputArgs("a.monitor")); // starts it (0 until its first chunk)
        Assert.Equal(0.5, Settled(() => meters.Level("output:a", () => throw new InvalidOperationException("started twice"))));
        Assert.Equal(1, meters.Running);
    }

    [Fact]
    public void AMeterNobodyAsksForStops()
    {
        long clock = 0;
        var (meters, _, feeds) = FakeMeters(() => clock);
        using (meters)
        {
            meters.Level("stream:798", () => PulseMeters.StreamArgs("798"));
            clock = PulseMeters.IdleMs;
            meters.Sweep();
            Assert.Equal(1, meters.Running); // asked exactly IdleMs ago: still wanted
            clock = PulseMeters.IdleMs + 1;
            meters.Sweep();
            Assert.Equal(0, meters.Running);
            Assert.True(Assert.Single(feeds.Values).Stopped);
        }
    }

    [Fact]
    public void AMeterWhoseCaptureEndsIsForgottenAndStartsAgain()
    {
        var started = 0;
        using var meters = new PulseMeters(_ => { started++; return new(new MemoryStream(Chunk(8192)), () => { }); });
        meters.Level("stream:1", () => PulseMeters.StreamArgs("1"));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (meters.Running > 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.Equal(0, meters.Running); // the stream ended
        meters.Level("stream:1", () => PulseMeters.StreamArgs("1"));
        Assert.Equal(2, started);
    }

    [Fact]
    public void MetersAreCappedAndStopWithTheBackend()
    {
        var (meters, started, feeds) = FakeMeters(() => 0);
        for (var i = 0; i < PulseMeters.MaxMeters + 5; i++) meters.Level("stream:" + i, () => PulseMeters.StreamArgs(i.ToString()));
        Assert.Equal(PulseMeters.MaxMeters, started.Count);
        meters.Dispose();
        Assert.All(feeds.Values, f => Assert.True(f.Stopped));
        Assert.Equal(0, meters.Level("stream:0", () => PulseMeters.StreamArgs("0")));
    }

    [Fact]
    public void ParecMetersTheMonitorSourceAndEachStreamButNoMicrophone()
    {
        var (meters, started, _) = FakeMeters(() => 0);
        using (meters)
        {
            var sinks = PulseDeckAudio.Parse(Sinks);
            var audio = PulseDeckAudio.Build(sinks, PulseDeckAudio.Parse(Sources), PulseDeckAudio.Parse(Inputs), Scarlett, Mic);
            PulseDeckAudio.WithLevels(audio, sinks, meters);
            Assert.Contains(started, a => a.EndsWith("--device=" + Scarlett + ".monitor", StringComparison.Ordinal));
            Assert.Contains(started, a => a.EndsWith("--monitor-stream=798", StringComparison.Ordinal));
            Assert.Contains(started, a => a.EndsWith("--monitor-stream=3746", StringComparison.Ordinal));
            Assert.DoesNotContain(started, a => a.Contains(Mic, StringComparison.Ordinal));
            Assert.All(started, a => Assert.StartsWith("--raw --format=s16le --rate=8000 --channels=2", a));
            Assert.Equal(audio.Devices.Count(d => d.Direction == "output") + audio.Sessions.Count, started.Count);
        }
    }

    [Fact]
    public void LevelsAreReadOnlyWhileAPhoneWantsThem()
    {
        long clock = 0;
        var (meters, started, _) = FakeMeters(() => clock);
        using var audio = new PulseDeckAudio(PactlFor, () => clock, meters);
        audio.Read(fresh: true);
        Assert.Empty(started); // ducking, profiles and the like start no meter
        audio.WantLevels();
        audio.Read(fresh: true);
        Assert.NotEmpty(started);
        var count = started.Count;
        clock = PulseMeters.IdleMs; // the phone stopped asking
        audio.Read(fresh: true);
        Assert.Equal(count, started.Count);
    }

    private static string? PactlFor(IReadOnlyList<string> args) => string.Join(' ', args) switch
    {
        "list sinks" => Sinks, "list sources" => Sources, "list sink-inputs" => Inputs, "info" => PipeWireInfo(Scarlett, Mic), _ => "",
    };

    [Fact]
    public void AStalledCaptureDoesNotHoldItsLastPeak()
    {
        long clock = 0;
        var (meters, _, _) = FakeMeters(() => clock); // one chunk, then the capture blocks without closing
        using (meters)
        {
            Assert.Equal(0.5, Settled(() => meters.Level("stream:1", () => PulseMeters.StreamArgs("1"))));
            clock = PulseMeters.WindowMs;
            Assert.Equal(0.5, meters.Level("stream:1", () => PulseMeters.StreamArgs("1"))); // still within the second
            clock = PulseMeters.WindowMs + 1;
            Assert.Equal(0, meters.Level("stream:1", () => PulseMeters.StreamArgs("1")));
            Assert.Equal(1, meters.Running); // still asked for, so it keeps running
        }
    }

    private static AgentConfig Pages(string widgets, bool enabled = true) => System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(
        $$"""{"pages":[{"id":"p","enabled":{{(enabled ? "true" : "false")}},"widgets":{{widgets}}}]}""",
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Theory]
    [InlineData("""[{"type":"audio-mixer"}]""", true, true)]
    [InlineData("""[{"type":"app-volume"}]""", true, true)]
    [InlineData("""[{"type":"device-volume"}]""", true, true)]
    [InlineData("""[{"type":"audio-balance"}]""", true, true)]
    [InlineData("""[{"type":"audio-meter"}]""", true, true)]
    [InlineData("""[{"type":"stack","options":{"widgets":[{"type":"audio-meter"}]}}]""", true, true)]
    [InlineData("""[{"type":"stack","options":{"widgets":[{"type":"audio-meter","enabled":false}]}}]""", true, false)]
    [InlineData("""[{"type":"panic-mute"},{"type":"audio-outputs"},{"type":"microphone"}]""", true, false)] // no output or app meter
    [InlineData("""[{"type":"audio-mixer"}]""", false, false)] // on a disabled page
    [InlineData("[]", true, false)]
    public void MetersStartOnlyForAWidgetThatShowsThem(string widgets, bool pageEnabled, bool shown) =>
        Assert.Equal(shown, ControlDeck.MetersShown(Pages(widgets, pageEnabled)));
}
