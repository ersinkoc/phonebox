using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// What the bridge's HTTP surface answers, and what Wi-Fi may reach. The endpoint lambdas in Program.cs are thin; the
/// decisions live in BridgeApi so they can be tested without starting the agent.
/// </summary>
public sealed class BridgeApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-api-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly ClipboardBridge _bridge;
    private readonly List<(string Title, string Subtitle, string Body)> _shown = [];
    private readonly NotificationBridge _notifications;
    private int _stamp;

    private sealed class RecordingNotifier(List<(string, string, string)> shown) : IMacNotifier
    {
        public Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
        {
            shown.Add((title, subtitle, body));
            return Task.FromResult(true);
        }
    }

    public BridgeApiTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        SetSwitch(true);
        _bridge = new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance);
        _notifications = new NotificationBridge(new RecordingNotifier(_shown), _store, _bridge, () => 1_000_000, NullLogger<NotificationBridge>.Instance, () => []);
    }

    private void SetSwitch(bool on, bool notifications = false)
    {
        File.WriteAllText(_config, "{\"bridge\":{\"clipboard\":" + (on ? "true" : "false") + ",\"notifications\":" + (notifications ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode!.Value;

    private static HttpRequest Request(string body, string contentType = "application/json")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx.Request;
    }

    // ---- POST /api/bridge/clipboard ----

    [Fact]
    public void AStoredClipboardIsOkAndLandsOnTheMac()
    {
        Assert.Equal(200, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
        Assert.Equal("hi", _pb.Text);
    }

    [Fact]
    public void WhileTheSwitchIsOffTheAnswerIs403AndNothingIsWritten()
    {
        SetSwitch(false);
        Assert.Equal(403, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public void TooMuchTextIs413()
    {
        Assert.Equal(413, Status(BridgeApi.PostClipboard(_bridge, new string('a', ClipboardBridge.MaxBytes + 1), "phoneA")));
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public void AMacThatRefusesTheWriteIs500()
    {
        _pb.Fail = true;
        Assert.Equal(500, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoTextIs400(string? text) => Assert.Equal(400, Status(BridgeApi.PostClipboard(_bridge, text, "phoneA")));

    [Fact]
    public void OffMacOSTheAnswerIs501()
    {
        Assert.Equal(501, Status(BridgeApi.PostClipboard(null, "hi", "phoneA")));
    }

    // ---- reading the request ----

    [Fact]
    public async Task TheBodyCarriesUnicodeText()
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request("{\"text\":\"héllo 🌍\\nline two\"}"));
        Assert.Null(error);
        Assert.Equal("héllo 🌍\nline two", text);
    }

    [Fact]
    public async Task AnOversizedRequestIsRefusedBeforeItIsParsed()
    {
        var big = "{\"text\":\"" + new string('a', BridgeApi.MaxRequestBytes) + "\"}";
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(big));
        Assert.Null(text);
        Assert.Equal(BridgeApi.RequestTooLarge, error);
        Assert.Equal(413, Status(BridgeApi.BodyError(error!)));
    }

    [Fact]
    public async Task SixtyFourKilobytesOfTextEscapedAsUnicodeSequencesIsStillRead()
    {
        // 50,000 characters, each sent as a six-character escape: 300 KB of JSON, 50 KB of text
        var json = "{\"text\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 50_000)) + "\"}";
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(json));
        Assert.Null(error);
        Assert.Equal(new string('a', 50_000), text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public async Task BrokenBodiesAreARequestError(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(text);
        Assert.NotNull(error);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Theory]
    [InlineData("{\"text\":\"\\ud800\"}")]          // a high surrogate with no low one
    [InlineData("{\"text\":\"a\\udc00b\"}")]        // a low surrogate on its own
    public async Task ATextThatIsNotValidUnicodeIsARequestErrorNotAServerError(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(text);
        Assert.NotNull(error);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Fact]
    public async Task BytesThatAreNotUtf8AreARequestErrorNotAServerError()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream([.. "{\"text\":\""u8, 0xff, 0xfe, .. "\"}"u8]);
        var (text, error) = await BridgeApi.ReadClipboardBody(ctx.Request);
        Assert.Null(text);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":5}")]
    [InlineData("[\"text\"]")]
    public async Task ABodyWithoutTextGivesNoText(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(error);
        Assert.Null(text);
    }

    // ---- GET /api/bridge/stream ----

    [Fact]
    public void TheStreamFollowsTheSwitch()
    {
        Assert.True(BridgeApi.StreamAllowed(_store));
        SetSwitch(false);
        Assert.False(BridgeApi.StreamAllowed(_store));
    }

    [Fact]
    public void AnEventFrameKeepsNewlinesInsideTheJsonString()
    {
        // text with blank lines and a fake "event:" line must not be able to end the frame early or start another one
        var frame = BridgeApi.EventFrame(new BridgeEvent(7, "clipboard", "a\n\nevent: evil\r\ndata: x", null));
        Assert.StartsWith("id: 7\nevent: clipboard\ndata: ", frame);
        Assert.Equal(frame.Length - 2, frame.IndexOf("\n\n", StringComparison.Ordinal));
        Assert.DoesNotContain("\r", frame);
        Assert.Contains("\"seq\":7", frame);
        Assert.Contains("a\\n\\nevent: evil\\r\\ndata: x", frame);
    }

    [Fact]
    public void ThePingIsAnSseComment() => Assert.Equal(": ping\n\n", BridgeApi.PingFrame);

    /// <summary>A response body that records what is written; after <paramref name="blockAfter"/> writes it stops taking
    /// more, like a phone that stopped reading, until the write is cancelled.</summary>
    private sealed class PhoneBody(int blockAfter = int.MaxValue) : Stream
    {
        private readonly StringBuilder _text = new();
        private int _writes;
        public string Text { get { lock (_text) return _text.ToString(); } }
        public int Writes => Volatile.Read(ref _writes);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) > blockAfter) await Task.Delay(Timeout.Infinite, ct);
            lock (_text) _text.Append(Encoding.UTF8.GetString(buffer.Span));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static DefaultHttpContext StreamContext(PhoneBody body, string? device, string? lastEventId = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = body;
        if (device is not null) ctx.Request.Headers["X-PhoneBox-Device"] = device;
        if (lastEventId is not null) ctx.Request.Headers["Last-Event-ID"] = lastEventId;
        return ctx;
    }

    private sealed class RunningStream(CancellationTokenSource stop, Task run) : IAsyncDisposable
    {
        public Task Completion => run;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await stop.CancelAsync();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { stop.Dispose(); }
        }
    }

    private RunningStream StartStream(DefaultHttpContext ctx, Func<bool> authorized, CancellationToken stopping = default)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        return new(stop, BridgeApi.StreamAsync(ctx, _store, _stream, authorized, stop.Token));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheStreamSendsWhatOthersCausedSkipsThePhonesOwnAndEndsWhenTheSwitchIsTurnedOff()
    {
        var body = new PhoneBody();
        await using var run = StartStream(StreamContext(body, "phoneA"), () => true, CancellationToken.None);
        await Until(() => body.Text.StartsWith(BridgeApi.PingFrame, StringComparison.Ordinal));
        _stream.Publish("clipboard", "sent by phoneA", "phoneA");
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Until(() => body.Text.Contains("copied on the Mac"));
        SetSwitch(false);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("sent by phoneA", body.Text);
    }

    [Fact]
    public async Task TheStreamEndsWhenTheAgentBeginsToStop()
    {
        // otherwise a restart, an update or Quit waits for the server's shutdown timeout while a phone has the stream open
        using var stopping = new CancellationTokenSource();
        var body = new PhoneBody();
        await using var run = StartStream(StreamContext(body, "phoneA"), () => true, stopping.Token);
        await Until(() => body.Writes > 0);
        stopping.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWriteStuckOnAPhoneThatStoppedReadingEndsWhenTheSwitchIsTurnedOff()
    {
        var body = new PhoneBody(blockAfter: 1);    // the first ping goes through, then the phone takes nothing more
        await using var run = StartStream(StreamContext(body, "phoneA"), () => true, CancellationToken.None);
        await Until(() => body.Writes == 1);
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Until(() => body.Writes == 2);        // that write is now stuck
        SetSwitch(false);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("copied on the Mac", body.Text);
    }

    [Fact]
    public void AnEventFrameIsIdThenEventThenData()
    {
        // the wire format the phone parses: the id line it sends back as Last-Event-ID, then the event, then the JSON
        Assert.Equal("id: 12\nevent: clipboard\ndata: {\"seq\":12,\"text\":\"hi\"}\n\n",
            BridgeApi.EventFrame(new BridgeEvent(12, "clipboard", "hi", null)));
    }

    [Fact]
    public async Task AnOpenStreamEndsWhenThePhonesKeyIsNoLongerValid()
    {
        // a lost phone keeps its stream alive with the pings: a new Wi-Fi key, Wi-Fi turned off or an unplugged USB phone must end it
        var valid = true;
        var body = new PhoneBody();
        await using var run = StartStream(StreamContext(body, "phoneA"), () => Volatile.Read(ref valid), CancellationToken.None);
        await Until(() => body.Writes > 0);
        _stream.Publish("clipboard", "before", null);
        await Until(() => body.Text.Contains("before"));
        Volatile.Write(ref valid, false);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        _stream.Publish("clipboard", "after", null);
        Assert.DoesNotContain("after", body.Text);
    }

    [Fact]
    public async Task AWriteStuckOnAPhoneEndsWhenItsKeyIsRevoked()
    {
        var valid = true;
        var body = new PhoneBody(blockAfter: 1);
        await using var run = StartStream(StreamContext(body, "phoneA"), () => Volatile.Read(ref valid), CancellationToken.None);
        await Until(() => body.Writes == 1);
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Until(() => body.Writes == 2);        // that write is now stuck: only the timer can end it
        Volatile.Write(ref valid, false);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- reconnecting with Last-Event-ID ----

    [Fact]
    public async Task AReconnectingPhoneIsSentWhatItMissedAfterItsLastEventId()
    {
        _stream.Publish("clipboard", "heard before the drop", null);   // id 1, the phone has it
        _stream.Publish("clipboard", "copied while it reconnected", null); // id 2, missed
        _stream.Publish("clipboard", "its own", "phoneA");               // id 3, sent by this phone: never back to it
        var body = new PhoneBody();
        using var stop = new CancellationTokenSource();
        await using var run = StartStream(StreamContext(body, "phoneA", "1"), () => true, stop.Token);
        await Until(() => body.Text.Contains("copied while it reconnected"));
        _stream.Publish("clipboard", "live", null);
        await Until(() => body.Text.Contains("live"));
        stop.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("heard before the drop", body.Text);
        Assert.DoesNotContain("its own", body.Text);
        Assert.Contains("id: 2\nevent: clipboard\n", body.Text);
        Assert.Contains("id: 4\nevent: clipboard\n", body.Text);
        Assert.Equal(1, Count(body.Text, "copied while it reconnected")); // replayed once, not again by the live loop
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("-1")]
    public async Task WithoutAUsableLastEventIdANewStreamHearsOnlyWhatIsCopiedFromNowOn(string? header)
    {
        _stream.Publish("clipboard", "earlier", null);
        var body = new PhoneBody();
        using var stop = new CancellationTokenSource();
        await using var run = StartStream(StreamContext(body, "phoneA", header), () => true, stop.Token);
        await Until(() => body.Writes > 0);
        _stream.Publish("clipboard", "now", null);
        await Until(() => body.Text.Contains("now"));
        stop.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("earlier", body.Text);
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("-1", null)]
    [InlineData("+1", null)]
    [InlineData(" 1", null)]
    [InlineData("1 ", null)]
    [InlineData("1.0", null)]
    [InlineData("0x10", null)]
    [InlineData("9223372036854775808", null)]       // one over long.MaxValue
    [InlineData("99999999999999999999999", null)]
    [InlineData("١٢", null)]                          // digits, but not ASCII ones
    public void TheLastEventIdIsAPlainDecimalOrNothing(string header, long? expected) =>
        Assert.Equal(expected, BridgeApi.LastEventId(new Microsoft.Extensions.Primitives.StringValues(header)));

    [Fact]
    public void TwoLastEventIdHeadersCountAsNone() =>
        Assert.Null(BridgeApi.LastEventId(new Microsoft.Extensions.Primitives.StringValues(["1", "2"])));

    // ---- the order of the clipboard endpoint's checks ----

    /// <summary>A body that fails the test if anything reads it.</summary>
    private sealed class UnreadableBody : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the body was read");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("the body was read");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new InvalidOperationException("the body was read");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpContext Unread()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new UnreadableBody();
        ctx.Request.ContentLength = 400_000;
        return ctx;
    }

    [Fact]
    public async Task WhileTheSwitchIsOffTheBodyIsNotRead()
    {
        SetSwitch(false);
        Assert.Equal(403, Status(await BridgeApi.PostClipboardAsync(Unread(), _bridge, _store, () => true)));
    }

    [Fact]
    public async Task OffMacOSTheBodyIsNotRead() =>
        Assert.Equal(501, Status(await BridgeApi.PostClipboardAsync(Unread(), null, _store, () => true)));

    [Fact]
    public async Task ARequestThatIsNotFromAPhoneIsRefusedBeforeAnythingElse()
    {
        Assert.Equal(403, Status(await BridgeApi.PostClipboardAsync(Unread(), null, _store, () => false)));
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public async Task ABodyWithContentLengthIsReadWhole()
    {
        var json = "{\"text\":\"" + new string('b', 60_000) + "\"}";
        var req = Request(json);
        req.ContentLength = Encoding.UTF8.GetByteCount(json);
        var (text, error) = await BridgeApi.ReadClipboardBody(req);
        Assert.Null(error);
        Assert.Equal(new string('b', 60_000), text);
    }

    [Theory]
    [InlineData("a1B2c3", "a1B2c3")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("has space", null)]
    [InlineData("bad;id", null)]
    [InlineData("12345678901234567890123456789012", "12345678901234567890123456789012")]
    [InlineData("123456789012345678901234567890123", null)]
    public void ADeviceIdIsTakenOnlyWhenItLooksLikeOne(string header, string? expected) =>
        Assert.Equal(expected, BridgeApi.DeviceId(header));

    // ---- POST /api/bridge/notification ----

    private static PhoneNotification Note(string key = "k1") => new(key, "Messages", "com.example.messages", "Anna", "hi", "message", 1);

    [Fact]
    public async Task ANotificationIsOkAndIsShown()
    {
        SetSwitch(true, notifications: true);
        Assert.Equal(200, Status(BridgeApi.PostNotification(_notifications, Note(), "phoneA")));
        await _notifications.DrainAsync();
        Assert.Equal([("Anna", "Messages", "hi")], _shown);
    }

    [Fact]
    public void ADuplicateNotificationIsStillOk()
    {
        SetSwitch(true, notifications: true);
        Assert.Equal(200, Status(BridgeApi.PostNotification(_notifications, Note(), "phoneA")));
        Assert.Equal(200, Status(BridgeApi.PostNotification(_notifications, Note(), "phoneA")));
    }

    [Fact]
    public void WhileTheNotificationSwitchIsOffTheAnswerIs403()
    {
        SetSwitch(true, notifications: false);
        Assert.Equal(403, Status(BridgeApi.PostNotification(_notifications, Note(), "phoneA")));
    }

    [Fact]
    public void NoBridgeIs501AndNoNotificationIs400()
    {
        SetSwitch(true, notifications: true);
        Assert.Equal(501, Status(BridgeApi.PostNotification(null, Note(), "phoneA")));
        Assert.Equal(400, Status(BridgeApi.PostNotification(_notifications, null, "phoneA")));
    }

    [Fact]
    public void TheGateAnswers501Then403BeforeAnyBodyIsRead()
    {
        Assert.Equal(501, Status(BridgeApi.NotificationGate(null)!));
        SetSwitch(true, notifications: false);
        Assert.Equal(403, Status(BridgeApi.NotificationGate(_notifications)!));
        SetSwitch(true, notifications: true);
        Assert.Null(BridgeApi.NotificationGate(_notifications));
    }

    [Fact]
    public async Task TheNotificationBodyIsReadWithUnicode()
    {
        var body = "{\"key\":\"k9\",\"app\":\"Μηνύματα\",\"package\":\"com.x\",\"title\":\"Άννα 🌍\",\"text\":\"Καλημέρα\\nline2\",\"category\":\"call\",\"time\":123}";
        var (n, error) = await BridgeApi.ReadNotificationBody(Request(body));
        Assert.Null(error);
        Assert.Equal(new PhoneNotification("k9", "Μηνύματα", "com.x", "Άννα 🌍", "Καλημέρα\nline2", "call", 123), n);
    }

    [Fact]
    public async Task MissingOptionalFieldsGetDefaults()
    {
        var (n, error) = await BridgeApi.ReadNotificationBody(Request("{\"key\":\"k\",\"title\":\"T\"}"));
        Assert.Null(error);
        Assert.Equal(new PhoneNotification("k", "", "", "T", "", "message", 0), n);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":\"no key\"}")]
    [InlineData("{\"key\":\"k\"}")]                       // neither a title nor a text
    [InlineData("{\"key\":5,\"title\":\"T\"}")]           // a key that is not text
    [InlineData("[\"x\"]")]
    public async Task ABodyThatIsNotANotificationGivesNone(string body)
    {
        var (n, error) = await BridgeApi.ReadNotificationBody(Request(body));
        Assert.Null(error);
        Assert.Null(n);
    }

    [Fact]
    public async Task ABrokenOrOversizedNotificationBodyIsARequestError()
    {
        var (_, broken) = await BridgeApi.ReadNotificationBody(Request("not json"));
        Assert.Equal(400, Status(BridgeApi.BodyError(broken!)));
        var big = "{\"key\":\"k\",\"title\":\"" + new string('a', BridgeApi.MaxNotificationBytes) + "\"}";
        var (n, tooLarge) = await BridgeApi.ReadNotificationBody(Request(big));
        Assert.Null(n);
        Assert.Equal(BridgeApi.RequestTooLarge, tooLarge);
    }

    // ---- POST /api/bridge/notification/test (local only) ----

    [Fact]
    public async Task TheTestNotificationGoesThroughTheSwitch()
    {
        SetSwitch(true, notifications: false);
        Assert.Equal(403, Status(BridgeApi.PostTestNotification(_notifications, 1)));
        SetSwitch(true, notifications: true);
        Assert.Equal(200, Status(BridgeApi.PostTestNotification(_notifications, 2)));
        await _notifications.DrainAsync();
        Assert.Equal("PhoneBox test", Assert.Single(_shown).Title);
        Assert.Equal(501, Status(BridgeApi.PostTestNotification(null, 3)));
    }

    // ---- what Wi-Fi can reach ----

    [Theory]
    [InlineData("/api/bridge/clipboard", true)]
    [InlineData("/api/bridge/stream", true)]
    [InlineData("/api/bridge", false)]          // the namespace root and other paths under it are not opened
    [InlineData("/api/bridge/other", false)]
    [InlineData("/api/bridge/clipboard/x", false)]
    [InlineData("/api/bridgex", false)]
    [InlineData("/api/bridge-clipboard", false)]
    [InlineData("/api/bridge/notification", true)]
    [InlineData("/api/bridge/notification/test", false)]       // the settings page's test button: local only
    [InlineData("/api/bridge/notification/test/x", false)]
    public void OnlyTheBridgePathsAreOpenOverWifi(string path, bool allowed) =>
        Assert.Equal(allowed, Lan.AllowedOverLan(new PathString(path)));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
