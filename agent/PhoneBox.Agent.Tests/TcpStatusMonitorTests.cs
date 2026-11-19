using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class TcpStatusMonitorTests
{
    [Fact]
    public void ParseConnectionsReadsNamesIconsHostsAndPorts()
    {
        var targets = TcpStatusMonitor.ParseConnections("NAS | 💾 | 192.168.1.10 | 445\nPrinter | 192.168.1.20 | 9100\nbroken\nServer | 🖥️ | invalid | nope");

        Assert.Collection(targets,
            target =>
            {
                Assert.Equal("NAS", target.Name);
                Assert.Equal("💾", target.Icon);
                Assert.Equal("192.168.1.10", target.Host);
                Assert.Equal(445, target.Port);
            },
            target =>
            {
                Assert.Equal("Printer", target.Name);
                Assert.Equal("🌐", target.Icon);
                Assert.Equal("192.168.1.20", target.Host);
                Assert.Equal(9100, target.Port);
            });
    }

    [Fact]
    public async Task ProbeReportsOpenTcpListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        var monitor = new TcpStatusMonitor();
        var status = await monitor.ProbeAsync(IPAddress.Loopback.ToString(), port, 1500, 1000, CancellationToken.None);
        using var client = await accept;

        Assert.True(status.Online);
        Assert.True(status.LatencyMs is >= 0);
    }

    [Fact]
    public async Task ProbeCachesAnOfflineResult()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var monitor = new TcpStatusMonitor();
        var first = await monitor.ProbeAsync(IPAddress.Loopback.ToString(), port, 250, 5000, CancellationToken.None);
        var second = await monitor.ProbeAsync(IPAddress.Loopback.ToString(), port, 250, 5000, CancellationToken.None);

        Assert.False(first.Online);
        Assert.False(second.Online);
        Assert.Equal(first.CheckedAt, second.CheckedAt);
    }
}
