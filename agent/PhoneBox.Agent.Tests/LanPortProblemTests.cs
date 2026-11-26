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
}
