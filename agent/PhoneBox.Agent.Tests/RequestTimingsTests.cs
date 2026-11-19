using PhoneBox.Agent;
using Xunit;

public sealed class RequestTimingsTests
{
    [Fact]
    public void SummaryGivesCountMedianSlowestAndTheSlowShare()
    {
        var t = new RequestTimings();
        foreach (var ms in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 1000 }) t.Add("POST /api/volume", ms);
        t.Add("GET /api/state", 5);

        var rows = t.Summary();
        var volume = rows.Single(r => r.Route == "POST /api/volume");
        Assert.Equal(10, volume.Count);
        Assert.Equal(55, volume.P50Ms);
        Assert.Equal(1000, volume.MaxMs);
        Assert.Equal(1, volume.Slow); // over 300 ms
        Assert.Equal(1, rows.Single(r => r.Route == "GET /api/state").Count);
    }

    [Fact]
    public void OnlyTheLatestRequestsOfARouteAreKept()
    {
        var t = new RequestTimings(keep: 5);
        for (var i = 1; i <= 20; i++) t.Add("POST /api/volume", i * 100);
        var row = t.Summary().Single();
        Assert.Equal(20, row.Count); // every request is counted
        Assert.Equal(2000, row.MaxMs);
        Assert.Equal(1800, row.P50Ms); // the median of the last five: 1600..2000
    }

    [Fact]
    public void DistinctRoutesAreCappedAndTheRestShareOneRow()
    {
        // a client that sends many different paths must not grow the table: only the first few routes get a row
        var t = new RequestTimings(keep: 5, maxRoutes: 3);
        foreach (var r in new[] { "GET /a", "GET /b", "GET /c" }) t.Add(r, 10);
        for (var i = 0; i < 100; i++) t.Add("GET /junk" + i, 20);

        var rows = t.Summary();
        Assert.Equal(4, rows.Count); // three routes and the shared row
        Assert.Equal(100, rows.Single(r => r.Route == RequestTimings.OtherRoute).Count);
        t.Add("GET /a", 10); // a route that has its row is still counted under its own name
        Assert.Equal(2, t.Summary().Single(r => r.Route == "GET /a").Count);
    }

    [Fact]
    public void OnlyARequestThatMatchedAnEndpointHasARoute()
    {
        // the pattern, not the path: /api/approvals/abc and /api/approvals/def are one route
        var matched = new Microsoft.AspNetCore.Routing.RouteEndpoint(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/approvals/{id}"), 0,
            Microsoft.AspNetCore.Http.EndpointMetadataCollection.Empty, "approvals");
        Assert.Equal("POST /api/approvals/{id}", RequestTimings.RouteOf(matched, "POST"));
        Assert.Null(RequestTimings.RouteOf(null, "GET")); // no endpoint: an unknown path, not recorded
    }
}
