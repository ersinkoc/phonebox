using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Camera widget: the JPEG splitting, what may reach the phone or the log, and when a camera is pulled.</summary>
public sealed class CameraFeedsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-camera-{Guid.NewGuid():N}.json");
    private const string Url = "rtsp://admin:s3cret@192.168.1.20:554/stream1";

    public void Dispose() { File.Delete(_path); File.Delete(_path + ".bak"); }

    private static byte[] Jpeg(byte fill, int size = 16)
    {
        var b = new byte[size];
        b[0] = 0xFF; b[1] = 0xD8;
        for (var i = 2; i < size - 2; i++) b[i] = fill;
        b[^2] = 0xFF; b[^1] = 0xD9;
        return b;
    }

    [Fact]
    public void BackToBackJpegsAreSplitEvenAcrossReads()
    {
        var got = new List<byte[]>();
        var split = new JpegSplitter(got.Add);
        var stream = Jpeg(1).Concat(Jpeg(2, 40)).Concat(Jpeg(3)).ToArray();
        // in odd pieces, including one that ends between 0xFF and 0xD9
        foreach (var chunk in stream.Chunk(7)) split.Push(chunk);
        Assert.Equal(3, got.Count);
        Assert.Equal(Jpeg(1), got[0]);
        Assert.Equal(Jpeg(2, 40), got[1]);
        Assert.Equal(Jpeg(3), got[2]);
    }

    [Fact]
    public void StuffedBytesAndNoiseBeforeAnImageDoNotEndOrStartIt()
    {
        var got = new List<byte[]>();
        var split = new JpegSplitter(got.Add);
        // FF 00 inside the data (byte stuffing) is not an end; garbage before FF D8 is skipped
        byte[] image = [0xFF, 0xD8, 0x10, 0xFF, 0x00, 0x20, 0xFF, 0xD9];
        split.Push([0x00, 0x13, 0x37, .. image]);
        Assert.Equal(image, Assert.Single(got));
    }

    [Fact]
    public void ARunawayImageIsDroppedInsteadOfGrowingWithoutBound()
    {
        var got = new List<byte[]>();
        var split = new JpegSplitter(got.Add, maxBytes: 64);
        split.Push([0xFF, 0xD8, .. new byte[100]]); // never ends within the limit
        split.Push(Jpeg(5));
        Assert.Equal(Jpeg(5), Assert.Single(got));
    }

    [Theory]
    [InlineData("[rtsp @ 0x1] method DESCRIBE failed: 401 Unauthorized", "login")]
    [InlineData("rtsp://cam/x: Connection refused", "unreachable")]
    [InlineData("Connection to tcp://10.0.0.9:554 failed: Connection timed out", "unreachable")]
    [InlineData("rtsp://cam.local/x: Error number -138 occurred", "unreachable")]
    [InlineData("[tcp @ 0x5601a4c0f401] Connection to tcp://10.0.0.9:5401/stream401 failed: Connection refused", "unreachable")]
    [InlineData("Invalid data found when processing input", "no-picture")]
    [InlineData(null, "no-picture")]
    public void FfmpegsMessagesBecomeAFixedState(string? error, string state) => Assert.Equal(state, CameraFeeds.Classify(error));

    [Fact]
    public void TheLogNeverGetsTheAddressOrAPassword()
    {
        var line = $"[rtsp @ 0x55] {Url}: 401 Unauthorized; also rtsp://u:p@other/x and http://cam/cgi?user=a&pwd=hunter2&x=1";
        var masked = CameraFeeds.Mask(line, Url);
        Assert.DoesNotContain("s3cret", masked);
        Assert.DoesNotContain("u:p@", masked);
        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("<camera>", masked);
        // ffmpeg also names the host on its own
        var host = CameraFeeds.Mask("Connection to tcp://192.168.1.20:554 failed: Connection timed out", Url);
        Assert.DoesNotContain("192.168.1.20", host);
        Assert.Contains("Connection timed out", host);
        // a host named like a secret parameter doesn't stop the parameter from being hidden
        var tricky = CameraFeeds.Mask("rtsp://token:554/live?token=secret123: Connection refused", "rtsp://admin:pw@token:554/live?token=secret123");
        Assert.DoesNotContain("secret123", tricky);
        // user info stops at ? and #: "?token=x@" is a query, and its value is still hidden
        Assert.DoesNotContain("secret123", CameraFeeds.Mask("rtsp://cam?token=secret123@x: refused", ""));
        // a long line with the password far in is masked whole, then shortened
        var longPassword = new string('p', 500);
        var longLine = CameraFeeds.Mask($"[rtsp @ 0x1] rtsp://admin:{longPassword}@10.0.0.2/live: 401 Unauthorized", "");
        Assert.DoesNotContain("pppp", longLine);
        Assert.Contains("401 Unauthorized", masked);
    }

    [Theory]
    [InlineData("rtsp://user:pw@10.0.0.2:554/live", true)]
    [InlineData("RTSPS://cam.example/live", true)]
    [InlineData("rtsp:///nohost", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("concat:a.ts|b.ts", false)]
    [InlineData("http://cam/video.mjpg", false)]
    [InlineData("-i evil", false)]
    public void OnlyRtspAddressesAreHandedToFfmpeg(string url, bool ok) => Assert.Equal(ok, CameraFeeds.ValidUrl(url));

    private ConfigStore Store(string cameraOption = "cam000001", bool pageEnabled = true, string url = Url)
    {
        File.WriteAllText(_path, $$$"""
            {"cameras":{"sources":[{"id":"cam000001","name":"Door","url":"{{{url}}}"}]},
             "pages":[{"id":"p","title":"P","enabled":{{{(pageEnabled ? "true" : "false")}}},"widgets":[{"id":"w","type":"camera","x":0,"y":0,"w":4,"h":3,"options":{"camera":"{{{cameraOption}}}"}}]},
                      {"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":4,"h":3}]}]}
            """);
        return new ConfigStore(_path);
    }

    [Fact]
    public void TheAddressNeverGoesToThePhone()
    {
        var pub = JsonSerializer.Serialize(Store().Get().ToPublic());
        Assert.DoesNotContain("s3cret", pub);
        Assert.DoesNotContain("192.168.1.20", pub);
        Assert.DoesNotContain("Door", pub); // only the id and the frame rate, for pacing the widget
        Assert.Contains("\"cameras\":[{\"Id\":\"cam000001\",\"Fps\":5}]", pub);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(60, 5)]
    public void TheFrameRateStaysWithinItsRange(int asked, int kept)
    {
        var store = Store();
        Assert.Null(store.Save($$$"""{"cameras":{"sources":[{"id":"cam000001","name":"Door","url":"rtsp://10.0.0.3/a","fps":{{{asked}}}}]}}"""));
        Assert.Equal(kept, store.Get().Cameras.Sources[0].Fps);
    }

    [Fact]
    public void APictureAWidgetAlreadyHasIsNotSentAgain()
    {
        var (feeds, runs, _) = Make(Store());
        using var __ = feeds;
        feeds.Frame("phone1", "p/w");
        runs[0].Frame(Jpeg(1));
        Assert.Equal(200, feeds.Frame("phone1", "p/w").Status);
        Assert.Equal(204, feeds.Frame("phone1", "p/w").Status);   // nothing newer: nothing to download
        Assert.Equal(200, feeds.Frame("phone2", "p/w").Status);   // another phone hasn't had it yet
        Assert.Equal(200, feeds.Frame("phone1", "p/w", fresh: true).Status); // the widget lost its picture: send it again
        runs[0].Frame(Jpeg(2));
        var next = feeds.Frame("phone1", "p/w");
        Assert.Equal(200, next.Status);
        Assert.Equal(Jpeg(2), next.Jpeg);
    }

    [Fact]
    public void SavingRefusesAnythingButRtspAndKeepsTheCameraId()
    {
        var store = Store();
        Assert.Contains("rtsp://", store.Save("""{"cameras":{"sources":[{"id":"cam000001","name":"Door","url":"file:///etc/passwd"}]}}"""));
        Assert.Null(store.Save("""{"cameras":{"sources":[{"id":"cam000001","name":"Front door","url":"rtsp://10.0.0.3/a"},{"name":"","url":"  "},{"name":"New","url":"rtsp://10.0.0.4/b","transport":"UDP"}]}}"""));
        var sources = store.Get().Cameras.Sources;
        Assert.Equal(2, sources.Count); // the empty row is dropped
        Assert.Equal("cam000001", sources[0].Id); // renamed, same id: its widgets keep showing it
        Assert.Matches("^[a-z0-9]{12}$", sources[1].Id); // a new camera gets a random id, not one made from its name
        Assert.Equal("udp", sources[1].Transport);
        // more than the limit is refused with a message, not silently cut
        var nine = string.Join(",", Enumerable.Range(1, CameraFeeds.MaxCameras + 1).Select(i => $$"""{"name":"C{{i}}","url":"rtsp://10.0.0.{{i}}/a"}"""));
        Assert.Contains($"more than {CameraFeeds.MaxCameras} cameras", store.Save("{\"cameras\":{\"sources\":[" + nine + "]}}"));
    }

    private sealed class FakeRun : ICameraRun
    {
        public bool HasEnded { get; set; }
        public string? LastError { get; set; }
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private (CameraFeeds Feeds, List<(FakeRun Run, Action<byte[]> Frame, CameraSource Source)> Runs, Func<long, long> Advance) Make(ConfigStore store, string? ffmpeg = "ffmpeg")
    {
        var runs = new List<(FakeRun, Action<byte[]>, CameraSource)>();
        long now = 1_000_000;
        var feeds = new CameraFeeds(store, NullLogger<CameraFeeds>.Instance, (_, source, onFrame) =>
        {
            var r = new FakeRun();
            runs.Add((r, onFrame, source));
            return r;
        }, () => now, () => ffmpeg, timer: false);
        return (feeds, runs, ms => now += ms);
    }

    [Fact]
    public void AWidgetOnAnEnabledPageStartsItsCameraAndGetsTheLatestFrame()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        var first = feeds.Frame(null, "p/w");
        Assert.Equal((503, "starting"), (first.Status, first.State));
        var (run, frame, source) = Assert.Single(runs);
        Assert.Equal(Url, source.Url);
        frame(Jpeg(1));
        frame(Jpeg(2));
        var live = feeds.Frame(null, "p/w");
        Assert.Equal(200, live.Status);
        Assert.Equal(Jpeg(2), live.Jpeg);
        // asking again doesn't start a second ffmpeg
        feeds.Frame(null, "p/w");
        Assert.Single(runs);
    }

    [Theory]
    [InlineData("p/w", false, "cam000001", 404, "unknown")]  // the page is hidden
    [InlineData("p/nope", true, "cam000001", 404, "unknown")]
    [InlineData("q/c", true, "cam000001", 404, "unknown")]   // a widget, but not a camera
    [InlineData("p/w", true, "", 404, "pick")]
    [InlineData("p/w", true, "removed123", 404, "pick")]
    public void NothingIsPulledWithoutACameraWidgetOnAnEnabledPage(string key, bool pageEnabled, string option, int status, string state)
    {
        var (feeds, runs, _) = Make(Store(option, pageEnabled));
        using var __ = feeds;
        var r = feeds.Frame(null, key);
        Assert.Equal((status, state), (r.Status, r.State));
        Assert.Null(r.Jpeg);
        Assert.Empty(runs);
    }

    [Fact]
    public void ACameraStopsSoonAfterThePanelStopsAsking()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        feeds.Frame(null, "p/w");
        advance(CameraFeeds.KeepMs - 1000); feeds.Tick();
        Assert.False(runs[0].Run.Disposed);
        advance(2000); feeds.Tick();
        Assert.True(runs[0].Run.Disposed);
        // the next request starts it again
        feeds.Frame(null, "p/w");
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void AnOldFrameIsNotShownAsLiveAndAStalledFfmpegIsRestartedLater()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        feeds.Frame(null, "p/w");
        runs[0].Frame(Jpeg(1));
        advance(CameraFeeds.FreshMs + 1000);
        var stale = feeds.Frame(null, "p/w");
        Assert.Equal(503, stale.Status);
        Assert.Null(stale.Jpeg);
        // keep asking, but no frame arrives: after the stall limit ffmpeg is stopped, then started again after a pause
        for (var t = 0; t < CameraFeeds.StallMs; t += 1000) { advance(1000); feeds.Frame(null, "p/w"); feeds.Tick(); }
        Assert.True(runs[0].Run.Disposed);
        for (var t = 0; t < 3000; t += 1000) { advance(1000); feeds.Frame(null, "p/w"); feeds.Tick(); }
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void AFailedLoginIsReportedAsAStateWithoutFfmpegsText()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        feeds.Frame(null, "p/w");
        runs[0].Run.LastError = $"[rtsp @ 0x1] {Url}: method DESCRIBE failed: 401 Unauthorized";
        runs[0].Run.HasEnded = true;
        advance(1000); feeds.Tick();
        var r = feeds.Frame(null, "p/w");
        Assert.Equal((503, "login"), (r.Status, r.State));
    }

    [Fact]
    public void ACameraThatNeverAnswersIsReportedUnreachable()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        // ffmpeg waits on the connection without a word and no picture ever comes
        for (var t = 0; t <= CameraFeeds.StallMs + 1000; t += 1000) { advance(1000); feeds.Frame(null, "p/w"); feeds.Tick(); }
        Assert.True(runs[0].Run.Disposed);
        Assert.Equal("unreachable", feeds.Frame(null, "p/w").State);
    }

    [Fact]
    public void AStoppedFfmpegsLateFrameIsNotShownAndDoesNotResetTheBackoff()
    {
        var (feeds, runs, advance) = Make(Store());
        using var _ = feeds;
        feeds.Frame(null, "p/w");
        runs[0].Run.HasEnded = true;
        advance(1000); feeds.Tick();                 // ended: stopped, next try after a pause
        runs[0].Frame(Jpeg(9));                      // a frame still in its pipe arrives after that
        var r = feeds.Frame(null, "p/w");
        Assert.Equal(503, r.Status);
        Assert.Null(r.Jpeg);
        Assert.Single(runs);                         // and the pause still holds: no new ffmpeg yet
        advance(2500); feeds.Tick();
        Assert.Equal(2, runs.Count);
        runs[0].Frame(Jpeg(9));                      // the old one again, now that a new one runs
        Assert.Equal(503, feeds.Frame(null, "p/w").Status);
        runs[1].Frame(Jpeg(1));
        Assert.Equal(Jpeg(1), feeds.Frame(null, "p/w").Jpeg);
    }

    [Fact]
    public void WithoutFfmpegNothingStartsAndThePanelIsTold()
    {
        var (feeds, runs, _) = Make(Store(), ffmpeg: null);
        using var __ = feeds;
        Assert.Equal("no-ffmpeg", feeds.Frame(null, "p/w").State);
        Assert.Empty(runs);
    }

    [Fact]
    public void AChangedAddressRestartsTheCameraAndARemovedOneStops()
    {
        var store = Store();
        var (feeds, runs, _) = Make(store);
        using var __ = feeds;
        feeds.Frame(null, "p/w");
        // same address, new name: keeps running
        Assert.Null(store.Save(File.ReadAllText(_path).Replace("\"Door\"", "\"Front door\"")));
        feeds.SettingsSaved();
        Assert.False(runs[0].Run.Disposed);
        // a new frame rate restarts it too (ffmpeg's filter has it)
        var saved = File.ReadAllText(_path);
        Assert.Contains("\"fps\": 5", saved);
        Assert.Null(store.Save(saved.Replace("\"fps\": 5", "\"fps\": 8")));
        feeds.SettingsSaved();
        Assert.True(runs[0].Run.Disposed);
        feeds.Frame(null, "p/w");
        Assert.Equal(8, runs[1].Source.Fps);
        // new address: stopped, and the next request uses the new one
        Assert.Null(store.Save(File.ReadAllText(_path).Replace("192.168.1.20", "192.168.1.21")));
        feeds.SettingsSaved();
        Assert.True(runs[1].Run.Disposed);
        feeds.Frame(null, "p/w");
        Assert.Contains("192.168.1.21", runs[2].Source.Url);
    }

    [Fact]
    public void ThePhoneMayAskForCameraPicturesOverWifi()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/camera")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/camera/sources")));
    }
}
