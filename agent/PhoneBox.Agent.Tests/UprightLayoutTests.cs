using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>A page's own upright layout (Settings › Pages › Upright): what the agent keeps and what the phone gets.</summary>
public class UprightLayoutTests
{
    private static AgentConfig Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-upright-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    private const string Page = """
        {"pages": [{"id": "games", "title": "Games", "upright": true, "widgets": [
          {"id": "a", "type": "note", "x": 0, "y": 0, "w": 5, "h": 2, "upright": {"x": 5, "y": 11, "w": 4, "h": 3}},
          {"id": "b", "type": "note", "x": 0, "y": 2, "w": 12, "h": 4, "upright": {"x": 0, "y": 0, "w": 9, "h": 20}},
          {"id": "c", "type": "note", "x": 5, "y": 0, "w": 7, "h": 2}
        ]}, {"id": "plain", "title": "Plain", "widgets": [
          {"id": "d", "type": "note", "x": 0, "y": 0, "w": 4, "h": 2, "upright": {"x": 1, "y": 1, "w": 2, "h": 2}}
        ]}]}
        """;

    [Fact]
    public void UprightPlacesStayInsideTheUprightGrid()
    {
        var pages = Load(Page).Pages;
        var games = pages.Single(p => p.Id == "games");
        Assert.True(games.Upright);
        var a = games.Widgets!.Single(w => w.Id == "a").Upright!;
        Assert.Equal((2, 9, 4, 3), (a.X, a.Y, a.W, a.H)); // moved back inside the 6 × 12 grid, size kept
        var b = games.Widgets!.Single(w => w.Id == "b").Upright!;
        Assert.Equal((0, 0, Catalog.UprightCols, Catalog.UprightRows), (b.X, b.Y, b.W, b.H));
        Assert.Null(games.Widgets!.Single(w => w.Id == "c").Upright); // left off the upright layout
        // the landscape places are untouched
        var landscape = games.Widgets!.Single(w => w.Id == "b");
        Assert.Equal((0, 2, 12, 4), (landscape.X, landscape.Y, landscape.W, landscape.H));
    }

    [Fact]
    public void APageWithoutItsOwnUprightLayoutDropsUprightPlaces()
    {
        var plain = Load(Page).Pages.Single(p => p.Id == "plain");
        Assert.Null(plain.Upright);
        Assert.Null(plain.Widgets!.Single().Upright);
        var off = Load(Page.Replace("\"upright\": true", "\"upright\": false")).Pages.Single(p => p.Id == "games");
        Assert.Null(off.Upright);
        Assert.All(off.Widgets!, w => Assert.Null(w.Upright));
    }

    [Fact]
    public void ThePhoneGetsTheUprightLayout()
    {
        var pub = JsonSerializer.SerializeToElement(Load(Page).ToPublic(), ConfigStore.WriteOpts);
        var pages = pub.GetProperty("pages").EnumerateArray().ToList();
        var games = pages.Single(p => p.GetProperty("id").GetString() == "games");
        Assert.True(games.GetProperty("upright").GetBoolean());
        var a = games.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("id").GetString() == "a").GetProperty("upright");
        Assert.Equal(2, a.GetProperty("x").GetInt32());
        Assert.Equal(9, a.GetProperty("y").GetInt32());
        // a page without its own layout sends nothing new, so an older app sees the same config as before
        var plain = pages.Single(p => p.GetProperty("id").GetString() == "plain");
        Assert.False(plain.TryGetProperty("upright", out _));
        Assert.False(plain.GetProperty("widgets")[0].TryGetProperty("upright", out _));
    }

    [Fact]
    public void ACopiedWidgetKeepsItsUprightPlaceSeparately()
    {
        var w = new WidgetConfig { Type = "note", Upright = new GridPlace { X = 1, Y = 2, W = 3, H = 4 } };
        var copy = w.Clone();
        copy.Upright!.X = 5;
        Assert.Equal(1, w.Upright.X);
    }
}
