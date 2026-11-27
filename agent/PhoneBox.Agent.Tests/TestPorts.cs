using System.Net;
using System.Net.Sockets;

/// <summary>Starts HTTP fixtures while holding the allocation lock, with bounded retries for external port races.</summary>
static class TestPorts
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<int> Issued = [];
    private static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
    /// <summary>A free loopback port nobody else in this test run has been given, for a fixture that binds it itself (an MQTT broker).</summary>
    public static int Reserve()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var port = Free();
                if (Issued.Add(port)) return port;
            }
            throw new IOException("Could not find a distinct free local port after 32 attempts");
        }
    }

    public static HttpListener Listen()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var port = Free();
                if (!Issued.Add(port)) continue;
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { listener.Start(); return listener; }
                catch (HttpListenerException ex) when (ex.NativeErrorCode is 48 or 98 or 10048 or 183)
                {
                    try { listener.Close(); } catch (HttpListenerException) { }
                }
            }
            throw new IOException("Could not bind a distinct local HTTP test listener after 32 attempts");
        }
    }
}
