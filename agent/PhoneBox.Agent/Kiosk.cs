namespace PhoneBox.Agent;

/// <summary>
/// Web kiosk: the phone's panel in a browser, for devices without the phone app (a tablet, a spare monitor, a Raspberry Pi
/// screen, Fully Kiosk Browser). /kiosk/?token=… serves exactly the pages the phone shows; the page then talks to the same
/// API as the phone (/agent/api/…), authenticated by a cookie set from the token. Browsers without container queries
/// or modern JavaScript, down to Firefox 68 on Android 4.4, are sent to /kiosk/lite/: the same panel built for them by
/// tools/kiosk-lite (kiosk-lite/ here). The kiosk has its own key (URLs end up in logs and bookmarks, so it is never the
/// phone's key).
/// </summary>
public static class Kiosk
{
    public const string CookieName = "phonebox_kiosk";

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
    /// Sent first in the panel's &lt;head&gt;: browsers without container queries or optional chaining go to the lite panel.
    /// ES5 on purpose, so that old browsers can run it. ?full=1 skips the check.
    /// </summary>
    public const string LegacyCheck = """
        <script>(function(){if(/[?&]full=1/.test(location.search))return;var ok=false;try{ok=!!(window.CSS&&CSS.supports&&CSS.supports('width','1cqw'))&&!!new Function('a','return a?.b??1');}catch(e){ok=false;}if(!ok)location.replace('lite/'+location.search);})();</script>
        """;

    /// <summary>A file of the phone's panel (embedded at build time): index.html, main.js, style.css… and widgets/*.js.</summary>
    public static IResult PanelFile(string? file, string folder = "www")
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
        return AppPaths.Resource(folder + "/" + file) is { } s ? Results.Stream(s, type) : Results.NotFound();
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

    public static void MapKiosk(this WebApplication app)
    {
        // The phone's pages, unchanged; /kiosk/lite/ the same panel built for older browsers (tools/kiosk-lite).
        // Relative URLs (style.css, main.js, widgets/…) need the trailing slash.
        app.MapGet("/kiosk/{*file}", (string? file, HttpRequest req) =>
        {
            if (string.IsNullOrEmpty(file) && !req.Path.Value!.EndsWith('/')) return Results.Redirect("/kiosk/" + req.QueryString);
            if (file == "lite") return Results.Redirect("/kiosk/lite/" + req.QueryString);
            if (file?.StartsWith("lite/", StringComparison.Ordinal) == true) return PanelFile(file["lite/".Length..], "kiosk-lite");
            return string.IsNullOrEmpty(file) || file == "index.html" ? Results.Content(PanelIndex(), "text/html; charset=utf-8") : PanelFile(file);
        });
    }
}
