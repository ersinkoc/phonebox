using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

// Now Playing on macOS and Linux is read by starting a tool (nowplaying-cli, playerctl, osascript); it is read every
// second only while a panel or a rule asks for it.
public sealed class MediaPollingTests
{
    [Fact]
    public void ReadsEverySecondOnlyWhileSomethingAskedRecently()
    {
        const long asked = 100_000;
        Assert.False(MediaMonitor.IsIdle(asked, asked));
        Assert.False(MediaMonitor.IsIdle(asked, asked + MediaMonitor.WantedForMs - 1));
        Assert.True(MediaMonitor.IsIdle(asked, asked + MediaMonitor.WantedForMs));
        Assert.True(MediaMonitor.IdlePoll > MediaMonitor.ActivePoll);
    }

    [Fact]
    public void AControlReadsAgainWhenTheLastReadingIsOldEvenIfSomethingJustAsked()
    {
        const long read = 100_000;
        Assert.False(MediaMonitor.IsStale(read, read + MediaMonitor.FreshForMs));
        Assert.True(MediaMonitor.IsStale(read, read + MediaMonitor.FreshForMs + 1));
        Assert.True(MediaMonitor.IsStale(long.MinValue / 2, read)); // never read yet
        Assert.True(MediaMonitor.FreshForMs < MediaMonitor.IdlePoll.TotalMilliseconds);
    }

    [Fact]
    public void TheFirstRequestAfterAQuietSpellReadsAtOnce()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        Assert.False(monitor.ReadingDue);
        monitor.Snapshot(); // nobody had asked before: the panel must not wait out the idle interval
        Assert.True(monitor.ReadingDue);
        monitor.Snapshot(); // asked again at once: still one reading due, and the wake does not overflow
        Assert.True(monitor.ReadingDue);
    }
}
