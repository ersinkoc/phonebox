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

    /// <summary>A fake pactl: answers the listings above and records every other call.</summary>
    private static (PulseDeckAudio Audio, List<string> Calls) Fake(string defaultSink = Scarlett, Func<string, bool>? fails = null)
    {
        var calls = new List<string>();
        var current = defaultSink;
        var audio = new PulseDeckAudio(args =>
        {
            var line = string.Join(' ', args);
            switch (line)
            {
                case "list sinks": return Sinks;
                case "list sources": return Sources;
                case "list sink-inputs": return Inputs;
                case "get-default-sink": return current + "\n";
                case "get-default-source": return Mic + "\n";
            }
            calls.Add(line);
            if (fails?.Invoke(line) == true) return null;
            if (args is ["set-default-sink", var name]) current = name;
            return "";
        });
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
        Assert.True(audio.DefaultDevice(Mic));
        Assert.Equal(["set-default-sink korvex_ch_musica", $"set-default-source {Mic}"], calls);
        Assert.False(audio.DefaultDevice(Scarlett, 3));
        Assert.False(audio.DefaultDevice("no-such-sink"));
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
    public void OverlappingChannelChangesBothLand()
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
                case "get-default-sink": return Scarlett;
                case "get-default-source": return Mic;
            }
            if (args is ["set-sink-volume", Scarlett, var left, var right])
                lock (gate) levels = [int.Parse(left.TrimEnd('%')), int.Parse(right.TrimEnd('%'))];
            return "";
        });
        var a = Task.Run(() => audio.Device(Scarlett, 70, null, channel: 0));
        var b = Task.Run(() => audio.Device(Scarlett, 60, null, channel: 1));
        Assert.True(a.Result && b.Result);
        Assert.Equal([70, 60], levels);
    }

    [Fact]
    public void AFailedListingIsUnavailableNotTheSystemOutputAlone()
    {
        // panic and "Mics off" must not take a list without microphones for the whole list
        var pulse = new PulseDeckAudio(_ => null);
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(new AudioControl(), media, _ => null, pulse);
        var state = native.Audio();
        Assert.False(state.Available);
        Assert.Empty(state.Devices);
    }

    [Fact]
    public void PortableNativeUsesPulseWhenAvailable()
    {
        var (pulse, calls) = Fake();
        using var media = new MediaMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(new AudioControl(), media, _ => null, pulse);
        Assert.Equal(3, native.Audio().Devices.Count);
        Assert.True(native.Session("798", 10, null));
        Assert.True(native.DefaultDevice(Mic));
        Assert.Equal(["set-sink-input-volume 798 10%", $"set-default-source {Mic}"], calls);
    }
}
