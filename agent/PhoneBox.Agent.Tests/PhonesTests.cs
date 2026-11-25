using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public class PhonesTests
{
    private static AgentConfig Config(object phones)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-phones-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { id = "home", title = "Home", widgets = Array.Empty<object>() },
                new { id = "music", title = "Music", widgets = Array.Empty<object>() },
                new { id = "clock", title = "Clock", widgets = Array.Empty<object>() },
            },
            phones,
        }));
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EachPhoneGetsItsPagesInTheUsualOrder()
    {
        var c = Config(new[] { new { id = "desk", name = "Desk", pages = new[] { "clock", "home" } } });
        Assert.Equal(["home", "clock"], c.PagesFor("desk").Select(p => p.Id));
        Assert.Equal(["home", "music", "clock"], c.PagesFor("tablet").Select(p => p.Id)); // not configured: all
        Assert.Equal(["home", "music", "clock"], c.PagesFor(null).Select(p => p.Id));
    }

    [Fact]
    public void UnknownOrEmptySelectionsFallBackToAllPages()
    {
        var c = Config(new object[] { new { id = "a", name = "", pages = new[] { "gone" } }, new { id = "b", name = "", pages = Array.Empty<string>() } });
        Assert.Equal(3, c.PagesFor("a").Count());
        Assert.Equal(3, c.PagesFor("b").Count());
    }

    [Fact]
    public void APhoneKeepsItsOwnViewportBetweenRequests()
    {
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        PhoneRegistry.Touch(first, "Tablet", "USB");
        PhoneRegistry.Touch(second, "Phone", "Wi-Fi");
        PhoneRegistry.SetViewport(first, 1138, 711);
        PhoneRegistry.SetViewport(second, 780, 360);
        PhoneRegistry.Touch(first, "Tablet", "USB");
        PhoneRegistry.SetViewport(first, -1, 9000);
        Assert.Equal(new PhoneViewport(1138, 711), PhoneRegistry.All().Single(p => p.Id == first).Viewport);
        Assert.Equal(new PhoneViewport(780, 360), PhoneRegistry.All().Single(p => p.Id == second).Viewport);
        PhoneRegistry.SetViewport(first, 711, 1138);
        Assert.Equal(new PhoneViewport(711, 1138), PhoneRegistry.All().Single(p => p.Id == first).Viewport);
    }

    [Fact]
    public void RegistryIgnoresMalformedIds()
    {
        PhoneRegistry.Touch("abc123", "Pixel 8", "USB");
        PhoneRegistry.Touch("../../etc", "x", "USB");
        PhoneRegistry.Touch(new string('a', 64), "x", "USB");
        Assert.Contains(PhoneRegistry.All(), p => p.Id == "abc123" && p.Model == "Pixel 8");
        Assert.DoesNotContain(PhoneRegistry.All(), p => p.Id.Contains('/') || p.Id.Length > 32);
    }
}
