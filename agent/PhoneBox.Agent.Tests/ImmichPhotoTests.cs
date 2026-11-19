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
        public List<string> Searches { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("x-api-key", out var v) ? v.Single() : null;
            lock (Seen) Seen.Add((request.RequestUri!.PathAndQuery, key));
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

    private (PhotoFrame Frame, ConfigStore Store) Frame(FakeImmich fake, bool widget = true, string album = Album, string key = Key, string source = "immich", string url = Server + "/")
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"photos":{"source":"{{source}}","immichUrl":"{{url}}","immichKey":"{{key}}","immichAlbum":"{{album}}","immichAlbumName":"apple"},
             "pages":[{"id":"p","title":"P","widgets":{{widgets}}}]}
            """);
        var store = new ConfigStore(path);
        return (new PhotoFrame(store, NullLogger<PhotoFrame>.Instance) { CacheDir = Path.Combine(_dir, "cache"), ImmichHandler = fake }, store);
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

    [Fact]
    public async Task NothingIsFetchedWhileNoEnabledPageShowsTheWidget()
    {
        var fake = new FakeImmich();
        var (frame, _) = Frame(fake, widget: false);
        Assert.Equal("Add a Photo frame widget to a page to show photos", frame.List().Error);
        Assert.Equal("Add a Photo frame widget to a page to show photos", (await frame.Get(A1, CancellationToken.None)).Error);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public void AMissingAlbumShowsAMessageNotAnAddress()
    {
        var fake = new FakeImmich { AlbumGone = true };
        var (frame, _) = Frame(fake);
        frame.ScanNow();
        var (ids, error, _) = frame.List();
        Assert.Empty(ids);
        Assert.Equal("That Immich album no longer exists", error);
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
