using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PhoneBox.Agent;

namespace PhoneBox.Agent.Tests;

public sealed class LlamaSwapMonitorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llamaswap-test").FullName;

    [Fact(DisplayName = "The refresh reads the version, the loaded models and the newest request's speed")]
    public async Task RefreshReadsServer()
    {
        await using var ls = new FakeLlamaSwap();
        ls.ActivityEntries = [Entry(DateTime.Now, 100, 10, tps: 58.42)];
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.True(s.Connected);
        Assert.Equal("v275", s.Version);
        Assert.Equal(["Qwen3.8-27B-GGUF"], s.LoadedModels); // the unloaded and the status-less model are out
        Assert.Equal(58.42, s.GenTps!.Value, 2); // the newest finished request measured this speed itself
    }

    [Fact(DisplayName = "Today's counts come from the request log and stop at the local midnight")]
    public async Task TodayCountsOnlyToday()
    {
        await using var ls = new FakeLlamaSwap();
        var now = DateTime.Today.AddHours(12); // frozen at noon: the fixture and the query share one day boundary
        var midnightToday = now.Date;
        ls.ActivityEntries = [
            Entry(midnightToday.AddSeconds(1), 100, 10),
            Entry(now, 50, 5),
            Entry(midnightToday.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59), 999, 999),
        ];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => now };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(2, s.TodayRequests);
        Assert.Equal(150, s.TodayInputTokens);
        Assert.Equal(15, s.TodayOutputTokens);
        Assert.Equal(now, s.LastRequestAt!.Value);
    }

    [Fact(DisplayName = "The speed is the newest finished request's tokens per second, not the older ones")]
    public async Task SpeedIsTheNewestRequestTps()
    {
        await using var ls = new FakeLlamaSwap();
        var now = DateTime.Now;
        ls.ActivityEntries = [
            Entry(now, 100, 10, tps: 58.42), // newest: the panel's speed
            Entry(now.AddMinutes(-1), 50, 5, tps: 40.31),
        ];
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        Assert.Equal(58.42, mon.Snapshot()!.GenTps!.Value, 2);
    }

    [Fact(DisplayName = "Today's count is the router's total for the day range, and every page is summed")]
    public async Task TodayCountIsExactFromTheDayRange()
    {
        var frozen = DateTime.Today.AddHours(12); // noon: the whole 1,100-entry log still fits in today
        await using var ls = new FakeLlamaSwap();
        ls.ActivityEntries = [.. Enumerable.Range(0, 1100).Select(i => Entry(frozen - TimeSpan.FromSeconds(i), 1, 2, tps: i == 0 ? 12.5 : null))];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        // the day range's total is exact, not a 999-entry lower bound; the tokens span both pages
        Assert.Equal(1100, s.TodayRequests);
        Assert.Equal(1100, s.TodayInputTokens);
        Assert.Equal(2200, s.TodayOutputTokens);
        Assert.Equal(12.5, s.GenTps!.Value, 2); // the newest finished request measured this speed itself
    }

    [Fact(DisplayName = "A day with no finished request still shows the newest request's speed and time")]
    public async Task EmptyDayKeepsTheNewestSpeedAndTime()
    {
        var frozen = DateTime.Today.AddHours(1);
        await using var ls = new FakeLlamaSwap();
        ls.ActivityEntries = [Entry(frozen.AddDays(-1), 100, 10, tps: 58.42)];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(0, s.TodayRequests); // nothing finished today
        Assert.Equal(58.42, s.GenTps!.Value, 2); // the newest finished request is still the speed
        Assert.NotNull(s.LastRequestAt);
    }

    [Fact(DisplayName = "An address with nothing listening is offline")]
    public async Task OfflineWhenNothingListens()
    {
        // a port that is free: TestPorts allocates and releases one, and the connection is refused
        var port = TestPorts.Listen();
        var url = port.Prefixes.Single().TrimEnd('/');
        ClosePort(port);
        var mon = Monitor(url);
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.False(s.Connected);
        Assert.Equal("offline", s.Error);
        Assert.Null(s.Version);
    }

    [Fact(DisplayName = "An empty address keeps the snapshot null")]
    public async Task EmptyUrlKeepsSnapshotNull()
    {
        var mon = Monitor("");
        await mon.RefreshAsync();
        Assert.False(mon.HasAddress); // so /api/llamaswap says "not configured", not "still loading"
        Assert.Null(mon.Snapshot()); // and a snapshot without an address would be a lie
    }

    [Fact(DisplayName = "The events stream keeps the in-flight list in step: snapshot, upsert, remove")]
    public async Task StreamKeepsInflightInStep()
    {
        await using var ls = new FakeLlamaSwap();
        var now = DateTime.Now; // frozen: the live elapsed time must not make the assertion drift
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => now, UtcNow = () => now };
        await mon.StreamOnceAsync(new Uri(ls.Url));
        Assert.Equal(["ModelB"], mon.Inflight.Select(r => r.Model)); // ModelA came in the snapshot, then got removed
        Assert.Equal("/v1/models", mon.Inflight[0].Path);
        Assert.Equal(50, mon.Inflight[0].ElapsedMs);
    }

    [Fact(DisplayName = "An over-long stream line is discarded and the events after it still apply")]
    public async Task OverlongStreamLineIsDiscarded()
    {
        await using var ls = new FakeLlamaSwap(oversizedLine: true);
        var mon = Monitor(ls.Url);
        await mon.StreamOnceAsync(new Uri(ls.Url));
        // the over-long line was skipped, not treated as EOF: the normal snapshot/upsert/remove still ran
        Assert.Equal(["ModelB"], mon.Inflight.Select(r => r.Model));
    }

    [Fact(DisplayName = "A router that ignores the day range still counts only today's entries")]
    public async Task RouterIgnoringTheDayRangeStillCountsOnlyToday()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { IgnoreStart = true };
        ls.ActivityEntries = [
            Entry(frozen, 5, 6),
            Entry(frozen.AddMinutes(-1), 7, 8),
            Entry(frozen.AddDays(-1), 999, 999),
        ];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(2, s.TodayRequests);
        Assert.Equal(12, s.TodayInputTokens);
        Assert.Equal(14, s.TodayOutputTokens);
    }

    [Fact(DisplayName = "A router that ignores the day range is walked only up to yesterday's newest entry")]
    public async Task RouterIgnoringTheDayRangeStopsAtYesterday()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { IgnoreStart = true, LimitCap = 1 }; // one entry per page
        ls.ActivityEntries = [
            Entry(frozen, 5, 6),
            .. Enumerable.Range(1, 6).Select(i => Entry(frozen.AddDays(-i), 999, 999)),
        ];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        Assert.Equal(2, ls.ActivityRequests); // today's page, then the first older entry ends the walk
        var s = mon.Snapshot()!;
        Assert.Equal(1, s.TodayRequests);
        Assert.Equal(5, s.TodayInputTokens);
        Assert.Equal(6, s.TodayOutputTokens);
    }

    [Fact(DisplayName = "A router that caps the page size below 999 still has every entry summed")]
    public async Task ActivityPageSizeCapStillSumsEveryEntry()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 100 };
        ls.ActivityEntries = [.. Enumerable.Range(0, 250).Select(i => Entry(frozen - TimeSpan.FromSeconds(i), 1, 2))];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(250, s.TodayRequests);
        Assert.Equal(250, s.TodayInputTokens);
        Assert.Equal(500, s.TodayOutputTokens);
    }

    [Fact(DisplayName = "A router that omits the total is walked until an empty page")]
    public async Task ActivityWithoutTotalWalksToTheEnd()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { OmitTotal = true };
        ls.ActivityEntries = [.. Enumerable.Range(0, 1100).Select(i => Entry(frozen - TimeSpan.FromSeconds(i), 1, 2))];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(1100, s.TodayRequests);
        Assert.Equal(1100, s.TodayInputTokens);
        Assert.Equal(2200, s.TodayOutputTokens);
    }

    [Fact(DisplayName = "A router reporting a huge total does not walk the request log without bound")]
    public async Task ActivityPaginationIsCapped()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 1 };
        // one nonempty page per request, with more pages than the cap: page 2 must not look like the end
        ls.ActivityEntries = [.. Enumerable.Range(0, LlamaSwapMonitor.MaxActivityPages + 1).Select(i => Entry(frozen - TimeSpan.FromSeconds(i), 1, 2))];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(LlamaSwapMonitor.MaxActivityPages, ls.ActivityRequests); // the cap itself ended the walk
        Assert.Equal(LlamaSwapMonitor.MaxActivityPages + 1, s.TodayRequests); // the reported total is still exact
        Assert.Null(s.TodayInputTokens); // an incomplete walk is not passed off as exact tokens
        Assert.Null(s.TodayOutputTokens);
    }

    [Fact(DisplayName = "The in-flight count rides along on the snapshot the phone reads")]
    public async Task InflightRidesAlongOnSnapshot()
    {
        await using var ls = new FakeLlamaSwap();
        var mon = Monitor(ls.Url);
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat/completions","elapsed_ms":9,"resp_bytes":1}}""");
        await mon.RefreshAsync();
        Assert.Equal(1, mon.Snapshot()!.Inflight);
    }

    [Fact(DisplayName = "The in-flight elapsed grows with the clock, and the per-request list rides on the snapshot")]
    public async Task InflightElapsedGrowsAndListRidesAlong()
    {
        await using var ls = new FakeLlamaSwap();
        var now = DateTime.Now;
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => now, UtcNow = () => now };
        await mon.StreamOnceAsync(new Uri(ls.Url));
        Assert.Equal(50, mon.Inflight[0].ElapsedMs); // seeded from the event
        now = now.AddSeconds(30);
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(1, s.Inflight);
        Assert.Equal(["ModelB"], s.InflightRequests.Select(r => r.Model));
        Assert.Equal([30_050], s.InflightRequests.Select(r => r.ElapsedMs));
        // a re-upsert of the same request keeps its original start; the router's own number does not reset the clock
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"2","model":"ModelB","req_path":"/v1/models","elapsed_ms":60000}}""");
        Assert.Equal(30_050, mon.Inflight[0].ElapsedMs);
        // removed and sent again: the clock starts over
        mon.ApplyInflightEvent("""{"operation":"remove","id":"2"}""");
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"2","model":"ModelB","req_path":"/v1/models","elapsed_ms":500}}""");
        Assert.Equal(500, mon.Inflight[0].ElapsedMs);
    }

    [Fact(DisplayName = "A request that outlives the limit without its remove event is dropped")]
    public void InflightAgedOutIsDropped()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { Now = () => now, UtcNow = () => now };
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat/completions"}}""");
        Assert.Single(mon.Inflight);
        now = now.Add(LlamaSwapMonitor.MaxInflightAge).AddMilliseconds(1);
        Assert.Empty(mon.Inflight); // a lost remove event cannot pin the count - and the fast poll - forever
    }

    [Fact(DisplayName = "Clearing the address drops the in-flight list")]
    public async Task ClearingTheAddressDropsInflight()
    {
        var mon = Monitor("");
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat/completions"}}""");
        await mon.RefreshAsync();
        Assert.Empty(mon.Inflight);
        Assert.Null(mon.Snapshot());
    }

    [Fact(DisplayName = "Going offline drops the in-flight list")]
    public async Task GoingOfflineDropsInflight()
    {
        // a port that is free: TestPorts allocates and releases one, and the connection is refused
        var port = TestPorts.Listen();
        var url = port.Prefixes.Single().TrimEnd('/');
        ClosePort(port);
        var mon = Monitor(url);
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat/completions"}}""");
        await mon.RefreshAsync();
        Assert.Empty(mon.Inflight);
        Assert.Equal(0, mon.Snapshot()!.Inflight);
    }

    [Fact(DisplayName = "A malformed in-flight event is dropped, not fatal")]
    public void MalformedInflightEventIsDropped()
    {
        var mon = Monitor("");
        mon.ApplyInflightEvent("not json");
        mon.ApplyInflightEvent("""{"operation":"frobnicate","request":{"id":"7","model":"M7"}}""");
        Assert.Empty(mon.Inflight);
    }

    [Fact(DisplayName = "A malformed in-flight entry is dropped without wiping the list it arrives in")]
    public void AMalformedEntryIsDroppedWithoutWipingTheList()
    {
        var frozen = DateTime.Today.AddHours(12);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = "", Now = () => frozen, UtcNow = () => frozen };
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat","elapsed_ms":9,"resp_bytes":1}}""");
        Assert.Single(mon.Inflight);
        // a snapshot whose first entry carries a non-integer elapsed_ms: that entry is dropped, the neighbour survives
        mon.ApplyInflightEvent("""{"operation":"snapshot","requests":[{"id":"a","elapsed_ms":1.5},{"id":"b","model":"M","req_path":"/v1/chat","elapsed_ms":7,"resp_bytes":1}]}""");
        Assert.Equal(["b"], mon.Inflight.Select(r => r.Id));
        Assert.Equal(7, mon.Inflight[0].ElapsedMs);
        // a malformed id in an upsert or a remove is a no-op, not a throw
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":1.5,"model":"M","elapsed_ms":3}}""");
        Assert.Equal(["b"], mon.Inflight.Select(r => r.Id));
        mon.ApplyInflightEvent("""{"operation":"remove","id":1.5}""");
        Assert.Equal(["b"], mon.Inflight.Select(r => r.Id));
    }

    [Fact(DisplayName = "A snapshot without a requests array keeps the live list")]
    public void ASnapshotWithoutRequestsKeepsTheLiveList()
    {
        var mon = Monitor("");
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat","elapsed_ms":9,"resp_bytes":1}}""");
        mon.ApplyInflightEvent("""{"operation":"snapshot"}""");
        Assert.Equal(["7"], mon.Inflight.Select(r => r.Id)); // malformed replacement, live list kept
        mon.ApplyInflightEvent("""{"operation":"snapshot","requests":[]}""");
        Assert.Empty(mon.Inflight); // an explicit empty array still clears it
    }

    [Fact(DisplayName = "A refresh for the old address cannot publish over the new router")]
    public async Task OlderRefreshCannotPublishOverNewAddress()
    {
        await using var old = new FakeLlamaSwap(versionDelayMs: 500) { Version = "old" };
        await using var newer = new FakeLlamaSwap { Version = "new" };
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = old.Url };
        var oldRefresh = mon.RefreshAsync();
        await Task.Delay(100); // the old refresh is past the gate and waiting for the old router's version body
        mon.UrlOverride = newer.Url;
        var newRefresh = mon.RefreshAsync(); // cancels the retired walk and waits for the gate
        await Task.WhenAll(oldRefresh, newRefresh);
        Assert.Equal("new", mon.Snapshot()!.Version);
    }

    [Fact(DisplayName = "A stream the lifetime gives up ends as a healthy end, not a failure")]
    public async Task LifetimeEndsTheStreamNormally()
    {
        await using var ls = new FakeLlamaSwap(keepEventsOpen: true);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, StreamLifetime = TimeSpan.FromMilliseconds(200) };
        var read = mon.StreamOnceAsync(new Uri(ls.Url));
        await read.WaitAsync(TimeSpan.FromSeconds(10)); // it returns on its own - and without throwing
    }

    [Fact(DisplayName = "Clearing the address stops an open stream, not only the next connection")]
    public async Task ClearingTheAddressStopsTheOpenStream()
    {
        await using var ls = new FakeLlamaSwap(keepEventsOpen: true);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        var read = mon.StreamOnceAsync(new Uri(ls.Url));
        await WaitUntil(() => mon.Inflight.Count > 0); // the snapshot and the upsert have been applied
        mon.UrlOverride = ""; // the address is cleared while the stream is still open
        await read.WaitAsync(TimeSpan.FromSeconds(10)); // the next line ends the loop
    }

    [Fact(DisplayName = "An object data and a numbered request id both reach the in-flight list")]
    public async Task ObjectDataAndNumericIdsReachInflight()
    {
        await using var ls = new FakeLlamaSwap(objectData: true);
        var mon = Monitor(ls.Url);
        await mon.StreamOnceAsync(new Uri(ls.Url));
        Assert.Contains(mon.Inflight, r => r.Id == "1");    // the snapshot event (a string data) parsed
        Assert.Contains(mon.Inflight, r => r.Id == "obj");  // the data was an object, not a string
        Assert.DoesNotContain(mon.Inflight, r => r.Id == "3"); // the remove found the numbered id
    }

    [Fact(DisplayName = "Changing the address ends the stream to the old address, not only the next connection")]
    public async Task ChangingTheAddressEndsTheOldStream()
    {
        await using var ls = new FakeLlamaSwap(keepEventsOpen: true);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        var read = mon.StreamOnceAsync(new Uri(ls.Url));
        await WaitUntil(() => mon.Inflight.Count > 0); // the snapshot and the upsert have been applied
        mon.UrlOverride = "http://127.0.0.1:1/elsewhere"; // the address is changed, not cleared
        await read.WaitAsync(TimeSpan.FromSeconds(10)); // the stream to the old address ends on the next line
    }

    [Fact(DisplayName = "An event from the old router is dropped, and one from the current router is kept")]
    public void EventFromAnOldRouterIsDropped()
    {
        var now = DateTime.Now; // frozen: the elapsed bookkeeping must not drift
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance)
        { UrlOverride = "http://127.0.0.1:1/current/", Now = () => now, UtcNow = () => now };
        const string late = """{"operation":"upsert","request":{"id":"late","model":"ModelLate","req_path":"/v1/chat","elapsed_ms":11,"resp_bytes":1}}""";
        // a line read before the address moved: its router is no longer the configured one
        mon.ApplyInflightEvent(late, new Uri("http://127.0.0.1:1/old/"));
        Assert.Empty(mon.Inflight);
        // control: the same event from the router the address points at now is accepted
        mon.ApplyInflightEvent(late, new Uri("http://127.0.0.1:1/current/"));
        Assert.Contains(mon.Inflight, r => r.Id == "late");
    }

    [Fact(DisplayName = "The wait for the stream's response headers gives up on its deadline")]
    public async Task HeaderWaitGivesUpOnItsDeadline()
    {
        var port = TestPorts.Listen(); // it listens but never answers: the client waits for headers that do not come
        var url = port.Prefixes.Single().TrimEnd('/');
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = url, HeaderWait = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mon.StreamOnceAsync(new Uri(url)));
        ClosePort(port);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "the deadline is far sooner than the stream lifetime");
    }

    [Fact(DisplayName = "An address change reaches the stream's header wait, not only a read that is already running")]
    public async Task AddressChangeCancelsTheHeaderWait()
    {
        var port = TestPorts.Listen(); // listens but never answers: the client sits in the header wait
        var url = port.Prefixes.Single().TrimEnd('/');
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = url, HeaderWait = TimeSpan.FromMinutes(5) };
        await mon.RefreshAsync(); // the address is on record, so the next refresh is the one that notices the change
        var read = mon.StreamOnceAsync(new Uri(url));
        await Task.Delay(200); // make sure it is in the wait
        mon.UrlOverride = "http://127.0.0.1:1/elsewhere";
        await mon.RefreshAsync(CancellationToken.None); // notices the change: the stream to the old address ends
        var cancelled = false;
        try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        ClosePort(port);
        Assert.True(cancelled, "the wait for the headers was not cancelled by the address change");
    }

    [Fact(DisplayName = "An address change refreshes at once, not at the next poll")]
    public async Task AddressChangeStopsTheStreamWithoutARefresh()
    {
        await using var ls = new FakeLlamaSwap(quietEvents: true);
        await using var other = new FakeLlamaSwap();
        var mon = new LlamaSwapMonitor(new ConfigStore(Path.Combine(_dir, "watch-config.json")), NullLogger<LlamaSwapMonitor>.Instance)
        { UrlOverride = ls.Url, Now = () => DateTime.Now, UtcNow = () => DateTime.UtcNow };
        await mon.StartAsync(CancellationToken.None); // the BackgroundService's own Start: PollLoop, StreamLoop and the watcher
        try
        {
            await WaitUntil(() => mon.Inflight.Count > 0); // the stream is open and has fed the list
            mon.UrlOverride = other.Url; // the watcher notices and refreshes it at once
            await WaitUntil(() => Volatile.Read(ref other.VersionRequests) >= 1); // the new router is asked, not at the next poll
        }
        finally
        {
            try { await mon.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "Changing the address clears the old list and ends the stream to a quiet old router")]
    public async Task ChangingTheAddressEndsTheStreamToAQuietRouter()
    {
        await using var ls = new FakeLlamaSwap(quietEvents: true);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        var read = mon.StreamOnceAsync(new Uri(ls.Url));
        await WaitUntil(() => mon.Inflight.Count > 0); // the snapshot arrived and the router went quiet
        await mon.RefreshAsync(CancellationToken.None); // the first poll: the address is on record
        mon.UrlOverride = "http://127.0.0.1:1/elsewhere";
        await mon.RefreshAsync(CancellationToken.None); // the next poll notices the new address
        Assert.Empty(mon.Inflight); // the old list does not ride on the new snapshot
        await read.WaitAsync(TimeSpan.FromSeconds(5)); // the stream ended - no line from the quiet router was needed
    }

    [Fact(DisplayName = "A failed version poll ends the stream, drops in-flight, and publishes offline")]
    public async Task AFailedRefreshEndsTheStreamAndGoesOffline()
    {
        await using var ls = new FakeLlamaSwap(keepEventsOpen: true, versionFails: true);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        var read = mon.StreamOnceAsync(new Uri(ls.Url));
        await WaitUntil(() => mon.Inflight.Count > 0); // the stream is alive and feeding
        await mon.RefreshAsync(CancellationToken.None); // the version endpoint answers 500
        Assert.Empty(mon.Inflight);
        Assert.False(mon.Snapshot()!.Connected);
        Assert.Equal("offline", mon.Snapshot()!.Error);
        await read.WaitAsync(TimeSpan.FromSeconds(5)); // EndStream ended it, rather than leaving it for StreamLifetime
    }

    [Fact(DisplayName = "A line read before the stream ended is dropped, not applied after the offline state")]
    public void EventFromAnEndedStreamIsDropped()
    {
        var now = DateTime.Now;
        var router = new Uri("http://127.0.0.1:1/");
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance)
        { UrlOverride = router.ToString(), Now = () => now, UtcNow = () => now };
        const string late = """{"operation":"upsert","request":{"id":"late","model":"ModelLate","req_path":"/v1/chat","elapsed_ms":11,"resp_bytes":1}}""";
        // the same router, but its stream was ended by a failed poll: the line it already read must not land
        mon.ApplyInflightEvent(late, router, new CancellationToken(canceled: true));
        Assert.Empty(mon.Inflight);
        // control: a stream that is still running applies the same line
        using var live = new CancellationTokenSource();
        mon.ApplyInflightEvent(late, router, live.Token);
        Assert.Contains(mon.Inflight, r => r.Id == "late");
    }

    [Fact(DisplayName = "A newly entered address ends the stream's reconnect wait at once")]
    public async Task NewAddressEndsTheReconnectWait()
    {
        await using var ls = new FakeLlamaSwap(quietEvents: true);
        // no address yet: the stream loop sits in its reconnect wait, here far longer than the test may take
        var mon = new LlamaSwapMonitor(new ConfigStore(Path.Combine(_dir, "wake-config.json")), NullLogger<LlamaSwapMonitor>.Instance)
        { UrlOverride = "", StreamRetryMin = TimeSpan.FromMinutes(10), StreamRetryMax = TimeSpan.FromMinutes(10) };
        await mon.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300); // the loop found no address and is waiting
            mon.UrlOverride = ls.Url; // the watcher notices within a second and wakes the wait
            await WaitUntil(() => mon.Inflight.Count > 0); // the stream to the new address is open and has fed the list
        }
        finally
        {
            try { await mon.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "A non-finite generation speed is dropped, the rest of the stats still publish")]
    public async Task NonFiniteSpeedIsDropped()
    {
        await using var ls = new FakeLlamaSwap();
        var now = DateTime.Today.AddHours(12);
        ls.ActivityEntries = [$"{{\"timestamp\":\"{Wire(now)}\",\"tokens\":{{\"input_tokens\":3,\"output_tokens\":4,\"tokens_per_second\":1e400}}}}"];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => now };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.True(s.Connected);
        Assert.Null(s.GenTps); // infinity would make the endpoint's JSON writer throw
        Assert.Equal(1, s.TodayRequests);
        System.Text.Json.JsonSerializer.Serialize(s); // and the snapshot serializes
    }

    [Fact(DisplayName = "A request finishing between two activity pages is not counted twice")]
    public async Task RowPushedOntoTheNextPageIsCountedOnce()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 2 };
        ls.ActivityEntries = [Row(3, frozen.AddMinutes(-1), 100), Row(2, frozen.AddMinutes(-2), 10), Row(1, frozen.AddMinutes(-3), 1)];
        ls.InsertAfterFirstPage = Row(4, frozen, 1000); // page 2 then starts with row 2 again
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(3, s.TodayRequests); // the total the first page reported
        Assert.Equal(111, s.TodayInputTokens); // rows 3, 2 and 1 once each - not row 2 twice
    }

    [Fact(DisplayName = "A repeated row does not count towards coverage, so the walk still reaches the oldest row")]
    public async Task RepeatedRowDoesNotEndTheWalkEarly()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 2 };
        ls.ActivityEntries = [Row(4, frozen.AddMinutes(-1), 1000), Row(3, frozen.AddMinutes(-2), 100), Row(2, frozen.AddMinutes(-3), 10), Row(1, frozen.AddMinutes(-4), 1)];
        ls.InsertAfterFirstPage = Row(5, frozen, 10000); // page 2 is then [3, 2], page 3 [1]
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(4, s.TodayRequests);
        Assert.Equal(1111, s.TodayInputTokens); // rows 4, 3, 2 and 1 once each - row 1 is not left out
        Assert.Equal(3, ls.ActivityRequests);
    }

    [Fact(DisplayName = "Requests finishing during the walk are left out, and the oldest row is still read")]
    public async Task RowsNewerThanTheWalkAreLeftOut()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 2 };
        ls.ActivityEntries = [Row(4, frozen.AddMinutes(-1), 1000), Row(3, frozen.AddMinutes(-2), 100), Row(2, frozen.AddMinutes(-3), 10), Row(1, frozen.AddMinutes(-4), 1)];
        // three finish after the first page: page 2 is then [5, 4], page 3 [3, 2], page 4 [1]
        ls.InsertManyAfterFirstPage = [Row(7, frozen, 70000), Row(6, frozen, 60000), Row(5, frozen, 50000)];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(4, s.TodayRequests); // the first page's total
        Assert.Equal(1111, s.TodayInputTokens); // rows 4 to 1, not row 5
    }

    [Fact(DisplayName = "A row that is not an object does not count towards the total")]
    public async Task NonObjectRowLeavesTheSumsUnknown()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { IgnoreStart = true }; // the fake's day filter reads objects only
        ls.ActivityEntries = [Row(1, frozen, 5), "null"];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(2, s.TodayRequests);
        Assert.Null(s.TodayInputTokens); // one row's tokens are not a two-request total
    }

    [Fact(DisplayName = "Without row ids, a total that moves between pages leaves the sums unknown")]
    public async Task MovingTotalWithoutIdsLeavesTheSumsUnknown()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 2 };
        ls.ActivityEntries = [Entry(frozen.AddMinutes(-1), 1000, 1), Entry(frozen.AddMinutes(-2), 100, 1), Entry(frozen.AddMinutes(-3), 10, 1)];
        ls.InsertAfterFirstPage = Entry(frozen, 5, 1);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(3, s.TodayRequests);
        Assert.Null(s.TodayInputTokens);
    }

    [Fact(DisplayName = "A broken optional activity page leaves the router online with unknown token totals")]
    public async Task BrokenActivityPageKeepsTheRouterOnline()
    {
        var frozen = DateTime.Today.AddHours(12);
        await using var ls = new FakeLlamaSwap { LimitCap = 1, BrokenActivityFromPage = 2 };
        ls.ActivityEntries = [Entry(frozen, 1, 2), Entry(frozen.AddMinutes(-1), 1, 2)];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.True(s.Connected); // the version answered: a cut-off log page is missing data, not an offline router
        Assert.Equal(2, s.TodayRequests); // the first page's total still stands
        Assert.Null(s.TodayInputTokens); // the sums it could not finish are unknown, not partial
    }

    [Fact(DisplayName = "An empty day without a total still shows the newest request's speed and time")]
    public async Task EmptyDayWithoutTotalKeepsTheNewestSpeed()
    {
        var frozen = DateTime.Today.AddHours(1);
        await using var ls = new FakeLlamaSwap { OmitTotal = true };
        ls.ActivityEntries = [Entry(frozen.AddDays(-1), 100, 10, tps: 58.42)];
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url, Now = () => frozen };
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.Equal(0, s.TodayRequests);
        Assert.Equal(58.42, s.GenTps!.Value, 2);
        Assert.NotNull(s.LastRequestAt);
    }

    [Fact(DisplayName = "An offline snapshot sends no in-flight rows to the phone")]
    public async Task OfflineSnapshotSendsNoRows()
    {
        await using var ls = new FakeLlamaSwap(versionFails: true);
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync(); // offline
        // a stream that reconnected before the next good poll fed a row
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","elapsed_ms":5}}""");
        Assert.NotEmpty(mon.Inflight);
        var (snapshot, rows) = mon.ReadForPhone();
        Assert.False(snapshot!.Connected);
        Assert.Empty(rows);
    }

    [Fact(DisplayName = "The phone gets the live rows with a connected snapshot, and nothing for a moved address")]
    public async Task ReadForPhonePairsTheSnapshotWithItsRouter()
    {
        await using var ls = new FakeLlamaSwap();
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","elapsed_ms":5}}""");
        var (snapshot, rows) = mon.ReadForPhone();
        Assert.True(snapshot!.Connected);
        Assert.Equal("7", Assert.Single(rows).Id);
        mon.UrlOverride = "http://127.0.0.1:1/elsewhere";
        var (moved, movedRows) = mon.ReadForPhone();
        Assert.Null(moved); // "loading" for the new router, not the old router's numbers
        Assert.Empty(movedRows);
    }

    [Fact(DisplayName = "A failed stream reconnect drops the rows the last stream left")]
    public async Task FailedReconnectDropsTheRows()
    {
        await using var ls = new FakeLlamaSwap { EventsFail = true };
        var mon = new LlamaSwapMonitor(new ConfigStore(Path.Combine(_dir, "events-config.json")), NullLogger<LlamaSwapMonitor>.Instance)
        { UrlOverride = ls.Url };
        // the last stream ended without its remove event
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","elapsed_ms":5}}""");
        await mon.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => mon.Inflight.Count == 0); // the reconnect answered 503: nothing feeds the row now
        }
        finally
        {
            try { await mon.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "A saved new refresh interval applies without waiting out the old one")]
    public async Task NewRefreshIntervalAppliesAtOnce()
    {
        await using var ls = new FakeLlamaSwap();
        var path = Path.Combine(_dir, "interval-config.json");
        File.WriteAllText(path, """{"llamaSwap":{"refreshSeconds":3600}}""");
        var mon = new LlamaSwapMonitor(new ConfigStore(path), NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        await mon.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => Volatile.Read(ref ls.VersionRequests) >= 1); // the first poll, then an hour's wait
            await Task.Delay(1500); // the watcher has seen the hour-long interval
            var before = Volatile.Read(ref ls.VersionRequests);
            File.WriteAllText(path, """{"llamaSwap":{"refreshSeconds":10}}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // a distinct stamp, however coarse the clock
            await WaitUntil(() => Volatile.Read(ref ls.VersionRequests) > before); // polled again within seconds
        }
        finally
        {
            try { await mon.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "A stream still waiting for its headers is not a healthy stream")]
    public async Task PendingHeadersAreNotHealthy()
    {
        await using var ls = new FakeLlamaSwap(keepEventsOpen: true, versionFails: true, eventsDelayMs: 2000);
        var mon = new LlamaSwapMonitor(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = ls.Url };
        mon.ApplyInflightEvent("""{"operation":"upsert","request":{"id":"7","model":"M7","req_path":"/v1/chat/completions"}}""");
        var read = mon.StreamOnceAsync(new Uri(ls.Url)); // the request is out, the headers are not back yet
        await Task.Delay(200);
        await mon.RefreshAsync(CancellationToken.None); // the REST endpoints fail while the headers are pending
        Assert.Empty(mon.Inflight);
        Assert.False(mon.Snapshot()!.Connected); // offline, not the previous connected snapshot
        // the refresh cancelled the pending stream: a read that only ended on the wait's timeout was never stopped
        var ended = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(ended);
    }

    [Fact(DisplayName = "The old router's numbers do not ride on while a new router is still answering")]
    public async Task ChangingTheAddressClearsTheOldSnapshot()
    {
        await using var ls = new FakeLlamaSwap();
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        Assert.NotNull(mon.Snapshot());
        await using var ls2 = new FakeLlamaSwap(versionDelayMs: 400);
        mon.UrlOverride = ls2.Url;
        var refresh = mon.RefreshAsync(CancellationToken.None); // the new router is still answering
        Assert.Null(mon.Snapshot()); // the panel says "loading", not "connected to the old router"
        await refresh;
        Assert.NotNull(mon.Snapshot()); // and the new router's numbers arrive with its own snapshot
    }

    [Fact(DisplayName = "A changed address hides the old snapshot until the next refresh has run for it")]
    public async Task ChangedAddressHidesTheSnapshotUntilTheNextRefresh()
    {
        await using var ls = new FakeLlamaSwap();
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        Assert.NotNull(mon.Snapshot());
        mon.UrlOverride = "http://127.0.0.1:1/elsewhere"; // a new address, but no poll has run for it yet
        Assert.Null(mon.Snapshot()); // the old router's numbers do not keep showing as if they were the new one's
        mon.UrlOverride = ls.Url;
        Assert.NotNull(mon.Snapshot()); // ... and the address the snapshot was polled for still shows it
    }

    [Fact(DisplayName = "The in-flight view sends the id, the model and the elapsed time - nothing else")]
    public void InflightViewSendsNothingOfTheRequestItself()
    {
        var view = LlamaSwapMonitor.InflightView(new[]
        {
            new LlamaSwapInflight("7", "M7", "/v1/chat/completions", 9, 1_000),
        });
        var json = System.Text.Json.JsonSerializer.Serialize(view, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateArray().Single().EnumerateObject().Select(p => p.Name);
        Assert.Equal(["elapsedMs", "id", "model"], keys.OrderBy(k => k).ToArray()); // no req_path, no resp_bytes
    }

    [Fact(DisplayName = "An address with a path keeps the path under the api endpoints")]
    public async Task AddressWithPathKeepsThePath()
    {
        await using var ls = new FakeLlamaSwap(basePath: "/llama");
        var mon = Monitor(ls.Url); // ends in /llama/, like Normalize keeps it
        await mon.RefreshAsync();
        Assert.True(mon.Snapshot()!.Connected); // api/version resolved under the path, not at the host root
    }

    /// <summary>Closes a test listener without letting the platform's cleanup race fail the test itself.</summary>
    private static void ClosePort(HttpListener port)
    {
        try { port.Stop(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        try { port.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
    }

    private static async Task WaitUntil(Func<bool> condition, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "the condition never held");
            await Task.Delay(25);
        }
    }

    [Fact(DisplayName = "A missing activity log leaves today's counts and the speed empty")]
    public async Task MissingActivityDegraded()
    {
        await using var ls = new FakeLlamaSwap(activity: false);
        var mon = Monitor(ls.Url);
        await mon.RefreshAsync();
        var s = mon.Snapshot()!;
        Assert.True(s.Connected);
        Assert.Null(s.TodayRequests);
        Assert.Null(s.GenTps); // the speed is a finished request's measurement: no log, no measurement
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LlamaSwapMonitor Monitor(string url)
        => new(NullLogger<LlamaSwapMonitor>.Instance) { UrlOverride = url };

    // the router's wire format, in the invariant culture: a th-TH runner would otherwise write a Buddhist year
    private static string Wire(DateTime local)
        => local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string Row(long id, DateTime local, long input)
        => $"{{\"id\":{id},\"timestamp\":\"{Wire(local)}\",\"tokens\":{{\"input_tokens\":{input},\"output_tokens\":1}}}}";

    private static string Entry(DateTime local, long input, long output, double? tps = null)
    {
        var tpsJson = tps is null ? "" : $",\"tokens_per_second\":{tps.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return $"{{\"timestamp\":\"{Wire(local)}\",\"tokens\":{{\"input_tokens\":{input},\"output_tokens\":{output}{tpsJson}}}}}";
    }
}

/// <summary>A llama-swap server: /api/version, /v1/models, /api/metrics/activity, /api/events.</summary>
internal sealed class FakeLlamaSwap : IAsyncDisposable
{
    private readonly HttpListener _server = TestPorts.Listen();
    private readonly bool _activity;
    private readonly bool _keepEventsOpen; // /api/events then stays open and sends keep-alive comments
    private readonly bool _objectData; // the in-flight events carry their data as an object, not a string
    private readonly bool _oversizedLine; // /api/events starts with one line longer than the client's cap
    private readonly bool _quietEvents; // /api/events stays open but sends nothing after the first events
    private readonly bool _versionFails; // /api/version then answers 500
    private readonly int _versionDelay; // /api/version waits this long before answering
    private readonly int _eventsDelayMs; // /api/events waits this long before sending its headers
    private readonly string _base;
    private readonly Task _loop;
    public string Url => _server.Prefixes.Single().TrimEnd('/') + (_base.Length > 0 ? _base + "/" : "");
    public volatile string[] ActivityEntries = [];
    public int? TotalOverride { get; set; }
    public int ActivityRequests;
    public bool IgnoreStart { get; set; }
    public bool OmitTotal { get; set; }
    public int? LimitCap { get; set; }
    public string Version { get; set; } = "v275";
    public int VersionRequests;
    public string? InsertAfterFirstPage;
    /// <summary>Several requests finish right after the first page (newest first).</summary>
    public string[]? InsertManyAfterFirstPage;
    /// <summary>From this activity page on, the body is cut off mid-JSON.</summary>
    public int? BrokenActivityFromPage { get; set; }
    /// <summary>/api/events answers 503.</summary>
    public bool EventsFail { get; set; }

    public FakeLlamaSwap(bool activity = true, bool keepEventsOpen = false, bool objectData = false, string basePath = "", bool quietEvents = false, bool versionFails = false, int versionDelayMs = 0, int eventsDelayMs = 0, bool oversizedLine = false)
    {
        _activity = activity;
        _keepEventsOpen = keepEventsOpen;
        _objectData = objectData;
        _oversizedLine = oversizedLine;
        _quietEvents = quietEvents;
        _versionFails = versionFails;
        _versionDelay = versionDelayMs;
        _eventsDelayMs = eventsDelayMs;
        _base = basePath.Length > 0 ? (basePath.StartsWith('/') ? basePath : "/" + basePath).TrimEnd('/') : "";
        _loop = Task.Run(Serve);
    }


    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => Answer(ctx));
        }
    }

    private async Task Answer(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url!;
        var path = url.AbsolutePath;
        if (_base.Length > 0 && !path.StartsWith(_base + "/", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Close();
            return;
        }
        if (path == _base + "/api/events" && EventsFail)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.OutputStream.Close();
            return;
        }
        if (path == _base + "/api/events" && (_keepEventsOpen || _quietEvents))
        {
            _ = Task.Run(() => KeepEventsOpen(ctx));
            return;
        }
        var p = _base.Length > 0 && path.StartsWith(_base, StringComparison.Ordinal) ? path[_base.Length..] : path;
        string body = "{}";
        var status = 200;
        var contentType = "application/json";
        switch (p)
        {
            case "/api/version":
                Interlocked.Increment(ref VersionRequests);
                body = "{\"version\":\"" + Version + "\",\"commit\":\"e0274d5\",\"build_date\":\"2026-11-20T07:15:47Z\"}";
                if (_versionFails) status = 500;
                if (_versionDelay > 0) await Task.Delay(_versionDelay);
                break;
            case "/v1/models":
                body = """{"data":[{"id":"Qwen3.8-27B-GGUF","status":{"value":"loaded"}},{"id":"Bonsai-27B-Q1_0","status":{"value":"unloaded"}},{"id":"NoStatus"}]}""";
                break;
            case "/api/metrics/activity" when _activity:
            {
                Interlocked.Increment(ref ActivityRequests);
                var qs = ctx.Request.QueryString;
                DateTime? start = DateTime.TryParse(qs["start"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var st) ? st : null;
                var entries = ActivityEntries.Where(e =>
                {
                    if (start is null || IgnoreStart) return true;
                    using var doc = System.Text.Json.JsonDocument.Parse(e);
                    return doc.RootElement.TryGetProperty("timestamp", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                        && DateTime.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)
                        && ts >= start;
                }).ToArray();
                var limit = int.TryParse(qs["limit"], out var l) && l > 0 ? l : 999;
                if (LimitCap is { } cap && cap > 0) limit = Math.Min(limit, cap);
                var page = int.TryParse(qs["page"], out var pg) && pg > 0 ? pg : 1;
                var total = OmitTotal ? "" : ",\"total\":" + (TotalOverride ?? entries.Length);
                body = "{\"data\":[" + string.Join(",", entries.Skip((page - 1) * limit).Take(limit)) + "],\"page\":" + page + ",\"limit\":" + limit + total + "}";
                if (BrokenActivityFromPage is { } broken && page >= broken) body = body[..(body.Length / 2)];
                // a request finishes right after the first page was served: it lands at the top of the log
                if (page == 1 && start is not null && Interlocked.Exchange(ref InsertAfterFirstPage, null) is { } fresh)
                    ActivityEntries = [fresh, .. ActivityEntries];
                if (page == 1 && start is not null && Interlocked.Exchange(ref InsertManyAfterFirstPage, null) is { } many)
                    ActivityEntries = [.. many, .. ActivityEntries];
                break;
            }
            case "/api/events":
                // the router's event stream: one in-flight event per line, a foreign event in between
                body = EventsBody() + "\n";
                contentType = "text/event-stream";
                break;
            default:
                status = 404;
                break;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private string EventsBody()
    {
        var oversized = _oversizedLine ? "data:" + new string('x', LlamaSwapMonitor.MaxLineChars + 16) + "\n" : "";
        return oversized + (_objectData
        ? "event:message\n"
          + "data:{\"type\":\"inflight\",\"data\":\"{\\\"operation\\\":\\\"snapshot\\\",\\\"requests\\\":[{\\\"id\\\":\\\"1\\\",\\\"model\\\":\\\"ModelA\\\",\\\"req_path\\\":\\\"/v1/chat/completions\\\",\\\"elapsed_ms\\\":1234,\\\"resp_bytes\\\":56}]}\"}\n\n"
          + "event:message\n"
          + "data:{\"type\":\"inflight\",\"data\":{\"operation\":\"upsert\",\"request\":{\"id\":\"obj\",\"model\":\"ModelObj\",\"req_path\":\"/v1/models\",\"elapsed_ms\":50,\"resp_bytes\":0}}}\n\n"
          + "data:{\"type\":\"inflight\",\"data\":\"{\\\"operation\\\":\\\"upsert\\\",\\\"request\\\":{\\\"id\\\":3,\\\"model\\\":\\\"ModelNum\\\",\\\"req_path\\\":\\\"/v1/chat\\\",\\\"elapsed_ms\\\":7,\\\"resp_bytes\\\":0}}\"}\n\n"
          + "data:{\"type\":\"inflight\",\"data\":\"{\\\"operation\\\":\\\"remove\\\",\\\"id\\\":3}\"}\n"
        : """
          event:message
          data:{"type":"inflight","data":"{\"operation\":\"snapshot\",\"requests\":[{\"id\":\"1\",\"model\":\"ModelA\",\"req_path\":\"/v1/chat/completions\",\"elapsed_ms\":1234,\"resp_bytes\":56}]}"}

          event:message
          data:{"type":"inflight","data":"{\"operation\":\"upsert\",\"request\":{\"id\":\"2\",\"model\":\"ModelB\",\"req_path\":\"/v1/models\",\"elapsed_ms\":50,\"resp_bytes\":0}}"}

          data:{"type":"modelStatus","data":"[]"}

          data:{"type":"inflight","data":"{\"operation\":\"remove\",\"id\":1}"}
          """);
    }

    private async Task KeepEventsOpen(HttpListenerContext ctx)
    {
        try
        {
            if (_eventsDelayMs > 0) await Task.Delay(_eventsDelayMs);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            var outStream = ctx.Response.OutputStream;
            var sse = System.Text.Encoding.UTF8.GetBytes(EventsBody() + "\n");
            await outStream.WriteAsync(sse, 0, sse.Length);
            await outStream.FlushAsync();
            if (_quietEvents)
            {
                // a quiet router: the connection stays open and nothing else comes over it
                while (_server.IsListening) await Task.Delay(1000);
                return;
            }
            while (_server.IsListening)
            {
                var ping = System.Text.Encoding.UTF8.GetBytes(": ping\n\n");
                await outStream.WriteAsync(ping, 0, ping.Length);
                await outStream.FlushAsync();
                await Task.Delay(50);
            }
        }
        catch (Exception) { // the reader went away: nothing to answer to
        }
    }

    public async ValueTask DisposeAsync()
    {
        _server.Stop();
        try { _server.Close(); } catch (HttpListenerException) { } // on Linux the socket can linger past Stop
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }
}
