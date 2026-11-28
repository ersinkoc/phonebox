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
        // The port comes from the ephemeral range, which the outgoing connections of tests running in parallel also draw
        // from: between two binds one of them can take it (seen once on macOS CI as "Address already in use"). A check
        // that kept the port would fail every attempt; that race fails one now and then, so a few fresh ports are tried.
        SocketException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var probe = new TcpListener(IPAddress.Any, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Assert.Null(Lan.PortProblem(port));
            // the check gives the port back: the agent can listen on it right after
            var again = new TcpListener(IPAddress.Any, port);
            try { again.Start(); again.Stop(); return; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { last = ex; }
        }
        Assert.Fail("the port stayed taken after the check on every attempt: " + last?.Message);
    }
}
