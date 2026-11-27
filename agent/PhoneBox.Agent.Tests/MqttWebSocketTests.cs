using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhoneBox.Agent;
using Xunit;

/// <summary>The WebSocket transport, ws:// and wss://, against the in-process broker behind a WebSocket endpoint on loopback.</summary>
public sealed class MqttWebSocketTests
{
    /// <summary>Hands what a WebSocket client sends on "/mqtt" to the broker's TCP port, and the broker's answers back.</summary>
    private static async Task<(WebApplication App, int Port)> BridgeAsync(int brokerPort, X509Certificate2? cert)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = MqttTestBroker.FreePort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => { if (cert is not null) o.UseHttps(cert); }));
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/mqtt", async ctx =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync("mqtt");
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, brokerPort);
                var stream = tcp.GetStream();
                var toBroker = Task.Run(async () =>
                {
                    var buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        await stream.WriteAsync(buffer.AsMemory(0, r.Count));
                    }
                });
                var toClient = Task.Run(async () =>
                {
                    var buffer = new byte[8192];
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0) await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, CancellationToken.None);
                });
                try { await Task.WhenAny(toBroker, toClient); } catch (Exception) { }
            });
            try { await app.StartAsync(); return (app, port); }
            catch (Exception) when (attempt < 10) { await app.DisposeAsync(); }
        }
    }

    private static MqttConfig Config(int port, bool tls) => new()
    {
        Enabled = true, Host = "127.0.0.1", Port = port, Transport = "websocket", WebSocketPath = "/mqtt", Tls = tls, ClientId = "phonebox-ws-test",
    };

    [Fact]
    public async Task ItConnectsOverAWebSocket()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var (app, port) = await BridgeAsync(broker.Port, null);
        await using var _ = app;
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(Config(port, false), new MqttConfig()), CancellationToken.None));
        Assert.Contains("phonebox-ws-test-test", broker.ClientIds);
    }

    [Fact]
    public async Task AWrongPathIsNotAMqttBroker()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var (app, port) = await BridgeAsync(broker.Port, null);
        await using var _ = app;
        var cfg = Config(port, false);
        cfg.WebSocketPath = "/elsewhere";
        var result = await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.NotNull(result.ErrorKind);
    }

    [Fact]
    public async Task SecureWebSocketsCheckTheCertificateToo()
    {
        using var cert = MqttTestBroker.Certificate();
        using var broker = await MqttTestBroker.StartAsync();
        var (app, port) = await BridgeAsync(broker.Port, cert);
        await using var _ = app;
        // self-signed: refused until the fingerprint is given
        var refused = await MqttConnection.TestAsync(MqttConfig.ForTest(Config(port, true), new MqttConfig()), CancellationToken.None);
        Assert.Equal(new MqttTestResult(false, "tls"), refused);
        var pinned = Config(port, true);
        pinned.PinnedFingerprint = MqttTestBroker.Fingerprint(cert);
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(pinned, new MqttConfig()), CancellationToken.None));
        var wrong = Config(port, true);
        wrong.PinnedFingerprint = new string('0', 64);
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(wrong, new MqttConfig()), CancellationToken.None));
    }
}
