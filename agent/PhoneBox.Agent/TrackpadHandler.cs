using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public static class TrackpadHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int MaxMovementDelta = 500;
    private const int MaxScrollDelta = 1000;

    /// <summary>The multi-finger gestures the phone sends: <c>swipe3up</c>, <c>swipe4left</c>, <c>spread</c>, <c>pinch</c>.</summary>
    internal static readonly Regex GestureName = new("^(?:swipe(?<n>[34])(?<dir>up|down|left|right)|(?<pinch>spread|pinch))$", RegexOptions.Compiled);

    public static bool IsAllowed(AgentConfig config) =>
        config.Widgets(enabledPagesOnly: true).Any(w => w.Type == "trackpad" && w.Bool("control") == true);

    private static volatile bool _executionErrorLogged;

    /// <summary>The first failure to act on the phone's input (macOS refusing without the Accessibility permission, say) is logged; the phone gets no answer.</summary>
    private static void ReportFailure(ILogger? log, Exception ex)
    {
        if (_executionErrorLogged) return;
        _executionErrorLogged = true;
        log?.LogWarning(ex, "Trackpad execution failed");
    }

    public static void ProcessPayload(ReadOnlySpan<byte> utf8, ConfigStore config, ILogger? log = null)
    {
        var snapshot = config.Get();
        if (!IsAllowed(snapshot))
        {
            log?.LogDebug("Trackpad payload ignored: widget not enabled with control=true");
            return;
        }

        try
        {
            var msg = JsonSerializer.Deserialize<TrackpadPayload>(utf8, JsonOpts);
            if (msg is not null)
            {
                try
                {
                    Execute(msg, ex => ReportFailure(log, ex));
                }
                catch (Exception ex)
                {
                    ReportFailure(log, ex);
                }
            }
        }
        catch (JsonException ex)
        {
            log?.LogDebug(ex, "Failed to parse trackpad payload");
        }
    }

    public static void Execute(TrackpadPayload msg, Action<Exception>? onFailure = null)
    {
        var action = (msg.Type ?? msg.Action)?.ToLowerInvariant();
        switch (action)
        {
            case "move":
            {
                var dxVal = msg.Dx;
                var dyVal = msg.Dy;
                var dx = dxVal is not null && double.IsFinite(dxVal.Value)
                    ? Math.Clamp((int)Math.Round(dxVal.Value), -MaxMovementDelta, MaxMovementDelta) : 0;
                var dy = dyVal is not null && double.IsFinite(dyVal.Value)
                    ? Math.Clamp((int)Math.Round(dyVal.Value), -MaxMovementDelta, MaxMovementDelta) : 0;
                if (dx != 0 || dy != 0) KeySender.MouseMove(dx, dy);
                break;
            }
            case "click":
                _leftButtonDown = false;
                KeySender.MouseClick();
                break;
            case "mousedown" or "down":
                _leftButtonDown = true;
                KeySender.MouseDown();
                break;
            case "mouseup" or "up":
                _leftButtonDown = false;
                KeySender.MouseUp();
                break;
            case "rightclick":
                KeySender.MouseRightClick();
                break;
            case "scroll":
            {
                var deltaVal = msg.Delta ?? msg.Dy;
                var delta = deltaVal is not null && double.IsFinite(deltaVal.Value)
                    ? Math.Clamp((int)Math.Round(deltaVal.Value), -MaxScrollDelta, MaxScrollDelta) : 0;
                var scrollDx = msg.Dx;
                var hDelta = scrollDx is not null && double.IsFinite(scrollDx.Value)
                    ? Math.Clamp((int)Math.Round(scrollDx.Value), -MaxScrollDelta, MaxScrollDelta) : 0;
                if (hDelta != 0) KeySender.MouseHorizontalScroll(hDelta);
                if (delta != 0) KeySender.MouseScroll(delta);
                break;
            }
            case "hscroll":
            {
                var hVal = msg.Delta ?? msg.Dx;
                var hDelta = hVal is not null && double.IsFinite(hVal.Value)
                    ? Math.Clamp((int)Math.Round(hVal.Value), -MaxScrollDelta, MaxScrollDelta) : 0;
                if (hDelta != 0) KeySender.MouseHorizontalScroll(hDelta);
                break;
            }
            default:
                // swipe3up, swipe4left, spread, pinch...: macOS only; anything else is ignored
                if (action is not null && GestureName.IsMatch(action)) KeySender.MouseGesture(action, onFailure);
                break;
        }
    }

    private static volatile bool _leftButtonDown;

    public static async Task HandleWebSocketAsync(WebSocket ws, ConfigStore config, ILogger? log, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.State == WebSocketState.CloseReceived)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    try
                    {
                        ProcessPayload(buffer.AsSpan(0, result.Count), config, log);
                    }
                    catch (Exception ex)
                    {
                        log?.LogDebug(ex, "Trackpad payload processing error");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            log?.LogDebug(ex, "Trackpad WebSocket connection closed");
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Trackpad WebSocket error");
        }
        finally
        {
            if (_leftButtonDown)
            {
                _leftButtonDown = false;
                try { KeySender.MouseUp(); } catch { }
            }
        }
    }

    public static async Task HandleEndpoint(HttpContext ctx)
    {
        var config = ctx.RequestServices?.GetService<ConfigStore>();
        var log = ctx.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("PhoneBox.Agent.Trackpad");

        // Fail closed: ConfigStore is required and must have the widget enabled with control=true
        if (config is null || !IsAllowed(config.Get()))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (ctx.WebSockets.IsWebSocketRequest)
        {
            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketAsync(ws, config, log, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                log?.LogDebug(ex, "Trackpad WebSocket accept failed");
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "Trackpad endpoint error");
            }
            return;
        }

        // Side effects require POST with a JSON body; GET or other methods do nothing
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            if (ctx.Request.ContentLength is > 0)
            {
                using var mem = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(mem, ctx.RequestAborted);
                ProcessPayload(mem.ToArray(), config, log);
            }
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }
}

public record TrackpadPayload(string? Type, string? Action, double? Dx, double? Dy, double? Delta);
