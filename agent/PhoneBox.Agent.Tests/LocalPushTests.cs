using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class LocalPushTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private static readonly LocalPushConfig Listed = new() { Origins = [Extension] };

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("moz-extension://2c127fa4-62c7-7e4f-90e5-472b45eecfdc", true)]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", false)]
    [InlineData("chrome-extension://*", false)]
    [InlineData("chrome-extension://", false)]
    [InlineData("https://example.com", false)]
    [InlineData("http://127.0.0.1:8765", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnExtensionOriginIsAccepted(string? origin, bool valid) =>
        Assert.Equal(valid, LocalPush.ValidOrigin(origin));

    [Theory]
    [InlineData("POST", "/api/push/workclock", true)]
    [InlineData("PUT", "/api/push/workclock", true)]
    // setting a value is all it may do: not listing or deleting values, and nothing outside /api/push
    [InlineData("GET", "/api/push", false)]
    [InlineData("GET", "/api/push/workclock", false)]
    [InlineData("DELETE", "/api/push/workclock", false)]
    [InlineData("DELETE", "/api/push", false)]
    [InlineData("POST", "/api/push", false)]
    [InlineData("POST", "/api/pushover", false)]
    [InlineData("POST", "/api/notify", false)]
    [InlineData("POST", "/api/goto/1", false)]
    [InlineData("POST", "/api/run/terminal", false)]
    [InlineData("POST", "/api/approvals/1/allow", false)]
    [InlineData("PUT", "/api/settings", false)]
    public void AListedExtensionMayOnlySetAPushedValue(string method, string path, bool allowed) =>
        Assert.Equal(allowed, LocalPush.Allows(Listed, Extension, method, new PathString(path)));

    [Fact]
    public void AnExtensionThatIsNotListedIsRefused()
    {
        var path = new PathString("/api/push/workclock");
        Assert.False(LocalPush.Allows(Listed, "chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba", "POST", path));
        Assert.False(LocalPush.Allows(Listed, "https://example.com", "POST", path));
        Assert.False(LocalPush.Allows(new LocalPushConfig(), Extension, "POST", path));
        Assert.False(LocalPush.Allows(null, Extension, "POST", path));
        // a web origin typed into the config by hand is still not an extension
        Assert.False(LocalPush.Allows(new LocalPushConfig { Origins = ["https://example.com"] }, "https://example.com", "POST", path));
    }
}
