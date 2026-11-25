using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The bridge endpoints behind the agent's real request gate, on real sockets: a local HTTP port, a USB tunnel port and a
/// Wi-Fi port, both TLS, the way Program.cs sets them up. The ports are free loopback ports picked for the test, never the
/// agent's own; the pasteboard is a fake, so no test touches a clipboard.
/// </summary>
public sealed class BridgeGateTests : IAsyncLifetime
{
    private const string UsbKey = "usb-key-0123456789abcdef0123456789abcdef";
    private const string WifiKey = "wifi-key-0123456789abcdef0123456789abcdef";
    private const string WebhookKey = "hook-key-0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-gate-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private int _stamp;
    private WebApplication? _app;
    private X509Certificate2? _cert;
    private HttpClient? _http;
    private UsbLink? _usb;
    private int _local, _usbPort, _wifi;

    public BridgeGateTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: true);
    }

    private void WriteConfig(bool wifiOn, string wifiKey, bool clipboard)
    {
        static string Bool(bool b) => b ? "true" : "false";
        File.WriteAllText(_config, "{\"lan\":{\"enabled\":" + Bool(wifiOn) + ",\"token\":\"" + wifiKey + "\",\"webhookToken\":\"" + WebhookKey
            + "\"},\"bridge\":{\"clipboard\":" + Bool(clipboard) + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    public async Task InitializeAsync()
    {
        _cert = Lan.Certificate(_root);
        for (var attempt = 0; ; attempt++)
        {
            (_local, _usbPort, _wifi) = (UsbLink.FreeLoopbackPort(), UsbLink.FreeLoopbackPort(), UsbLink.FreeLoopbackPort());
            if (_local == _usbPort || _local == _wifi || _usbPort == _wifi) continue;
            try { _app = await StartAsync(); break; }
            catch (IOException) when (attempt < 5) { /* another process took a port between picking and binding it */ }
        }
        var thumbprint = _cert.Thumbprint;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.GetCertHashString() == thumbprint },
        }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, _local);
            k.Listen(IPAddress.Loopback, _usbPort, l => l.UseHttps(_cert!));
            k.Listen(IPAddress.Loopback, _wifi, l => l.UseHttps(_cert!)); // the agent's is on every address; loopback is enough here
        });
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_stream);
        builder.Services.AddSingleton(_ => new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance));
        var app = builder.Build();
        _usb = new UsbLink(_usbPort, "pin");
        _usb.SetKey("serial1", UsbKey);
        var gate = new RequestGate(_local, [$"http://127.0.0.1:{_local}", $"http://localhost:{_local}"], _usb, _wifi, _store,
            _ => false, NullLogger.Instance);
        app.UseRouting();
        app.Use(gate.InvokeAsync);
        BridgeApi.Map(app, gate);
        // an endpoint the gate alone must keep from the local port, whatever the bridge endpoints check themselves
        app.MapGet("/api/bridge/probe", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _cert?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Url(string via, string path) => via switch
    {
        "local" => $"http://127.0.0.1:{_local}{path}",
        "usb" => $"https://127.0.0.1:{_usbPort}{path}",
        _ => $"https://127.0.0.1:{_wifi}{path}",
    };

    private static HttpRequestMessage Message(HttpMethod method, string url, string? key, string? text = null)
    {
        var m = new HttpRequestMessage(method, url);
        if (key is not null) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (text is not null) m.Content = new StringContent("{\"text\":\"" + text + "\"}", Encoding.UTF8, "application/json");
        return m;
    }

    private async Task<HttpStatusCode> Post(string via, string? key, string text = "hello")
    {
        using var res = await _http!.SendAsync(Message(HttpMethod.Post, Url(via, "/api/bridge/clipboard"), key, text));
        return res.StatusCode;
    }

    private async Task<HttpResponseMessage> OpenStream(string via, string? key, string path = "/api/bridge/stream") =>
        await _http!.SendAsync(Message(HttpMethod.Get, Url(via, path), key), HttpCompletionOption.ResponseHeadersRead);

    // ---- the local port ----

    [Theory]
    [InlineData("/api/bridge/clipboard")]
    [InlineData("/api/bridge/stream")]
    [InlineData("/api/bridge/stream/")]
    [InlineData("/API/BRIDGE/STREAM")]
    [InlineData("/api/bridge/probe")]
    public async Task TheLocalPortRefusesTheBridgeEvenWithAPhonesKey(string path)
    {
        foreach (var key in new[] { null, UsbKey, WifiKey })
        {
            using var get = await _http!.SendAsync(Message(HttpMethod.Get, Url("local", path), key));
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            using var post = await _http.SendAsync(Message(HttpMethod.Post, Url("local", path), key, "from a local process"));
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public async Task ALocalProcessHearsNothingCopiedOnTheMac()
    {
        using var res = await OpenStream("local", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotEqual("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    // ---- the phone's ports ----

    [Fact]
    public async Task TheUsbPortWithThePhonesKeyIsAccepted()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("usb", UsbKey, "over USB"));
        Assert.Equal("over USB", _pb.Text);
        using var res = await OpenStream("usb", UsbKey);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheWifiPortWithThePairedKeyIsAccepted()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("wifi", WifiKey, "over Wi-Fi"));
        Assert.Equal("over Wi-Fi", _pb.Text);
        using var res = await OpenStream("wifi", WifiKey);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("usb", null, HttpStatusCode.Unauthorized)]
    [InlineData("usb", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("usb", WifiKey, HttpStatusCode.Unauthorized)]       // the Wi-Fi key does not open USB
    [InlineData("wifi", null, HttpStatusCode.Forbidden)]
    [InlineData("wifi", "wrong", HttpStatusCode.Forbidden)]
    [InlineData("wifi", UsbKey, HttpStatusCode.Forbidden)]          // nor the USB key Wi-Fi
    [InlineData("wifi", WebhookKey, HttpStatusCode.Forbidden)]      // the webhook key opens only the webhook paths
    public async Task AWrongKeyIsRefused(string via, string? key, HttpStatusCode expected)
    {
        Assert.Equal(expected, await Post(via, key));
        using var res = await OpenStream(via, key);
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal(0, _pb.Writes);
    }

    // ---- an open stream whose key stops being valid ----

    /// <summary>Reads the stream until the agent ends it; fails if it is still open after a few seconds.</summary>
    private static async Task<string> UntilEnded(HttpResponseMessage res)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var body = await res.Content.ReadAsStreamAsync(timeout.Token);
        var text = new StringBuilder();
        var buf = new byte[4096];
        try
        {
            int n;
            while ((n = await body.ReadAsync(buf, timeout.Token)) > 0) text.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (OperationCanceledException) { Assert.Fail("the stream stayed open"); }
        return text.ToString();
    }

    /// <summary>The stream is open (the agent has started its loop and its key check) before the test changes anything.</summary>
    private static async Task Opened(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await Task.Delay(100);
    }

    [Fact]
    public async Task AWifiStreamEndsWhenANewWifiKeyIsMade()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        WriteConfig(wifiOn: true, wifiKey: "a-new-key-0123456789abcdef0123456789abcd", clipboard: true);
        var text = await UntilEnded(res);
        Assert.StartsWith(BridgeApi.PingFrame, text);
    }

    [Fact]
    public async Task AWifiStreamEndsWhenWifiIsTurnedOff()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        WriteConfig(wifiOn: false, wifiKey: WifiKey, clipboard: true);
        await UntilEnded(res);
    }

    [Fact]
    public async Task AUsbStreamEndsWhenThePhonesKeyIsForgotten()
    {
        using var res = await OpenStream("usb", UsbKey);
        await Opened(res);
        _usb!.Forget("serial1");
        await UntilEnded(res);
    }

    [Fact]
    public async Task AStreamWithAValidKeyStaysOpenAndHearsTheMac()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var body = await res.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(body);
        _stream.Publish("clipboard", "copied on the Mac", null);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            lines.Add(line);
            if (line.StartsWith("data:", StringComparison.Ordinal)) break;
        }
        Assert.Contains("id: 1", lines);
        Assert.Contains("event: clipboard", lines);
        Assert.Contains("data: {\"seq\":1,\"text\":\"copied on the Mac\"}", lines);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false);
        await reader.ReadToEndAsync(timeout.Token); // the switch must close the connection
    }
}
