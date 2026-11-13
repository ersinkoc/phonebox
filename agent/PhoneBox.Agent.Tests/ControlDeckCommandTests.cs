using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ControlDeckCommandTests
{
    private sealed class Native : IDeckNative
    {
        public readonly List<string> Writes=[];
        public List<DeckSession> Sessions=[];
        public DeckAudio Audio()=>new(true,null,[new("speaker","Speaker","output",true,40,false,0,2),new("headset","Headset","output",false,60,false,0,2),new("mic","Mic","input",true,70,true,0,1)],Sessions.ToArray());
        public bool Device(string id,int? volume,bool? muted,int? channel=null){Writes.Add("device:"+id);return true;}
        public bool Session(string id,int? volume,bool? muted){Writes.Add("session:"+id);Sessions=Sessions.Select(s=>s.Id==id?s with{Volume=volume??s.Volume,Muted=muted??s.Muted}:s).ToList();return true;}
        public bool DefaultDevice(string id,int? role=null){Writes.Add("default:"+id+":"+role);return true;}
        public IReadOnlyList<DeckMonitor> Monitors()=>[];
        public bool Brightness(string id,int value)=>false;
        public IReadOnlyList<DeckItem> PowerPlans()=>[];
        public Task<bool> PlatformAction(string kind,string target,string? argument,int value,CancellationToken ct){Writes.Add("platform:"+kind);return Task.FromResult(true);}
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct)=>Task.FromResult<IReadOnlyList<DeckMedia>>([new("Spotify.exe","Spotify","Track",true,0,100000,true,true,true,false,"None")]);
        public Task<bool> MediaAction(string id,string action,long value,CancellationToken ct){Writes.Add("media:"+action);return Task.FromResult(true);}
        public void Play(string id,string file,string? device,int volume){Writes.Add("sound:"+volume);}
        public void StopSound(string? id){Writes.Add("stop:"+(id ?? ""));}
        public IReadOnlyList<DeckItem> MidiPorts()=>[];
        public void Midi(int port,string message,int value){}
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){await Task.CompletedTask;yield break;}
        public void Dispose(){}
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory=Path.Combine(Path.GetTempPath(),"phonebox-controls-"+Guid.NewGuid().ToString("N"));
        public readonly Native Native=new();
        public readonly ControlDeck Deck;
        public long Now=1000;
        public Fixture(string controls="{}")
        {
            Directory.CreateDirectory(_directory);var path=Path.Combine(_directory,"config.json");
            File.WriteAllText(path,"{\"controls\":"+controls+"}");
            Deck=new(new ConfigStore(path),Native,NullLogger<ControlDeck>.Instance,()=>Now);
        }
        public Task<DeckResult> Act(string kind,string id,int? value=null,string? op=null)=>Deck.Act(kind,id,op,value,null,null,default);
        public void Dispose(){Deck.Dispose();Directory.Delete(_directory,true);}
    }

    [Fact]
    public async Task MissingExplicitProfileDeviceDoesNotWriteAnyLevels()
    {
        using var f=new Fixture("""{"profiles":[{"id":"profile","output":"unplugged","levels":[{"kind":"device","id":"speaker","volume":20,"muted":false}]}]}""");
        Assert.False((await f.Act("profile","profile")).Ok);Assert.Empty(f.Native.Writes);
    }
    [Fact]
    public async Task ExplicitProfileOutputOverridesCapturedRoleSelection()
    {
        using var f=new Fixture("""{"profiles":[{"id":"profile","output":"headset","defaults":{"output:1":"speaker"},"levels":[]}]}""");
        Assert.True((await f.Act("profile","profile")).Ok);
        Assert.Equal(["default:speaker:1","default:headset:"],f.Native.Writes);
    }
    [Fact]
    public async Task PhoneCannotProvideAnExecutableOrUnknownTarget()
    {
        using var f=new Fixture();
        Assert.False((await f.Act("action","powershell.exe")).Ok);
        Assert.False((await f.Act("device","unplugged",50)).Ok);
        Assert.False((await f.Act("session","ended",50)).Ok);
        Assert.False((await f.Act("device","speaker",101)).Ok);
        Assert.False((await f.Act("device","speaker")).Ok);
        Assert.Empty(f.Native.Writes);
    }
    [Fact]
    public async Task PowerActionsCannotBypassOptInOrHoldNonce()
    {
        using var f=new Fixture("""{"allowPower":true}""");
        Assert.False((await f.Act("power","shutdown")).Ok);
        Assert.False((await f.Act("power-arm","format-drive")).Ok);
        Assert.Empty(f.Native.Writes);
    }
    [Fact]
    public async Task ZeroSoundVolumeIsPreserved()
    {
        using var f=new Fixture("""{"actions":[{"id":"sound","kind":"sound","target":"configured.wav","value":0}]}""");
        Assert.True((await f.Act("action","sound")).Ok);Assert.Equal(["sound:0"],f.Native.Writes);
    }
    [Fact]
    public async Task SleepFadesOnlyTheSelectedPlayerThenPausesAndRestoresVolume()
    {
        using var f=new Fixture();
        f.Native.Sessions=[new("spotify-session","Spotify",123,"speaker",80,false,0),new("browser","Chrome",124,"speaker",55,false,0)];
        Assert.True((await f.Act("sleep","Spotify.exe",1)).Ok);
        f.Now+=45_000;await f.Deck.TickSleep(default);
        Assert.Equal(40,f.Native.Sessions[0].Volume);Assert.Equal(55,f.Native.Sessions[1].Volume);
        f.Now+=15_000;await f.Deck.TickSleep(default);
        Assert.Equal(80,f.Native.Sessions[0].Volume);Assert.Equal(55,f.Native.Sessions[1].Volume);
        Assert.Contains("media:pause",f.Native.Writes);
    }
    [Fact]
    public async Task SleepFadePreservesManualVolumeChanges()
    {
        using var f=new Fixture();f.Native.Sessions=[new("spotify-session","Spotify",123,"speaker",80,false,0)];
        Assert.True((await f.Act("sleep","Spotify.exe",1)).Ok);
        f.Now+=45_000;await f.Deck.TickSleep(default);Assert.Equal(40,f.Native.Sessions[0].Volume);
        f.Native.Sessions[0]=f.Native.Sessions[0] with{Volume=65};
        f.Now+=15_000;await f.Deck.TickSleep(default);Assert.Equal(65,f.Native.Sessions[0].Volume);
    }

    /// <summary>
    /// Verifies disabled controls stay hidden from snapshots, refuse execution (while still allowing stop),
    /// stop running instances on disable, and preserve profile/Spotify/default-enabled semantics.
    /// </summary>
    [Fact]
    public async Task DisabledControlIsHiddenRejectedAndStopped()
    {
        using var f=new Fixture("""{"actions":[{"id":"r1","label":"Station 1","kind":"radio","target":"https://example.com/1","value":80,"enabled":false},{"id":"r2","label":"Station 2","kind":"radio","target":"https://example.com/2","value":80,"enabled":true},{"id":"s1","label":"Chime","kind":"sound","target":"chime.wav","value":50,"enabled":false}]}""");
        var snap=System.Text.Json.JsonSerializer.Serialize(await f.Deck.Snapshot(default));
        Assert.DoesNotContain("\"r1\"",snap);
        Assert.Contains("\"r2\"",snap);
        var res=await f.Act("action","r1");
        Assert.False(res.Ok);
        Assert.Equal("Control is disabled",res.Error);
        var stopRadio=await f.Act("action","r1",op:"stop");
        Assert.True(stopRadio.Ok);
        var stopSound=await f.Act("action","s1",op:"stop");
        Assert.True(stopSound.Ok);
        f.Deck.StopDisabledControls();
        Assert.Contains("stop:r1",f.Native.Writes);
        Assert.Contains("stop:s1",f.Native.Writes);
        Assert.DoesNotContain("stop:r2",f.Native.Writes);
    }

    /// <summary>
    /// Verifies disabled playlist controls return "Control is disabled", profiles skip disabled actions,
    /// disabled tasks/servers can still be stopped, and missing JSON "enabled" defaults to true.
    /// </summary>
    [Fact]
    public async Task DisabledPlaylistProfileAndDefaultEnablementBehaveSafely()
    {
        var dir=Path.Combine(Path.GetTempPath(),"phonebox-ctrl-disabled-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath=Path.Combine(dir,"config.json");
            File.WriteAllText(cfgPath,"""
                {"controls":{
                  "actions":[
                    {"id":"pl1","label":"Chill","kind":"playlist","target":"spotify:playlist:37i9dQZF1DX4WYpdgoIcn6","enabled":false},
                    {"id":"r1","label":"Disabled Radio","kind":"radio","target":"https://example.com/1","value":70,"enabled":false},
                    {"id":"r2","label":"Enabled Radio","kind":"radio","target":"https://example.com/2","value":75},
                    {"id":"t1","label":"Disabled Task","kind":"task","target":"missing-shortcut","enabled":false}
                  ],
                  "profiles":[{"id":"p1","label":"Mix","levels":[],"actions":["r1","r2"]}]
                }}
                """);
            var cfg=new ConfigStore(cfgPath);
            Assert.True(cfg.Get().Controls.Actions.First(a=>a.Id=="r2").Enabled);

            using var spotify=new SpotifyControl(cfg,new AgentEndpoint(19885),new HttpClient());
            var spRes=await spotify.Act("playlist","pl1",CancellationToken.None);
            Assert.False(spRes.Ok);
            Assert.Equal("Control is disabled",spRes.Error);

            var native=new Native();
            using var deck=new ControlDeck(cfg,native,NullLogger<ControlDeck>.Instance,()=>1000);
            var taskStop=await deck.Act("action","t1","stop",null,null,null,default);
            Assert.False(taskStop.Ok);
            Assert.Equal("This task is not running",taskStop.Error);

            var profRes=await deck.Act("profile","p1",null,null,null,null,default);
            Assert.True(profRes.Ok);
            Assert.Contains("sound:75",native.Writes);
            Assert.DoesNotContain("sound:70",native.Writes);
        }
        finally
        {
            try{Directory.Delete(dir,true);}catch{}
        }
    }
}
