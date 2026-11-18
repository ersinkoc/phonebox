using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>An approval ends exactly once: by the phone's decision, by its expiry or by the terminal (cancel).</summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with ApprovalExpiryTests
public sealed class ApprovalOutcomeTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public long Now = 100_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    /// <summary>Holds "from the phone" log lines until released, to stall a decision between its checks and its result.</summary>
    private sealed class StallingLog : ILogger<ApprovalStore>
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (!format(state, ex).Contains("from the phone")) return;
            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private readonly Clock _clock = new();
    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-approval-outcome-").FullName;
    private readonly PushStore _events;

    public ApprovalOutcomeTests()
    {
        _events = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _events.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Approval Request(long expires) => new("test", "test", "s", "p", "tool", "detail", null, _clock.Now, expires);

    // With 100 ms left the request ends after those 100 ms (no one-second floor), and leaves the phone's list at its expiry.
    [Fact]
    public async Task ARequestEndsAtItsExpiryAndIsNotListedAfterIt()
    {
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        using var abort = new CancellationTokenSource();
        var pending = store.AskAsync(Request(_clock.Now + 100), abort.Token);
        Assert.Single(store.Snapshot());
        _clock.Now += 100;
        Assert.Empty(store.Snapshot());
        try { Assert.Null(await pending.WaitAsync(TimeSpan.FromMilliseconds(800))); }
        finally { abort.Cancel(); }
    }

    // A decision that stalls after its checks while the terminal takes over must not be reported as accepted.
    [Fact]
    public async Task ADecisionIsAcceptedOnlyIfTheRequestReturnsIt()
    {
        var log = new StallingLog();
        var store = new ApprovalStore(_events, log, _clock);
        using var abort = new CancellationTokenSource();
        var pending = store.AskAsync(Request(_clock.Now + 60_000), abort.Token);
        var decide = Task.Run(() => store.Decide("test", "allow"));
        try
        {
            await log.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            abort.Cancel(); // the user answered in the terminal
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally { log.Release.TrySetResult(); }
        var accepted = await decide.WaitAsync(TimeSpan.FromSeconds(10));
        var returned = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(accepted, returned == "allow");
    }
}
