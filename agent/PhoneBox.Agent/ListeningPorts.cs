using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// The TCP ports one process listens on that are reachable through 127.0.0.1 (or ::1). Used to find local servers that
/// pick their port at run time and don't print it on their command line. Every failure yields an empty list.
/// </summary>
internal static class ListeningPorts
{
    /// <summary>Default number of ports returned, so a busy process can't use up a caller's time budget.</summary>
    internal const int DefaultMax = 8;

    /// <summary>
    /// A listening port and the loopback address to reach it on: 127.0.0.1 for an IPv4 listener, ::1 for an IPv6 one.
    /// An IPv6 socket may refuse IPv4 (on Windows it does by default), so the family the process chose is kept.
    /// </summary>
    internal readonly record struct Listener(IPAddress Address, int Port);

    /// <summary>
    /// Lists the loopback (or all-interfaces) TCP listening ports of <paramref name="pid"/>, lowest first; a port open on
    /// both families is listed once, with IPv4.
    /// </summary>
    public static IReadOnlyList<Listener> ForProcess(int pid, int max = DefaultMax)
    {
        if (pid <= 0 || max <= 0) return [];
        try
        {
            IEnumerable<Listener> found = OperatingSystem.IsWindows() ? Windows(pid)
                : OperatingSystem.IsLinux() ? Linux(pid)
                : OperatingSystem.IsMacOS() ? MacOs(pid)
                : [];
            // two sockets on one port (127.0.0.1 and [::1]) are different servers, so both stay
            return found.OrderBy(l => l.Port).ThenBy(l => l.Address.AddressFamily == AddressFamily.InterNetworkV6)
                .Distinct().Take(max).ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>An IPv4-mapped IPv6 address (::ffff:127.0.0.2) as the IPv4 address it stands for.</summary>
    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Whether a listener on this local address answers connections to the loopback address of its family.</summary>
    internal static bool ReachableOnLoopback(IPAddress address)
    {
        address = Plain(address);
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    /// <summary>
    /// Where to reach a listener: its own address when it is bound to a loopback address (127.0.0.2 is one too), the
    /// loopback address of its family when it is bound to every interface.
    /// </summary>
    internal static IPAddress LoopbackFor(IPAddress listener)
    {
        listener = Plain(listener);
        return IPAddress.IsLoopback(listener) ? listener
            : listener.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    }

    /// <summary>The address as a URL host: IPv6 in brackets.</summary>
    internal static string UrlHost(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    // ---- Windows: GetExtendedTcpTable, never netstat (its state column is localized) ----

    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MibTcpStateListen = 2;
    internal const int Ipv4RowSize = 24, Ipv6RowSize = 56;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int addressFamily,
        int tableClass, uint reserved);

    private static IEnumerable<Listener> Windows(int pid) =>
        ParseOwnerPidTable(ReadTcpTable(AfInet), ipv6: false, pid)
            .Concat(ParseOwnerPidTable(ReadTcpTable(AfInet6), ipv6: true, pid));

    /// <summary>Reads the listener table of one address family; the table can grow between the size query and the read.</summary>
    private static byte[] ReadTcpTable(int family)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (result != ErrorInsufficientBuffer || size <= 0 || size > 16 << 20) return [];
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 0)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, size);
                    return bytes;
                }
                if (result != ErrorInsufficientBuffer) return [];
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    /// <summary>
    /// Decodes a <c>MIB_TCPTABLE_OWNER_PID</c> or <c>MIB_TCP6TABLE_OWNER_PID</c>: a 4-byte row count, then fixed-size rows.
    /// Ports are in network byte order in the low 16 bits of their DWORD.
    /// </summary>
    internal static List<Listener> ParseOwnerPidTable(ReadOnlySpan<byte> table, bool ipv6, int pid)
    {
        var ports = new List<Listener>();
        if (table.Length < 4) return ports;
        var count = BitConverter.ToUInt32(table[..4]);
        var rowSize = ipv6 ? Ipv6RowSize : Ipv4RowSize;
        // Offsets inside a row (MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID).
        var (stateAt, addrAt, addrLength, portAt, pidAt) = ipv6 ? (48, 0, 16, 20, 52) : (0, 4, 4, 8, 20);
        for (long i = 0; i < count; i++)
        {
            var start = 4 + i * rowSize;
            if (start + rowSize > table.Length) break;
            var row = table.Slice((int)start, rowSize);
            if (BitConverter.ToInt32(row[pidAt..]) != pid) continue;
            if (BitConverter.ToInt32(row[stateAt..]) != MibTcpStateListen) continue;
            var address = new IPAddress(row.Slice(addrAt, addrLength));
            if (!ReachableOnLoopback(address)) continue;
            var port = (row[portAt] << 8) | row[portAt + 1];
            if (port is > 0 and <= 65535) ports.Add(new Listener(LoopbackFor(address), port));
        }
        return ports;
    }

    // ---- Linux: the process's socket inodes, matched against the kernel's LISTEN rows ----

    private static IEnumerable<Listener> Linux(int pid)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
        {
            string? target;
            try { target = new FileInfo(fd).LinkTarget; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
                inodes.Add(target["socket:[".Length..^1]);
        }
        if (inodes.Count == 0) return [];
        var ports = new List<Listener>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            ports.AddRange(ParseProcNetTcp(text, inodes));
        }
        return ports;
    }

    /// <summary>
    /// Reads <c>/proc/net/tcp</c> or <c>tcp6</c> text: listening rows (state <c>0A</c>) owned by one of the socket inodes,
    /// on a loopback or all-interfaces address. Addresses are hex 32-bit words in host (little-endian) order.
    /// </summary>
    internal static List<Listener> ParseProcNetTcp(string text, IReadOnlySet<string> inodes)
    {
        var ports = new List<Listener>();
        foreach (var line in text.Split('\n'))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 10 || !cols[0].EndsWith(':')) continue; // the header row and torn lines
            if (cols[3] != "0A" || !inodes.Contains(cols[9])) continue;
            var local = cols[1].Split(':');
            if (local.Length != 2 || ProcAddress(local[0]) is not { } address || !ReachableOnLoopback(address)) continue;
            if (int.TryParse(local[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
                ports.Add(new Listener(LoopbackFor(address), port));
        }
        return ports;
    }

    private static IPAddress? ProcAddress(string hex)
    {
        if (hex.Length is not (8 or 32)) return null;
        var bytes = new byte[hex.Length / 2];
        for (var word = 0; word < hex.Length / 8; word++)
        for (var b = 0; b < 4; b++)
        {
            // Each 32-bit word is printed as a number, so its bytes appear in reverse memory order.
            if (!byte.TryParse(hex.AsSpan(word * 8 + (3 - b) * 2, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var value)) return null;
            bytes[word * 4 + b] = value;
        }
        return new IPAddress(bytes);
    }

    // ---- macOS: lsof, started without a shell ----

    private static IEnumerable<Listener> MacOs(int pid)
    {
        // A launchd PATH often lacks /usr/sbin, where macOS keeps lsof.
        var lsof = ToolProcess.Find("lsof") ?? (File.Exists("/usr/sbin/lsof") ? "/usr/sbin/lsof" : null);
        if (lsof is null) return [];
        var start = new ProcessStartInfo(lsof);
        foreach (var arg in new[] { "-nP", "-a", "-p", pid.ToString(CultureInfo.InvariantCulture), "-iTCP", "-sTCP:LISTEN", "-Ftn" })
            start.ArgumentList.Add(arg);
        // lsof exits with 1 when nothing matches; the output decides, not the exit code.
        var result = ToolProcess.RunAsync(start, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        return ParseLsofNames(result.Output);
    }

    /// <summary>
    /// Reads <c>lsof -Ftn</c> output: per file (<c>f</c>), a <c>t</c> line with the socket type (IPv4 / IPv6) and an
    /// <c>n</c> line with <c>host:port</c>, IPv6 hosts in brackets and <c>*</c> for every interface of either family,
    /// which is why the type is read too. Other field lines are skipped.
    /// </summary>
    internal static List<Listener> ParseLsofNames(string output)
    {
        var ports = new List<Listener>();
        string? type = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && line[0] is 'p' or 'f') { type = null; continue; }
            if (line.Length > 1 && line[0] == 't') { type = line[1..]; continue; }
            if (line.Length < 2 || line[0] != 'n') continue;
            var name = line[1..];
            var colon = name.LastIndexOf(':');
            if (colon <= 0 || name.Contains("->", StringComparison.Ordinal)) continue;
            var host = name[..colon].Trim('[', ']');
            IPAddress? address = host == "*" ? (type == "IPv6" ? IPAddress.IPv6Loopback : IPAddress.Loopback)
                : host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
                : IPAddress.TryParse(host, out var parsed) && ReachableOnLoopback(parsed) ? LoopbackFor(parsed) : null;
            if (address is not null && int.TryParse(name[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
                ports.Add(new Listener(address, port));
        }
        return ports;
    }
}
