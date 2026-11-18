using PhoneBox.Agent;
using Xunit;

public sealed class ConfigLegacyOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-legacy-" + Guid.NewGuid().ToString("N"));

    public ConfigLegacyOptionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("[\"cpu\",42,\"ram\"]")]
    [InlineData("[false,\"cpu\",{},null,[],\"ram\"]")]
    [InlineData("[\"cpu\",\"ram\"]")]
    public void LegacyHomeKeepsStringTilesAndSkipsOtherJsonTypes(string tiles)
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Null(store.Save("{\"pages\":[{\"type\":\"home\",\"options\":{\"tiles\":" + tiles + "}}]}"));
        Assert.Equal(["cpu", "ram"], store.Get().Pages.Single().Widgets!
            .Where(w => w.Type == "stat").Select(w => w.Str("metric")));
    }

    [Theory]
    [InlineData("\"three\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("2.5")]
    [InlineData("2147483648")]
    public void LegacyDeckIgnoresColumnsThatAreNotAnInteger(string columns)
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Null(store.Save("{\"pages\":[{\"type\":\"deck\",\"options\":{\"columns\":" + columns + "}}]}"));
        Assert.Equal(4, store.Get().Pages.Single().Widgets!.Single(w => w.Type == "shortcuts").Int("columns"));
    }

    [Fact]
    public void LegacyDeckPreservesNumericColumns()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Null(store.Save("""{"pages":[{"type":"deck","options":{"columns":3}}]}"""));
        Assert.Equal(3, store.Get().Pages.Single().Widgets!.Single(w => w.Type == "shortcuts").Int("columns"));
    }
}
