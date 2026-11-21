using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class FeedParserTests
{
    [Theory]
    [InlineData("https://www.francetvinfo.fr/monde/a_1.html#xtor=RSS-3-[lestitres]", "https://www.francetvinfo.fr/monde/a_1.html#xtor=RSS-3-%5Blestitres%5D")]
    [InlineData("https://a.example/p[1]?q=[a]#f", "https://a.example/p%5B1%5D?q=%5Ba%5D#f")]
    [InlineData("http://[::1]:8080/x[y]", "http://[::1]:8080/x%5By%5D")]
    [InlineData("https://user@a.example/plain?q=1#top", "https://user@a.example/plain?q=1#top")]
    public void TheLaunchedAddressHasNoRawBracketsAfterTheHost(string link, string expected) =>
        Assert.Equal(expected, FeedParser.LaunchAddress(FeedParser.SafeLink(link)!));

    private static readonly Uri Base = new("https://example.com/blog/feed.xml");
    private static List<FeedItem> Parse(string xml, int max = 50) => FeedParser.Parse(Encoding.UTF8.GetBytes(xml), Base, "k", max);

    [Fact]
    public void Rss2ItemsKeepTitleLinkAndDate()
    {
        var items = Parse("""
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>Blog</title>
              <item><title>First &amp;amp; best</title><link>https://example.com/a</link><pubDate>Mon, 16 Nov 2026 09:30:00 GMT</pubDate></item>
              <item><title><![CDATA[<b>Bold</b> move]]></title><link>/b</link><pubDate>Tue, 17 Nov 26 10:00 +0200</pubDate></item>
            </channel></rss>
            """);
        Assert.Equal(2, items.Count);
        Assert.Equal("First & best", items[0].Title);
        Assert.Equal("https://example.com/a", items[0].Link);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), items[0].Published);
        Assert.Equal("Bold move", items[1].Title);
        Assert.Equal("https://example.com/b", items[1].Link); // relative links resolve against the feed
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), items[1].Published);
    }

    [Fact]
    public void AtomPrefersTheAlternateLinkAndHtmlTitlesBecomeText()
    {
        var items = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>A</title>
              <entry><id>urn:1</id><title type="html">&lt;i&gt;Hi&lt;/i&gt; there</title>
                <link rel="self" href="https://example.com/self"/><link rel="alternate" href="https://example.com/post"/>
                <updated>2026-11-16T08:00:00Z</updated></entry>
              <entry><id>urn:2</id><title>Plain &lt;3</title><link href="post2"/><published>2026-11-15T08:00:00+01:00</published></entry>
            </feed>
            """);
        Assert.Equal("Hi there", items[0].Title);
        Assert.Equal("https://example.com/post", items[0].Link);
        Assert.Equal("Plain <3", items[1].Title);
        Assert.Equal("https://example.com/blog/post2", items[1].Link);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 7, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), items[1].Published);
    }

    [Fact]
    public void AtomLinksFollowXmlBase()
    {
        var items = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://publisher.example/blog/">
              <entry xml:base="posts/"><id>1</id><title>Nested</title><link href="42"/></entry>
              <entry><id>2</id><title>Feed base</title><link href="7"/></entry>
              <entry><id>3</id><title>On the link</title><link xml:base="https://cdn.example/a/" href="b"/></entry>
            </feed>
            """);
        Assert.Equal(new string?[] { "https://publisher.example/blog/posts/42", "https://publisher.example/blog/7", "https://cdn.example/a/b" },
            items.Select(i => i.Link).ToArray());
    }

    [Fact]
    public void Rss1RdfItemsAreRead()
    {
        var items = Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>R</title></channel>
              <item><title>Old school</title><link>https://example.com/rdf</link><dc:date>2026-11-01T00:00:00Z</dc:date></item>
            </rdf:RDF>
            """);
        Assert.Single(items);
        Assert.Equal("Old school", items[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), items[0].Published);
    }

    [Fact]
    public void OnlyHttpLinksAreKept()
    {
        var items = Parse("""
            <rss><channel>
              <item><title>Script</title><link>javascript:alert(1)</link></item>
              <item><title>File</title><link>file:///C:/Windows/System32/calc.exe</link></item>
              <item><title>App</title><link>ms-settings:privacy</link></item>
              <item><title>Guid</title><guid>https://example.com/g</guid></item>
              <item><title>Not a link</title><guid isPermaLink="false">https://example.com/x</guid></item>
            </channel></rss>
            """);
        Assert.Equal(new string?[] { null, null, null, "https://example.com/g", null }, items.Select(i => i.Link).ToArray());
        Assert.All(items.Take(3), i => Assert.False(i.Openable));
    }

    [Fact]
    public void ExternalEntitiesAreNeverLoaded()
    {
        var secret = Path.Combine(Path.GetTempPath(), "phonebox-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOPSECRET");
        try
        {
            var xml = $"""<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x SYSTEM "file:///{secret.Replace('\\', '/')}">]><rss><channel><item><title>&x;</title></item></channel></rss>""";
            var ex = Record.Exception(() => Parse(xml));
            Assert.True(ex is XmlException || (ex is null && !Parse(xml).Any(i => i.Title.Contains("TOPSECRET"))));
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public void ADoctypeAloneDoesNotRejectAnOldFeed()
    {
        var items = Parse("""<?xml version="1.0"?><!DOCTYPE rss PUBLIC "-//Netscape Communications//DTD RSS 0.91//EN" "http://my.netscape.com/publish/formats/rss-0.91.dtd"><rss version="0.91"><channel><item><title>Ninety-one</title><link>https://example.com/91</link></item></channel></rss>""");
        Assert.Equal("Ninety-one", Assert.Single(items).Title);
    }

    [Fact]
    public void TitlesLoseControlAndDirectionCharactersAndAreCapped()
    {
        var items = Parse($"<rss><channel><item><title>evil\u202Egnp.exe&#x9; line\nbreak\u2066</title></item><item><title>{new string('a', 1000)}</title></item></channel></rss>");
        Assert.Equal("evilgnp.exe line break", items[0].Title);
        Assert.True(items[1].Title.Length <= 301);
        Assert.Equal("a b", FeedParser.Clean("a\u0007\u0000b")); // control characters XML itself would refuse
    }

    [Fact]
    public void NotAFeedIsRefused() => Assert.Throws<XmlException>(() => Parse("<html><body>hi</body></html>"));

    [Fact]
    public void TheItemCountIsCapped() =>
        Assert.Equal(3, Parse("<rss><channel>" + string.Concat(Enumerable.Range(0, 10).Select(i => $"<item><title>t{i}</title></item>")) + "</channel></rss>", max: 3).Count);

    [Fact]
    public void IdsAreStableAndDifferPerFeed()
    {
        Assert.Equal(FeedParser.IdOf("a", "x"), FeedParser.IdOf("a", "x"));
        Assert.NotEqual(FeedParser.IdOf("a", "x"), FeedParser.IdOf("b", "x"));
    }

    [Theory]
    [InlineData("Mon, 16 Nov 2026 09:30:00 GMT", "2026-11-16T09:30:00Z")]
    [InlineData("16 Nov 2026 09:30:00 +0000", "2026-11-16T09:30:00Z")]
    [InlineData("Mon, 16 Nov 2026 04:30:00 EST", "2026-11-16T09:30:00Z")]
    [InlineData("Monday, 16 November 2026 09:30 PDT", "2026-11-16T16:30:00Z")]
    [InlineData("Wed, 02 Oct 02 13:00:00 GMT", "2002-10-02T13:00:00Z")]
    [InlineData("Wed, 02 Oct 99 13:00:00 GMT", "1999-10-02T13:00:00Z")]
    [InlineData("2026-11-16T09:30:00Z", "2026-11-16T09:30:00Z")]
    [InlineData("2026-11-16T11:30:00+02:00", "2026-11-16T09:30:00Z")]
    public void DatesInTheUsualFormats(string raw, string utc) =>
        Assert.Equal(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), FeedParser.ParseDate(raw));

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("31 Foo 2026 10:00 GMT")]
    [InlineData("31 Feb 2026 10:00 GMT")]
    public void UnreadableDatesAreZero(string raw) => Assert.Equal(0, FeedParser.ParseDate(raw));
}

public sealed class FeedHttpTests
{
    [Theory]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("::1", true, false)]
    [InlineData("169.254.169.254", true, false)]
    [InlineData("0.0.0.0", true, false)]
    [InlineData("192.168.1.10", false, false)]
    [InlineData("192.168.1.10", true, true)]
    [InlineData("10.0.0.5", true, true)]
    [InlineData("100.101.102.103", true, true)]
    [InlineData("fd00::1", true, true)]
    [InlineData("93.184.216.34", false, true)]
    [InlineData("::ffff:127.0.0.1", true, false)]
    public void AddressRules(string ip, bool allowPrivate, bool allowed) =>
        Assert.Equal(allowed, FeedHttp.Allowed(IPAddress.Parse(ip), allowPrivate));

    [Theory]
    [InlineData("https://example.com/feed", true)]
    [InlineData("feed://example.com/feed", true)]
    [InlineData("http://example.com/feed", true)]
    [InlineData("ftp://example.com/feed", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://user:pass@example.com/feed", false)]
    [InlineData("example.com/feed", false)]
    public void OnlyHttpAddressesWithoutUserInfo(string raw, bool ok) => Assert.Equal(ok, FeedHttp.ParseTarget(raw) is not null);

    [Fact]
    public async Task TheRealHandlerNeverConnectsToThisComputer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var http = new HttpClient(FeedHttp.CreateHandler(allowPrivate: true));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            FeedHttp.GetAsync(_ => http, $"http://127.0.0.1:{port}/feed", null, null, CancellationToken.None));
        Assert.Contains("refused", ex.Message);
        // and the handler itself, for a name that resolves to this computer (the connect callback, not the literal check)
        var ex2 = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{port}/feed"));
        Assert.Contains("refused", ex2.ToString());
        Assert.False(listener.Pending()); // nothing reached the socket
    }

    [Fact]
    public async Task ARedirectToTheAgentIsRefusedBeforeAnyRequest()
    {
        var handler = new Fake(req => req.RequestUri!.Host == "feeds.example"
            ? Redirect("http://127.0.0.1:8765/api/settings")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            FeedHttp.GetAsync(_ => http, "https://feeds.example/rss", null, null, CancellationToken.None));
        Assert.Contains("refused", ex.Message);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task RedirectsAreFollowedAndCapped()
    {
        var hops = 0;
        var handler = new Fake(_ => ++hops <= 3 ? Redirect($"https://feeds.example/{hops}") : Ok("<rss/>"));
        using var http = new HttpClient(handler);
        var r = await FeedHttp.GetAsync(_ => http, "https://feeds.example/rss", null, null, CancellationToken.None);
        Assert.Equal("https://feeds.example/3", r.FinalUri!.AbsoluteUri);

        var loop = new Fake(_ => Redirect("https://feeds.example/again"));
        using var http2 = new HttpClient(loop);
        await Assert.ThrowsAsync<HttpRequestException>(() => FeedHttp.GetAsync(_ => http2, "https://feeds.example/rss", null, null, CancellationToken.None));
        Assert.Equal(6, loop.Seen.Count);
    }

    [Fact]
    public async Task ABodyOverTheCapIsRefused()
    {
        var big = new byte[FeedHttp.MaxBytes + 1];
        // no Content-Length: the cap must hold while reading
        var handler = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(big)) });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => FeedHttp.GetAsync(_ => http, "https://feeds.example/rss", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task ValidatorsAreSentAndNotModifiedIsReported()
    {
        var handler = new Fake(req =>
        {
            Assert.Equal("\"v1\"", req.Headers.GetValues("If-None-Match").Single());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        using var http = new HttpClient(handler);
        var r = await FeedHttp.GetAsync(_ => http, "https://feeds.example/rss", "\"v1\"", null, CancellationToken.None);
        Assert.True(r.NotModified);
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("nas", true)]
    [InlineData("nas.local", true)]
    [InlineData("reader.lan", true)]
    [InlineData("box.home.arpa", true)]
    [InlineData("pc.tail1234.ts.net", true)]
    [InlineData("rss.example.com", false)]
    [InlineData("local.example.com", false)]
    [InlineData("evil.com", false)]
    public void LocalNames(string host, bool local) => Assert.Equal(local, FeedHttp.IsLocalName(host));

    [Theory]
    [InlineData("https://rss.example.com/feed", false)] // a public name: its DNS must not point into the network
    [InlineData("http://192.168.1.5/feed", true)]
    [InlineData("http://nas.local/feed", true)]
    public async Task PrivateAddressesOnlyForLocalHosts(string url, bool allowPrivate)
    {
        var local = new Fake(_ => Ok("<rss/>"));
        var pub = new Fake(_ => Ok("<rss/>"));
        using var localHttp = new HttpClient(local);
        using var publicHttp = new HttpClient(pub);
        await FeedHttp.GetAsync(p => p ? localHttp : publicHttp, url, null, null, CancellationToken.None);
        Assert.Equal(allowPrivate ? 1 : 0, local.Seen.Count);
        Assert.Equal(allowPrivate ? 0 : 1, pub.Seen.Count);
    }

    [Fact]
    public async Task ARedirectFromALocalHostToAnotherHostIsPublicOnly()
    {
        // separate clients, so a pooled connection to a local host can never carry a request that must stay public
        var local = new Fake(_ => Redirect("http://other.local/feed"));
        var pub = new Fake(_ => Ok("<rss/>"));
        using var localHttp = new HttpClient(local);
        using var publicHttp = new HttpClient(pub);
        await FeedHttp.GetAsync(p => p ? localHttp : publicHttp, "http://nas.local/feed", null, null, CancellationToken.None);
        Assert.Equal(["nas.local"], local.Seen.Select(u => u.Host).ToArray());
        Assert.Equal(["other.local"], pub.Seen.Select(u => u.Host).ToArray());
    }

    [Fact]
    public void HostileTitlesAreCutBeforeCleaning()
    {
        // many '<' without '>' used to make the tag pattern rescan the rest of the text for each one
        var title = FeedParser.Clean(new string('<', 2_000_000) + "tail");
        Assert.True(title.Length <= 301);
        Assert.Equal("a b c", FeedParser.Clean("a<x>b</x>c"));
        Assert.Equal("1 < 2", FeedParser.Clean("1 &lt; 2"));
    }

    internal static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };
    internal static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") };

    internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Seen) Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}

public sealed class FeedMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-feeds-" + Guid.NewGuid().ToString("N"));
    private long _now = 1_800_000_000_000;

    public FeedMonitorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(string widgetType = "feeds", bool pageEnabled = true, string feeds = """[{"name":"Blog","url":"https://feeds.example/rss"}]""", bool open = true)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""{"feeds":{{feeds}},"pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":[{"id":"w","type":"{{widgetType}}","x":0,"y":0,"w":6,"h":4,"options":{"open":{{(open ? "true" : "false")}} } }]},{"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4}]}]}""");
        return new ConfigStore(path);
    }

    private const string Rss = """<rss><channel><item><title>Hello</title><link>https://feeds.example/hello</link><pubDate>Mon, 16 Nov 2026 09:30:00 GMT</pubDate></item><item><title>Nolink</title></item></channel></rss>""";

    private FeedMonitor Monitor(ConfigStore cfg, HttpMessageHandler handler, List<Uri>? opened = null) =>
        new(cfg, NullLogger<FeedMonitor>.Instance) { Handler = handler, Now = () => _now, Opener = u => opened?.Add(u) };

    private sealed class GatedFeed : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return FeedHttpTests.Ok(Rss);
        }
    }

    [Theory]
    [InlineData("Blog")]
    [InlineData("Renamed")]
    [InlineData(null)]
    public async Task InFlightFetchPublishesOnlyCurrentSources(string? name)
    {
        var cfg = Config();
        using var handler = new GatedFeed();
        using var monitor = Monitor(cfg, handler);
        var pending = monitor.TickAsync(CancellationToken.None);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (name != "Blog")
            {
                var settings = JsonNode.Parse(File.ReadAllText(cfg.Path))!;
                if (name is null) settings["feeds"] = new JsonArray();
                else settings["feeds"]![0]!["name"] = name;
                Assert.Null(cfg.Save(settings.ToJsonString()));
            }
        }
        finally { handler.Release.TrySetResult(); }

        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var actual = monitor.Snapshot().Items.Select(item => item.Source).ToArray();
        string[] expected = name is null ? [] : [name, name];
        Assert.True(expected.SequenceEqual(actual),
            $"EXPECTED: sources=[{string.Join(", ", expected)}] | ACTUAL: sources=[{string.Join(", ", actual)}]");
    }

    [Fact]
    public async Task EachFeedIsFetchedAtMostEveryFifteenMinutes()
    {
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        using var m = Monitor(Config(), handler);
        await m.TickAsync(CancellationToken.None);
        Assert.Single(handler.Seen);
        Assert.Equal(["Hello", "Nolink"], m.Snapshot().Items.Select(i => i.Title).ToArray());
        Assert.All(m.Snapshot().Items, i => Assert.Equal("Blog", i.Source));

        Wake.Now(); // /api/refresh does not bypass the interval
        _now += (long)FeedMonitor.Interval.TotalMilliseconds - 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Single(handler.Seen);

        _now += 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task NothingIsFetchedWithoutAFeedsWidgetOnAnEnabledPage()
    {
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        using var hidden = Monitor(Config(pageEnabled: false), handler);
        await hidden.TickAsync(CancellationToken.None);
        using var other = Monitor(Config(widgetType: "clock"), handler);
        await other.TickAsync(CancellationToken.None);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task AFailedFetchKeepsTheLastHeadlinesAndReportsTheError()
    {
        var fail = false;
        var handler = new FeedHttpTests.Fake(_ => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : FeedHttpTests.Ok(Rss));
        using var m = Monitor(Config(), handler);
        await m.TickAsync(CancellationToken.None);
        fail = true;
        _now += (long)FeedMonitor.Interval.TotalMilliseconds;
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(2, m.Snapshot().Items.Count);
        Assert.Equal("Blog: HTTP 500", Assert.Single(m.Snapshot().Errors));
    }

    [Fact]
    public async Task ArticlesOpenByIdOnlyWhenAllowed()
    {
        var opened = new List<Uri>();
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        using var m = Monitor(Config(), handler, opened);
        await m.TickAsync(CancellationToken.None);
        var items = m.Snapshot().Items;

        Assert.Null(m.Open(items[0].Id));
        Assert.Equal("https://feeds.example/hello", Assert.Single(opened).AbsoluteUri);
        Assert.NotNull(m.Open(items[1].Id)); // no link
        Assert.NotNull(m.Open("0123456789abcdef")); // not in the list
        Assert.NotNull(m.Open(null));
        Assert.Single(opened);
    }

    [Fact]
    public async Task OpeningNeedsTheWidgetOnAnEnabledPage()
    {
        var opened = new List<Uri>();
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        var path = Path.Combine(_dir, "config.json");
        var cfg = Config();
        using var m = Monitor(cfg, handler, opened);
        await m.TickAsync(CancellationToken.None);
        var id = m.Snapshot().Items[0].Id;

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"enabled\":true", "\"enabled\":false"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // a new stamp, however coarse the file system clock
        Assert.False(m.OpenAllowed());
        Assert.NotNull(m.Open(id));
        Assert.Empty(opened);
    }

    [Fact]
    public async Task OpeningIsOffUntilTheWidgetAllowsIt()
    {
        var opened = new List<Uri>();
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        using var m = Monitor(Config(open: false), handler, opened);
        await m.TickAsync(CancellationToken.None);
        Assert.True(m.Shown()); // the headlines are still fetched and shown
        Assert.False(m.OpenAllowed());
        Assert.NotNull(m.Open(m.Snapshot().Items[0].Id));
        Assert.Empty(opened);
    }

    [Fact]
    public async Task NoFetchStartsAfterTheWidgetIsRemoved()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = Config(feeds: """[{"name":"A","url":"https://a.example/rss"},{"name":"B","url":"https://b.example/rss"}]""");
        var handler = new FeedHttpTests.Fake(req =>
        {
            // while the first feed is being fetched, the page with the widget is hidden
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"enabled\":true", "\"enabled\":false"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            return FeedHttpTests.Ok(Rss);
        });
        using var m = Monitor(cfg, handler);
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(["a.example"], handler.Seen.Select(u => u.Host).ToArray());
    }

    [Fact]
    public void TheLinkNeverReachesThePhone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new FeedItem("id", "t", "s", 1, "https://secret.example/?token=abc", "Tech"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("secret", json);
        Assert.Contains("\"openable\":true", json);
        Assert.Contains("\"category\":\"Tech\"", json);
    }

    [Fact]
    public async Task FeedCategoriesAreAttachedToItems()
    {
        var cfg = Config(feeds: """[{"name":"TechBlog","url":"https://feeds.example/rss","category":"Technology"}]""");
        var handler = new FeedHttpTests.Fake(_ => FeedHttpTests.Ok(Rss));
        using var m = Monitor(cfg, handler);
        await m.TickAsync(CancellationToken.None);
        var items = m.Snapshot().Items;
        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.Equal("TechBlog", i.Source);
            Assert.Equal("Technology", i.Category);
        });
    }

    [Fact]
    public void FeedAddressesStayOffThePhone()
    {
        var cfg = Config(feeds: """[{"name":"Private","url":"https://feeds.example/rss?key=s3cret","category":"Personal"}]""");
        var pub = System.Text.Json.JsonSerializer.Serialize(cfg.Get().ToPublic());
        Assert.DoesNotContain("s3cret", pub);
    }

    [Fact]
    public void FeedCategoryIsNormalizedInConfig()
    {
        var longCat = new string('x', 50);
        var cfg = Config(feeds: $$"""[{"name":"Blog","url":"https://feeds.example/rss","category":"  {{longCat}}  "}]""");
        var feed = Assert.Single(cfg.Get().Feeds);
        Assert.Equal(40, feed.Category.Length);
        Assert.Equal(new string('x', 40), feed.Category);
    }
}
