using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record ObsSnapshot(bool Connected, string? Error, bool Streaming, long StreamMs, bool Recording, bool RecordPaused, long RecordMs,
    string? Scene, IReadOnlyList<string> Scenes, bool? MicMuted, string? MicName);

/// <summary>
/// OBS Studio through obs-websocket 5 (built into OBS 28+; Tools › WebSocket Server Settings). Keeps one connection
/// while enabled, reads the stream / recording state and scenes every two seconds, and runs the OBS widget's buttons.
/// </summary>
public sealed partial class ObsMonitor(ConfigStore config, ILogger<ObsMonitor> log) : BackgroundService
{
    private volatile ObsSnapshot _snap = new(false, null, false, 0, false, false, 0, null, [], null, null);
    private volatile ClientWebSocket? _ws;
    // Retiring a session and publishing a deck built on it are ordered by this lock (never held across an await).
    private readonly Lock _sessionGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);

    public ObsSnapshot Snapshot() => _snap;
    public bool Live => _snap.Connected && (_snap.Streaming || (_snap.Recording && !_snap.RecordPaused));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            var obs = config.Get().Obs;
            if (!obs.Enabled) { _snap = _snap with { Connected = false, Error = null }; InvalidateDeck(); await Task.Delay(2000, ct); continue; }
            try
            {
                await RunConnection(obs, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var msg = ex is WebSocketException or HttpRequestException ? "OBS is not running or its WebSocket server is off" : ex.Message;
                _snap = _snap with { Connected = false, Error = msg };
                if (msg != lastError) { log.LogInformation("obs: {Msg}", msg); lastError = msg; }
            }
            await Task.Delay(5000, ct);
        }
    }

    private async Task RunConnection(ObsConfig obs, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri(string.IsNullOrWhiteSpace(obs.Url) ? "ws://127.0.0.1:4455" : obs.Url), connectTimeout.Token);

        // Hello -> Identify (with the challenge answer if a password is set) -> Identified
        var hello = await Receive(ws, ct) ?? throw new Exception("OBS closed the connection");
        var auth = hello["d"]?["authentication"];
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (auth is not null)
        {
            if (string.IsNullOrEmpty(obs.Password)) throw new Exception("OBS asks for a password (Settings › General › OBS)");
            identify["authentication"] = AuthAnswer(obs.Password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        await SendRaw(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, ct);
        var identified = await Receive(ws, ct);
        if (identified?["op"]?.GetValue<int>() != 2) throw new Exception("OBS rejected the password");
        log.LogInformation("obs: connected");
        _ws = ws;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() => ReadLoop(ws, stop.Token), CancellationToken.None);
        try
        {
            string? mic = null;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && config.Get().Obs.Enabled)
            {
                var stream = await Request("GetStreamStatus", null, ct);
                var record = await Request("GetRecordStatus", null, ct);
                var scenes = await Request("GetSceneList", null, ct);
                mic ??= (await Request("GetSpecialInputs", null, ct))?["mic1"]?.GetValue<string>();
                bool? muted = mic is null ? null : (await Request("GetInputMute", new JsonObject { ["inputName"] = mic }, ct))?["inputMuted"]?.GetValue<bool>();
                _snap = new ObsSnapshot(true, null,
                    stream?["outputActive"]?.GetValue<bool>() ?? false, stream?["outputDuration"]?.GetValue<long>() ?? 0,
                    record?["outputActive"]?.GetValue<bool>() ?? false, record?["outputPaused"]?.GetValue<bool>() ?? false, record?["outputDuration"]?.GetValue<long>() ?? 0,
                    scenes?["currentProgramSceneName"]?.GetValue<string>(),
                    // OBS lists scenes bottom-up
                    scenes?["scenes"]?.AsArray().Select(s => s?["sceneName"]?.GetValue<string>() ?? "").Where(s => s != "").Reverse().ToList() ?? [],
                    muted, mic);
                await Task.Delay(2000, ct);
            }
        }
        finally
        {
            lock (_sessionGate)
            {
                _ws = null;
                _snap = _snap with { Connected = false };
                InvalidateDeck();
            }
            stop.Cancel();
            try { await reader; } catch { }
            foreach (var p in _pending.Values) p.TrySetException(new IOException("OBS disconnected"));
            _pending.Clear();
        }
    }

    private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await Receive(ws, ct);
            if (msg is null) break;
            if (msg["op"]?.GetValue<int>() != 7) continue; // only request responses
            var d = msg["d"];
            var id = d?["requestId"]?.GetValue<string>();
            if (id is null || !_pending.TryRemove(id, out var tcs)) continue;
            var ok = d?["requestStatus"]?["result"]?.GetValue<bool>() == true;
            if (ok) tcs.TrySetResult(d?["responseData"]?.DeepClone() ?? new JsonObject());
            else tcs.TrySetException(new Exception(d?["requestStatus"]?["comment"]?.GetValue<string>() ?? "OBS refused the request"));
        }
    }

    private async Task<JsonNode?> Request(string type, JsonObject? data, CancellationToken ct)
    {
        var ws = _ws ?? throw new Exception("OBS is not connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null) d["requestData"] = data;
        try
        {
            // Disconnect cleanup retires _ws before it fails the pending requests: one registered after that point
            // would never be failed, so it stops here.
            if (!ReferenceEquals(_ws, ws)) throw new Exception("OBS is not connected");
            await SendRaw(ws, new JsonObject { ["op"] = 6, ["d"] = d }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            // The deadline completes this request itself, even when cleanup already dropped it from _pending; an
            // answer that arrived first keeps its result.
            await using var _ = timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("OBS did not answer")));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>The OBS widget's buttons: record, pause, stream, scene, mic.</summary>
    public async Task<string?> ActAsync(string action, string? scene)
    {
        if (!config.Get().Widgets(enabledPagesOnly: true).Any(w => (w.Type == "obs" || w.Type.StartsWith("obs-")) && w.Bool("control") == true))
            return "controlling OBS is turned off in the widget's settings";
        try
        {
            switch (action)
            {
                case "record": await Request("ToggleRecord", null, CancellationToken.None); break;
                case "pause": await Request("ToggleRecordPause", null, CancellationToken.None); break;
                case "stream": await Request("ToggleStream", null, CancellationToken.None); break;
                case "mic" when _snap.MicName is { } mic: await Request("ToggleInputMute", new JsonObject { ["inputName"] = mic }, CancellationToken.None); break;
                case "scene" when !string.IsNullOrEmpty(scene) && _snap.Scenes.Contains(scene):
                    await Request("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene }, CancellationToken.None); break;
                default: return "unknown action";
            }
            log.LogInformation("obs: {Action} {Scene}", action, scene);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Settings › Test: connects once with the given (possibly unsaved) address and password.</summary>
    public static async Task<string> TestAsync(string? url, string? password)
    {
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("obswebsocket.json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(string.IsNullOrWhiteSpace(url) ? "ws://127.0.0.1:4455" : url), cts.Token);
            var hello = await Receive(ws, cts.Token);
            var auth = hello?["d"]?["authentication"];
            var version = hello?["d"]?["obsWebSocketVersion"]?.GetValue<string>();
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
            if (auth is not null)
            {
                if (string.IsNullOrEmpty(password)) return "OBS asks for a password";
                identify["authentication"] = AuthAnswer(password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
            }
            await SendOnce(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, cts.Token);
            var r = await Receive(ws, cts.Token);
            return r?["op"]?.GetValue<int>() == 2 ? $"Connected (obs-websocket {version})" : "Wrong password";
        }
        catch (Exception ex) { return ex is WebSocketException or OperationCanceledException ? "No answer: is OBS running with its WebSocket server on?" : ex.Message; }
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge)), as obs-websocket 5 specifies.</summary>
    public static string AuthAnswer(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task SendRaw(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    private static Task SendOnce(ClientWebSocket ws, JsonObject msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonNode?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }
}
