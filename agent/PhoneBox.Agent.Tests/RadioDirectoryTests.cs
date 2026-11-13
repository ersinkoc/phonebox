using System.Net;
using System.Text;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioDirectoryTests
{
    [Fact]
    public void ParseCountriesSerializesStationCountAndDeduplicatesByIsoCode()
    {
        var countries = RadioBrowserDirectory.ParseCountries("""
            [
              { "iso_3166_1": "TR", "name": "Turkey", "stationcount": 420 },
              { "iso_3166_1": "tr", "name": "Türkiye", "stationcount": 512 },
              { "iso_3166_1": "DE", "name": "Germany", "stationcount": 3200 },
              { "iso_3166_1": "XX", "name": "Empty", "stationcount": 0 }
            ]
            """);
        Assert.Equal(2, countries.Count);
        Assert.Equal("DE", countries[0].Code);
        Assert.Equal(3200, countries[0].StationCount);
        Assert.Equal("TR", countries[1].Code);
        Assert.Equal(512, countries[1].StationCount);

        var json = JsonSerializer.Serialize(countries[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"stationCount\":512", json);
    }

    [Fact]
    public void ParseStationsDeduplicatesAndFiltersNonHttpStreams()
    {
        var stations = RadioBrowserDirectory.ParseStations("""
            [
              { "name": "  Power   FM ", "url_resolved": "https://stream.example.com/power.mp3", "favicon": "https://example.com/logo.png", "clickcount": 1200, "votes": 80 },
              { "name": "Power FM", "url_resolved": "https://stream.example.com/power-mirror.mp3", "clickcount": 900, "votes": 50 },
              { "name": "Unsafe RTSP", "url_resolved": "rtsp://stream.example.com/live", "clickcount": 500, "votes": 10 },
              { "name": "Local Stream", "url_resolved": "http://127.0.0.1/local.mp3", "clickcount": 400, "votes": 10 },
              { "name": "Fallback FM", "url_resolved": "", "url": "https://stream.example.com/fallback.mp3", "favicon": "http://192.168.1.10/logo.png", "clickcount": 350, "votes": 25 },
              { "name": "Jazz Radio", "url_resolved": "https://stream.example.com/jazz.aac", "favicon": "ftp://bad", "clickcount": 300, "votes": 20 }
            ]
            """, "TR", 10);

        Assert.Equal(3, stations.Count);
        Assert.Equal("radio-tr-power-fm", stations[0].Id);
        Assert.Equal("Power FM", stations[0].Label);
        Assert.Equal("https://example.com/logo.png", stations[0].LogoUrl);
        Assert.Equal("radio-tr-fallback-fm", stations[1].Id);
        Assert.Equal("https://stream.example.com/fallback.mp3", stations[1].StreamUrl);
        Assert.Null(stations[1].LogoUrl);
        Assert.Equal("radio-tr-jazz-radio", stations[2].Id);
        Assert.Null(stations[2].LogoUrl);
        Assert.Equal("istanbul-fm", RadioBrowserDirectory.Slugify("İstanbul FM"));
        Assert.False(RadioBrowserDirectory.IsSafePublicUri(new Uri("http://127.0.0.1:8765/api/state")));
        Assert.False(RadioBrowserDirectory.IsSafePublicUri(new Uri("http://192.168.1.50/stream.mp3")));
    }

    [Fact]
    public async Task CheckStreamsReportsLiveAndHtmlWithoutMutatingInput()
    {
        using var http = new HttpClient(new FakeHandler());
        var items = new[]
        {
            new RadioCheckItem("s1", "https://stream.example.com/live.mp3"),
            new RadioCheckItem("s2", "https://stream.example.com/page.html"),
            new RadioCheckItem("s3", "ftp://invalid"),
            new RadioCheckItem("s4", "http://127.0.0.1:8765/api/state"),
            new RadioCheckItem("s5", "https://stream.example.com/net-error.mp3"),
            new RadioCheckItem("s6", "https://stream.example.com/downgrade.mp3"),
            new RadioCheckItem("s7", "https://stream.example.com/private-redirect.mp3")
        };
        var results = await RadioBrowserDirectory.CheckStreamsAsync(items, default, http);
        Assert.Equal(7, results.Count);
        Assert.True(results[0].Ok);
        Assert.Equal("Live", results[0].Status);
        Assert.False(results[1].Ok);
        Assert.Contains("HTML", results[1].Status);
        Assert.False(results[2].Ok);
        Assert.Equal("Invalid URL", results[2].Status);
        Assert.False(results[3].Ok);
        Assert.Equal("local address, not checked", results[3].Status);
        Assert.False(results[4].Ok);
        Assert.Equal("Connection failed", results[4].Status);
        Assert.DoesNotContain("HttpRequestException", results[4].Status);
        Assert.False(results[5].Ok);
        Assert.Equal("Invalid redirect", results[5].Status);
        Assert.False(results[6].Ok);
        Assert.Equal("local address, not checked", results[6].Status);
    }

    [Fact]
    public async Task GetCountriesFallsBackAcrossMirrorsAndThrowsReadableErrorWhenAllFail()
    {
        using var fallbackHttp = new HttpClient(new MirrorFakeHandler(failAll: false));
        var countries = await RadioBrowserDirectory.GetCountriesAsync(default, fallbackHttp);
        Assert.Single(countries);
        Assert.Equal("TR", countries[0].Code);
        Assert.Equal(512, countries[0].StationCount);

        using var brokenHttp = new HttpClient(new MirrorFakeHandler(failAll: true));
        await Assert.ThrowsAsync<RadioDirectoryUnavailableException>(() =>
            RadioBrowserDirectory.GetCountriesAsync(default, brokenHttp));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/net-error.mp3"))
                throw new HttpRequestException("simulated failure");
            if (path.EndsWith("/downgrade.mp3"))
            {
                var redir = new HttpResponseMessage(HttpStatusCode.Redirect);
                redir.Headers.Location = new Uri("http://stream.example.com/plain.mp3");
                return Task.FromResult(redir);
            }
            if (path.EndsWith("/private-redirect.mp3"))
            {
                var redir = new HttpResponseMessage(HttpStatusCode.Redirect);
                redir.Headers.Location = new Uri("https://127.0.0.1/internal");
                return Task.FromResult(redir);
            }
            var isHtml = path.EndsWith(".html");
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data", Encoding.UTF8, isHtml ? "text/html" : "audio/mpeg")
            };
            return Task.FromResult(res);
        }
    }

    private sealed class MirrorFakeHandler(bool failAll) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "";
            if (!failAll && host.StartsWith("fi1."))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{ "iso_3166_1": "TR", "name": "Turkey", "stationcount": 512 }]""",
                        Encoding.UTF8,
                        "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }
    }
}
