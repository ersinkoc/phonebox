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

    private const string StampedTemplate = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":600,"kMRMediaRemoteNowPlayingInfoElapsedTime":__ELAPSED__,"kMRMediaRemoteNowPlayingInfoPlaybackRate":__RATE__,"kMRMediaRemoteNowPlayingInfoTimestamp":__STAMP__}""";

    private static string Stamped(double elapsed, int rate, string stamp) =>
        StampedTemplate.Replace("__ELAPSED__", elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__RATE__", rate.ToString()).Replace("__STAMP__", stamp);

    [Fact]
    public void PositionIsExtrapolatedFromWhenTheSourcePublishedIt()
    {
        // a browser published 0:00 when playback began and never refreshed it: the phone must show 0:15, not 0:00
        var now = DateTimeOffset.UtcNow;
        var published = now.AddSeconds(-15);
        var iso = "\"" + published.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        var epoch = (published.ToUnixTimeMilliseconds() / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var stamp in new[] { iso, epoch })
        {
            var pos = new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(0, 1, stamp), now).Item1!.PosMs;
            Assert.InRange(pos, 14000, 16000); // the ISO form is whole seconds
        }
    }

    [Fact]
    public void ThePublishedPositionIsKeptWhilePausedOrWhenTheTimestampCannotBeTrusted()
    {
        var now = DateTimeOffset.UtcNow;
        var old = "\"" + now.AddSeconds(-15).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 0, old), now).Item1!.PosMs);
        // a timestamp in the future, or one that is not a date at all, is ignored (the position then advances from the sample)
        var future = "\"" + now.AddMinutes(10).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, future), now).Item1!.PosMs);
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, "\"soon\""), now).Item1!.PosMs);
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, "12"), now).Item1!.PosMs);
    }

    [Fact]
    public void ASampleReportsItsAgeToThePhoneWithoutExposingTheClockReading()
    {
        var fresh = new MediaSnapshot("App", "T", "A", "", true, 5000, 60000, 0, true);
        Assert.Equal(0, fresh.AgeMs); // radio and other undated snapshots
        var aged = fresh with { SampledAt = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 3 / 2 };
        Assert.InRange(aged.AgeMs, 1500, 1700);
        var json = System.Text.Json.JsonSerializer.Serialize(aged, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"ageMs\":", json);
        Assert.DoesNotContain("sampledAt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpotifyAndMusicAnchorOnThePlayersOwnPositionWhenMacOsReportsNone()
    {
        // macOS 26: Spotify reports ElapsedTime 0 for the whole track and no Timestamp, so counting from the first sample
        // would show 0:00 while it is really at 0:15. The player knows; ask it now and then and count in between.
        var asked = new List<string>();
        long answer = 15_000;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = bundle => { asked.Add(bundle); return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":200,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(15_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(17_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // counted, not asked again
        Assert.Single(asked);
        answer = 31_000; // a seek, noticed at the next check
        Assert.Equal(31_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(5)).Item1!.PosMs);
        Assert.Equal(2, asked.Count);
        // pausing is noticed at once and the position holds
        var paused = monitor.ReadMacSystemMedia(json.Replace("PlaybackRate\":1", "PlaybackRate\":0"), t0.AddSeconds(6)).Item1!;
        Assert.False(paused.Playing);
        Assert.Equal(31_000, paused.PosMs);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public void AfterASeekThePlayerIsAskedAgainAtOnceNotAtTheNextCheck()
    {
        long answer = 20_000;
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":300,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(20_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(21_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(1)).Item1!.PosMs);
        Assert.Equal(1, asked);

        answer = 150_000; // the phone seeked to 2:30
        monitor.ForgetPlayerPosition();
        Assert.Equal(150_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // not 22 s for the next 5 s
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ATrackThatComesBackAfterAGapIsAskedAboutAgainAtOnce()
    {
        // the now-playing info can go empty for a moment; when the same track returns its position starts over from the raw
        // ElapsedTime (0 for Spotify), and the player must be asked then, not at the next five-second check
        long answer = 40_000;
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":300,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(40_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Null(monitor.ReadMacSystemMedia("{}", t0.AddSeconds(1)).Item1); // nothing playing for a moment

        answer = 44_000;
        Assert.Equal(44_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // not 0
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ThePositionIsDatedWhenItIsComputedNotAfterTheArtworkIsDecoded()
    {
        // the artwork of a TIFF is converted with sips (seconds, at worst) after the position is known; the sample's age
        // is counted from the position, so the clock reading is taken there
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        monitor.ReadMacSystemMedia(json);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.InRange(monitor.LastSampleTicks, before, after);
    }

    [Fact]
    public void WhenThePlayerCannotBeAskedThePositionStillAdvancesAndOtherPlayersAreNeverAsked()
    {
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return null; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":200,"kMRMediaRemoteNowPlayingInfoElapsedTime":4,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(4000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(7000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(3)).Item1!.PosMs);
        Assert.Equal(1, asked); // a refusal is not retried every second

        var other = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return 99_000; } };
        Assert.Equal(4000, other.ReadMacSystemMedia(json.Replace("com.spotify.client", "com.google.Chrome"), t0).Item1!.PosMs);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ExtrapolatedPositionNeverPassesTheDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var iso = "\"" + now.AddMinutes(-30).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(600000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(5, 1, iso), now).Item1!.PosMs);
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
        Assert.Equal(expected, new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null }.ReadMacSystemMedia(json).Item1!.App);
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
            var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null }; // not the Spotify that may be playing here
            var loadArt = typeof(MediaMonitor).GetMethod("LoadArt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<AlbumArt?> InvokeLoadArt(string url) => await (Task<AlbumArt?>)loadArt.Invoke(monitor, [url])!;

            var validPng = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(validPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loaded = await InvokeLoadArt(new Uri(validPng).AbsoluteUri);
            Assert.NotNull(loaded);
            Assert.Equal("image/png", loaded.Mime);

            // Chromium / Brave on Linux write the art to a temp file whose random suffix looks like an extension
            var chromiumPng = Path.Combine(dir, ".org.chromium.Chromium.Wa6XAo");
            File.WriteAllBytes(chromiumPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loadedChromium = await InvokeLoadArt(new Uri(chromiumPng).AbsoluteUri);
            Assert.NotNull(loadedChromium);
            Assert.Equal("image/png", loadedChromium.Mime);

            var chromiumText = Path.Combine(dir, ".org.chromium.Chromium.tmp123");
            File.WriteAllText(chromiumText, "plain text not an image");
            Assert.Null(await InvokeLoadArt(new Uri(chromiumText).AbsoluteUri));

            // any other name without an image extension stays refused, even with image bytes
            var extensionlessPng = Path.Combine(dir, "extensionless-png");
            File.WriteAllBytes(extensionlessPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            Assert.Null(await InvokeLoadArt(new Uri(extensionlessPng).AbsoluteUri));

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
