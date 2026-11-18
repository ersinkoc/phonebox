using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class MacMediaTests
{
    [Fact]
    public void SystemMetadataAdvancesFromTheReportedPositionAndStopsWhenPaused()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(10000, monitor.ReadMacSystemMedia(json, now).Item1!.PosMs);
        Assert.Equal(12000, monitor.ReadMacSystemMedia(json, now.AddSeconds(2)).Item1!.PosMs);
        var paused = monitor.ReadMacSystemMedia(json.Replace("PlaybackRate\":1", "PlaybackRate\":0"), now.AddSeconds(4)).Item1!;
        Assert.False(paused.Playing);
        Assert.Equal(12000, paused.PosMs);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"kMRMediaRemoteNowPlayingInfoTitle\":42}")]
    [InlineData("not json")]
    public void MissingOrMalformedMetadataIsUnavailable(string json) =>
        Assert.Null(new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1);

    [Theory]
    [InlineData("com.spotify.client", "Spotify")]
    [InlineData("com.github.th-ch.youtube-music", "YouTube Music")]
    [InlineData("org.example.player", "player")]
    public void KnownPlayersAreNamedAfterTheirBundle(string bundle, string expected)
    {
        var json = $$"""{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"{{bundle}}","kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        Assert.Equal(expected, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1!.App);
    }

    [Fact]
    public void DelayedMacArtworkRefreshUpdatesArtIdAndClearsWhenMissing()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        var pngOne = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
        var pngTwo = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47, 5, 6, 7, 8]);
        const string template = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1__ART__}""";

        var withoutArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", "")).Item1!;
        Assert.Equal(0, withoutArt.ArtId);
        Assert.Null(monitor.Art);

        var firstArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngOne}}" """)).Item1!;
        Assert.Equal(1, firstArt.ArtId);
        Assert.Equal("image/png", monitor.Art?.Mime);

        var replacedArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngTwo}}" """)).Item1!;
        Assert.Equal(2, replacedArt.ArtId);

        var failedDecode = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"not-valid-base64!!!" """)).Item1!;
        Assert.Equal(0, failedDecode.ArtId);
        Assert.Null(monitor.Art);

        var recoveredArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngTwo}}" """)).Item1!;
        Assert.Equal(3, recoveredArt.ArtId);

        var clearedArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", "")).Item1!;
        Assert.Equal(0, clearedArt.ArtId);
        Assert.Null(monitor.Art);
    }

    [Fact]
    public async Task LoadArtRejectsNonImagesAndLoopbackAndSystemMediaHonorsSmallSeeks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-media-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var loopback = new TcpListener(IPAddress.Loopback, 0);
        loopback.Start();
        var port = ((IPEndPoint)loopback.LocalEndpoint).Port;

        try
        {
            var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
            var loadArt = typeof(MediaMonitor).GetMethod("LoadArt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<AlbumArt?> InvokeLoadArt(string url) => await (Task<AlbumArt?>)loadArt.Invoke(monitor, [url])!;

            var validPng = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(validPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loaded = await InvokeLoadArt(new Uri(validPng).AbsoluteUri);
            Assert.NotNull(loaded);
            Assert.Equal("image/png", loaded.Mime);

            var secretJson = Path.Combine(dir, "config.json");
            File.WriteAllText(secretJson, "{\"token\":\"secret\"}");
            Assert.Null(await InvokeLoadArt(new Uri(secretJson).AbsoluteUri));

            var disguisedPng = Path.Combine(dir, "fake.png");
            File.WriteAllText(disguisedPng, "{\"not\":\"an-image\"}");
            Assert.Null(await InvokeLoadArt(new Uri(disguisedPng).AbsoluteUri));

            // nothing accepts on the listener, so a connection that got through stays queued for Pending() to see
            Assert.Null(await InvokeLoadArt($"http://127.0.0.1:{port}/art.png").WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(loopback.Pending());

            var t0 = DateTimeOffset.UtcNow;
            const string json10 = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
            const string json102 = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10.2,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
            Assert.Equal(10000, monitor.ReadMacSystemMedia(json10, t0).Item1!.PosMs);
            Assert.Equal(40000, monitor.ReadMacSystemMedia(json10, t0.AddSeconds(30)).Item1!.PosMs);
            Assert.Equal(10200, monitor.ReadMacSystemMedia(json102, t0.AddSeconds(31)).Item1!.PosMs);
            Assert.Null(monitor.ReadMacSystemMedia("", t0.AddSeconds(32)).Item1);
            Assert.Equal(10200, monitor.ReadMacSystemMedia(json102, t0.AddSeconds(52)).Item1!.PosMs);
        }
        finally
        {
            try { loopback.Stop(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkDownloadDeadlineCoversAStalledBody()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var release = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync();
            var stream = c.GetStream();
            if (await stream.ReadAsync(new byte[4096]) == 0) return; // the request (any part of it will do)
            // the headers and the first bytes of a PNG, then nothing more until the test ends
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 4096\r\n\r\n"));
            await stream.WriteAsync(new byte[] { 0x89, 0x50, 0x4e, 0x47 });
            try { await Task.Delay(Timeout.Infinite, release.Token); } catch (OperationCanceledException) { }
        });
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var download = MediaMonitor.DownloadArt(http, $"http://127.0.0.1:{port}/art.png", TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Cancel();
            server.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
