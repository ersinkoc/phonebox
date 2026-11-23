using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Photo frame's Immich source against a fake Immich: album listing, key handling, ids, gating, errors.</summary>
public sealed class ImmichPhotoTests : IDisposable
{
    private const string Key = "test-key-0123456789", Server = "http://immich.test:2283";
    private const string Album = "11111111-1111-1111-1111-111111111111", Other = "22222222-2222-2222-2222-222222222222";
    private const string A1 = "aaaaaaaa-0000-0000-0000-000000000001", A2 = "aaaaaaaa-0000-0000-0000-000000000002",
        Video = "aaaaaaaa-0000-0000-0000-000000000003", Trashed = "aaaaaaaa-0000-0000-0000-000000000004";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-immich-" + Guid.NewGuid().ToString("N"));
    public ImmichPhotoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeImmich(Func<HttpRequestMessage, HttpResponseMessage>? custom = null) : HttpMessageHandler
    {
        public List<(string Path, string? Key)> Seen { get; } = [];
        public bool AlbumGone { get; set; }
        /// <summary>When set, every request fails this way: an exception (server down) or a status.</summary>
        public Exception? Down { get; set; }
        public HttpStatusCode? Answer { get; set; }
        public List<string> Searches { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("x-api-key", out var v) ? v.Single() : null;
            lock (Seen) Seen.Add((request.RequestUri!.PathAndQuery, key));
            if (Down is not null) throw Down;
            if (Answer is { } status) return Task.FromResult(new HttpResponseMessage(status));
            if (custom is not null) return Task.FromResult(custom(request));
            if (key != Key) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/albums")
                return Task.FromResult(Json("""[{"id":"22222222-2222-2222-2222-222222222222","albumName":"Zebra","assetCount":3},{"id":"11111111-1111-1111-1111-111111111111","albumName":"apple","assetCount":4},{"id":"not-a-guid","albumName":"bad","assetCount":1}]"""));
            if (path == "/api/search/metadata" && request.Method == HttpMethod.Post)
            {
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;
                var album = body.GetProperty("albumIds")[0].GetString();
                var page = body.GetProperty("page").GetInt32();
                if (AlbumGone || album != Album) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                lock (Seen) Searches.Add(body.GetRawText());
                // two pages: the second one holds the repeated id, the video and the trashed image
                return Task.FromResult(page == 1
                    ? Json($$$"""{"assets":{"items":[{"id":"{{{A1}}}","type":"IMAGE","updatedAt":"2026-01-02T03:04:05.000Z"},{"id":"{{{A2}}}","type":"IMAGE"}],"nextPage":"2"}}""")
                    : Json($$$"""{"assets":{"items":[{"id":"{{{A2}}}","type":"IMAGE"},{"id":"{{{Video}}}","type":"VIDEO"},{"id":"{{{Trashed}}}","type":"IMAGE","isTrashed":true}],"nextPage":null}}"""));
            }
            if (path.StartsWith("/api/assets/") && path.EndsWith("/thumbnail"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(path.Contains(A2) ? "not an image"u8.ToArray() : Jpeg) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private (PhotoFrame Frame, ConfigStore Store) Frame(HttpMessageHandler fake, bool widget = true, string album = Album, string key = Key, string source = "immich", string url = Server + "/", Func<long>? now = null)
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"photos":{"source":"{{source}}","immichUrl":"{{url}}","immichKey":"{{key}}","immichAlbum":"{{album}}","immichAlbumName":"apple"},
             "pages":[{"id":"p","title":"P","widgets":{{widgets}}}]}
            """);
        var store = new ConfigStore(path);
        return (new PhotoFrame(store, NullLogger<PhotoFrame>.Instance) { CacheDir = Path.Combine(_dir, "cache"), ImmichHandler = fake, Now = now ?? (() => Environment.TickCount64) }, store);
    }

    [Fact]
    public async Task AlbumsAreListedByNameWithTheKeyInAHeader()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        var (albums, error) = await frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(["apple", "Zebra"], albums!.Select(a => a.Name).ToArray()); // sorted, the malformed id dropped
        Assert.Equal(Album, albums![0].Id);
        Assert.All(fake.Seen, s => Assert.Equal(Key, s.Key));
        Assert.DoesNotContain(fake.Seen, s => s.Path.Contains(Key)); // never in the address
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"5\"")]
    [InlineData("{}")]
    [InlineData("1.5")]
    public async Task AnAlbumWhoseCountIsNotAnIntegerIsStillListed(string count)
    {
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""[{"id":"{{{Album}}}","albumName":"Odd","assetCount":{{{count}}}},{"id":"{{{Other}}}","albumName":"Fine","assetCount":7}]""", Encoding.UTF8, "application/json"),
        });
        var (albums, error) = await Frame(fake).Frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(["Fine", "Odd"], albums!.Select(a => a.Name).ToArray());
        Assert.Equal(0, albums!.Single(a => a.Name == "Odd").Count); // the count is only a label: an odd value reads as 0
        Assert.Equal(7, albums!.Single(a => a.Name == "Fine").Count);
    }

    [Fact]
    public async Task TheSavedKeyStandsInOnlyForTheSavedAddress()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        Assert.Null((await frame.ImmichAlbums(Server, "", CancellationToken.None)).Error);
        var (albums, error) = await frame.ImmichAlbums("http://elsewhere.test:2283", "", CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("Enter an API key", error);
        Assert.DoesNotContain(fake.Seen, s => s.Path.Contains("elsewhere"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://immich.test/")]
    [InlineData("http://user:pass@immich.test/")]
    [InlineData("http://immich.test/?x=1")]
    [InlineData("not a url")]
    public async Task OnlyPlainHttpAddressesAreUsed(string url)
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        var (albums, error) = await frame.ImmichAlbums(url, Key, CancellationToken.None);
        Assert.Null(albums);
        Assert.NotNull(error);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task ARejectedKeyIsReportedWithoutTheKeyOrTheAddress()
    {
        var (frame, _) = Frame(new FakeImmich());
        var (albums, error) = await frame.ImmichAlbums(Server, "wrong", CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("The Immich server rejected the API key", error);
        Assert.DoesNotContain("wrong", error);
        Assert.DoesNotContain("immich.test", error);
    }

    [Fact]
    public async Task AKeyWithTooFewPermissionsNamesTheMissingOne()
    {
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("""{"message":"Missing required permission: asset.view"}""", Encoding.UTF8, "application/json") });
        var (frame, _) = Frame(fake);
        var (_, error) = await frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Equal("The Immich API key is missing the asset.view permission", error);

        var odd = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Missing required permission: <script>") });
        Assert.Equal("The Immich server rejected the API key", (await Frame(odd).Frame.ImmichAlbums(Server, Key, CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"Missing required permission: asset.view\"]")]
    [InlineData("\"Missing required permission: asset.view\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task AForbiddenBodyThatIsNotAnObjectIsAKeyErrorNotAnUnreachableServer(string body)
    {
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var (_, error) = await Frame(fake).Frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Equal("The Immich server rejected the API key", error);
        Assert.False(ImmichClient.IsTransient(error)); // a rejected key must show, not hide behind the last good list
    }

    /// <summary>A body of zeros that never says how long it is and counts what is read from it.</summary>
    private sealed class EndlessZeros(long total) : Stream
    {
        private long _read;
        public long Read_ => Interlocked.Read(ref _read);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, Math.Max(0, total - Interlocked.Read(ref _read)));
            Array.Clear(buffer, offset, n);
            Interlocked.Add(ref _read, n);
            return n;
        }
    }

    [Fact]
    public async Task ALargeForbiddenBodyIsNotReadInFull()
    {
        var body = new EndlessZeros(200L * 1024 * 1024);
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(body) });
        var (_, error) = await Frame(fake).Frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Equal("The Immich server rejected the API key", error);
        Assert.True(body.Read_ < 64 * 1024, $"read {body.Read_} bytes of a 200 MB error body");
    }

    [Fact]
    public async Task AForbiddenBodyThatSaysItIsLargeIsNotRead()
    {
        var body = new EndlessZeros(1_000_000);
        var content = new StreamContent(body);
        content.Headers.ContentLength = 1_000_000;
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content });
        var (_, error) = await Frame(fake).Frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Equal("The Immich server rejected the API key", error);
        Assert.Equal(0, body.Read_);
    }

    [Fact]
    public async Task AnAnswerOverSixteenMegabytesIsRefused()
    {
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessZeros(17L * 1024 * 1024)) });
        var (albums, error) = await Frame(fake).Frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("The Immich answer is too large", error);
    }

    [Fact]
    public void AFullPageOfAThousandAssetsStillFits()
    {
        // about 2.5 KB an asset, more than the metadata Immich sends for one
        var pad = new string('x', 2300);
        var items = string.Join(",", Enumerable.Range(0, 1000).Select(i => $$$"""{"id":"aaaaaaaa-0000-0000-0000-{{{i:D12}}}","type":"IMAGE","pad":"{{{pad}}}"}"""));
        var body = $$$"""{"assets":{"items":[{{{items}}}],"nextPage":null}}""";
        Assert.True(body.Length > 2_000_000 && body.Length < 16 * 1024 * 1024);
        var fake = new FakeImmich(r => r.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        Assert.Equal(1000, frame.List().Ids.Count);
    }

    [Fact]
    public async Task ARedirectIsAnErrorNotFollowed()
    {
        var fake = new FakeImmich(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://other.test/") } });
        var (frame, _) = Frame(fake);
        var (albums, error) = await frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("The Immich server answered 302", error);
        Assert.Single(fake.Seen);
    }

    /// <summary>A frame that talks to a real local server through the production handler (no test seam); with an address it is set up for that Immich album.</summary>
    private PhotoFrame RealFrame(string? immichUrl = null, TimeSpan? timeout = null, string key = Key)
    {
        var path = Path.Combine(_dir, "config-real.json");
        var photos = immichUrl is null ? "" : $$"""
            "photos":{"source":"immich","immichUrl":"{{immichUrl}}","immichKey":{{JsonSerializer.Serialize(key)}},"immichAlbum":"{{Album}}"},
            """;
        File.WriteAllText(path, "{" + photos + """
            "pages":[{"id":"p","title":"P","widgets":[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]}]}
            """);
        return new PhotoFrame(new ConfigStore(path), NullLogger<PhotoFrame>.Instance)
            { CacheDir = Path.Combine(_dir, "cache-real"), ImmichTimeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    /// <summary>Serves a local listener until the test ends; every request goes to <paramref name="handle"/>.</summary>
    private static void Serve(HttpListener listener, CancellationToken stop, Func<HttpListenerContext, Task> handle) =>
        _ = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var c = await listener.GetContextAsync(); _ = Task.Run(() => handle(c).ContinueWith(_ => { })); } }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });

    /// <summary>The status line and headers go out, a few body bytes follow, and then the server goes quiet.</summary>
    private static async Task StallBody(HttpListenerContext c, CancellationToken stop, int status = 200, string type = "application/json")
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = type;
        c.Response.SendChunked = true;
        await c.Response.OutputStream.WriteAsync("[{"u8.ToArray(), stop);
        await c.Response.OutputStream.FlushAsync(stop);
        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
    }

    /// <summary>An album of several pages; the second page waits until the test lets it go (or the request is cancelled).</summary>
    private sealed class PagedImmich(int pages) : HttpMessageHandler
    {
        public int SearchRequests;
        public readonly TaskCompletionSource SecondPageStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/api/search/metadata") return new HttpResponseMessage(HttpStatusCode.NotFound);
            var page = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("page").GetInt32();
            Interlocked.Increment(ref SearchRequests);
            if (page == 2) { SecondPageStarted.TrySetResult(); await Release.Task.WaitAsync(ct); }
            var next = page < pages ? $"\"{page + 1}\"" : "null";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"assets":{"items":[{"id":"aaaaaaaa-0000-0000-0000-{{{page:D12}}}","type":"IMAGE"}],"nextPage":{{{next}}}}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private void WriteConfig(string album, bool widget)
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"photos":{"source":"immich","immichUrl":"{{Server}}/","immichKey":"{{Key}}","immichAlbum":"{{album}}"},
             "pages":[{"id":"p","title":"P","widgets":{{widgets}}}]}
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(++_configEdits)); // the store notices a newer file
    }
    private int _configEdits = 10;

    private static async Task ScanToEnd(PhotoFrame frame)
    {
        var field = typeof(PhotoFrame).GetField("_scan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < 1000 && field.GetValue(frame) is Task t && !t.IsCompleted; i++) await Task.Delay(10);
        await Task.Delay(50); // the scan clears its slot just after it publishes
    }

    /// <summary>A scan stopped in the middle of its second page, with a way to see how many pages were asked for in all.</summary>
    private async Task<(PhotoFrame Frame, PagedImmich Server)> ScanInTheMiddle()
    {
        var server = new PagedImmich(pages: 5);
        var (frame, _) = Frame(server);
        _ = frame.List(); // starts the scan in the background
        await server.SecondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (frame, server);
    }

    [Fact]
    public async Task AScanStopsRequestingPagesWhenTheWidgetIsRemoved()
    {
        var (frame, server) = await ScanInTheMiddle();
        WriteConfig(Album, widget: false);
        frame.SettingsSaved();
        server.Release.TrySetResult();
        await ScanToEnd(frame);
        Assert.Equal(2, Volatile.Read(ref server.SearchRequests)); // before the fix the scan went on to pages 3 to 5
    }

    [Fact]
    public async Task AScanStopsRequestingPagesWhenTheAlbumChanges()
    {
        var (frame, server) = await ScanInTheMiddle();
        WriteConfig(Other, widget: true);
        frame.SettingsSaved();
        server.Release.TrySetResult();
        await ScanToEnd(frame);
        Assert.Equal(2, Volatile.Read(ref server.SearchRequests));
        Assert.Empty(frame.List().Ids); // nothing of the old album was published (the new one is only now starting)
    }

    [Fact]
    public async Task AScanStopsRequestingPagesWhenAListIsAskedForWhileNoPageShowsTheWidget()
    {
        var (frame, server) = await ScanInTheMiddle();
        WriteConfig(Album, widget: false); // the settings were edited on disk; the next request from the phone notices
        Assert.Equal("Add a Photo frame widget to a page to show photos", frame.List().Error);
        server.Release.TrySetResult();
        await ScanToEnd(frame);
        Assert.Equal(2, Volatile.Read(ref server.SearchRequests));
    }

    [Fact]
    public async Task SavingTheSameSettingsLetsTheScanFinish()
    {
        var (frame, server) = await ScanInTheMiddle();
        WriteConfig(Album, widget: true); // same source, same album
        frame.SettingsSaved();
        server.Release.TrySetResult();
        await ScanToEnd(frame);
        Assert.Equal(5, Volatile.Read(ref server.SearchRequests));
        Assert.Equal(5, frame.List().Ids.Count);
    }

    [Fact]
    public async Task AServerThatStallsAfterTheHeadersDoesNotHangTheAlbumList()
    {
        using var stop = new CancellationTokenSource();
        using var server = TestPorts.Listen();
        try
        {
            Serve(server, stop.Token, c => StallBody(c, stop.Token));
            var (albums, error) = await RealFrame(timeout: TimeSpan.FromSeconds(1))
                .ImmichAlbums(server.Prefixes.Single(), Key, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(albums);
            Assert.Equal("The Immich server didn't answer in time", error);
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public async Task AServerThatStallsAfterTheHeadersDoesNotKeepAScanRunning()
    {
        using var stop = new CancellationTokenSource();
        using var server = TestPorts.Listen();
        try
        {
            Serve(server, stop.Token, c => StallBody(c, stop.Token));
            var frame = RealFrame(server.Prefixes.Single().TrimEnd('/'), TimeSpan.FromSeconds(1));
            await Task.Run(frame.ScanNow).WaitAsync(TimeSpan.FromSeconds(10)); // before the fix this waits for the 30 s cap and publishes nothing
            var (ids, error, scanning) = frame.List();
            Assert.Empty(ids);
            Assert.Equal("The Immich server didn't answer in time", error);
            Assert.False(scanning);
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public async Task APreviewThatStallsAfterTheHeadersDoesNotHangThePhoto()
    {
        using var stop = new CancellationTokenSource();
        using var server = TestPorts.Listen();
        try
        {
            Serve(server, stop.Token, async c =>
            {
                if (c.Request.HttpMethod == "POST")
                {
                    var body = Encoding.UTF8.GetBytes($$$"""{"assets":{"items":[{"id":"{{{A1}}}","type":"IMAGE"}],"nextPage":null}}""");
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
                else await StallBody(c, stop.Token, type: "image/webp");
            });
            // 3 s, not 1: the same limit covers the album read, which a busy macOS runner answered in over 1 s (empty list)
            var frame = RealFrame(server.Prefixes.Single().TrimEnd('/'), TimeSpan.FromSeconds(3));
            frame.ScanNow();
            var id = Assert.Single(frame.List().Ids);
            var photo = await frame.Get(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(photo.Bytes);
            Assert.Equal("That photo can't be shown", photo.Error);
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public void TheProductionHandlerDoesNotFollowRedirectsAndRecyclesConnections()
    {
        using var handler = PhotoFrame.NewImmichHandler();
        Assert.False(handler.AllowAutoRedirect);
        // pooled connections are dropped now and then, so a changed address (a new DNS answer) is picked up
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
    }

    [Fact]
    public async Task ARedirectIsNotFollowedByTheProductionHandler()
    {
        using var target = TestPorts.Listen();
        using var first = TestPorts.Listen();
        var targetHits = 0;
        _ = Task.Run(async () =>
        {
            try { while (true) { var c = await target.GetContextAsync(); Interlocked.Increment(ref targetHits); c.Response.StatusCode = 200; c.Response.Close(); } }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                var c = await first.GetContextAsync();
                c.Response.StatusCode = 302;
                c.Response.RedirectLocation = target.Prefixes.Single() + "api/albums";
                c.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });
        var (albums, error) = await RealFrame().ImmichAlbums(first.Prefixes.Single(), Key, CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("The Immich server answered 302", error);
        Assert.Equal(0, Volatile.Read(ref targetHits)); // the key was never sent to the other address
    }

    [Fact]
    public async Task AnUnreachableServerIsReportedWithoutItsAddress()
    {
        var (frame, _) = Frame(new FakeImmich(_ => throw new HttpRequestException("connect to 10.9.8.7:2283 failed")));
        var (_, error) = await frame.ImmichAlbums(Server, Key, CancellationToken.None);
        Assert.Equal("The Immich server can't be reached", error);
    }

    [Fact]
    public void TheListHoldsStillImagesAsKeyedIds()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        var (ids, error, scanning) = frame.List();
        Assert.Null(error);
        Assert.False(scanning);
        Assert.Equal(2, ids.Count); // the video, the trashed image and the repeated id are left out
        Assert.All(ids, id => Assert.Equal(16, id.Length));
        Assert.DoesNotContain(ids, id => id.Contains("aaaaaaaa"));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(2, fake.Searches.Count); // both pages were read
        Assert.All(fake.Searches, q => Assert.Contains("\"type\":\"IMAGE\"", q));
        Assert.Contains(fake.Seen, s => s.Path == "/api/search/metadata" && s.Key == Key);
    }

    /// <summary>Lists again after the five minutes are up and waits for the background scan that starts.</summary>
    private static void RescanAfterTheInterval(PhotoFrame frame, ref long clock)
    {
        Interlocked.Add(ref clock, 6 * 60 * 1000);
        _ = frame.List();
        var field = typeof(PhotoFrame).GetField("_scan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < 500 && field.GetValue(frame) is Task t && !t.IsCompleted; i++) Thread.Sleep(10);
        Thread.Sleep(50); // the scan clears its slot just after it publishes
    }

    [Theory]
    [InlineData("down", true)]
    [InlineData("503", true)]
    [InlineData("502", true)]
    [InlineData("401", false)]
    [InlineData("403", false)]
    [InlineData("400", false)]
    public void AScanThatFailsForTheNetworkKeepsTheLastGoodList(string failure, bool keep)
    {
        long clock = 1_000;
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake, now: () => Interlocked.Read(ref clock));
        frame.ScanNow();
        Assert.Equal(2, frame.List().Ids.Count);

        if (failure == "down") fake.Down = new HttpRequestException("connection refused");
        else fake.Answer = (HttpStatusCode)int.Parse(failure);
        RescanAfterTheInterval(frame, ref clock);

        var (ids, error, _) = frame.List();
        if (keep) { Assert.Equal(2, ids.Count); Assert.Null(error); }
        else { Assert.Empty(ids); Assert.NotNull(error); } // a rejected key or a missing album is for the user to fix, so it shows
    }

    [Fact]
    public void ARecoveredServerReplacesTheKeptList()
    {
        long clock = 1_000;
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake, now: () => Interlocked.Read(ref clock));
        frame.ScanNow();
        fake.Down = new HttpRequestException("connection refused");
        RescanAfterTheInterval(frame, ref clock);
        Assert.Equal(2, frame.List().Ids.Count);
        fake.Down = null;
        RescanAfterTheInterval(frame, ref clock);
        Assert.Equal(2, frame.List().Ids.Count);
        Assert.Contains(fake.Seen, s => s.Path == "/api/search/metadata");
    }

    [Fact]
    public async Task APhotoIsServedByIdAndAnUnknownIdIsNot()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        var ids = frame.List().Ids;
        var results = new List<(byte[]? Bytes, string? Type, string? Error)>();
        foreach (var id in ids) results.Add(await frame.Get(id, CancellationToken.None));
        var good = results.Single(r => r.Bytes is not null);
        Assert.Equal(Jpeg, good.Bytes);
        Assert.Equal("image/jpeg", good.Type);
        // the other asset answered something that is not an image
        Assert.Equal("That photo can't be shown", results.Single(r => r.Bytes is null).Error);
        Assert.Equal("Unknown photo", (await frame.Get(A1, CancellationToken.None)).Error); // an asset id is not a photo id
        Assert.Contains(fake.Seen, s => s.Path.EndsWith("/thumbnail?size=preview") && s.Key == Key);
    }

    private static byte[] PngOf(int w, int h) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
         (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0];

    [Theory]
    [InlineData(20000, 20000, false)] // far too many pixels for an old phone
    [InlineData(9000, 100, false)]    // over 8192 px a side
    [InlineData(4097, 4097, false)]   // one pixel over the area folder photos are held to
    [InlineData(4096, 4096, true)]
    [InlineData(2048, 1152, true)]
    public async Task APreviewIsHeldToTheSameSizeLimitsAsAFolderPhoto(int w, int h, bool served)
    {
        var picture = PngOf(w, h);
        var fake = new FakeImmich(r => r.RequestUri!.AbsolutePath.EndsWith("/thumbnail")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) }
            : r.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""{"assets":{"items":[{"id":"{{{A1}}}","type":"IMAGE"}],"nextPage":null}}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        var photo = await frame.Get(Assert.Single(frame.List().Ids), CancellationToken.None);
        if (served) { Assert.Equal(picture, photo.Bytes); Assert.Equal("image/png", photo.Type); }
        else { Assert.Null(photo.Bytes); Assert.Equal("That photo is too large to show", photo.Error); }
    }

    /// <summary>A JPEG with <paramref name="padding"/> bytes of APP1 data in front of its frame header (as a large EXIF block or colour profile would be).</summary>
    private static byte[] JpegWith(int padding, int w, int h, bool frame = true)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        for (var left = padding; left > 0;)
        {
            var n = Math.Min(left, 60_000);
            ms.Write([0xFF, 0xE1, (byte)((n + 2) >> 8), (byte)(n + 2)]);
            ms.Write(new byte[n]);
            left -= n;
        }
        if (frame) ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private async Task<(byte[]? Bytes, string? Type, string? Error)> ServePreview(byte[] picture)
    {
        var fake = new FakeImmich(r => r.RequestUri!.AbsolutePath.EndsWith("/thumbnail")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) }
            : r.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""{"assets":{"items":[{"id":"{{{A1}}}","type":"IMAGE"}],"nextPage":null}}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        return await frame.Get(Assert.Single(frame.List().Ids), CancellationToken.None);
    }

    [Fact]
    public async Task APreviewWhoseSizeIsPastTheFirstQuarterMegabyteIsStillMeasured()
    {
        var picture = JpegWith(padding: 300_000, w: 20000, h: 20000); // the frame header comes after 300 KB of metadata
        Assert.True(picture.Length > 256 * 1024);
        var photo = await ServePreview(picture);
        Assert.Null(photo.Bytes);
        Assert.Equal("That photo is too large to show", photo.Error);
    }

    [Fact]
    public async Task APreviewWithNoReadableSizeIsRefused()
    {
        var photo = await ServePreview(JpegWith(padding: 1000, w: 0, h: 0, frame: false));
        Assert.Null(photo.Bytes);
        Assert.Equal("That photo can't be shown", photo.Error);
    }

    [Fact]
    public async Task APreviewWithALargeMetadataBlockAndAnAcceptableSizeIsServed()
    {
        var picture = JpegWith(padding: 300_000, w: 2048, h: 1152);
        var photo = await ServePreview(picture);
        Assert.Equal(picture, photo.Bytes);
        Assert.Equal("image/jpeg", photo.Type);
    }

    [Fact]
    public async Task NothingIsFetchedWhileNoEnabledPageShowsTheWidget()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake, widget: false);
        Assert.Equal("Add a Photo frame widget to a page to show photos", frame.List().Error);
        Assert.Equal("Add a Photo frame widget to a page to show photos", (await frame.Get(A1, CancellationToken.None)).Error);
        Assert.Empty(fake.Seen);
    }

    [Theory]
    [InlineData(400)] // what the test server answers; an Immich that doesn't take the album filter may answer this too
    [InlineData(404)]
    public void ARejectedAlbumSearchShowsANeutralMessageNotAnAddress(int status)
    {
        var fake = new FakeImmich { Answer = (HttpStatusCode)status };
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        var (ids, error, _) = frame.List();
        Assert.Empty(ids);
        Assert.Equal("Immich couldn't read that album", error); // it doesn't claim the album was deleted
        Assert.DoesNotContain("immich.test", error);
    }

    [Theory]
    [InlineData("", Album, Key)]
    [InlineData("immich", "", Key)]
    [InlineData("immich", Album, "")]
    [InlineData("immich", Album, Key, "")]
    [InlineData("immich", Album, Key, "ftp://immich.test/")]
    public void AnIncompleteImmichSetupShowsNothing(string source, string album, string key, string? url = null)
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake, album: album, key: key, source: source == "" ? "folder" : source, url: url ?? Server + "/");
        Assert.Null(Record.Exception(frame.ScanNow)); // nothing to scan, nothing thrown
        var (ids, error, scanning) = frame.List();
        Assert.Empty(ids);
        Assert.Null(error);
        Assert.False(scanning);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public void ChangingTheAlbumDropsTheOldList()
    {
        var fake = new FakeImmich();
        var (frame, store) = Frame(fake);
        frame.ScanNow();
        Assert.Equal(2, frame.List().Ids.Count);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(Album, Other));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        frame.SettingsSaved();
        Assert.Empty(frame.List().Ids); // the new album is being scanned; nothing of the old one is shown
        Assert.Equal(Other, store.Get().Photos.ImmichAlbum);
    }

    [Fact]
    public void TheAddressAndKeyNeverReachThePhone()
    {
        var (_, store) = Frame(new FakeImmich());
        var json = JsonSerializer.Serialize(store.Get().ToPublic(), ConfigStore.WriteOpts);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain("immich.test", json);
        Assert.DoesNotContain("immich", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ab\ncd")]
    [InlineData("ab\0cd")]
    [InlineData("ab\r\nX-Other: 1")]
    public async Task AKeyWithControlCharactersIsAnErrorNotAnException(string badKey)
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake);
        var (albums, error) = await frame.ImmichAlbums(Server, badKey, CancellationToken.None);
        Assert.Null(albums);
        Assert.Equal("The Immich API key has characters that are not allowed", error);
        Assert.Empty(fake.Seen); // nothing was sent
    }

    [Fact]
    public void ControlCharactersAreRemovedFromTheSavedKey()
    {
        var path = Path.Combine(_dir, "ctl.json");
        File.WriteAllText(path, """{"photos":{"source":"immich","immichUrl":"http://immich.test:2283","immichKey":"  ab\u0000c\nd\r  ","immichAlbum":"11111111-1111-1111-1111-111111111111"}}""");
        var store = new ConfigStore(path);
        Assert.Equal("abcd", store.Get().Photos.ImmichKey);
        // and a frame set up with it neither throws nor sends a malformed header
        var fake = new FakeImmich();
        var frame = new PhotoFrame(store, NullLogger<PhotoFrame>.Instance) { ImmichHandler = fake, CacheDir = Path.Combine(_dir, "cache-ctl") };
        Assert.Null(Record.Exception(frame.ScanNow));
        Assert.Null(Record.Exception(() => frame.List()));
    }

    [Theory]
    [InlineData("IMMICH", "immich")]
    [InlineData("  immich ", "immich")]
    [InlineData("dropbox", "folder")]
    [InlineData(null, "folder")]
    public void TheSourceSettingIsNormalized(string? source, string expected)
    {
        var path = Path.Combine(_dir, "norm.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { photos = new { source, immichUrl = "  " + Server + "  ", immichKey = new string('k', 400), immichAlbum = Album } }));
        var photos = new ConfigStore(path).Get().Photos;
        Assert.Equal(expected, photos.Source);
        Assert.Equal(Server, photos.ImmichUrl);
        Assert.Equal(256, photos.ImmichKey.Length);
        Assert.Equal("", new ConfigStore(Path.Combine(_dir, "none.json")).Get().Photos.ImmichKey);
    }
}
