using PhoneBox.Agent;
using Xunit;

/// <summary>
/// What a phone's bridge stream is sent when it reconnects with Last-Event-ID: the recent events it missed, from the ring
/// of the last 20, never its own, never old ones.
/// </summary>
public sealed class BridgeReplayTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }

    private readonly Clock _clock = new();
    private readonly BridgeStream _stream;

    public BridgeReplayTests() => _stream = new BridgeStream(_clock);

    [Fact]
    public void WithoutAnIdANewStreamStartsAtTheCurrentEventAndIsSentNothing()
    {
        _stream.Publish("clipboard", "a", null);
        _stream.Publish("clipboard", "b", null);
        var (start, replay) = _stream.Resume(null, "phoneA");
        Assert.Equal(2, start);
        Assert.Empty(replay);
    }

    [Fact]
    public void WithAnIdTheEventsAfterItAreReplayedInOrder()
    {
        for (var i = 1; i <= 5; i++) _stream.Publish("clipboard", "t" + i, null);
        var (start, replay) = _stream.Resume(2, "phoneA");
        Assert.Equal(5, start);
        Assert.Equal(["t3", "t4", "t5"], replay.Select(e => e.Text));
        Assert.Empty(_stream.Resume(5, "phoneA").Replay);   // it has everything
    }

    [Fact]
    public void OnlyEventsYoungerThanTwoMinutesAreReplayed()
    {
        _stream.Publish("clipboard", "old", null);
        _clock.Now += BridgeStream.ReplayWindow;                // now exactly two minutes old: too old
        _stream.Publish("clipboard", "recent", null);
        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(["recent"], _stream.Resume(0, "phoneA").Replay.Select(e => e.Text));
        _clock.Now += BridgeStream.ReplayWindow;
        Assert.Empty(_stream.Resume(0, "phoneA").Replay);
    }

    [Fact]
    public void AnOpenStreamDoesNotReceiveExpiredEvents()
    {
        _stream.Publish("clipboard", "old", null);
        _clock.Now += BridgeStream.ReplayWindow;
        Assert.Empty(_stream.Since(0, "phoneA"));
        _stream.Publish("clipboard", "recent", null);
        Assert.Equal(["recent"], _stream.Since(0, "phoneA").Select(e => e.Text));
    }

    [Fact]
    public void APhonesOwnEventsAreNeverReplayedToIt()
    {
        _stream.Publish("clipboard", "from A", "phoneA");
        _stream.Publish("clipboard", "from the Mac", null);
        _stream.Publish("clipboard", "from B", "phoneB");
        Assert.Equal(["from the Mac", "from B"], _stream.Resume(0, "phoneA").Replay.Select(e => e.Text));
        Assert.Equal(["from A", "from the Mac"], _stream.Resume(0, "phoneB").Replay.Select(e => e.Text));
    }

    [Fact]
    public void AtMostTheLastTwentyAreReplayed()
    {
        for (var i = 1; i <= 25; i++) _stream.Publish("clipboard", "t" + i, null);
        var replay = _stream.Resume(0, "phoneA").Replay;
        Assert.Equal(20, replay.Count);
        Assert.Equal(6, replay[0].Seq);
        Assert.Equal(25, replay[^1].Seq);
    }

    [Fact]
    public void AnIdFromBeforeAnAgentRestartCountsAsNoId()
    {
        // the numbers start again at 1 after a restart: starting from the phone's old id 57 would skip events 1..57 of this run
        _stream.Publish("clipboard", "a", null);
        var (start, replay) = _stream.Resume(57, "phoneA");
        Assert.Equal(1, start);
        Assert.Empty(replay);
    }

    [Fact]
    public void EventsCarryTheTimeTheyWerePublished()
    {
        var e = _stream.Publish("clipboard", "a", null);
        Assert.Equal(_clock.Now.ToUnixTimeMilliseconds(), e.AtMs);
    }

    [Fact]
    public void ClearForgetsTheTextsButKeepsTheNumbering()
    {
        _stream.Publish("clipboard", "a", null);
        _stream.Publish("clipboard", "b", null);
        _stream.Clear();
        Assert.Empty(_stream.Since(0, null));
        Assert.Empty(_stream.Resume(0, "phoneA").Replay);
        Assert.Equal(3, _stream.Publish("clipboard", "c", null).Seq);
    }
}
