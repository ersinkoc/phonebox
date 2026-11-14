using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioCardViewsTests
{
    [Fact]
    public async Task RadioLogoCacheResolvesStationsCachesRasterAndFallsBackToMonogramSvg()
    {
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://127.0.0.1:8765/api/state")));
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://192.168.1.10/logo.png")));
        Assert.False(RadioLogoCache.IsSafePublicUri(new Uri("http://localhost/logo.png")));
        Assert.True(RadioLogoCache.IsSafePublicUri(new Uri("https://cdn.radyofenomen.com/logo.png")));
        Assert.True(Lan.AllowedOverLan("/api/controls/radio-logo"));

        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-" + Guid.NewGuid().ToString("N"));
        var fakePng = new byte[48];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fakePng, 0);
        var hits = 0;

        try
        {
            using var http = new HttpClient(new FakeHandler(req =>
            {
                Interlocked.Increment(ref hits);
                if (req.RequestUri!.AbsoluteUri == "https://cdn.example.com/logo.png")
                    return BytesResponse(req, fakePng, "image/png");
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };
            }));
            using var cache = new RadioLogoCache(cacheDir, http);

            // Offline by default + no Icon -> zero network calls, cached monogram SVG fallback
            var offlineStation = new DeckAction { Id = "r0", Label = "PowerTürk", Kind = "radio", Target = "https://example.com/powerturk.mp3" };
            var offlineLogo = await cache.GetAsync(offlineStation, allowOnlineLookup: false);
            Assert.Equal(0, hits);
            Assert.Equal("image/svg+xml", offlineLogo.Mime);
            Assert.True(offlineLogo.IsFallback);

            // Explicit Icon is downloaded (still SSRF-guarded) and cached even when allowOnlineLookup is false
            var explicitIcon = new DeckAction
            {
                Id = "r1",
                Label = "Radyo Fenomen",
                Kind = "radio",
                Target = "https://live.radyofenomen.com/fenomen/128/icecast.audio",
                Icon = "https://cdn.example.com/logo.png",
            };
            var first = await cache.GetAsync(explicitIcon, allowOnlineLookup: false);
            var second = await cache.GetAsync(explicitIcon, allowOnlineLookup: false);
            Assert.Equal("image/png", first.Mime);
            Assert.False(first.IsFallback);
            Assert.Equal(fakePng, first.Bytes);
            Assert.Equal(1, hits);
            Assert.Equal(first.Bytes, second.Bytes);

            // Https -> http downgrade on redirect is rejected and falls back to monogram SVG
            var downgradeAction = new DeckAction
            {
                Id = "r-downgrade",
                Label = "Downgrade FM",
                Kind = "radio",
                Target = "https://example.com/stream.mp3",
                Icon = "https://cdn.example.com/redirect-downgrade.png",
            };
            using var downgradeHttp = new HttpClient(new FakeHandler(req =>
            {
                if (req.RequestUri!.AbsoluteUri == "https://cdn.example.com/redirect-downgrade.png")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = req };
                    redirect.Headers.Location = new Uri("http://cdn.example.com/insecure.png");
                    return redirect;
                }
                Assert.Fail("Unexpected redirect hop to HTTP");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using var downgradeCache = new RadioLogoCache(cacheDir + "-dg", downgradeHttp);
            var downgradeLogo = await downgradeCache.GetAsync(downgradeAction, allowOnlineLookup: false);
            Assert.Equal("image/svg+xml", downgradeLogo.Mime);
            Assert.True(downgradeLogo.IsFallback);

            // Unicode monogram fallback without replacement characters
            var unknown = new DeckAction { Id = "r2", Label = "📻 Jazz", Kind = "radio", Target = "https://example.com/live.mp3" };
            var fallback = await cache.GetAsync(unknown, allowOnlineLookup: false);
            Assert.Equal("image/svg+xml", fallback.Mime);
            Assert.True(fallback.IsFallback);
            var svgText = Encoding.UTF8.GetString(fallback.Bytes);
            Assert.Contains("<svg", svgText);
            Assert.DoesNotContain('\uFFFD', svgText);

            // Editing Icon / Label changes CacheKey rev
            var editedIcon = new DeckAction
            {
                Id = "r1",
                Label = explicitIcon.Label,
                Kind = explicitIcon.Kind,
                Target = explicitIcon.Target,
                Icon = "https://cdn.example.com/logo-v2.png",
            };
            Assert.NotEqual(RadioLogoCache.CacheKey(explicitIcon, false), RadioLogoCache.CacheKey(editedIcon, false));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
            if (Directory.Exists(cacheDir + "-dg")) Directory.Delete(cacheDir + "-dg", recursive: true);
        }
    }

    [Fact]
    public async Task RadioLogoCacheFallsBackToMonogramWhenBudgetTimeoutExpires()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-timeout-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new AsyncFakeHandler(async (req, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req };
            }));
            using var cache = new RadioLogoCache(cacheDir, http);
            var action = new DeckAction
            {
                Id = "r-slow",
                Label = "Slow Radio",
                Kind = "radio",
                Target = "https://example.com/slow.mp3",
                Icon = "https://cdn.example.com/hanging-logo.png",
            };

            var logo = await cache.GetAsync(action, allowOnlineLookup: false, CancellationToken.None);
            Assert.Equal("image/svg+xml", logo.Mime);
            Assert.True(logo.IsFallback);
            Assert.Contains("<svg", Encoding.UTF8.GetString(logo.Bytes));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void IsPublicIpAddress_ClassifiesAddressesCorrectly(string raw, bool expected)
    {
        Assert.Equal(expected, RadioLogoCache.IsPublicIpAddress(IPAddress.Parse(raw)));
    }

    [Fact]
    public async Task TapToToggleStartsAndStopsRadioWithoutSeparateStopAction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Jazz FM", "kind": "radio", "target": "https://example.com/jazz.mp3", "value": 80 },
                      { "id": "r-2", "label": "Classic FM", "kind": "radio", "target": "https://example.com/classic.mp3", "value": 75 }
                    ]
                  }
                }
                """);

            var native = new StatefulRadioNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);

            Assert.True((await deck.Act("action", "r-1", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            // Empty op (e.g. from ApplyProfile) keeps playing instead of toggling off
            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-2"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RadioLogoCacheEvictsOldestMemoryEntriesWithoutClearingWholeCache()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-evict-" + Guid.NewGuid().ToString("N"));
        var fakePng = new byte[48];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fakePng, 0);
        var hits = 0;

        try
        {
            using var http = new HttpClient(new FakeHandler(req =>
            {
                Interlocked.Increment(ref hits);
                return BytesResponse(req, fakePng, "image/png");
            }));
            using var cache = new RadioLogoCache(cacheDir, http);

            DeckAction Station(int i) => new()
            {
                Id = $"r-{i}",
                Label = $"Station {i}",
                Kind = "radio",
                Target = $"https://example.com/stream-{i}.mp3",
                Icon = $"https://cdn.example.com/logo-{i}.png",
            };

            for (var i = 0; i < 130; i++)
                await cache.GetAsync(Station(i), allowOnlineLookup: false);

            Assert.Equal(130, hits);

            // Delete disk cache files so only in-memory hits avoid HTTP calls
            foreach (var file in Directory.EnumerateFiles(cacheDir))
                File.Delete(file);

            // Newer entry stays in memory; oldest entry was evicted
            await cache.GetAsync(Station(129), allowOnlineLookup: false);
            Assert.Equal(130, hits);

            await cache.GetAsync(Station(0), allowOnlineLookup: false);
            Assert.Equal(131, hits);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task RadioLogoCachePrunesExpiredDiskEntriesOnWrite()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        var fakePng = new byte[48];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fakePng, 0);

        try
        {
            var expiredBin = Path.Combine(cacheDir, "expiredkey.bin");
            var expiredMime = Path.Combine(cacheDir, "expiredkey.mime");
            File.WriteAllBytes(expiredBin, fakePng);
            File.WriteAllText(expiredMime, "image/png");
            File.SetLastWriteTimeUtc(expiredBin, DateTime.UtcNow.AddDays(-15));
            File.SetLastWriteTimeUtc(expiredMime, DateTime.UtcNow.AddDays(-15));

            using var http = new HttpClient(new FakeHandler(req => BytesResponse(req, fakePng, "image/png")));
            using var cache = new RadioLogoCache(cacheDir, http);

            var fresh = new DeckAction
            {
                Id = "r-fresh",
                Label = "Fresh FM",
                Kind = "radio",
                Target = "https://example.com/fresh.mp3",
                Icon = "https://cdn.example.com/fresh.png",
            };
            var logo = await cache.GetAsync(fresh, allowOnlineLookup: false);
            Assert.False(logo.IsFallback);

            Assert.False(File.Exists(expiredBin));
            Assert.False(File.Exists(expiredMime));
            var freshKey = RadioLogoCache.CacheKey(fresh, false);
            Assert.True(File.Exists(Path.Combine(cacheDir, freshKey + ".bin")));
            Assert.True(File.Exists(Path.Combine(cacheDir, freshKey + ".mime")));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task PunctuationOnlyStationLabelSkipsDirectoryDownloadsAndReturnsMonogram()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "phonebox-logos-punct-" + Guid.NewGuid().ToString("N"));
        var fakePng = new byte[48];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(fakePng, 0);
        var hits = 0;

        try
        {
            using var http = new HttpClient(new FakeHandler(req =>
            {
                Interlocked.Increment(ref hits);
                var uri = req.RequestUri!.AbsoluteUri;
                if (uri.StartsWith("https://de1.api.radio-browser.info/", StringComparison.Ordinal))
                    return BytesResponse(req, Encoding.UTF8.GetBytes("""[{"name":"Any Station","favicon":"https://cdn.example.com/rb.png"}]"""), "application/json");
                if (uri.StartsWith("https://opml.radiotime.com/", StringComparison.Ordinal))
                    return BytesResponse(req, Encoding.UTF8.GetBytes("""{"body":[{"text":"Any Station","image":"https://cdn.example.com/tunein.png"}]}"""), "application/json");
                return BytesResponse(req, fakePng, "image/png");
            }));
            using var cache = new RadioLogoCache(cacheDir, http);

            var punctStation = new DeckAction
            {
                Id = "r-punct",
                Label = "!!📻!!",
                Kind = "radio",
                Target = "https://example.com/s.mp3",
            };
            var logo = await cache.GetAsync(punctStation, allowOnlineLookup: true);
            Assert.Equal(0, hits);
            Assert.True(logo.IsFallback);
            Assert.Equal("image/svg+xml", logo.Mime);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static HttpResponseMessage BytesResponse(HttpRequestMessage req, byte[] body, string mediaType)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new ByteArrayContent(body) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return res;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncFakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class StatefulRadioNative : IDeckNative
    {
        public readonly HashSet<string> PlayingRadios = [];
        public DeckAudio Audio() => new(true, null, [], []);
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => true;
        public bool Session(string id, int? volume, bool? muted) => true;
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public string? ActionState(DeckAction action) => action.Kind is "sound" or "radio"
            ? (PlayingRadios.Contains(action.Id) ? "Playing" : "Stopped")
            : null;
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(true);
        public void Play(string id, string file, string? device, int volume) => PlayingRadios.Add(id);
        public void StopSound(string? id)
        {
            if (id is null) PlayingRadios.Clear();
            else PlayingRadios.Remove(id);
        }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() => PlayingRadios.Clear();
    }
}
