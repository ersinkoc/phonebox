using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class WidgetFeedProxyTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task BodyConnectionFailuresBecomeBadGateway(int failAfter)
    {
        var body = new FeedTransportFixture.Body(32, failAfter: failAfter);
        var actual = await FeedTransportFixture.Fetch(body);
        Assert.Null(actual.Error);
        Assert.Equal(502, actual.Code);
        Assert.True(actual.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationRemainsCancellation(bool baseException)
    {
        var actual = await FeedTransportFixture.Cancel(baseException);
        Assert.True(actual.Canceled);
        Assert.Null(actual.Code);
        Assert.True(actual.Disposed);
    }

    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(WidgetFeedProxy.MaxResponseBytes - 1, 200, WidgetFeedProxy.MaxResponseBytes - 1)]
    [InlineData(WidgetFeedProxy.MaxResponseBytes, 200, WidgetFeedProxy.MaxResponseBytes)]
    [InlineData(WidgetFeedProxy.MaxResponseBytes + 1, 502, 0)]
    [InlineData(WidgetFeedProxy.MaxResponseBytes * 2, 502, 0)]
    public async Task UnknownLengthBodiesRespectTheDecodedByteLimit(int size, int code, int length)
    {
        var actual = await FeedTransportFixture.Fetch(new FeedTransportFixture.Body(size));
        Assert.Null(actual.Error);
        Assert.Equal(code, actual.Code);
        Assert.Equal(length, actual.Length);
        Assert.True(actual.Disposed);
    }

    [Fact]
    public async Task ADeclaredOversizeBodyIsRejectedBeforeReading()
    {
        var actual = await FeedTransportFixture.Fetch(new FeedTransportFixture.Body(16), declaredLength: WidgetFeedProxy.MaxResponseBytes + 1);
        Assert.Equal(502, actual.Code);
        Assert.Equal(0, actual.Reads);
        Assert.True(actual.Disposed);
    }

    [Fact]
    public async Task UpstreamCancellationWithoutCallerCancellationStaysGatewayTimeout()
    {
        Assert.Equal(504, await FeedTransportFixture.UpstreamFailure(new TaskCanceledException("owned dependency canceled")));
        Assert.Equal(502, await FeedTransportFixture.UpstreamFailure(new HttpRequestException("owned connection failed")));
    }

    [Fact]
    public async Task AnAlreadyCanceledCallerDoesNotReadTheBody()
    {
        using var body = new FeedTransportFixture.Body(16);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var actual = await FeedTransportFixture.Fetch(body, stop.Token);
        Assert.True(actual.Canceled);
        Assert.Null(actual.Code);
        Assert.Equal(0, actual.Reads);
    }

    [Fact]
    public async Task ACompleteUpstreamErrorBodyRetainsItsStatus()
    {
        var actual = await FeedTransportFixture.Fetch(new FeedTransportFixture.Body(9), status: HttpStatusCode.ServiceUnavailable);
        Assert.Equal(503, actual.Code);
        Assert.Equal(9, actual.Length);
        Assert.True(actual.Disposed);
    }
}

public sealed record FeedTransportOutcome(int? Code, int Length, bool Disposed, bool Canceled, string? Error, int Reads);

// The production client seam supplies response bodies with controlled I/O failure/cancellation.
// No real connection or timer decides the outcome; timeouts below only bound failed fixture teardown.
public static class FeedTransportFixture
{
    public const string Url = "https://api.open-meteo.com/v1/forecast";

    private sealed class Handler(Body body, HttpStatusCode status, long? length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            response.Content.Headers.ContentLength = length;
            return Task.FromResult(response);
        }
    }

    private sealed class FailingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    public static async Task<int?> UpstreamFailure(Exception error)
    {
        using var client = new HttpClient(new FailingHandler(error));
        var result = await WidgetFeedProxy.FetchAsync(Url, default, client);
        return ((IStatusCodeHttpResult)result).StatusCode;
    }

    public static async Task<FeedTransportOutcome> Fetch(Body body, CancellationToken ct = default,
        HttpStatusCode status = HttpStatusCode.OK, long? declaredLength = null)
    {
        using var client = new HttpClient(new Handler(body, status, declaredLength));
        var pending = WidgetFeedProxy.FetchAsync(Url, ct, client);
        try
        {
            var result = await pending;
            if (result is IStatusCodeHttpResult statusResult)
                return new(statusResult.StatusCode, 0, body.Disposed, pending.IsCanceled, null, body.Reads);
            var context = new DefaultHttpContext();
            using var output = new MemoryStream();
            context.Response.Body = output;
            await result.ExecuteAsync(context);
            return new(context.Response.StatusCode, (int)output.Length, body.Disposed, pending.IsCanceled, null, body.Reads);
        }
        catch (Exception ex)
        {
            return new(null, 0, body.Disposed, pending.IsCanceled, ex.GetType().Name, body.Reads);
        }
    }

    public static async Task<FeedTransportOutcome> Cancel(bool baseException = false)
    {
        var body = new Body(16, pause: true, baseCancellation: baseException);
        using var stop = new CancellationTokenSource();
        var pending = Fetch(body, stop.Token);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            return await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            body.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public sealed class Body(int size, int? failAfter = null, bool pause = false, bool baseCancellation = false) : Stream
    {
        private int _offset;
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Reads++;
            if (pause)
            {
                Entered.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (baseCancellation) { throw new OperationCanceledException(cancellationToken); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (failAfter is { } limit && _offset >= limit) throw new IOException("owned peer ended mid-body");
            var count = Math.Min(buffer.Length, size - _offset);
            if (failAfter is { } end) count = Math.Min(count, end - _offset);
            buffer.Span[..count].Fill((byte)'x');
            _offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
