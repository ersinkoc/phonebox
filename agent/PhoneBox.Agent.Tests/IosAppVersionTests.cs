using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class IosAppVersionTests
{
    [Theory]
    [InlineData("ios", null)]
    [InlineData("iOS", "Unknown")]
    [InlineData(null, "iOS 27.2.1 iPhone")]
    [InlineData(null, "iPhone17,5")]
    [InlineData(null, "iPad14,1")]
    [InlineData(null, "iPod9,1")]
    public void IosHeadersAreRecognized(string? platform, string? model)
    {
        var headers = new HeaderDictionary();
        if (platform is not null) headers["X-PhoneBox-Platform"] = platform;
        if (model is not null) headers["X-PhoneBox-Model"] = model;
        Assert.True(RequestGate.IsIosApp(headers));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("android", "SM-A307G")]
    [InlineData(null, "Galaxy A30s")]
    [InlineData(null, "Browser")]
    public void AndroidAndBrowserHeadersAreNotIos(string? platform, string? model)
    {
        var headers = new HeaderDictionary();
        if (platform is not null) headers["X-PhoneBox-Platform"] = platform;
        if (model is not null) headers["X-PhoneBox-Model"] = model;
        Assert.False(RequestGate.IsIosApp(headers));
    }
}
