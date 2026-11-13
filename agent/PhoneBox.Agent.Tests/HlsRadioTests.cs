using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PhoneBox.Agent;
using Xunit;

public sealed class HlsRadioTests
{
    [Fact]
    public void DetectsHlsUrlsWithQueryStringsAndProbesExtensionlessManifestsWithoutReadingInfiniteStreams()
    {
        Assert.True(HlsPlaylist.IsHlsUrl(new Uri("https://stream.radyotvonline.net/hls/play/kafaradyo.m3u8?UserCode=123")));
        Assert.False(HlsPlaylist.IsHlsUrl(new Uri("https://live.radyofenomen.com/fenomen/128/listen.m3u")));
        Assert.False(HlsPlaylist.IsHlsUrl(new Uri("https://live.radyofenomen.com/fenomen/128/icecast.audio")));
        Assert.False(HlsPlaylist.IsHlsUrl(new Uri("file:///tmp/test.m3u8")));

        Assert.True(HlsPlaylist.LooksLikeHlsManifest("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\nseg0.ts\n"));
        Assert.True(HlsPlaylist.LooksLikeHlsManifest("\uFEFF#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\naudio.m3u8\n"));
        Assert.False(HlsPlaylist.LooksLikeHlsManifest("#EXTM3U\n#EXTINF:-1,Classic Radio\nhttps://radio.example/stream.mp3\n"));

        using var hlsClient = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/live-hls")
                return TextResponse(req, "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n", "application/octet-stream");
            if (req.RequestUri!.AbsolutePath == "/master-hls")
                return TextResponse(req, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=96000\nlive.m3u8\n", "application/vnd.apple.mpegurl");
            if (req.RequestUri!.AbsolutePath == "/classic-m3u")
                return TextResponse(req, "#EXTM3U\n#EXTINF:-1,Station\nhttps://radio.example/stream.mp3\n", "audio/x-mpegurl");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StreamContent(new InfiniteAudioStream()) };
        }));

        Assert.True(HlsPlaylist.ProbeManifest(new Uri("https://radio.example/live-hls"), hlsClient));
        Assert.True(HlsPlaylist.ProbeManifest(new Uri("https://radio.example/master-hls"), hlsClient));
        Assert.False(HlsPlaylist.ProbeManifest(new Uri("https://radio.example/classic-m3u"), hlsClient));
        Assert.False(HlsPlaylist.ProbeManifest(new Uri("https://radio.example/infinite-icecast"), hlsClient));
    }

    [Fact]
    public void ProbeManifestAbortsWhenResponseBodyStallsAfterHeaders()
    {
        using var client = new HttpClient(new FakeHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StreamContent(new StallingBodyStream()) }));

        var sw = Stopwatch.StartNew();
        var ok = HlsPlaylist.ProbeManifest(new Uri("https://radio.example/stalled"), client, TimeSpan.FromMilliseconds(150));
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected stalled probe to abort quickly, took {sw.Elapsed}");
    }

    [Theory]
    [InlineData(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", true)]
    [InlineData(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", true)]
    [InlineData("{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", true)]
    [InlineData(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{99999999-2222-3333-4444-555555555555}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", false)]
    [InlineData(null, "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", false)]
    public void MatchesAudioRenderDevice_MatchesCoreAudioIdsAgainstSwdInterfacePaths(string? interfaceId, string mmDeviceId, bool expected)
    {
        Assert.Equal(expected, HlsPlaylist.MatchesAudioRenderDevice(interfaceId, mmDeviceId));
    }

    private static HttpResponseMessage TextResponse(HttpRequestMessage req, string body, string mediaType)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent(body, Encoding.UTF8) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return res;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StallingBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InfiniteAudioStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)0xFF, offset, count);
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
