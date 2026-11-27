using System.Text;
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

    [Fact]
    public void PartialRefreshRetainsPreviousQuoteAndRejectsInvalidPrices()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        Assert.Null(store.Save("""{"stocks":["sh000001","hk00700"]}"""));
        using var feed = new QuoteFeed();
        using var http = new HttpClient(feed);
        var stocks = new StockService(store, NullLogger<StockService>.Instance, http);

        feed.Body = Quote("sh000001", "3375.21") + Quote("hk00700", "410.8");
        var first = stocks.Snapshot();
        Assert.Equal(2, first.Quotes.Count);
        Assert.Null(first.Error);

        Assert.Null(store.Save("""{"stocks":["sh000001","hk00700","usaapl"]}"""));
        feed.Body = Quote("sh000001", "3377.25") + Quote("usAAPL", "0");
        var partial = stocks.Snapshot();
        Assert.Equal("incomplete quotes", partial.Error);
        Assert.Equal(first.Updated, partial.Updated);
        Assert.Collection(partial.Quotes,
            quote => Assert.Equal(3377.25, quote.Price),
            quote => Assert.Equal(first.Quotes[1], quote));
    }

    [Fact]
    public void QuotesCarryEnglishNamesForUsAndHongKongSymbols()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        Assert.Null(store.Save("""{"stocks":["usaapl","hk00700","sh000001"]}"""));
        using var feed = new QuoteFeed
        {
            Body = Quote("usAAPL", "355.58", "苹果", "Apple Inc.")
                + Quote("hk00700", "352.4", "腾讯控股", "TENCENT")
                + Quote("sh000001", "3375.21", "上证指数")
        };
        using var http = new HttpClient(feed);
        var stocks = new StockService(store, NullLogger<StockService>.Instance, http);

        var snapshot = stocks.Snapshot();

        Assert.Collection(snapshot.Quotes,
            quote =>
            {
                Assert.Equal("苹果", quote.Name);
                Assert.Equal("Apple Inc.", quote.EnglishName);
            },
            quote =>
            {
                Assert.Equal("腾讯控股", quote.Name);
                Assert.Equal("TENCENT", quote.EnglishName);
            },
            quote =>
            {
                Assert.Equal("上证指数", quote.Name);
                Assert.Null(quote.EnglishName);
            });
    }

    private static string Quote(string code, string price, string? name = null, string? englishName = null)
    {
        var fields = new string[47];
        fields[1] = name ?? code;
        fields[3] = price;
        fields[45] = "123456.78"; // market value, never a fallback share price
        if (englishName is not null) fields[46] = englishName;
        return $"v_{code}=\"{string.Join('~', fields)}\";\n";
    }

    [Fact]
    public async Task MinuteDataDropsInvalidPricesAndRejectsAnEmptySeries()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        Assert.Null(store.Save("""{"stocks":["sh000001","hk00700"]}"""));
        using var feed = new QuoteFeed
        {
            Body = """
                {"data":{
                  "sh000001":{"data":{"date":"20261125","data":["0930 0 0 0","0931 -1 0 0","0932 3375.21 0 0"]}},
                  "hk00700":{"data":{"date":"20261125","data":["0930 0 0 0"]}}
                }}
                """
        };
        using var http = new HttpClient(feed);
        var stocks = new StockService(store, NullLogger<StockService>.Instance, http);

        var minutes = await stocks.MinutesAsync("sh000001");
        Assert.Null(minutes.Error);
        Assert.Equal(new object[] { "0932", 3375.21 }, Assert.Single(minutes.Points!));
        var unavailable = await stocks.MinutesAsync("hk00700");
        Assert.Null(unavailable.Points);
        Assert.NotNull(unavailable.Error);
    }

    private sealed class QuoteFeed : HttpMessageHandler
    {
        public string Body { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.GetEncoding("GB18030").GetBytes(Body)) });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
