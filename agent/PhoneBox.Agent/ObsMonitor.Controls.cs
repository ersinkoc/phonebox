using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>What happened to the OBS virtual camera when the Control Deck asked it to stop.</summary>
public enum VirtualCameraStop { Stopped, AlreadyOff, NotConnected, Failed }

/// <summary>The OBS virtual camera as the Control Deck sees it (a fake in tests).</summary>
internal interface IVirtualCamera
{
    Task<VirtualCameraStop> StopVirtualCamAsync(CancellationToken ct);
}

public sealed partial class ObsMonitor : IVirtualCamera
{
    private readonly SemaphoreSlim _deckGate = new(1, 1);
    /// <summary>A built deck and the OBS session it describes; it is served only while that session is current.</summary>
    private sealed record DeckCache(JsonObject Deck, System.Net.WebSockets.ClientWebSocket Session);
    private volatile DeckCache? _deck;
    private long _deckAt;
    private readonly Lock _deckRefreshGate = new();
    private Task<JsonObject>? _deckRefresh;

    private void InvalidateDeck()
    {
        _deck = null;
        Interlocked.Exchange(ref _deckAt, 0);
    }

    private JsonObject DisconnectedDeck() =>
        new() { ["connected"] = false, ["error"] = _snap.Error ?? "Configure the OBS connection in desktop settings" };

    // Turning OBS off in the settings stops the deck at once, before the connection loop notices.
    private bool DeckUsable() => _snap.Connected && config.Get().Obs.Enabled;

    /// <summary>The cached deck if it belongs to the current session (and, with <paramref name="fresh"/>, is recent).</summary>
    private JsonObject? CachedDeck(bool fresh = true) =>
        _deck is { } cached && ReferenceEquals(cached.Session, _ws)
        && (!fresh || Environment.TickCount64 - Interlocked.Read(ref _deckAt) < 1500) ? cached.Deck : null;

    public async Task<JsonObject> DeckSnapshot(CancellationToken ct)
    {
        if (!DeckUsable()) return DisconnectedDeck();
        if (CachedDeck() is { } cached) return cached;
        Task<JsonObject> refresh;
        lock (_deckRefreshGate)
        {
            if (_deckRefresh?.IsCompleted != false) _deckRefresh = BuildDeckSnapshot(CancellationToken.None);
            refresh = _deckRefresh;
        }
        // Discovery can involve many inputs. Keep the phone proxy responsive while one shared refresh completes.
        var completed = await Task.WhenAny(refresh, Task.Delay(1000, ct));
        ct.ThrowIfCancellationRequested();
        if (!DeckUsable()) return DisconnectedDeck();
        if (completed == refresh) return await refresh;
        return CachedDeck(fresh: false) ?? new() { ["connected"] = false, ["loading"] = true, ["error"] = "Discovering OBS controls; waiting for OBS" };
    }

    private async Task<JsonObject> BuildDeckSnapshot(CancellationToken ct)
    {
        var session = _ws;
        if (!_snap.Connected || session is null || session.State != System.Net.WebSockets.WebSocketState.Open)
            return DisconnectedDeck();
        if (CachedDeck() is { } fast) return fast;
        await _deckGate.WaitAsync(ct);
        try
        {
            if (!_snap.Connected || !ReferenceEquals(_ws, session) || session.State != System.Net.WebSockets.WebSocketState.Open)
                return DisconnectedDeck();
            if (CachedDeck() is { } fresh) return fresh;
            async Task<JsonNode?> Optional(string request, JsonObject? args = null)
            { try { return await Request(request, args, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return null; } }
            var rawInputs = (await Optional("GetInputList"))?["inputs"] as JsonArray;
            var inputs = new JsonArray();
            foreach (var input in rawInputs?.Take(64) ?? [])
            {
                if (!_snap.Connected || !ReferenceEquals(_ws, session)) return DisconnectedDeck();
                var name = input?["inputName"]?.GetValue<string>(); if (string.IsNullOrEmpty(name)) continue;
                var args = new JsonObject { ["inputName"] = name };
                var volume = await Optional("GetInputVolume", args);
                var mute = await Optional("GetInputMute", new() { ["inputName"] = name });
                inputs.Add(new JsonObject { ["id"] = name, ["name"] = name,
                    ["volume"] = volume?["inputVolumeMul"] is { } mul ? Math.Round(mul.GetValue<double>() * 100) : null,
                    ["muted"] = mute?["inputMuted"]?.DeepClone() });
            }
            var scene = _snap.Scene;
            var rawItems = scene is null ? null : (await Optional("GetSceneItemList", new() { ["sceneName"] = scene }))?["sceneItems"] as JsonArray;
            var items = new JsonArray();
            foreach (var item in rawItems ?? []) items.Add(new JsonObject { ["id"] = item?["sceneItemId"]?.DeepClone(),
                ["name"] = item?["sourceName"]?.DeepClone(), ["enabled"] = item?["sceneItemEnabled"]?.DeepClone() });
            var transitions = await Optional("GetSceneTransitionList");
            var transition = await Optional("GetCurrentSceneTransition");
            var replay = await Optional("GetReplayBufferStatus");
            var camera = await Optional("GetVirtualCamStatus");
            if (!_snap.Connected || !ReferenceEquals(_ws, session) || session.State != System.Net.WebSockets.WebSocketState.Open || !config.Get().Obs.Enabled)
                return DisconnectedDeck();
            JsonObject? built = new JsonObject
            {
                ["connected"] = true, ["scene"] = scene, ["scenes"] = new JsonArray(_snap.Scenes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["inputs"] = inputs, ["sources"] = items, ["streaming"] = _snap.Streaming, ["recording"] = _snap.Recording,
                ["recordPaused"] = _snap.RecordPaused, ["streamMs"] = _snap.StreamMs, ["recordMs"] = _snap.RecordMs,
                ["transitions"] = transitions?["transitions"]?.DeepClone() ?? new JsonArray(),
                ["transition"] = transition?["transitionName"]?.DeepClone(), ["duration"] = transition?["transitionDuration"]?.DeepClone(),
                ["replayAvailable"] = replay is not null, ["replayActive"] = replay?["outputActive"]?.DeepClone(),
                ["cameraAvailable"] = camera is not null, ["cameraActive"] = camera?["outputActive"]?.DeepClone(),
            };
            lock (_sessionGate)
            {
                // The session may have been retired since the check above; then its cleanup already ran and this
                // deck must neither be cached nor reported as connected.
                if (!_snap.Connected || !ReferenceEquals(_ws, session)) built = null;
                else
                {
                    _deck = new(built, session);
                    Interlocked.Exchange(ref _deckAt, Environment.TickCount64);
                }
            }
            return built ?? DisconnectedDeck();
        }
        finally { _deckGate.Release(); }
    }

    /// <summary>
    /// Stops the OBS virtual camera for the Control Deck's "Mics off, camera off" (#387). A privacy action, so it does
    /// not need the OBS widget's control option; it never starts a stopped camera (StopVirtualCam, not
    /// ToggleVirtualCam) and reports plainly when the camera could not be turned off.
    /// </summary>
    public async Task<VirtualCameraStop> StopVirtualCamAsync(CancellationToken ct)
    {
        var session = _ws;
        if (!DeckUsable() || session is null) return VirtualCameraStop.NotConnected;
        bool? active;
        try { active = (await Request("GetVirtualCamStatus", null, ct))?["outputActive"]?.GetValue<bool>(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return CameraFailure(ex, session); }
        // OBS refuses StopVirtualCam for a camera that is not running, which would read as a failure
        if (active == false) return VirtualCameraStop.AlreadyOff;
        try
        {
            await Request("StopVirtualCam", null, ct);
            Interlocked.Exchange(ref _deckAt, 0);
            log.LogInformation("obs: virtual camera stopped");
            return VirtualCameraStop.Stopped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return CameraFailure(ex, session); }
    }

    private VirtualCameraStop CameraFailure(Exception ex, System.Net.WebSockets.ClientWebSocket session)
    {
        log.LogInformation("obs: the virtual camera could not be stopped: {Msg}", ex.Message);
        // the session ended under the request: OBS went away, it did not refuse
        return ex is IOException || !_snap.Connected || !ReferenceEquals(_ws, session) ? VirtualCameraStop.NotConnected : VirtualCameraStop.Failed;
    }

    private bool DeckControlAllowed() =>
        config.Get().Widgets(enabledPagesOnly: true).Any(w => (w.Type == "obs" || w.Type.StartsWith("obs-")) && w.Bool("control") == true);

    public async Task<DeckResult> DeckAct(string action, string? id, int? value, bool? enabled, CancellationToken ct)
    {
        const string controlOff = "Enable OBS control in the widget's desktop settings";
        if (!DeckControlAllowed()) return new(false, controlOff);
        if (!_snap.Connected) return new(false, "OBS is not connected");
        try
        {
            var state = await DeckSnapshot(ct);
            // discovery awaited OBS: the widget may have been hidden or its control turned off meanwhile
            if (!DeckControlAllowed()) return new(false, controlOff);
            var inputs = state["inputs"] as JsonArray;
            switch (action)
            {
                case "volume" when value is >= 0 and <= 100 && inputs?.Any(i => i?["id"]?.GetValue<string>() == id && i?["volume"] is not null) == true:
                    await Request("SetInputVolume", new() { ["inputName"] = id, ["inputVolumeMul"] = value.Value / 100d }, ct); break;
                case "mute" when enabled is not null && inputs?.Any(i => i?["id"]?.GetValue<string>() == id && i?["muted"] is not null) == true:
                    await Request("SetInputMute", new() { ["inputName"] = id, ["inputMuted"] = enabled.Value }, ct); break;
                case "source" when enabled is not null && int.TryParse(id, out var itemId)
                    && (state["sources"] as JsonArray)?.Any(s => s?["id"]?.GetValue<int>() == itemId) == true:
                    await Request("SetSceneItemEnabled", new() { ["sceneName"] = state["scene"]?.DeepClone(), ["sceneItemId"] = itemId, ["sceneItemEnabled"] = enabled.Value }, ct); break;
                case "scene" when id is not null && _snap.Scenes.Contains(id):
                    await Request("SetCurrentProgramScene", new() { ["sceneName"] = id }, ct); break;
                case "transition" when (state["transitions"] as JsonArray)?.Any(t => t?["transitionName"]?.GetValue<string>() == id) == true:
                    await Request("SetCurrentSceneTransition", new() { ["transitionName"] = id }, ct); break;
                case "duration" when value is >= 50 and <= 10000:
                    await Request("SetCurrentSceneTransitionDuration", new() { ["transitionDuration"] = value.Value }, ct); break;
                case "record": await Request("ToggleRecord", null, ct); break;
                case "pause": await Request("ToggleRecordPause", null, ct); break;
                case "stream": await Request("ToggleStream", null, ct); break;
                case "replay" when state["replayAvailable"]?.GetValue<bool>() == true: await Request("ToggleReplayBuffer", null, ct); break;
                case "save-replay" when state["replayActive"]?.GetValue<bool>() == true: await Request("SaveReplayBuffer", null, ct); break;
                case "camera" when state["cameraAvailable"]?.GetValue<bool>() == true: await Request("ToggleVirtualCam", null, ct); break;
                default: return new(false, "This OBS control is unavailable");
            }
            _deckAt = 0; return new(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex, "OBS Control Deck action failed"); return new(false, "OBS refused this control; check its settings"); }
    }
}
