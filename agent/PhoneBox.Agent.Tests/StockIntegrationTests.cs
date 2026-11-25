using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class StockIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("phonebox-markets-").FullName;

    [Fact]
    public void NormalizesTheWatchlist()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
            {"stocks":[" SH000001 ","sh000001","HK00700","usAAPL","hk5","usMicrosoft","sh123456?"]}
            """);
        var cfg = new ConfigStore(path).Get();

        Assert.Equal(["sh000001", "hk00700", "usaapl"], cfg.Stocks);
    }

    [Fact]
    public async Task ChartRequestsOutsideTheWatchlistDoNotReachTheFeed()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        var stocks = new StockService(store, NullLogger<StockService>.Instance);
        var rejected = await stocks.MinutesAsync("sh000001");
        Assert.Null(rejected.Points);
        Assert.Equal("code not in watchlist", rejected.Error);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
