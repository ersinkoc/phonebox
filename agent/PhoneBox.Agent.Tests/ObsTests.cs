using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The OBS client against a fake obs-websocket 5 server: handshake with a password, state, buttons.</summary>
public sealed class ObsTests : IDisposable
{
    private const string Password = "supersecret";
    private const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    private const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly HttpListener _server;
    private readonly string _ws;
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-obs-{Guid.NewGuid():N}.json");
    private readonly List<string> _requests = [];
    private bool _recording;
    private string _scene = "Desk";
    private TaskCompletionSource? _inputsReady;
    private TaskCompletionSource? _inputsEntered;
    private WebSocket? _activeSocket;
    private volatile bool _virtualCamActive;
    // request types the fake OBS refuses (requestStatus.result false)
    private readonly HashSet<string> _refuse = [];

    public ObsTests()
    {
        _server = TestPorts.Listen();
        _ws = _server.Prefixes.Single().Replace("http://", "ws://", StringComparison.Ordinal);
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Session(ctx));
        }
    }

    private async Task Session(HttpListenerContext ctx)
    {
        var wsc = await ctx.AcceptWebSocketAsync("obswebsocket.json");
        var ws = wsc.WebSocket;
        _activeSocket = ws;
        async Task Send(object o) => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, default);
        async Task<JsonNode?> Receive()
        {
            var buf = new byte[65536];
            var r = await ws.ReceiveAsync(buf, default);
            return r.MessageType == WebSocketMessageType.Close ? null : JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
        }
        try
        {
            await Send(new { op = 0, d = new { obsWebSocketVersion = "5.5.0", rpcVersion = 1, authentication = new { challenge = Challenge, salt = Salt } } });
            var identify = await Receive();
            if (identify?["d"]?["authentication"]?.GetValue<string>() != ObsMonitor.AuthAnswer(Password, Salt, Challenge))
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed.", default);
                return;
            }
            await Send(new { op = 2, d = new { negotiatedRpcVersion = 1 } });
            while (ws.State == WebSocketState.Open)
            {
                var m = await Receive();
                if (m is null) break;
                var d = m["d"]!;
                var type = d["requestType"]!.GetValue<string>();
                if (type == "GetInputList") _inputsEntered?.TrySetResult();
                if (type == "GetInputList" && _inputsReady is { } ready) await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                lock (_requests) _requests.Add(type + (d["requestData"]?["sceneName"] is { } sn ? ":" + sn.GetValue<string>() : ""));
                object data = type switch
                {
                    "GetStreamStatus" => new { outputActive = false, outputDuration = 0 },
                    "GetRecordStatus" => new { outputActive = _recording, outputPaused = false, outputDuration = _recording ? 61_000 : 0 },
                    "GetSceneList" => new { currentProgramSceneName = _scene, scenes = new[] { new { sceneName = "BRB" }, new { sceneName = "Desk" }, new { sceneName = "Camera" } } },
                    "GetSpecialInputs" => new { mic1 = "Mic/Aux" },
                    "GetInputMute" => new { inputMuted = true },
                    "GetInputList" => new { inputs = new[] { new { inputName = "Mic/Aux" } } },
                    "GetInputVolume" => new { inputVolumeMul = 0.65 },
                    "GetSceneItemList" => new { sceneItems = new[] { new { sceneItemId = 7, sourceName = "Webcam", sceneItemEnabled = true } } },
                    "GetSceneTransitionList" => new { transitions = new[] { new { transitionName = "Fade" } } },
                    "GetCurrentSceneTransition" => new { transitionName = "Fade", transitionDuration = 300 },
                    "GetReplayBufferStatus" => new { outputActive = true },
                    "GetVirtualCamStatus" => new { outputActive = _virtualCamActive },
                    _ => new { },
                };
                bool refused;
                lock (_refuse) refused = _refuse.Contains(type);
                if (refused)
                {
                    await Send(new { op = 7, d = new { requestType = type, requestId = d["requestId"]!.GetValue<string>(), requestStatus = new { result = false, code = 500, comment = "refused by the test" } } });
                    continue;
                }
                if (type == "StopVirtualCam") _virtualCamActive = false;
                if (type == "ToggleRecord") _recording = !_recording;
                if (type == "SetCurrentProgramScene") _scene = d["requestData"]!["sceneName"]!.GetValue<string>();
                await Send(new { op = 7, d = new { requestType = type, requestId = d["requestId"]!.GetValue<string>(), requestStatus = new { result = true, code = 100 }, responseData = data } });
            }
        }
        catch { }
    }

    private ObsMonitor Monitor(string password, bool control = true)
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            obs = new { enabled = true, url = _ws, password },
            pages = new[] { new { id = "p", title = "P", widgets = new object[] { new { id = "o", type = "obs", x = 0, y = 0, w = 4, h = 3, options = new { control } } } } },
        }));
        return new ObsMonitor(new ConfigStore(_configPath), NullLogger<ObsMonitor>.Instance);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task ConnectsReadsStateAndRunsButtons()
    {
        var obs = Monitor(Password);
        using var cts = new CancellationTokenSource();
        await obs.StartAsync(cts.Token);
        await Until(() => obs.Snapshot().Connected);
        var s = obs.Snapshot();
        Assert.Equal("Desk", s.Scene);
        Assert.Equal(["Camera", "Desk", "BRB"], s.Scenes); // OBS lists scenes bottom-up
        Assert.True(s.MicMuted);
        Assert.False(obs.Live);

        Assert.Null(await obs.ActAsync("record", null));
        Assert.Null(await obs.ActAsync("scene", "Camera"));
        Assert.NotNull(await obs.ActAsync("scene", "No such scene"));
        await Until(() => obs.Snapshot().Recording && obs.Snapshot().Scene == "Camera");
        Assert.True(obs.Live);
        Assert.Contains("SetCurrentProgramScene:Camera", _requests);
        await obs.StopAsync(default);
    }

    [Fact]
    public async Task ButtonsNeedTheWidgetsPermission()
    {
        var obs = Monitor(Password, control: false);
        Assert.NotNull(await obs.ActAsync("record", null));
        Assert.False((await obs.DeckAct("record",null,null,null,default)).Ok);
    }

    [Fact]
    public async Task DeckReadsCapabilitiesAndSendsOnlyValidatedResources()
    {
        var obs=Monitor(Password);await obs.StartAsync(default);
        try
        {
            await Until(()=>obs.Snapshot().Connected);
            var state=await UntilDeck(obs);
            Assert.Equal(65,state["inputs"]![0]!["volume"]!.GetValue<double>());
            Assert.True(state["replayActive"]!.GetValue<bool>());
            Assert.False((await obs.DeckAct("volume","unknown",50,null,default)).Ok);
            Assert.False((await obs.DeckAct("volume","Mic/Aux",101,null,default)).Ok);
            Assert.False((await obs.DeckAct("source","99",null,false,default)).Ok);
            Assert.False((await obs.DeckAct("transition","unknown",null,null,default)).Ok);
            Assert.True((await obs.DeckAct("volume","Mic/Aux",45,null,default)).Ok);
            Assert.True((await obs.DeckAct("source","7",null,false,default)).Ok);
            Assert.True((await obs.DeckAct("transition","Fade",null,null,default)).Ok);
            Assert.True((await obs.DeckAct("save-replay",null,null,null,default)).Ok);
            Assert.True((await obs.DeckAct("camera",null,null,null,default)).Ok);
            lock(_requests)
            {
                Assert.Single(_requests,r=>r=="SetInputVolume");
                Assert.Contains("SetSceneItemEnabled:Desk",_requests);
                Assert.Contains("SaveReplayBuffer",_requests);Assert.Contains("ToggleVirtualCam",_requests);
            }
        }
        finally { await obs.StopAsync(default); }
    }

    [Fact]
    public async Task MicsOffCameraOffStopsTheVirtualCameraWithoutTogglingIt()
    {
        _virtualCamActive = true;
        // a privacy action: it does not need the OBS widget's control option
        var obs = Monitor(Password, control: false);
        await obs.StartAsync(default);
        try
        {
            await Until(() => obs.Snapshot().Connected);
            Assert.Equal(VirtualCameraStop.Stopped, await obs.StopVirtualCamAsync(default));
            Assert.False(_virtualCamActive);
            // a camera that is already off stays off: StopVirtualCam is not sent again, and nothing toggles it on
            Assert.Equal(VirtualCameraStop.AlreadyOff, await obs.StopVirtualCamAsync(default));
            Assert.False(_virtualCamActive);
            lock (_requests)
            {
                Assert.Single(_requests, r => r == "StopVirtualCam");
                Assert.DoesNotContain("ToggleVirtualCam", _requests);
            }
        }
        finally { await obs.StopAsync(default); }
    }

    [Fact]
    public async Task MicsOffCameraOffReportsACameraObsDidNotStop()
    {
        _virtualCamActive = true;
        lock (_refuse) _refuse.Add("StopVirtualCam");
        var obs = Monitor(Password);
        await obs.StartAsync(default);
        try
        {
            await Until(() => obs.Snapshot().Connected);
            Assert.Equal(VirtualCameraStop.Failed, await obs.StopVirtualCamAsync(default));
            Assert.True(_virtualCamActive);
            lock (_requests) Assert.DoesNotContain("ToggleVirtualCam", _requests);

            // OBS without a virtual camera refuses the status request: still not reported as off
            lock (_refuse) _refuse.Add("GetVirtualCamStatus");
            Assert.Equal(VirtualCameraStop.Failed, await obs.StopVirtualCamAsync(default));
        }
        finally { await obs.StopAsync(default); }
    }

    [Fact]
    public async Task MicsOffCameraOffSaysObsIsNotConnected()
    {
        _virtualCamActive = true;
        var obs = Monitor(Password); // configured, but never connected
        Assert.Equal(VirtualCameraStop.NotConnected, await obs.StopVirtualCamAsync(default));
        lock (_requests) Assert.Empty(_requests);
        Assert.True(_virtualCamActive);
    }

    private static async Task<JsonObject> UntilDeck(ObsMonitor obs)
    {
        for(var i=0;i<30;i++)
        {
            var state=await obs.DeckSnapshot(default);
            if(state["connected"]?.GetValue<bool>()==true && state["inputs"] is JsonArray)return state;
            await Task.Delay(100);
        }
        throw new TimeoutException("OBS discovery did not complete");
    }

    [Fact]
    public async Task SlowObsDiscoveryReturnsLoadingThenTheMeasuredControls()
    {
        _inputsReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var obs=Monitor(Password);await obs.StartAsync(default);
        try
        {
            await Until(()=>obs.Snapshot().Connected);
            var loading=await obs.DeckSnapshot(default);
            Assert.True(loading["loading"]!.GetValue<bool>());
            Assert.False(loading["connected"]!.GetValue<bool>());
            _inputsReady.SetResult();
            var state=await UntilDeck(obs);
            Assert.Equal("Mic/Aux",state["inputs"]![0]!["id"]!.GetValue<string>());
            Assert.Equal(65,state["inputs"]![0]!["volume"]!.GetValue<double>());
        }
        finally { _inputsReady.TrySetResult();await obs.StopAsync(default); }
    }

    [Fact]
    public async Task TestReportsTheWrongPassword()
    {
        Assert.StartsWith("Connected", await ObsMonitor.TestAsync(_ws, Password));
        Assert.NotEqual("Connected", await ObsMonitor.TestAsync(_ws, "wrong"));
    }

    [Fact]
    public async Task DisconnectingMidDiscoveryNeverPoisonsCachedDeckSnapshot()
    {
        _inputsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var obs = Monitor(Password);
        await obs.StartAsync(default);
        try
        {
            await Until(() => obs.Snapshot().Connected);
            var discovery = obs.DeckSnapshot(default);
            await _inputsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            // DeckSnapshot stores the shared discovery once it first yields, which can be just after the request arrives
            Task<JsonObject>? refresh = null;
            for (var i = 0; i < 40 && refresh is null; i++)
            {
                refresh = (Task<JsonObject>?)typeof(ObsMonitor).GetField("_deckRefresh", fields)!.GetValue(obs);
                if (refresh is null) await Task.Delay(50);
            }
            Assert.NotNull(refresh);

            try { await _activeSocket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
            _activeSocket?.Abort();
            _inputsReady.TrySetResult();

            var snap = await discovery.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(snap["connected"]!.GetValue<bool>());
            // DeckSnapshot may answer "loading" before discovery ends: the shared discovery itself must finish
            // disconnected and leave no deck behind
            var discovered = await refresh.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(discovered["connected"]!.GetValue<bool>());
            Assert.Null(typeof(ObsMonitor).GetField("_deck", fields)!.GetValue(obs));
        }
        finally
        {
            _inputsReady.TrySetResult();
            await obs.StopAsync(default);
        }
    }

    public void Dispose()
    {
        // Close performs the stop as well; avoid a second endpoint removal/rebind on Unix.
        _server.Close();
        try { File.Delete(_configPath); } catch { }
    }
}
