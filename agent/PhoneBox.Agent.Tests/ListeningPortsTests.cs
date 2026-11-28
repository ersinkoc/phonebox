using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PhoneBox.Agent;
using Xunit;

/// <summary>Tests for finding the loopback TCP ports a process listens on (used for Antigravity's run-time HTTPS port).</summary>
// alone, like the other tests that start a process: on macOS the lookup runs lsof with a 2-second deadline, which a
// busy runner (and every other test's listening sockets) can exceed, so the port went missing at random
[Collection("Process deadline")]
public sealed class ListeningPortsTests
{
    [Fact]
    public void FindsAListenerOfThisProcess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Other tests run fixtures in this process too, so the default cap could hide this port.
            Assert.Contains(new ListeningPorts.Listener(IPAddress.Loopback, port), ListeningPorts.ForProcess(Environment.ProcessId, int.MaxValue));
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InvalidProcessIdsHaveNoPorts(int pid) => Assert.Empty(ListeningPorts.ForProcess(pid));

    [Fact]
    public void ResultIsCappedSortedAndDistinct()
    {
        var listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var l in listeners) l.Start();
        try
        {
            var ports = ListeningPorts.ForProcess(Environment.ProcessId, 2).Select(l => l.Port).ToArray();
            Assert.Equal(2, ports.Length);
            Assert.Equal(ports.Order().Distinct(), ports);
        }
        finally { foreach (var l in listeners) l.Stop(); }
    }

    [Fact]
    public void ProcNetTcpKeepsOnlyOwnLoopbackListeners()
    {
        const string tcp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:F2DD 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 111 1 0000000000000000 100 0 0 10 0
               1: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 112 1 0000000000000000 100 0 0 10 0
               2: 0100007F:2328 0100007F:F2DD 01 00000000:00000000 00:00000000 00000000  1000        0 114 1 0000000000000000 20 4 30 10 -1
               3: 0A00000A:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 113 1 0000000000000000 100 0 0 10 0
               4: 0100007F:1F91 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 999 1 0000000000000000 100 0 0 10 0
               5: 0100007F:ZZZZ 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 111 1 0000000000000000 100 0 0 10 0
               6: 0100007F
            """;
        var ours = new HashSet<string> { "111", "112", "113", "114" };
        Assert.Equal([V4(62173), V4(8080)], ListeningPorts.ParseProcNetTcp(tcp, ours));
    }

    [Fact]
    public void ProcNetTcp6ReadsLoopbackAndAnyAddresses()
    {
        const string tcp6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000001000000:F2DE 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 211 1 0000000000000000 100 0 0 10 0
               1: 00000000000000000000000000000000:1F92 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 212 1 0000000000000000 100 0 0 10 0
               2: 000080FE00000000FF005450B6AD1DFE:0016 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 213 1 0000000000000000 100 0 0 10 0
            """;
        Assert.Equal([V6(62174), V6(8082)], ListeningPorts.ParseProcNetTcp(tcp6, new HashSet<string> { "211", "212", "213" }));
    }

    [Fact]
    public void LsofNamesKeepLoopbackAndWildcardListeners()
    {
        const string output = "p4242\nf12\ntIPv4\nn127.0.0.1:62173\nf13\ntIPv6\nn[::1]:62174\r\nf14\ntIPv4\nn*:8080\n"
            + "f15\ntIPv4\nn192.168.1.5:9000\nf16\ntIPv4\nn127.0.0.1:5000->127.0.0.1:6000\nf17\ntIPv4\nnlocalhost:7000\n"
            + "f18\ntIPv4\nn127.0.0.1:99999\nf19\ntIPv6\nn[::]:8081\nf20\ntIPv4\nn127.0.0.2:8083\n";
        Assert.Equal([V4(62173), V6(62174), V4(8080), V4(7000), V6(8081), new(IPAddress.Parse("127.0.0.2"), 8083)],
            ListeningPorts.ParseLsofNames(output));
        // macOS lsof prints "*" for an IPv6 wildcard too; the t line of the same file tells the family
        Assert.Equal([V6(8084), V4(8085)], ListeningPorts.ParseLsofNames("p1\nf3\ntIPv6\nn*:8084\nf4\ntIPv4\nn*:8085\n"));
        Assert.Empty(ListeningPorts.ParseLsofNames(""));
    }

    [Fact]
    public void WindowsOwnerPidTablesKeepOnlyTheProcessesLoopbackListeners()
    {
        var v4 = Table(ListeningPorts.Ipv4RowSize,
            Row4(state: 2, [127, 0, 0, 1], 62173, pid: 77),
            Row4(state: 2, [0, 0, 0, 0], 8080, pid: 77),
            Row4(state: 2, [127, 0, 0, 1], 8081, pid: 78),
            Row4(state: 2, [192, 168, 1, 5], 9000, pid: 77),
            Row4(state: 5, [127, 0, 0, 1], 9001, pid: 77),
            Row4(state: 2, [127, 0, 0, 2], 9004, pid: 77));
        // a wildcard listener is reached on 127.0.0.1, a concrete loopback one (127.0.0.2) on its own address
        Assert.Equal([V4(62173), V4(8080), new(IPAddress.Parse("127.0.0.2"), 9004)],
            ListeningPorts.ParseOwnerPidTable(v4, ipv6: false, pid: 77));

        var v6 = Table(ListeningPorts.Ipv6RowSize,
            Row6(IPAddress.IPv6Loopback, 62174, pid: 77),
            Row6(IPAddress.IPv6Any, 8082, pid: 77),
            Row6(IPAddress.Parse("fe80::1"), 9002, pid: 77),
            Row6(IPAddress.IPv6Loopback, 9003, pid: 78));
        // an IPv6 socket may refuse IPv4 (on Windows it does by default), so it is reached on ::1
        Assert.Equal([V6(62174), V6(8082)], ListeningPorts.ParseOwnerPidTable(v6, ipv6: true, pid: 77));

        // A row count larger than the buffer stops at the last whole row instead of reading past it.
        var torn = v4[..(4 + ListeningPorts.Ipv4RowSize + 10)];
        Assert.Equal([V4(62173)], ListeningPorts.ParseOwnerPidTable(torn, ipv6: false, pid: 77));
        Assert.Empty(ListeningPorts.ParseOwnerPidTable([1, 0], ipv6: false, pid: 77));
    }

    [Fact]
    public void AnIpv6OnlyListenerIsReachedOnIpv6Loopback()
    {
        if (!Socket.OSSupportsIPv6) return; // nothing to find on a host without IPv6
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = false };
        socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        socket.Listen();
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Assert.Contains(V6(port), ListeningPorts.ForProcess(Environment.ProcessId, int.MaxValue));
    }

    [Fact]
    public void BothFamiliesOnOnePortAreKept()
    {
        if (!Socket.OSSupportsIPv6) return;
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        try
        {
            var port = ((IPEndPoint)v4.LocalEndpoint).Port;
            using var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = false };
            v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            v6.Listen();
            var found = ListeningPorts.ForProcess(Environment.ProcessId, int.MaxValue);
            Assert.Contains(V4(port), found);
            Assert.Contains(V6(port), found);
        }
        finally { v4.Stop(); }
    }

    [Theory]
    [InlineData("::ffff:127.0.0.2", "127.0.0.2")]
    [InlineData("::ffff:0.0.0.0", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("::", "::1")]
    public void MappedAddressesAreReadAsIpv4(string listener, string expected)
    {
        var address = IPAddress.Parse(listener);
        Assert.True(ListeningPorts.ReachableOnLoopback(address));
        Assert.Equal(IPAddress.Parse(expected), ListeningPorts.LoopbackFor(address));
    }

    private static ListeningPorts.Listener V4(int port) => new(IPAddress.Loopback, port);
    private static ListeningPorts.Listener V6(int port) => new(IPAddress.IPv6Loopback, port);

    private static byte[] Table(int rowSize, params byte[][] rows)
    {
        var table = new byte[4 + rows.Length * rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++) rows[i].CopyTo(table, 4 + i * rowSize);
        return table;
    }

    private static byte[] Row4(int state, byte[] address, int port, int pid)
    {
        var row = new byte[ListeningPorts.Ipv4RowSize];
        BinaryPrimitives.WriteInt32LittleEndian(row, state);
        address.CopyTo(row, 4);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(8), (ushort)port);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(20), pid);
        return row;
    }

    private static byte[] Row6(IPAddress address, int port, int pid)
    {
        var row = new byte[ListeningPorts.Ipv6RowSize];
        address.GetAddressBytes().CopyTo(row, 0);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(20), (ushort)port);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(48), 2);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(52), pid);
        return row;
    }
}
