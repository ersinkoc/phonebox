using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Web kiosk: the phone's panel in a browser, for devices without the phone app (a tablet, a spare monitor, a Raspberry Pi
/// screen, Fully Kiosk Browser). /kiosk/?token=… serves exactly the pages the phone shows; the page then talks to the same
/// API as the phone (/agent/api/…), authenticated by a cookie set from the token. Browsers too old for the panel (Android
/// 4.4, old iPads) are sent to /kiosk/lite, a small ES5 page with keys, now playing, PC stats, pushed values and
/// notifications. The kiosk has its own key (URLs end up in logs and bookmarks, so it is never the phone's key).
/// </summary>
public static class Kiosk
{
    public const string CookieName = "phonebox_kiosk";

    /// <summary>Panel events the lite page acts on; the others (page switches, rule cards…) need the full panel.</summary>
    private static readonly HashSet<string> Events = ["notify", "reload"];

    /// <summary>Read-only images the panel loads with &lt;img&gt;, which cannot carry the panel header (like the phone's proxy).</summary>
    private static readonly HashSet<string> Images = ["/api/art", "/api/procs/icon"];

    public static bool IsKioskPath(PathString path) => path.StartsWithSegments("/kiosk");

    /// <summary>The kiosk key in ?token=, an Authorization: Bearer header or the kiosk cookie, compared in constant time.</summary>
    public static bool Authorized(string? authorization, string? queryToken, KioskConfig kiosk, string? cookie = null) =>
        kiosk.Enabled && !string.IsNullOrEmpty(kiosk.Token)
        && (Lan.Matches(authorization, kiosk.Token) || Lan.MatchesQuery(queryToken, kiosk.Token) || Lan.MatchesQuery(cookie, kiosk.Token));

    /// <summary>Same-origin requests only: fetch POSTs from the page itself send Origin, other sites are refused.</summary>
    public static bool OriginAllowed(string origin, string scheme, string host) =>
        origin == "" || string.Equals(origin, $"{scheme}://{host}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the panel in a kiosk browser may call: the phone's API (path already rewritten from /agent/api), and only from
    /// the panel's own scripts, which send X-PhoneBox-Panel (a sandboxed Custom HTML widget cannot add it without a
    /// preflight), except the read-only images.
    /// </summary>
    public static bool PanelApiAllowed(PathString path, string method, bool panelHeader) =>
        Lan.AllowedOverLan(path) && (panelHeader || (HttpMethods.IsGet(method) && Images.Contains(path.Value ?? "")));

    /// <summary>
    /// Sent first in the panel's &lt;head&gt;: browsers without ES modules, optional chaining or modern CSS go to the lite page.
    /// ES5 on purpose, so that old browsers can run it. ?full=1 skips the check.
    /// </summary>
    public const string LegacyCheck = """
        <script>(function(){if(/[?&]full=1/.test(location.search))return;var ok=false;try{ok='noModule' in HTMLScriptElement.prototype&&!!(window.CSS&&CSS.supports&&CSS.supports('aspect-ratio','1')&&CSS.supports('color','var(--x)'))&&!!new Function('a','return a?.b??1');}catch(e){ok=false;}if(!ok)location.replace('lite'+location.search);})();</script>
        """;

    /// <summary>A file of the phone's panel (embedded at build time): index.html, main.js, style.css… and widgets/*.js.</summary>
    public static IResult PanelFile(string? file)
    {
        file = string.IsNullOrEmpty(file) ? "index.html" : file;
        if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^(widgets/)?[A-Za-z0-9_.-]+$") || file.Contains("..")) return Results.NotFound();
        var type = Path.GetExtension(file) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            _ => "application/octet-stream",
        };
        return AppPaths.Resource("www/" + file) is { } s ? Results.Stream(s, type) : Results.NotFound();
    }

    /// <summary>The panel's index.html with the legacy-browser check in front of everything else.</summary>
    public static string PanelIndex()
    {
        using var s = AppPaths.Resource("www/index.html")!;
        var html = new StreamReader(s).ReadToEnd();
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? LegacyCheck + html : html.Insert(head + "<head>".Length, "\n" + LegacyCheck);
    }

    /// <summary>Remembers the key for the panel's own requests: HttpOnly, and SameSite=Strict so other sites never send it.</summary>
    public static void SetCookie(HttpResponse response, string token, bool https) =>
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = https,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(400),
        });

    /// <summary>What the kiosk shows. Shortcut commands, arguments and other secrets never leave this computer.</summary>
    public static object State(IServiceProvider sp, long? since)
    {
        var cfg = sp.GetRequiredService<ConfigStore>().Get();
        var push = sp.GetRequiredService<PushStore>();
        var audio = sp.GetRequiredService<AudioControl>();
        return new
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            host = Environment.MachineName,
            evSeq = push.Seq,
            events = since is { } s ? push.Since(s).Where(e => Events.Contains(e.Kind)) : [],
            pc = sp.GetRequiredService<SystemMonitor>().Snapshot(),
            media = sp.GetRequiredService<MediaMonitor>().Snapshot(),
            audio = audio.Snapshot(),
            shortcuts = cfg.Shortcuts.Select(sc => new { sc.Id, sc.Label, sc.Icon, sc.Color, toggle = sc.Toggle || sc.Cmd == "builtin:mute" }),
            toggles = Shortcuts.States(cfg, push, audio),
            pushVersion = push.Version,
            push = push.Snapshot().Select(kv => new { key = kv.Key, data = kv.Value.Data, at = kv.Value.At }),
        };
    }

    public static void MapKiosk(this WebApplication app)
    {
        // The phone's pages, unchanged. Relative URLs (style.css, main.js, widgets/…) need the trailing slash.
        app.MapGet("/kiosk/lite", () =>
        {
            using var s = AppPaths.Resource("kiosk.html");
            if (s is null) return Results.NotFound();
            using var r = new StreamReader(s);
            return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
        });
        // The phone's pages, unchanged. Relative URLs (style.css, main.js, widgets/…) need the trailing slash.
        app.MapGet("/kiosk/{*file}", (string? file, HttpRequest req) =>
        {
            if (string.IsNullOrEmpty(file) && !req.Path.Value!.EndsWith('/')) return Results.Redirect("/kiosk/" + req.QueryString);
            return string.IsNullOrEmpty(file) || file == "index.html" ? Results.Content(PanelIndex(), "text/html; charset=utf-8") : PanelFile(file);
        });

        app.MapGet("/kiosk/api/state", (long? ev, HttpContext ctx) => Results.Json(State(ctx.RequestServices, ev)));

        // Server-sent events: "state" every 2 s, "event" as soon as a notification arrives. A comment line keeps idle
        // connections alive through proxies; the page reconnects by itself when the connection drops.
        app.MapGet("/kiosk/api/stream", async (HttpContext ctx, PushStore push) =>
        {
            var ct = ctx.RequestAborted;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            async Task Write(string text)
            {
                await ctx.Response.WriteAsync(text, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            Task Send(string name, object data) => Write($"event: {name}\ndata: {JsonSerializer.Serialize(data, opts)}\n\n");
            using var wake = new SemaphoreSlim(0);
            void OnEmit() { try { wake.Release(); } catch (ObjectDisposedException) { } }
            push.Emitted += OnEmit;
            try
            {
                await Write("retry: 3000\n\n");
                var seq = push.Seq;
                var next = 0L;
                while (!ct.IsCancellationRequested)
                {
                    foreach (var e in push.Since(seq))
                    {
                        seq = e.Seq;
                        if (Events.Contains(e.Kind)) await Send("event", e);
                    }
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now >= next)
                    {
                        await Send("state", State(ctx.RequestServices, null));
                        next = now + 2000;
                    }
                    await wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(5, next - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally { push.Emitted -= OnEmit; }
        });

        app.MapPost("/kiosk/api/run/{id}", (string id, ConfigStore cfg, PushStore push, AudioControl audio, ILogger<KioskLog> log) =>
            Shortcuts.Run(cfg.Get(), id, log, push, audio) is { } error ? Results.Problem(error) : Results.Ok());

        app.MapPost("/kiosk/api/media/{action}", async (string action, MediaMonitor media) =>
            action is "playpause" or "next" or "prev" && await media.Control(action, null)
                ? Results.Ok() : Results.BadRequest());

        app.MapPost("/kiosk/api/volume", (string? op, AudioControl audio) =>
        {
            var ok = op switch
            {
                "mute" => audio.ToggleMute(),
                "up" => audio.Step(+5),
                "down" => audio.Step(-5),
                _ => false,
            };
            return ok ? Results.Json(audio.Snapshot()) : Results.BadRequest();
        });

        app.MapGet("/kiosk/api/art", (MediaMonitor media) =>
            media.Art is { } art ? Results.File(art.Bytes, art.Mime) : Results.NotFound());
    }
}

/// <summary>Log category for the kiosk endpoints.</summary>
public sealed class KioskLog;
