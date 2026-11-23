using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>A device's own pages and home page (Settings › Phones), for phones and for a browser panel opened with
/// /panel/?device=&lt;id&gt;.</summary>
public sealed class DeviceHomePageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-device-home-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AgentConfig Load(string json)
    {
        var path = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return new ConfigStore(path).Get();
    }

    private const string Config = """
        {"pages":[{"id":"home","title":"Home","widgets":[]},{"id":"work","title":"Work","widgets":[]},{"id":"monitor","title":"Monitor","widgets":[]}],
         "display":{"homePage":"home"},
         "phones":[{"id":"monitor","pages":["monitor","work"],"homePage":"monitor"},
                   {"id":"phone1","pages":["home","work"],"homePage":"monitor"},
                   {"id":"phone2","homePage":"  "}]}
        """;

    private static string? PublicHomePage(AgentConfig c, string? device)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(c.ToPublic(device), ConfigStore.WriteOpts));
        // left out (or null) without a home page of the device's own: the panel then uses display.homePage
        return doc.RootElement.TryGetProperty("homePage", out var home) && home.ValueKind == JsonValueKind.String ? home.GetString() : null;
    }

    [Fact]
    public void ADeviceGetsItsOwnPagesAndHomePage()
    {
        var c = Load(Config);
        Assert.Equal(["work", "monitor"], c.PagesFor("monitor").Select(p => p.Id)); // in the pages' own order
        Assert.Equal("monitor", PublicHomePage(c, "monitor"));
    }

    [Fact]
    public void ADisabledHomePageFallsBackToTheGlobalOne()
    {
        var c = Load("""
            {"pages":[{"id":"home","title":"Home","widgets":[]},{"id":"monitor","title":"Monitor","enabled":false,"widgets":[]}],
             "phones":[{"id":"monitor","homePage":"monitor"}]}
            """);
        Assert.Null(PublicHomePage(c, "monitor"));
    }

    [Fact]
    public void AHomePageTheDeviceDoesNotShowIsIgnored()
    {
        // phone1 does not show "monitor": it falls back to the global home page in the panel
        Assert.Null(PublicHomePage(Load(Config), "phone1"));
    }

    [Fact]
    public void WithoutADeviceOrAChoiceTheGlobalHomePageApplies()
    {
        var c = Load(Config);
        Assert.Null(PublicHomePage(c, null));
        Assert.Null(PublicHomePage(c, "phone2"));
        Assert.Null(c.Phones.Single(p => p.Id == "phone2").HomePage); // blank is normalized away
        Assert.Equal(3, c.PagesFor(null).Count());
    }
}
