using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class StateFrameCacheTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StreamsAskingTogetherShareOneBuildAndAFrameIsAStateBlock()
    {
        long clock = 1000;
        var n = 0;
        var cache = new StateFrameCache(() => new { n = ++n, host = "pc" }, Web, 400, () => clock);
        var a = cache.Get(5);
        clock += 50;
        var b = cache.Get(5);
        Assert.Same(a, b);
        Assert.Equal(1, cache.Builds);
        Assert.Equal("event: state\ndata: {\"n\":1,\"host\":\"pc\"}\n\n", a);
    }

    [Fact]
    public void AnOldFrameOrANewEventForcesARebuild()
    {
        long clock = 1000;
        var n = 0;
        var cache = new StateFrameCache(() => new { n = ++n }, Web, 400, () => clock);
        cache.Get(5);
        clock += 399;
        cache.Get(5);
        Assert.Equal(1, cache.Builds);
        clock += 1; // 400 ms old
        cache.Get(5);
        Assert.Equal(2, cache.Builds);
        cache.Get(6); // an event was emitted: the phone must not get a frame with the older counter
        Assert.Equal(3, cache.Builds);
        cache.Get(6, signal: 1); // something changed (a volume set, a new track): the frame is out of date
        Assert.Equal(4, cache.Builds);
        cache.Get(6, signal: 1);
        Assert.Equal(4, cache.Builds);
    }

    [Fact]
    public void ADefaultFrameIsNotReusedPastAboutAHundredMilliseconds()
    {
        // a frame carries the age of the media position as of when it was built, so a reused one understates it by how
        // long it has waited: that must stay well under the 0.3 s the phone ignores
        long clock = 1000;
        var n = 0;
        var cache = new StateFrameCache(() => new { n = ++n }, Web, clock: () => clock);
        cache.Get(1);
        clock += 99;
        cache.Get(1);
        Assert.Equal(1, cache.Builds);
        clock += 1;
        cache.Get(1);
        Assert.Equal(2, cache.Builds);
    }

    [Fact]
    public async Task ConcurrentStreamsBuildOnce()
    {
        var builds = 0;
        var cache = new StateFrameCache(() => { Interlocked.Increment(ref builds); Thread.Sleep(30); return new { ok = true }; }, Web, 5000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Get(1))));
        Assert.Equal(1, builds);
    }
}
