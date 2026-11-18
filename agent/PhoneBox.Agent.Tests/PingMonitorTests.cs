using PhoneBox.Agent;
using Xunit;

public sealed class PingMonitorTests
{
    private static void WithConfig(string json, Action<ConfigStore> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-ping-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { check(new ConfigStore(path)); }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void ExistingConfigsKeepPingingTheDefaultHost() => WithConfig("{}", store =>
        Assert.Equal(PingMonitor.DefaultHost, store.Get().Ping.Host));

    [Theory]
    [InlineData("""{"ping":{"host":""}}""", "")]
    [InlineData("""{"ping":{"host":null}}""", "")]
    [InlineData("""{"ping":{"host":"  192.168.1.1 "}}""", "192.168.1.1")]
    [InlineData("""{"ping":{"host":"bad host"}}""", PingMonitor.DefaultHost)]
    public void HostIsTrimmedEmptyMeansOffAndJunkFallsBack(string json, string expected) => WithConfig(json, store =>
        Assert.Equal(expected, store.Get().Ping.Host));

    [Theory]
    [InlineData("current.example", true)]
    [InlineData("next.example", false)]
    [InlineData("", false)]
    public void ChangingTheTargetImmediatelyHidesPreviousMeasurements(string newHost, bool visible) =>
        WithConfig("""{"ping":{"host":"current.example"}}""", store =>
        {
            var monitor = new PingMonitor(store);
            monitor.Sample(12, "current.example");
            Assert.Equal("current.example", monitor.Snapshot()?.Host);

            Assert.Null(store.Save(System.Text.Json.JsonSerializer.Serialize(new { ping = new { host = newHost } })));
            monitor.Sample(15, "current.example"); // an already-started old probe completes after the save
            var actual = monitor.Snapshot()?.Host;
            var expected = visible ? "current.example" : null;
            Assert.True(actual == expected, $"EXPECTED: {expected ?? "no snapshot"} | ACTUAL: {actual ?? "no snapshot"}");
        });

    [Fact]
    public async Task AnEmptyHostSendsNothingAndClearsTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-ping-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"ping":{"host":""}}""");
        try
        {
            var monitor = new PingMonitor(new ConfigStore(path));
            monitor.Sample(12, "stale"); // as if a host had been set before
            using var cts = new CancellationTokenSource();
            await monitor.StartAsync(cts.Token);
            // .NET 10 runs ExecuteAsync on a thread-pool thread, so the first pass (which clears the old host's
            // snapshot) has no fixed start time; a busy CI machine took longer than a fixed 300 ms
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (monitor.Snapshot() is not null && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Null(monitor.Snapshot());
            await monitor.StopAsync(CancellationToken.None);
        }
        finally { File.Delete(path); }
    }
}
