using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using PhoneBox.Agent;
using Xunit;

public sealed class WidgetFeedProxyTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    [Theory]
    [InlineData("https://api.open-meteo.com/v1/forecast?latitude=59.4&longitude=24.7", true)]
    [InlineData("https://air-quality-api.open-meteo.com/v1/air-quality?latitude=1&longitude=2", true)]
    [InlineData("https://hacker-news.firebaseio.com/v0/topstories.json", true)]
    [InlineData("https://api.frankfurter.dev/v1/latest", true)]
    [InlineData("https://api.coingecko.com/api/v3/coins/markets", true)]
    [InlineData("http://api.open-meteo.com/v1/forecast", false)]
    [InlineData("https://api.open-meteo.com:8443/v1/forecast", false)]
    [InlineData("https://user:pass@api.open-meteo.com/v1/forecast", false)]
    [InlineData("https://127.0.0.1/latest", false)]
    [InlineData("https://169.254.169.254/latest/meta-data/", false)]
    [InlineData("https://example.com/feed.json", false)]
    [InlineData("", false)]
    public void TryValidateTargetEnforcesHttpsDefaultPortAndAllowlist(string url, bool expected)
        => Assert.Equal(expected, WidgetFeedProxy.TryValidateTarget(url, out _));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void IsPublicAddressRejectsLoopbackPrivateAndLinkLocal(string address, bool expected)
        => Assert.Equal(expected, WidgetFeedProxy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task FetchAsyncBlocksRedirectsAndOversizedResponses()
    {
        using var redirectInvoker = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));
        var redirectResult = await WidgetFeedProxy.FetchAsync("https://api.open-meteo.com/v1/forecast", default, redirectInvoker);
        var status = Assert.IsType<StatusCodeHttpResult>(redirectResult);
        Assert.Equal(502, status.StatusCode);

        var oversized = new byte[WidgetFeedProxy.MaxResponseBytes + 1];
        using var bigInvoker = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized),
        }));
        var bigResult = await WidgetFeedProxy.FetchAsync("https://api.open-meteo.com/v1/forecast", default, bigInvoker);
        Assert.Equal(502, Assert.IsType<StatusCodeHttpResult>(bigResult).StatusCode);

        var invalidResult = await WidgetFeedProxy.FetchAsync("https://example.com/secret", default, bigInvoker);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(invalidResult).StatusCode);

        using var okInvoker = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        }));
        var okResult = await WidgetFeedProxy.FetchAsync("https://api.frankfurter.dev/v1/latest", default, okInvoker);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        await okResult.ExecuteAsync(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.StartsWith("application/json", httpContext.Response.ContentType);
        httpContext.Response.Body.Position = 0;
        Assert.Equal("""{"ok":true}""", new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEnd());
    }

    [Fact]
    public void ProxyEndpointStaysLocalAndOffLanAllowlist()
        => Assert.False(Lan.AllowedOverLan("/api/proxy"));
}
