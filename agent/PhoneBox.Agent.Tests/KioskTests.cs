using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

/// <summary>Web kiosk (/kiosk?token=…): its own key, only its own paths, and a page old browsers can run.</summary>
public class KioskTests
{
    private static AgentConfig Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-kiosk-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("/kiosk", true)]
    [InlineData("/kiosk/api/state", true)]
    [InlineData("/kiosk/api/run/terminal", true)]
    [InlineData("/kioskx", false)]
    [InlineData("/api/state", false)]
    [InlineData("/", false)]
    public void OnlyKioskPathsAreKioskPaths(string path, bool kiosk) => Assert.Equal(kiosk, Kiosk.IsKioskPath(new PathString(path)));

    [Theory]
    [InlineData("/kiosk")]
    [InlineData("/kiosk/api/state")]
    public void KioskPathsAreNotPartOfThePhoneOrWebhookApi(string path)
    {
        Assert.False(Lan.AllowedOverLan(new PathString(path)));
        Assert.False(Lan.IsWebhook(new PathString(path)));
    }

    [Fact]
    public void TheKeyIsRequiredAndOnlyWhileEnabled()
    {
        var on = new KioskConfig { Enabled = true, Token = "secret-kiosk-key" };
        Assert.True(Kiosk.Authorized(null, "secret-kiosk-key", on));
        Assert.True(Kiosk.Authorized("Bearer secret-kiosk-key", null, on));
        Assert.False(Kiosk.Authorized(null, null, on));
        Assert.False(Kiosk.Authorized(null, "", on));
        Assert.False(Kiosk.Authorized(null, "secret-kiosk-ke", on));
        Assert.False(Kiosk.Authorized("Bearer wrong", "wrong", on));
        Assert.False(Kiosk.Authorized(null, "secret-kiosk-key", new KioskConfig { Enabled = false, Token = "secret-kiosk-key" }));
        Assert.False(Kiosk.Authorized(null, "", new KioskConfig { Enabled = true, Token = "" }));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://192.168.1.5:8766", true)]
    [InlineData("HTTPS://192.168.1.5:8766", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://192.168.1.5:8766", false)]
    public void OnlyTheKioskPageItselfMayCallItsApi(string origin, bool allowed) =>
        Assert.Equal(allowed, Kiosk.OriginAllowed(origin, "https", "192.168.1.5:8766"));

    [Fact]
    public void TheCookieAlsoCarriesTheKey()
    {
        var on = new KioskConfig { Enabled = true, Token = "secret-kiosk-key" };
        Assert.True(Kiosk.Authorized(null, null, on, "secret-kiosk-key"));
        Assert.False(Kiosk.Authorized(null, null, on, "old-key"));
        Assert.False(Kiosk.Authorized(null, null, new KioskConfig { Enabled = false, Token = "secret-kiosk-key" }, "secret-kiosk-key"));
    }

    [Theory]
    [InlineData("/api/state", "GET", true, true)]
    [InlineData("/api/run/terminal", "POST", true, true)]
    [InlineData("/api/approvals/abc", "POST", true, true)]
    [InlineData("/api/state", "GET", false, false)]          // a sandboxed Custom HTML widget cannot add the panel header
    [InlineData("/api/art", "GET", false, true)]             // <img> loads
    [InlineData("/api/procs/icon", "GET", false, true)]
    [InlineData("/api/procs/kill", "POST", false, false)]
    [InlineData("/api/settings", "GET", true, false)]        // settings, pairing and tests stay local, as for the phone
    [InlineData("/api/pair", "GET", true, false)]
    [InlineData("/api/shortcuts/test", "POST", true, false)]
    public void ThePanelInAKioskGetsThePhonesApi(string path, string method, bool header, bool allowed) =>
        Assert.Equal(allowed, Kiosk.PanelApiAllowed(new PathString(path), method, header));

    [Fact]
    public void ThePanelIsServedUnchangedWithTheLegacyCheckFirst()
    {
        var html = Kiosk.PanelIndex();
        using var s = AppPaths.Resource("www/index.html")!;
        var original = new StreamReader(s).ReadToEnd();
        Assert.Equal(original, html.Replace("\n" + Kiosk.LegacyCheck, ""));
        Assert.True(html.IndexOf(Kiosk.LegacyCheck, StringComparison.Ordinal) < html.IndexOf("<link", StringComparison.Ordinal));
        Assert.DoesNotContain("=>", Kiosk.LegacyCheck);
        Assert.Contains("location.replace('lite/'", Kiosk.LegacyCheck);
    }

    [Fact]
    public void ConfigGetsItsOwnStableKioskKeyAndStartsOff()
    {
        var c = Load("{}");
        Assert.False(c.Kiosk.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(c.Kiosk.Token));
        Assert.NotEqual(c.Lan.Token, c.Kiosk.Token);
        Assert.NotEqual(c.Lan.WebhookToken, c.Kiosk.Token);
        var kept = Load("""{"kiosk":{"enabled":true,"token":"keep-me"}}""");
        Assert.True(kept.Kiosk.Enabled);
        Assert.Equal("keep-me", kept.Kiosk.Token);
        Assert.False(string.IsNullOrWhiteSpace(Load("""{"kiosk":null}""").Kiosk.Token));
    }

    [Fact]
    public void ThePhoneNeverSeesTheKioskKey()
    {
        var c = Load("""{"kiosk":{"enabled":true,"token":"kiosk-secret"}}""");
        Assert.DoesNotContain("kiosk-secret", JsonSerializer.Serialize(c.ToPublic()));
    }

    [Fact]
    public void TheLitePanelIsEmbeddedAndLoweredForFirefox68()
    {
        string Read(string name)
        {
            using var s = AppPaths.Resource("kiosk-lite/" + name);
            Assert.NotNull(s);
            return new StreamReader(s!).ReadToEnd();
        }
        var index = Read("index.html");
        Assert.Contains("src=\"polyfill.js\"", index);
        Assert.Contains("type=\"module\" src=\"main.js\"", index);
        Assert.Contains("too old for the PhoneBox kiosk", index);
        var js = Read("main.js");
        Assert.DoesNotContain("?.", js.Replace("\"?.\"", "").Replace("'?.'", ""));
        Assert.DoesNotContain("||=", js);
        var css = Read("style.css");
        Assert.DoesNotMatch(@"\d(cqw|cqh|cqmin)\b|[^\w-](min|max|clamp)\(|color-mix\(|:is\(|:has\(|@container|aspect-ratio", css);
        Assert.Contains("PF_TABLE", Read("polyfill.js"));
    }
}
