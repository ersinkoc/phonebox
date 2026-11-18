using PhoneBox.Agent;
using Xunit;

public sealed class ScreenCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledAcknowledgmentDoesNotPreserveAnOlderAppliedState(bool initialLocked)
    {
        var actual = await ScreenCancellationFixture.Run(canceled: true, initialLocked);
        Assert.True(actual.CancellationPropagated);
        Assert.Equal(initialLocked, actual.PhoneLocked);
        Assert.Equal(3, actual.Calls);
    }

    [Fact]
    public async Task CancellationKeepsOtherDevicesConfirmed()
    {
        Assert.Equal(["first:False", "second:False", "first:True", "second:True", "second:True"],
            await ScreenCancellationFixture.Multiple());
    }

    [Fact]
    public async Task CancelingAQueuedCallDoesNotInvalidateAnUnattemptedDevice()
    {
        Assert.Equal(2, await ScreenCancellationFixture.Queued());
    }
}

public sealed record ScreenCancellationOutcome(bool PhoneLocked, int Calls, bool CancellationPropagated);

// The fake applies the remote side effect, then holds the acknowledgment until cancellation/failure.
// This models an unknown command outcome without sending ADB or relying on wall-clock races.
public static class ScreenCancellationFixture
{
    public static async Task<(int AfterReset, int AfterReconnect)> ResetAndReconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sync = new ScreenSynchronizer(() => true, async (_, _, ct) =>
        {
            if (++calls == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        var applying = sync.ReconcileAsync(["phone"], default);
        Task? reset = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reset = sync.ResetAsync(default);
            release.TrySetResult();
            await Task.WhenAll(applying, reset).WaitAsync(TimeSpan.FromSeconds(5));
            await sync.ReconcileAsync(["phone"], default);
            var afterReset = calls;
            await sync.ReconcileAsync([], default);
            await sync.ReconcileAsync(["phone"], default);
            return (afterReset, calls);
        }
        finally
        {
            release.TrySetResult();
            await applying.WaitAsync(TimeSpan.FromSeconds(5));
            if (reset is not null) await reset.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public static async Task<string[]> Multiple()
    {
        var locked = false;
        var calls = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        var sync = new ScreenSynchronizer(() => locked, async (device, state, ct) =>
        {
            calls.Add(device + ":" + state);
            if (device != "second" || !state || held) return;
            held = true;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await sync.ReconcileAsync(["first", "second"], default);
        locked = true;
        using var stop = new CancellationTokenSource();
        var pending = sync.ReconcileAsync(["first", "second"], stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await sync.ReconcileAsync(["first", "second"], default);
            return calls.ToArray();
        }
        finally
        {
            stop.Cancel(); release.TrySetResult();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    public static async Task<int> Queued()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScreenSynchronizer(() => false, async (_, _, ct) =>
        {
            calls++;
            if (calls != 2) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await sync.ReconcileAsync(["phone"], default);
        var active = sync.ReconcileAsync(["phone"], default, force: true);
        using var stop = new CancellationTokenSource();
        Task<bool>? queued = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = sync.ReconcileAsync(["other"], stop.Token);
            stop.Cancel();
            try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected queued cancellation"); }
            catch (OperationCanceledException) { }
            release.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            await sync.ReconcileAsync(["phone"], default);
            return calls;
        }
        finally
        {
            stop.Cancel(); release.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (queued is not null) try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    public static async Task<ScreenCancellationOutcome> Run(bool canceled, bool initialLocked)
    {
        var locked = initialLocked;
        var phone = initialLocked;
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScreenSynchronizer(() => locked, async (_, state, ct) =>
        {
            calls++;
            phone = state;
            if (calls != 2) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            if (!canceled) throw new IOException("owned acknowledgment failed");
        });
        await sync.ReconcileAsync(["phone"], default);
        locked = !initialLocked;
        using var stop = new CancellationTokenSource();
        var pending = sync.ReconcileAsync(["phone"], stop.Token);
        var propagated = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (canceled) stop.Cancel(); else release.TrySetResult();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected acknowledgment failure"); }
            catch (OperationCanceledException) when (canceled) { propagated = pending.IsCanceled; }
            catch (AggregateException) when (!canceled) { }
            locked = initialLocked;
            await sync.ReconcileAsync(["phone"], default);
            return new(phone, calls, propagated);
        }
        finally
        {
            stop.Cancel();
            release.TrySetResult();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }
        }
    }
}
