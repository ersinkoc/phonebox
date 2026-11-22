using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>One headline. <see cref="Link"/> stays on this computer: the phone opens an item by its id.</summary>
public sealed record FeedItem(string Id, string Title, string Source, long Published, [property: JsonIgnore] string? Link, string Category = "")
{
    public bool Openable => Link is not null;
}

public sealed record FeedSnapshot(IReadOnlyList<FeedItem> Items, long Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Headlines from the RSS and Atom feeds in <see cref="AgentConfig.Feeds"/>, for the Feeds widget. Each address is
/// fetched at most every 15 minutes, and only while an enabled page shows a Feeds widget; /api/refresh does not
/// bypass that. The addresses stay on this computer (private feeds carry keys in them); the phone gets titles, the
/// feed's name and times, and opens an article on this computer by its id.
/// </summary>
public sealed class FeedMonitor(ConfigStore config, ILogger<FeedMonitor> log) : BackgroundService
{
    internal const string WidgetType = "feeds";
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int MaxPerFeed = 50, MaxItems = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FeedState> _feeds = [];
    private volatile FeedSnapshot _snap = new([], 0, []);

    /// <summary>Test seam: the HTTP handler behind the fetches (the real ones refuse loopback, and private addresses except for local hosts).</summary>
    internal HttpMessageHandler? Handler { get; init; }
    /// <summary>Test seam: opens an article on this computer.</summary>
    internal Action<Uri> Opener { get; init; } = OpenInBrowser;
    /// <summary>Test seam: the clock, in Unix milliseconds.</summary>
    internal Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // two clients, so a pooled connection opened for a local host can never serve a request that must stay public
    private HttpClient? _public, _local;
    private HttpClient Client(bool local) => local
        ? _local ??= new HttpClient(Handler ?? FeedHttp.CreateHandler(allowPrivate: true), disposeHandler: Handler is null) { Timeout = Timeout.InfiniteTimeSpan }
        : _public ??= new HttpClient(Handler ?? FeedHttp.CreateHandler(allowPrivate: false), disposeHandler: Handler is null) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class FeedState
    {
        public List<FeedItem> Items = [];   // with an empty Source: the name is applied when the snapshot is built
        public long NextAt;
        public string? Error;
        public string? ETag;
        public string? LastModified;
    }

    public FeedSnapshot Snapshot() => _snap;

    /// <summary>Feeds are fetched only while an enabled page (or an enabled Smart Stack child) shows the widget.</summary>
    public bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    /// <summary>Opening an article starts the browser on this computer: only with the widget's "open" option on (off by default).</summary>
    public bool OpenAllowed() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType && w.Bool("open") == true);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("feeds: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Fetches every feed that is due. Nothing is fetched while no enabled page shows a Feeds widget.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var sources = Sources();
        lock (_gate)
            foreach (var gone in _feeds.Keys.Except(sources.Select(s => s.Url)).ToList()) _feeds.Remove(gone);
        if (sources.Count == 0 || !Shown()) { Publish(sources); return; }

        foreach (var src in sources)
        {
            // a fetch can take seconds: the widget or the feed may have been removed while the previous one ran
            if (!Shown()) break;
            if (!Sources().Any(x => x.Url == src.Url)) continue;
            FeedState st;
            lock (_gate)
            {
                if (!_feeds.TryGetValue(src.Url, out st!)) _feeds[src.Url] = st = new FeedState();
                if (Now() < st.NextAt) continue;
                st.NextAt = Now() + (long)Interval.TotalMilliseconds; // failed fetches wait just as long
            }
            await FetchAsync(src, st, ct);
        }
        Publish(Sources()); // a completed request can outlive a feed rename or removal
    }

    private List<FeedSource> Sources() => config.Get().Feeds
        .Where(f => !string.IsNullOrWhiteSpace(f.Url))
        .GroupBy(f => f.Url, StringComparer.Ordinal).Select(g => g.First()).ToList();

    private async Task FetchAsync(FeedSource src, FeedState st, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await FeedHttp.GetAsync(Client, src.Url, st.ETag, st.LastModified, timeout.Token);
            if (r.NotModified) { lock (_gate) st.Error = null; return; }
            var items = FeedParser.Parse(r.Body!, r.FinalUri!, src.Url, MaxPerFeed);
            lock (_gate) { st.Items = items; st.Error = null; st.ETag = r.ETag; st.LastModified = r.LastModified; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = ex is OperationCanceledException ? "no answer in 20 s" : ex is XmlException ? "not an RSS or Atom feed" : ex.Message;
            lock (_gate) st.Error = msg; // the last good headlines stay
            log.LogInformation("feed {Name}: {Msg}", NameOf(src), msg);
        }
    }

    private static string NameOf(FeedSource s) =>
        !string.IsNullOrWhiteSpace(s.Name) ? s.Name.Trim() : Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Host : "Feed";

    private void Publish(List<FeedSource> sources)
    {
        var items = new List<FeedItem>();
        var errors = new List<string>();
        lock (_gate)
            foreach (var src in sources)
            {
                if (!_feeds.TryGetValue(src.Url, out var st)) continue;
                var name = NameOf(src);
                var category = src.Category?.Trim() ?? "";
                items.AddRange(st.Items.Select(i => i with { Source = name, Category = category }));
                if (st.Error is { } e) errors.Add($"{name}: {e}");
            }
        // newest first; undated items keep their feed order after the dated ones
        var ordered = items.Select((it, i) => (it, i)).OrderByDescending(x => x.it.Published).ThenBy(x => x.i)
            .Select(x => x.it).Take(MaxItems).ToList();
        _snap = new(ordered, Now(), errors);
    }

    /// <summary>Opens a headline from the current list on this computer; returns an error message, or null when it worked.</summary>
    public string? Open(string? id)
    {
        if (!OpenAllowed()) return "Opening articles is off: turn on \"Open articles on the PC\" in the News feeds widget's settings";
        if (string.IsNullOrEmpty(id) || _snap.Items.FirstOrDefault(i => i.Id == id) is not { } item) return "That headline is no longer in the list";
        if (FeedParser.SafeLink(item.Link) is not { } uri) return "This headline has no link";
        try { Opener(uri); }
        catch (Exception ex) { log.LogWarning("feed: could not open {Host}: {Msg}", uri.Host, ex.Message); return "Could not open it on the computer"; }
        log.LogInformation("feed article opened from the phone: {Host}", uri.Host);
        return null;
    }

    /// <summary>
    /// Hands the address to the default browser without a shell: rundll32 url.dll on Windows, open(1) on macOS, xdg-open on
    /// Linux, with the address as one argument. rundll32 and open pass it on and exit, so one that hangs is ended after
    /// 15 s; xdg-open can become the browser itself on some desktops, so it is left running.
    /// </summary>
    private static void OpenInBrowser(Uri uri)
    {
        var psi = BrowserStart(uri, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
        var p = Process.Start(psi) ?? throw new InvalidOperationException("the browser launcher did not start");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) { p.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            using (p)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(); } catch { } }
            }
        });
    }

    /// <summary>The launcher and its arguments for an article address (no side effects; tested).</summary>
    internal static ProcessStartInfo BrowserStart(Uri uri, bool windows, bool mac)
    {
        // explorer.exe opens File Explorer instead of the browser for some addresses (#386, #411); url.dll hands the address
        // to the http(s) handler and rundll32 exits, like explorer did
        var psi = new ProcessStartInfo(windows ? Path.Combine(Environment.SystemDirectory, "rundll32.exe") : mac ? "/usr/bin/open" : "xdg-open")
            { UseShellExecute = false, CreateNoWindow = true };
        if (windows) psi.ArgumentList.Add("url.dll,FileProtocolHandler");
        psi.ArgumentList.Add(windows ? FeedParser.LaunchAddress(uri) : uri.AbsoluteUri);
        return psi;
    }

    public override void Dispose()
    {
        _public?.Dispose();
        _local?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Outbound requests for feeds. Never to this computer (loopback), link-local, multicast or unspecified addresses.
/// Private and Tailscale addresses are allowed only for the host the user typed in Settings, and only when that host
/// is itself local: an IP address, a name without dots, or a .local / .lan / .home / .internal / .home.arpa / .ts.net
/// name (a self-hosted reader at home). A public name whose DNS points into the local network is refused, and so is
/// a redirect from the typed host to any other host that is not public. Redirects are followed by hand, at most 5.
/// </summary>
internal static class FeedHttp
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;

    internal sealed record Response(byte[]? Body, Uri? FinalUri, bool NotModified, string? ETag, string? LastModified);

    /// <summary>A handler whose connections only go to allowed addresses; <paramref name="allowPrivate"/> is fixed per handler, so per pool.</summary>
    internal static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (ctx, token) =>
        {
            var addresses = IPAddress.TryParse(ctx.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
            var ip = addresses.FirstOrDefault(a => Allowed(a, allowPrivate))
                ?? throw new HttpRequestException(allowPrivate
                    ? "refused: the address is this computer or not routable"
                    : $"refused: {ctx.DnsEndPoint.Host} is not a public address (for a feed on your network, use its IP address or local name)");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Public addresses always; private, CGNAT/Tailscale and unique-local ones only when <paramref name="allowPrivate"/>.</summary>
    internal static bool Allowed(IPAddress ip, bool allowPrivate)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (WidgetFeedProxy.IsPublicAddress(ip)) return true;
        return allowPrivate && IsPrivate(ip);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xFE) == 0xFC;
    }

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".internal", ".home.arpa", ".ts.net"];

    /// <summary>A host that names the local network itself: an IP address, a single label, or a local-only suffix.</summary>
    internal static bool IsLocalName(string host)
    {
        host = host.TrimEnd('.');
        return IPAddress.TryParse(host, out _) || !host.Contains('.')
            || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An http(s) address without user info, or null.</summary>
    internal static Uri? ParseTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2048) return null;
        raw = raw.Trim();
        if (raw.StartsWith("feed://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw[7..];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(u.UserInfo) ? u : null;
    }

    /// <param name="client">The client for a hop: <c>client(true)</c> may reach private addresses, <c>client(false)</c> only public ones.</param>
    internal static async Task<Response> GetAsync(Func<bool, HttpClient> client, string configured, string? etag, string? lastModified, CancellationToken ct)
    {
        var first = ParseTarget(configured) ?? throw new HttpRequestException("the address must start with http:// or https://");
        var uri = first;
        for (var hop = 0; ; hop++)
        {
            var allowPrivate = string.Equals(uri.Host, first.Host, StringComparison.OrdinalIgnoreCase) && IsLocalName(first.Host);
            // a literal address that is not allowed can be refused before any connection
            if (IPAddress.TryParse(uri.Host, out var lit) && !Allowed(lit, allowPrivate))
                throw new HttpRequestException($"refused: {uri.Host} is not a public address");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version}");
            req.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.1");
            if (hop == 0 && etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (hop == 0 && lastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            using var res = await client(allowPrivate).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= MaxRedirects) throw new HttpRequestException("too many redirects");
                var loc = res.Headers.Location ?? throw new HttpRequestException($"HTTP {code} without a Location");
                uri = ParseTarget(new Uri(uri, loc).AbsoluteUri) ?? throw new HttpRequestException("redirected to an address that is not http(s)");
                continue;
            }
            if (res.StatusCode == HttpStatusCode.NotModified) return new(null, uri, true, etag, lastModified);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {code}");
            if (res.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("the feed is larger than 4 MB");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var buf = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buf.Length + n > MaxBytes) throw new HttpRequestException("the feed is larger than 4 MB");
                buf.Write(chunk, 0, n);
            }
            return new(buf.ToArray(), uri, false, res.Headers.ETag?.ToString(), res.Content.Headers.LastModified?.ToString("R"));
        }
    }
}

/// <summary>RSS 2.0, RSS 1.0 (RDF) and Atom. No DTDs, no external entities; titles become plain text.</summary>
internal static partial class FeedParser
{
    private const int MaxTitle = 300;

    internal static List<FeedItem> Parse(byte[] xml, Uri baseUri, string feedKey, int max)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // old RSS 0.91 feeds carry a DOCTYPE; it is skipped, never loaded
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = FeedHttp.MaxBytes * 2L,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        // from the bytes, so the encoding in the XML declaration wins over a wrong HTTP charset
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new XmlException("empty document");
        if (root.Name.LocalName is not ("rss" or "RDF" or "feed")) throw new XmlException("not a feed");

        var atom = root.Name.LocalName == "feed";
        var entries = root.Descendants().Where(e => e.Name.LocalName == (atom ? "entry" : "item"));
        var list = new List<FeedItem>();
        foreach (var e in entries)
        {
            var title = atom ? AtomText(Child(e, "title")) : Clean(Child(e, "title")?.Value);
            var link = atom ? AtomLink(e, baseUri) : RssLink(e, baseUri);
            var key = (atom ? Child(e, "id")?.Value : Child(e, "guid")?.Value)?.Trim();
            if (string.IsNullOrEmpty(key)) key = link ?? title;
            if (string.IsNullOrEmpty(title)) title = link is not null && Uri.TryCreate(link, UriKind.Absolute, out var lu) ? lu.Host : "";
            if (string.IsNullOrEmpty(title)) continue;
            var date = atom
                ? Child(e, "published")?.Value ?? Child(e, "updated")?.Value
                : Child(e, "pubDate")?.Value ?? Child(e, "date")?.Value; // dc:date in RSS 1.0
            list.Add(new FeedItem(IdOf(feedKey, key!), title, "", ParseDate(date), link));
            if (list.Count >= max) break;
        }
        return list;
    }

    internal static string IdOf(string feedKey, string itemKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(feedKey + "\n" + itemKey)))[..16];

    private static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    private static string AtomText(XElement? t)
    {
        if (t is null) return "";
        var type = (string?)t.Attribute("type");
        // type="html" holds escaped markup; xhtml holds elements whose text is the title
        return type == "html" ? Clean(t.Value) : Clean(t.Value, decodeEntities: false);
    }

    private static string? AtomLink(XElement e, Uri baseUri)
    {
        var links = e.Elements().Where(c => c.Name.LocalName == "link").ToList();
        var link = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault();
        return link is null ? null : Absolute(link.Attribute("href")?.Value, BaseOf(link, baseUri));
    }

    /// <summary>The base a relative href resolves against: the feed's address with every xml:base from the root down (RFC 4287 §2).</summary>
    private static Uri BaseOf(XElement el, Uri feedUri)
    {
        var uri = feedUri;
        foreach (var a in el.AncestorsAndSelf().Reverse())
            if (a.Attribute(XNamespace.Xml + "base")?.Value is { Length: > 0 and <= 2048 } b && Uri.TryCreate(uri, b.Trim(), out var u))
                uri = u;
        return uri;
    }

    private static string? RssLink(XElement e, Uri baseUri)
    {
        if (Absolute(Child(e, "link")?.Value, baseUri) is { } l) return l;
        var guid = Child(e, "guid");
        return guid is not null && !string.Equals((string?)guid.Attribute("isPermaLink"), "false", StringComparison.OrdinalIgnoreCase)
            ? Absolute(guid.Value, baseUri) : null;
    }

    private static string? Absolute(string? href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(baseUri, href.Trim(), out var u) ? SafeLink(u.AbsoluteUri)?.AbsoluteUri : null;
    }

    /// <summary>Only http and https links are ever opened (no file:, javascript:, custom app schemes).</summary>
    /// <summary>
    /// The address as handed to the Windows launcher. .NET leaves '[' and ']' raw after the host (as in a
    /// <c>#xtor=RSS-3-[lestitres]</c> tracking fragment), and explorer.exe then opens the Documents folder instead of the
    /// browser, so they are percent-encoded there. An IPv6 host keeps its brackets. macOS and Linux get the address as is.
    /// </summary>
    internal static string LaunchAddress(Uri uri)
    {
        var authority = uri.GetLeftPart(UriPartial.Authority);
        return authority + uri.AbsoluteUri[authority.Length..].Replace("[", "%5B").Replace("]", "%5D");
    }

    internal static Uri? SafeLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.AbsoluteUri.Length <= 4096
            ? u : null;

    /// <summary>Plain text: HTML tags and entities resolved, control and direction-override characters dropped, capped.</summary>
    internal static string Clean(string? s, bool decodeEntities = true)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // a title is short: cut the raw text first so nothing below works on megabytes
        if (s.Length > MaxRaw) s = s[..MaxRaw];
        if (decodeEntities) s = WebUtility.HtmlDecode(StripTags(s));
        var sb = new StringBuilder(Math.Min(s.Length, MaxTitle + 1));
        var space = false;
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r) || Rune.IsControl(r)) { space = sb.Length > 0; continue; }
            // direction overrides, isolates and marks would let a title rearrange the text around it
            if (r.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 or 0x200E or 0x200F or 0x061C or 0xFEFF) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(r.ToString());
            if (sb.Length >= MaxTitle) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    private const int MaxRaw = 8 * MaxTitle;

    /// <summary>Removes &lt;…&gt; tags in one pass; a '&lt;' with no '&gt;' after it is kept as text.</summary>
    private static string StripTags(string s)
    {
        var lt = s.IndexOf('<');
        if (lt < 0) return s;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (lt >= 0)
        {
            var gt = s.IndexOf('>', lt + 1);
            if (gt < 0) break;
            sb.Append(s, i, lt - i).Append(' ');
            i = gt + 1;
            lt = s.IndexOf('<', i);
        }
        return sb.Append(s, i, s.Length - i).ToString();
    }

    [GeneratedRegex(@"^(?:[A-Za-z]+,?\s*)?(\d{1,2})\s+([A-Za-z]{3})[A-Za-z]*\.?\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(.*)$")]
    private static partial Regex Rfc822();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5, ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
    };

    /// <summary>RFC 822 (RSS) and ISO 8601 (Atom, dc:date) dates in Unix ms; 0 when unreadable.</summary>
    internal static long ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.Length > 64) return 0; // no real date is longer; keeps the patterns below cheap
        var m = Rfc822().Match(s);
        if (m.Success)
        {
            var mon = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant());
            if (mon < 0) return 0;
            var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (m.Groups[3].Value.Length == 2) year += year < 50 ? 2000 : 1900;
            var zone = m.Groups[7].Value.Trim();
            var offset = TimeSpan.Zero;
            if (zone.Length == 5 && zone[0] is '+' or '-' && int.TryParse(zone.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
                offset = TimeSpan.FromMinutes((hhmm / 100 * 60 + hhmm % 100) * (zone[0] == '-' ? -1 : 1));
            else if (Zones.TryGetValue(zone, out var hours)) offset = TimeSpan.FromHours(hours);
            try
            {
                var sec = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
                var d = new DateTimeOffset(year, mon + 1, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Math.Min(sec, 59), offset);
                return d.ToUnixTimeMilliseconds();
            }
            catch (ArgumentOutOfRangeException) { return 0; }
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var iso)
            ? iso.ToUnixTimeMilliseconds() : 0;
    }
}
