using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

[CollectionDefinition("Approval request expiry", DisableParallelization = true)]
public sealed class ApprovalExpiryCollection { }

[Collection("Approval request expiry")]
public sealed class ApprovalExpiryTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public long Now = 100_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    private readonly Clock _clock = new();
    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-approval-expiry-" + Guid.NewGuid().ToString("N"));
    private readonly ApprovalStore _store;

    public ApprovalExpiryTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json")),
            NullLogger<ApprovalStore>.Instance, _clock);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }
    private Approval Request(long expires) => new("test", "test", "s", "p", "tool", "detail", null, _clock.Now, expires);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task AnAlreadyExpiredRequestIsNotOfferedToThePhone(int delta)
    {
        var pending = _store.AskAsync(Request(_clock.Now + delta), default);
        Assert.Empty(_store.Snapshot());
        Assert.False(_store.Decide("test", "allow"));
        Assert.Null(await pending);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ADecisionMustArriveStrictlyBeforeTheRequestExpires(int delta)
    {
        using var abort = new CancellationTokenSource();
        var pending = _store.AskAsync(Request(130_000), abort.Token);
        Assert.Single(_store.Snapshot());
        _clock.Now = 130_000 + delta;
        var accepted = _store.Decide("test", "allow");
        Assert.Equal(delta < 0, accepted);
        if (!accepted) abort.Cancel();
        Assert.Equal(delta < 0 ? "allow" : null, await pending);
        Assert.Empty(_store.Snapshot());
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        Directory.Delete(_dir, recursive: true);
    }
}
