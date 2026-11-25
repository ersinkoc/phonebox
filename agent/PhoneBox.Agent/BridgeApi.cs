using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace PhoneBox.Agent;

/// <summary>
/// What the phone bridge's endpoints decide, kept out of the Program.cs lambdas so tests can reach it. All text here is
/// inert data from the phone: it is stored on the clipboard and relayed, never run, opened or interpreted.
/// </summary>
internal static class BridgeApi
{
    /// <summary>The largest request the clipboard endpoint reads (the text limit is lower; JSON escapes make the body bigger).</summary>
    public const int MaxRequestBytes = 512 * 1024; // 64 KiB of text can grow six times once JSON escapes it (each character as a six-character escape)
    /// <summary>The largest notification request: a title and text are cut to 2.2 KB anyway (up to about 13 KB once JSON-escaped), and the app icon is at most about 43 KB of base64.</summary>
    public const int MaxNotificationBytes = 64 * 1024;
    public const string RequestTooLarge = "request too large";
    /// <summary>An SSE comment, sent now and then so a dead connection is noticed.</summary>
    public const string PingFrame = ": ping\n\n";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// POST /api/bridge/clipboard and GET /api/bridge/stream. <see cref="RequestGate"/> already refuses them on the local port;
    /// each endpoint asks it again (a middleware moved in front of the gate must not open them), and the stream keeps asking.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, RequestGate gate)
    {
        app.MapPost("/api/bridge/clipboard", (HttpContext ctx, ConfigStore cfg) =>
            PostClipboardAsync(ctx, ctx.RequestServices.GetService<ClipboardBridge>(), cfg, () => Authorized(gate, ctx)));
        app.MapPost("/api/bridge/notification", (HttpContext ctx, ConfigStore _) =>
            PostNotificationAsync(ctx, ctx.RequestServices.GetService<NotificationBridge>(), () => Authorized(gate, ctx)));
        // the Settings page's test button; not on the Wi-Fi allow-list (Lan.cs), so only this computer can ask for it
        app.MapPost("/api/bridge/notification/test", (HttpContext ctx) =>
            PostTestNotification(ctx.RequestServices.GetService<NotificationBridge>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        app.MapGet("/api/bridge/stream", async (HttpContext ctx, ConfigStore cfg, BridgeStream bridge, IHostApplicationLifetime life) =>
        {
            // what the request came with, so the open stream can be checked again with exactly that (see StreamAsync)
            var (port, https, method, path, origin, key) = (ctx.Connection.LocalPort, ctx.Request.IsHttps, ctx.Request.Method,
                ctx.Request.Path, ctx.Request.Headers.Origin.ToString(), ctx.Request.Headers.Authorization.ToString());
            bool StillAuthorized() => gate.PhoneAllowed(port, https, method, path, origin, key);
            if (!StillAuthorized()) return Results.Json(new { error = "not allowed here" }, statusCode: 403);
            if (ctx.RequestServices.GetService<ClipboardBridge>() is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
            if (!StreamAllowed(cfg)) return Results.Json(new { error = "off" }, statusCode: 403);
            await StreamAsync(ctx, cfg, bridge, StillAuthorized, life.ApplicationStopping);
            return Results.Empty;
        });
    }

    public static async Task<IResult> PostNotificationAsync(HttpContext ctx, NotificationBridge? bridge, Func<bool> authorized)
    {
        if (!authorized()) return Results.Json(new { error = "not allowed here" }, statusCode: 403);
        if (NotificationGate(bridge) is { } refused) return refused; // off or not macOS: refused before the body is read
        var (notification, error) = await ReadNotificationBody(ctx.Request);
        if (error is not null) return BodyError(error);
        if (!authorized()) return Results.Json(new { error = "not allowed here" }, statusCode: 403);
        return PostNotification(bridge, notification, DeviceId(ctx.Request.Headers["X-PhoneBox-Device"]));
    }

    private static bool Authorized(RequestGate gate, HttpContext ctx) =>
        gate.PhoneAllowed(ctx.Connection.LocalPort, ctx.Request.IsHttps, ctx.Request.Method, ctx.Request.Path,
            ctx.Request.Headers.Origin.ToString(), ctx.Request.Headers.Authorization.ToString());

    /// <summary>
    /// The clipboard endpoint, in the order that costs least: who is asking, then the platform (501) and the switch (403),
    /// and only then is the body read and parsed. A phone that keeps sending while sharing is off costs no buffer.
    /// </summary>
    public static async Task<IResult> PostClipboardAsync(HttpContext ctx, ClipboardBridge? bridge, ConfigStore cfg, Func<bool> authorized)
    {
        if (!authorized()) return Results.Json(new { error = "not allowed here" }, statusCode: 403);
        if (bridge is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
        if (!cfg.Get().Bridge.Clipboard) return Results.Json(new { error = "off" }, statusCode: 403);
        var (text, error) = await ReadClipboardBody(ctx.Request);
        if (error is not null) return BodyError(error);
        if (!authorized()) return Results.Json(new { error = "not allowed here" }, statusCode: 403);
        return PostClipboard(bridge, text, DeviceId(ctx.Request.Headers["X-PhoneBox-Device"]));
    }

    public static IResult PostClipboard(ClipboardBridge? bridge, string? text, string? deviceId)
    {
        if (bridge is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
        if (string.IsNullOrEmpty(text)) return Results.Json(new { error = "give a text" }, statusCode: 400);
        return bridge.ReceiveFromPhone(text, deviceId) switch
        {
            ClipboardResult.Stored => Results.Ok(new { ok = true }),
            ClipboardResult.Off => Results.Json(new { error = "off" }, statusCode: 403),
            ClipboardResult.TooLarge => Results.Json(new { error = "too large" }, statusCode: 413),
            _ => Results.Json(new { error = "could not write the clipboard" }, statusCode: 500),
        };
    }

    /// <summary>Reads at most <paramref name="max"/> bytes of JSON, also for chunked requests that state no length, and extracts <typeparamref name="T"/> before clearing the pooled buffer.</summary>
    private static async Task<(T? Value, string? Error)> ReadJson<T>(HttpRequest req, int max, Func<JsonElement, T?> extract)
    {
        if (req.ContentLength > max) return (default, RequestTooLarge);
        // Sized from Content-Length when the phone sends one (Kestrel ends the body there); a chunked request, which has none,
        // gets room for the limit and one byte more, so a body over it is noticed. Pooled, and cleared on return: it held the text.
        var room = (int)(req.ContentLength ?? max) + 1;
        var buf = ArrayPool<byte>.Shared.Rent(room); // may be larger than asked: never read past room
        try
        {
            var n = 0;
            while (n < room)
            {
                var count = await req.Body.ReadAsync(buf.AsMemory(n, room - n), req.HttpContext.RequestAborted);
                if (count == 0) break;
                n += count;
            }
            if (n > max) return (default, RequestTooLarge);
            try
            {
                using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
                return (extract(doc.RootElement), null);
            }
            catch (JsonException) { return (default, "invalid JSON"); }
            // JSON whose text is not valid Unicode (a lone surrogate escape such as "\ud800", or bytes that are not UTF-8): GetString
            // throws this for it, and uncaught it would become a 500 instead of a request error
            catch (InvalidOperationException) { return (default, "invalid text"); }
        }
        finally { ArrayPool<byte>.Shared.Return(buf, clearArray: true); }
    }

    /// <summary>Reads <c>{"text": "..."}</c>. A body without a usable text gives (null, null); the caller answers 400.</summary>
    public static Task<(string? Text, string? Error)> ReadClipboardBody(HttpRequest req) =>
        ReadJson<string>(req, MaxRequestBytes, root =>
            root is { ValueKind: JsonValueKind.Object }
            && root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null);

    /// <summary>
    /// Reads a phone notification: <c>{"key","app","package","title","text","category","time"}</c> and an optional <c>"icon"</c> (base64 of a small PNG, see <see cref="NotificationIcon"/>). A body that is not one (no
    /// text key, or neither a title nor a text) gives (null, null); the caller answers 400.
    /// </summary>
    public static Task<(PhoneNotification? Notification, string? Error)> ReadNotificationBody(HttpRequest req) =>
        ReadJson<PhoneNotification>(req, MaxNotificationBytes, root =>
        {
            if (root is not { ValueKind: JsonValueKind.Object }) return null;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var key = Str("key");
            var title = Str("title") ?? "";
            var text = Str("text") ?? "";
            if (string.IsNullOrEmpty(key) || (title.Length == 0 && text.Length == 0)) return null;
            var time = root.TryGetProperty("time", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.TryGetInt64(out var t) ? t : 0;
            var category = Str("category") is { Length: > 0 and <= 32 } c ? c : "message";
            return new PhoneNotification(key.Length > 128 ? key[..128] : key, Str("app") ?? "", Str("package") ?? "", title, text, category, time, NotificationIcon.TryDecode(Str("icon")));
        });

    public static IResult PostNotification(NotificationBridge? bridge, PhoneNotification? notification, string? deviceId)
    {
        if (bridge is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
        if (notification is null) return Results.Json(new { error = "give a key and a title or a text" }, statusCode: 400);
        return bridge.Receive(notification, deviceId) switch
        {
            NotifyResult.Queued or NotifyResult.Duplicate => Results.Ok(new { ok = true }),
            NotifyResult.Off => Results.Json(new { error = "off" }, statusCode: 403),
            _ => Results.Json(new { error = "could not show the notification" }, statusCode: 500),
        };
    }

    /// <summary>Refuses before a body is read: 501 without a bridge, 403 while the switch is off; null when the request may go on.</summary>
    public static IResult? NotificationGate(NotificationBridge? bridge) =>
        bridge is null ? Results.Json(new { error = "not supported on this system" }, statusCode: 501)
        : !bridge.Enabled ? Results.Json(new { error = "off" }, statusCode: 403)
        : null;

    /// <summary>The Settings page's "Send a test notification" button (local only, see Lan.cs): a notification through the same switch.</summary>
    public static IResult PostTestNotification(NotificationBridge? bridge, long now)
    {
        if (bridge is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
        if (!bridge.Enabled) return Results.Json(new { error = "Show phone notifications is off: turn it on and save first" }, statusCode: 403);
        return PostNotification(bridge, new PhoneNotification("test-" + now, "PhoneBox", "net.oxog.phonebox", "PhoneBox test",
            "If you can read this, the phone bridge works on this computer.", "message", now), "test");
    }

    public static IResult BodyError(string error) =>
        Results.Json(new { error }, statusCode: error == RequestTooLarge ? 413 : 400);

    /// <summary>The device id header, only when it looks like the ids phones send (letters and digits, at most 32).</summary>
    public static string? DeviceId(string? header) =>
        !string.IsNullOrWhiteSpace(header) && header.Length <= 32 && header.All(char.IsAsciiLetterOrDigit) ? header : null;

    /// <summary>
    /// The Last-Event-ID header of a reconnecting stream: the <c>id:</c> of the last event the phone received, a plain decimal
    /// number. Anything else (no header, two headers, a sign, spaces, letters, a number too large) counts as no header.
    /// </summary>
    public static long? LastEventId(StringValues header) =>
        header.Count == 1 && header[0] is { Length: > 0 and <= 19 } value
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The bridge stream follows the clipboard switch: when it is off a new connection is refused and an open one ends.</summary>
    public static bool StreamAllowed(ConfigStore config) => config.Get().Bridge.Clipboard;

    /// <summary>How often an open stream looks at the switch and the phone's key on its own, apart from every pass of its loop.</summary>
    private const int SwitchCheckMs = 500;
    private const int PingMs = 15_000;

    /// <summary>
    /// GET /api/bridge/stream once the endpoint has checked the phone and the switch: the events other devices caused, from now
    /// on (or, with Last-Event-ID, the recent ones the phone missed while it reconnected), until the phone goes away, the agent
    /// begins to stop, the switch is turned off, or <paramref name="authorized"/> stops holding (the phone's key was revoked).
    /// </summary>
    public static async Task StreamAsync(HttpContext ctx, ConfigStore cfg, BridgeStream bridge, Func<bool> authorized, CancellationToken stopping)
    {
        // The agent stopping ends it too: the server otherwise waits up to 30 s for this never-ending response before a restart,
        // an update or Quit can finish (as /api/stream does). The switch and the key cancel it as well, so a write that is stuck
        // on a phone that stopped reading ends when sharing is turned off instead of when the connection finally times out.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var ct = cts.Token;
        bool Open() => StreamAllowed(cfg) && authorized();
        using var watch = new System.Threading.Timer(_ =>
        {
            // a timer callback that throws ends the process, so nothing may escape
            try { if (!Open()) cts.Cancel(); }
            catch (Exception) { /* disposed after the stream ended, or the config could not be read: the loop checks again */ }
        }, null, SwitchCheckMs, SwitchCheckMs);
        var device = DeviceId(ctx.Request.Headers["X-PhoneBox-Device"]);
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        using var wake = new SemaphoreSlim(0);
        void OnEmit() { try { wake.Release(); } catch (ObjectDisposedException) { } }
        bridge.Emitted += OnEmit;
        try
        {
            var (seq, replay) = bridge.Resume(LastEventId(ctx.Request.Headers["Last-Event-ID"]), device);
            var nextPing = Environment.TickCount64 + PingMs;
            await ctx.Response.WriteAsync(PingFrame, ct); // lets the phone see the connection is up
            foreach (var e in replay)
            {
                if (!Open()) return;
                if (bridge.StillQueued(e.Seq)) await ctx.Response.WriteAsync(EventFrame(e), ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
            // the switch and the key are checked every pass (at least once a second), so either one ends the stream
            while (!ct.IsCancellationRequested && Open())
            {
                foreach (var e in bridge.Since(seq, device))
                {
                    if (!Open()) return; // the switch was turned off (or the key revoked) during this batch: nothing more is written
                    if (bridge.StillQueued(e.Seq)) await ctx.Response.WriteAsync(EventFrame(e), ct);
                    seq = e.Seq;
                }
                if (Environment.TickCount64 >= nextPing)
                {
                    await ctx.Response.WriteAsync(PingFrame, ct);
                    nextPing = Environment.TickCount64 + PingMs;
                }
                await ctx.Response.Body.FlushAsync(ct);
                await wake.WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the phone went away in the middle of a write
        finally { bridge.Emitted -= OnEmit; }
    }

    /// <summary>
    /// One server-sent event: <c>id: 7\nevent: clipboard\ndata: {"seq":7,"text":"..."}\n\n</c>. The id is the event's number,
    /// which the phone sends back as Last-Event-ID when it reconnects. The text travels as a JSON string, so a newline inside
    /// it cannot end the frame.
    /// </summary>
    public static string EventFrame(BridgeEvent e) =>
        $"id: {e.Seq.ToString(CultureInfo.InvariantCulture)}\nevent: {e.Name}\ndata: {JsonSerializer.Serialize(new { seq = e.Seq, text = e.Text }, Web)}\n\n";
}
