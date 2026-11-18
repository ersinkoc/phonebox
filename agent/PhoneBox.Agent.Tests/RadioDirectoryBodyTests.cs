using System.Net;
using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>What the radio directory accepts from a mirror, and which addresses count as public.</summary>
public sealed class RadioDirectoryBodyTests
{
    [Fact]
    public async Task AListWithAUtf8ByteOrderMarkIsRead()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""[{ "iso_3166_1": "TR", "name": "Turkey", "stationcount": 512 }]""")).ToArray();
        using var http = new HttpClient(new BytesHandler(body));
        var countries = await RadioBrowserDirectory.GetCountriesAsync(default, http).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("TR", Assert.Single(countries).Code);
    }

    [Theory]
    [InlineData("3fff::1", false)]
    [InlineData("3fff:fff:ffff::1", false)]
    [InlineData("3fff:1000::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void DocumentationPrefixesAreNotPublic(string address, bool expected) =>
        Assert.Equal(expected, RadioBrowserDirectory.IsPublicIpAddress(IPAddress.Parse(address)));

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
