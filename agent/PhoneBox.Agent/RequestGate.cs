namespace PhoneBox.Agent;

/// <summary>
/// Who may reach what, decided by the port a request came in on. It lives here rather than inline in Program.cs so tests
/// can put it in front of real endpoints on real (free) ports, and so a long-lived phone stream can ask it again later.
/// <list type="bullet">
/// <item>Local port (127.0.0.1 only): the Host check blocks DNS rebinding; from browsers only the settings page itself (same
/// origin) is accepted (no CSRF). The phone bridge's paths are refused here: nothing on this computer needs them, and any
/// local process (another user's, a sandboxed app with network access) could otherwise read every copy.</item>
/// <item>The phone's USB tunnel ends on a port of its own (UsbLink): every app on the phone can reach the tunnel, so it is
/// TLS with the certificate the app pins (handed over adb), needs the key the PhoneBox app made (read over adb), and serves
/// only the phone API.</item>
/// <item>The optional Wi-Fi port only serves the phone API, only over TLS, and only with the paired token.</item>
/// </list>
/// </summary>
/// <param name="lanPort">The Wi-Fi listener's port, or null when the agent did not open one.</param>
/// <param name="spotifyCallback">True for the browser's return from the Spotify sign-in, which a foreign site starts.</param>
internal sealed class RequestGate(int localPort, string[] sameOrigins, UsbLink usb, int? lanPort, ConfigStore config,
    Func<HttpContext, bool> spotifyCallback, ILogger log)
{
    /// <summary>The phone bridge's namespace: reached only through the USB tunnel or the Wi-Fi port, with the phone's key.</summary>
    public static bool IsBridgePath(PathString path) => path.StartsWithSegments("/api/bridge");

    /// <summary>
    /// True while a request on this port with this key may reach a phone path: the USB key check or the Wi-Fi check, the
    /// same ones <see cref="InvokeAsync"/> makes. A stream that stays open asks again with what its request carried, so a
    /// key that is revoked (a new Wi-Fi key, Wi-Fi turned off, a phone unplugged) ends it. Never true for the local port.
    /// </summary>
    public bool PhoneAllowed(int port, bool isHttps, string method, PathString path, string origin, string? authorization)
    {
        if (!isHttps) return false;
        if (port == usb.Port) return usb.Authorize(method, path, origin, authorization) == UsbLink.Access.Allowed;
        if (lanPort is { } wifi && port == wifi)
            return Lan.AuthorizeRequest(config.Get().Lan, method, path, origin, authorization, null) == Lan.LanAccess.Phone;
        return false;
    }

    public async Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (ctx.Connection.LocalPort == usb.Port && ctx.Request.IsHttps)
        {
            // the phone over USB: only with its app's key, and only the paths the phone uses
            var access = usb.Authorize(ctx.Request.Method, ctx.Request.Path, origin, ctx.Request.Headers.Authorization);
            if (access != UsbLink.Access.Allowed)
            {
                ctx.Response.StatusCode = access == UsbLink.Access.Unauthorized ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (lanPort is { } wifi && ctx.Connection.LocalPort == wifi && ctx.Request.IsHttps)
        {
            // The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
            // or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).
            var lan = config.Get().Lan;
            var access = Lan.AuthorizeRequest(lan, ctx.Request.Method, ctx.Request.Path, origin,
                ctx.Request.Headers.Authorization, ctx.Request.Query["token"]);
            if (access == Lan.LanAccess.Denied)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (access is Lan.LanAccess.PairingClaim or Lan.LanAccess.Webhook)
            {
                // pairing claims and webhooks must not touch phone registry, app version or "phone connected" updates
                if (access == Lan.LanAccess.PairingClaim)
                    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1024;
                ctx.Response.Headers.CacheControl = "no-store";
                await next();
                return;
            }
        }
        else
        {
            var host = ctx.Request.Host.Host;
            if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !sameOrigins.Contains(origin))
                || IsBridgePath(ctx.Request.Path)
                || (!spotifyCallback(ctx) && BrowserGuard.IsForeign(ctx.Request.Headers["Sec-Fetch-Site"], ctx.Request.Headers["Sec-Fetch-Mode"], ctx.Request.Method, ctx.Request.Path)))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        // any request from the app shows the phone is there, not only a state poll: an app on the live stream polls
        // nothing, and a stream that is not yet (or no longer) open must not hide the phone from a permission prompt (#497)
        if (ctx.Request.Headers.ContainsKey("X-PhoneBox-App"))
            Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (ctx.Request.Headers.TryGetValue("X-PhoneBox-Device", out var device))
        {
            var problem = PhoneRegistry.Touch(device.ToString(), ctx.Request.Headers["X-PhoneBox-Model"].ToString(),
                ctx.Connection.LocalPort == usb.Port ? "USB" : ctx.Connection.LocalPort == localPort ? "local" : "Wi-Fi", ctx.Request.Headers["X-PhoneBox-WebView"].ToString());
            if (problem is not null) log.LogWarning("WebView: {Problem}", problem);
        }
        if (ctx.Request.Headers.TryGetValue("X-PhoneBox-App", out var appVersion) && appVersion.ToString().Length is > 0 and < 32)
            UpdateService.AppVersion = appVersion.ToString();
        ctx.Response.Headers.CacheControl = "no-store";
        // only the panel may be framed, and only by the settings page itself (the live preview)
        ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/panel") ? "SAMEORIGIN" : "DENY";
        await next();
    }
}
