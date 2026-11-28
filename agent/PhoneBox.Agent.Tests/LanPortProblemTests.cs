using System.Net;
using System.Net.Sockets;
using PhoneBox.Agent;
using Xunit;

// #614: a Wi-Fi port the agent cannot open (another program's, or a range Windows reserves) used to stop the whole agent
// when Kestrel started listening. The agent now checks the port first and leaves Wi-Fi off, saying why.
public sealed class LanPortProblemTests
{
    [Fact]
    public void APortAnotherProgramListensOnIsReported()
    {
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            Assert.Contains(Lan.PortProblem(port), new[] { "in-use", "reserved" });
        }
        finally { holder.Stop(); }
    }

    // #710: the agent's own port, which it opens on 127.0.0.1 only, is checked on that address before the agent starts
    [Fact]
    public void ALoopbackPortAnotherProgramListensOnIsInUse()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        try { Assert.Equal("in-use", Lan.PortProblem(((IPEndPoint)holder.LocalEndpoint).Port, IPAddress.Loopback)); }
        finally { holder.Stop(); }
    }

    [Fact]
    public void AFreePortHasNoProblemAndIsLeftFree()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Assert.Null(Lan.PortProblem(port));
        // the check gives the port back: the agent can listen on it right after
        var again = new TcpListener(IPAddress.Any, port);
        again.Start();
        again.Stop();
    }

    [Fact]
    public void AServiceIsToldFromAStartByHand()
    {
        string? None(string _) => null;
        Assert.False(Lan.RunsUnderSupervisor(None));
        Assert.False(Lan.RunsUnderSupervisor(k => k == "XPC_SERVICE_NAME" ? "0" : null)); // a Terminal shell on macOS
        var service = Lan.RunsUnderSupervisor(k => k == "INVOCATION_ID" ? "5f2c" : null);
        var job = Lan.RunsUnderSupervisor(k => k == "XPC_SERVICE_NAME" ? "net.oxog.phonebox.agent" : null);
        Assert.Equal(!OperatingSystem.IsWindows(), service);
        Assert.Equal(!OperatingSystem.IsWindows(), job);
    }
}
