using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A pasteboard that cannot be reached (no window server, AppKit missing) must never stop the agent from starting, and
/// must not even be touched while the bridge is off.
/// </summary>
public sealed class LazyPasteboardTests : IDisposable
{
    private sealed class CountingLogger : ILogger<LazyPasteboard>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-lazy-" + Guid.NewGuid());

    [Fact]
    public void NothingIsCreatedUntilTheFirstUse()
    {
        var created = 0;
        _ = new LazyPasteboard(() => { created++; return new FakePasteboard(); }, new CountingLogger());
        Assert.Equal(0, created);
    }

    [Fact]
    public void AFactoryThatThrowsGivesAnEmptyPasteboardAndOneWarning()
    {
        var created = 0;
        var log = new CountingLogger();
        var pb = new LazyPasteboard(() => { created++; throw new InvalidOperationException("no window server"); }, log);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0, pb.ChangeCount);
            Assert.Null(pb.ReadShareableText());
            Assert.Null(pb.WriteText("x"));
        }
        Assert.Equal(1, created);   // asked once, not on every poll
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public void AWorkingFactoryIsUsedAndCreatedOnce()
    {
        var created = 0;
        var fake = new FakePasteboard();
        var pb = new LazyPasteboard(() => { created++; return fake; }, new CountingLogger());
        Assert.NotNull(pb.WriteText("hello"));
        Assert.Equal("hello", pb.ReadShareableText());
        Assert.Equal(fake.Count, pb.ChangeCount);
        Assert.Equal(1, created);
    }

    [Fact]
    public void ABridgeThatIsOffNeverCreatesThePasteboard()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"bridge":{"clipboard":false}}""");
        var created = 0;
        var bridge = new ClipboardBridge(new LazyPasteboard(() => { created++; return new FakePasteboard(); }, new CountingLogger()),
            new ConfigStore(path), new BridgeStream(), NullLogger<ClipboardBridge>.Instance);
        bridge.PollOnce();
        bridge.PollOnce();
        Assert.Equal(0, created);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
