using System.Diagnostics;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>
/// Setting the volume from the phone while the computer is busy: osascript / wpctl can take seconds, and the slider
/// must neither wait for a pointless read first nor snap back to the old volume until the next reading.
/// </summary>
public class AudioSetVolumeTests
{
    [Fact]
    public void ASuccessfulSetShowsTheNewVolumeAtOnce()
    {
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level => level);
        Assert.Equal(50, audio.Current()?.Volume);

        Assert.True(audio.SetVolume(80));
        var shown = audio.Snapshot()!; // what the next state sends to the phone
        Assert.Equal(80, shown.Volume);
        Assert.False(shown.Muted);
        Assert.True(shown.CanControl);
    }

    [Fact]
    public void ASlowReaderDoesNotHoldUpASetWhenAnOlderReadingExists()
    {
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        var audio = new AudioControl(() =>
        {
            if (Interlocked.Increment(ref reads) > 1) gate.Wait(TimeSpan.FromSeconds(10)); // the busy computer
            return new AudioSnapshot(50, false, true);
        }, set: level => level);
        audio.Current();
        Thread.Sleep(1000); // the reading is now older than the cache allows (900 ms)

        var clock = Stopwatch.StartNew();
        Assert.True(audio.SetVolume(70));
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the set waited {clock.ElapsedMilliseconds} ms for a read");
        gate.Set();
    }

    [Fact]
    public void AFailedSetKeepsTheReadingAndReportsFailure()
    {
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: _ => null);
        audio.Current();
        Assert.False(audio.SetVolume(90));
        Assert.Equal(50, audio.Snapshot()?.Volume);
    }

    [Fact]
    public async Task TwoSetsAtOnceAreWrittenAndRememberedInOrder()
    {
        // a dragged slider sends requests that overlap: the cache must end on the volume the device really ended on,
        // not on the one whose write happened to finish last
        using var firstInside = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var gate = new object();
        int running = 0, maxRunning = 0, calls = 0;
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level =>
        {
            lock (gate) { running++; maxRunning = Math.Max(maxRunning, running); }
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { firstInside.Set(); releaseFirst.Wait(TimeSpan.FromSeconds(10)); }
                return level;
            }
            finally { lock (gate) running--; }
        });
        audio.Current();

        var first = Task.Run(() => audio.SetVolume(20));
        Assert.True(firstInside.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => audio.SetVolume(80)); // arrives while the first write is still going
        await Task.Delay(200); // without the gate it would have finished by now
        releaseFirst.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxRunning);
        Assert.Equal(80, audio.Snapshot()?.Volume);
    }

    [Theory]
    [InlineData(true, true, false)]   // the backend cleared mute: not muted
    [InlineData(false, true, true)]   // a volume-only write on a muted sink: still muted
    [InlineData(false, false, false)]
    public void ASetThatDoesNotClearMuteKeepsTheMuteState(bool clearsMute, bool wasMuted, bool expected) =>
        Assert.Equal(expected, AudioControl.MutedAfterSet(clearsMute, new AudioSnapshot(50, wasMuted, true)));

    [Fact]
    public void WithNoEarlierReadingAVolumeOnlyWriteLeavesTheMuteStateUnknown()
    {
        Assert.Null(AudioControl.MutedAfterSet(false, null)); // not "unmuted": nobody knows
        Assert.False(AudioControl.MutedAfterSet(true, null));  // a write that clears mute does know
    }

    [Fact]
    public void AToggleFlipsTheRememberedMuteAtOnce()
    {
        var toggles = 0;
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level => level, toggleMute: () => { toggles++; return true; });
        audio.Current();
        Assert.True(audio.ToggleMute());
        Assert.Equal(1, toggles);
        var shown = audio.Snapshot()!;
        Assert.True(shown.Muted); // not the old reading until a background read catches up
        Assert.Equal(50, shown.Volume);
        Assert.True(audio.ToggleMute());
        Assert.False(audio.Snapshot()!.Muted);
    }

    [Fact]
    public void AFailedToggleKeepsNothingStale()
    {
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level => level, toggleMute: () => false);
        audio.Current();
        Assert.False(audio.ToggleMute());
        Assert.False(audio.Snapshot()!.Muted);
    }

    [Theory]
    [InlineData(new[] { true, true }, true)]    // stereo, both written
    [InlineData(new[] { true, false }, false)]  // one channel written, one refused: not done
    [InlineData(new[] { false, false }, false)]
    public void StereoNeedsBothChannelsWritten(bool[] results, bool expected) =>
        Assert.Equal(expected, MacAudioProbe.AllWritten(results.Select(r => (bool?)r)));

    [Fact]
    public void AMonoOutputNeedsOnlyItsOneChannelAndNoChannelsIsFailure()
    {
        Assert.True(MacAudioProbe.AllWritten([true, null]));  // the second channel does not exist
        Assert.False(MacAudioProbe.AllWritten([null, null])); // nothing to write
    }

    [Fact]
    public void AMuteThatCannotBeReadIsOnlyFineWhenTheOutputHasNoMuteProperty()
    {
        Assert.True(MacAudioProbe.Unmuted(0, true, () => throw new Exception("must not write")));   // not muted: nothing to clear
        Assert.True(MacAudioProbe.Unmuted(1, true, () => true));     // muted, cleared
        Assert.False(MacAudioProbe.Unmuted(1, true, () => false));   // muted, could not be cleared
        // the property exists but cannot be read (it may even be read-only: that is not "absent"): unknown, so the fallback runs
        Assert.False(MacAudioProbe.Unmuted(null, true, () => true));
        Assert.False(MacAudioProbe.Unmuted(null, null, () => true)); // not known whether it exists
        Assert.True(MacAudioProbe.Unmuted(null, false, () => throw new Exception("must not write"))); // there is no mute at all
    }

    [Theory]
    [InlineData(new[] { 0.2f, 0.8f }, 0.25f, new[] { 0.1f, 0.4f })]   // halved: the balance stays 1 : 4
    [InlineData(new[] { 0.2f, 0.8f }, 0.5f, new[] { 0.2f, 0.8f })]    // already at the target: untouched
    [InlineData(new[] { 0.2f, 0.8f }, 0.9f, new[] { 0.8f, 1.0f })]    // the louder one stops at 1: the rest goes to the other, so the level is reached
    [InlineData(new[] { 0.5f, 1.0f }, 1.0f, new[] { 1.0f, 1.0f })]   // everything up
    [InlineData(new[] { 0.9f, 0.9f }, 1.0f, new[] { 1.0f, 1.0f })]
    [InlineData(new[] { 0f, 0f }, 0.5f, new[] { 0.5f, 0.5f })]        // silent: nothing to keep, both go to the target
    [InlineData(new[] { 0.6f }, 0.3f, new[] { 0.3f })]                // mono
    public void ChannelsKeepTheirBalanceWhenTheVolumeChanges(float[] current, float target, float[] expected)
    {
        var got = MacAudioProbe.ScaleChannels(current, target);
        Assert.Equal(expected.Length, got.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], 3);
    }

    [Fact]
    public void AWriteThatCouldNotReachTheLevelIsRememberedAtTheLevelItReached()
    {
        // channel scaling can stop short (a channel passes 1): the device then reports a lower average than was asked for,
        // and the phone must not be shown the requested level for a moment before the next read corrects it
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level => Math.Min(level, 68));
        audio.Current();
        Assert.True(audio.SetVolume(90));
        Assert.Equal(68, audio.Snapshot()?.Volume);
        Assert.True(audio.SetVolume(40));
        Assert.Equal(40, audio.Snapshot()?.Volume);
    }

    [Theory]
    [InlineData(new[] { 0.36f, 1.0f }, 68)]  // [0.2, 0.8] asked for 0.9: the louder channel stopped at 1
    [InlineData(new[] { 0.1f, 0.4f }, 25)]
    [InlineData(new[] { 0.3f }, 30)]
    public void TheEffectiveLevelIsTheAverageOfWhatWasWritten(float[] written, int expected) =>
        Assert.Equal(expected, MacAudioProbe.EffectiveLevel(written));

    [Fact]
    public void AReadThatLandsDuringAToggleIsNotFlippedAgain()
    {
        // a background read can finish after the device has toggled but before the toggle call returns: it already holds the
        // new mute state, and flipping the remembered one again would show the opposite of the truth
        AudioControl? audio = null;
        var reading = new AudioSnapshot(50, false, true);
        audio = new AudioControl(() => reading, set: level => level, toggleMute: () =>
        {
            Thread.Sleep(1000);                       // the earlier reading is now too old to be reused (900 ms)
            reading = new AudioSnapshot(50, true, true); // the device has toggled: a read now sees it muted
            audio!.Current();                         // a read lands while the toggle is still in progress
            return true;
        });
        audio.Current(); // unmuted
        Assert.True(audio.ToggleMute());
        Assert.True(audio.Snapshot()!.Muted);
    }

    [Fact]
    public void AFailedActionMakesTheNextOneStartFromAFreshRead()
    {
        // a failed action invalidates the reading; the sink is then muted from outside; the next toggle must not flip the
        // stale "unmuted" and call it fresh
        var muted = false;
        var audio = new AudioControl(() => new AudioSnapshot(50, muted, true), set: _ => null, toggleMute: () => { muted = !muted; return true; });
        Assert.False(audio.Current()!.Muted);
        Assert.False(audio.SetVolume(60)); // fails: the reading is invalidated
        muted = true;                      // muted from outside (the keyboard, another app)

        Assert.True(audio.ToggleMute());   // the sink is now unmuted
        Assert.False(audio.Current()!.Muted); // not the stale reading flipped to "muted"
    }

    [Fact]
    public void AnInvalidatedReadingIsUnknownNotABaseline()
    {
        // the rule the toggle and the volume write rely on, checked directly because on macOS both re-read first and so
        // cannot show it
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: level => (level == 60 ? null : level));
        Assert.Null(audio.KnownReading());          // nothing read yet
        audio.Current();
        Assert.NotNull(audio.KnownReading());       // a reading
        Assert.False(audio.SetVolume(60));          // a failed action invalidates it
        Assert.Null(audio.KnownReading());
        Assert.True(audio.SetVolume(40));           // a successful one gives a new known reading
        Assert.Equal(40, audio.KnownReading()?.Volume);
    }
}
