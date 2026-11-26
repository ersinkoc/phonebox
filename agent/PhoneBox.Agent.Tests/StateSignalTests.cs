using PhoneBox.Agent;
using Xunit;

public sealed class StateSignalTests
{
    [Fact]
    public void APokeWakesSubscribersAndBumpsTheVersion()
    {
        var woken = 0;
        void On() => Interlocked.Increment(ref woken);
        StateSignal.Changed += On;
        try
        {
            var before = StateSignal.Version;
            StateSignal.Poke();
            StateSignal.Poke();
            // at least: other tests running in parallel poke the same global signal too
            Assert.True(woken >= 2, $"woken {woken} times");
            Assert.True(StateSignal.Version >= before + 2);
        }
        finally { StateSignal.Changed -= On; }
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotStopThePokeOrTheCaller()
    {
        var reached = false;
        void Bad() => throw new InvalidOperationException("boom");
        void Good() => reached = true;
        StateSignal.Changed += Bad;
        StateSignal.Changed += Good;
        try { StateSignal.Poke(); }
        finally { StateSignal.Changed -= Bad; StateSignal.Changed -= Good; }
        Assert.True(reached);
    }

    [Fact]
    public void AMediaChangeThatMattersPokesButTheTickingPositionDoesNot()
    {
        var a = new MediaSnapshot("Spotify", "One", "A", "", true, 1000, 200000, 1, true);
        Assert.False(StateSignal.MediaChanged(a, a with { PosMs = 2200 }));          // just time passing
        Assert.True(StateSignal.MediaChanged(a, a with { Playing = false }));          // pause
        Assert.True(StateSignal.MediaChanged(a, a with { Title = "Two", PosMs = 0 })); // next track
        Assert.True(StateSignal.MediaChanged(a, a with { PosMs = 90_000 }));           // a seek, not 1 s of playback
        Assert.True(StateSignal.MediaChanged(a, a with { PlaybackRate = 2.0 }));       // speed change reaches the phone now
        var fast = a with { PlaybackRate = 2.0 };
        Assert.False(StateSignal.MediaChanged(fast, fast with { PosMs = 5_000 }));     // 2 s at 2x is normal playback
        Assert.True(StateSignal.MediaChanged(fast, fast with { PosMs = 20_000 }));     // real seek at 2x
        Assert.True(StateSignal.MediaChanged(null, a));
        Assert.True(StateSignal.MediaChanged(a, null));
        Assert.False(StateSignal.MediaChanged(null, null));
    }
}
