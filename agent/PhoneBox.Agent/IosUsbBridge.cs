using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>
/// Carries the iOS app's panel traffic over the USB cable.
///
/// iOS has no "adb reverse": usbmuxd only lets the computer open connections into the phone. The direction is
/// therefore inverted (the same shape metro-usb uses for Metro):
///
/// 1. The iOS app listens on 127.0.0.1:8765. This service finds a USB iPhone through usbmuxd and opens a
///    connection to that port.
/// 2. The app sends a fresh nonce and the service answers HMAC-SHA256(token, nonce), using the Wi-Fi token
///    from the agent's config. The token itself never goes over the cable, and a process on the phone that
///    finds the loopback port cannot join the pool.
/// 3. Each authenticated pipe waits for the app's "GO", then is connected to this agent's own Wi-Fi HTTPS
///    listener. The app's TLS (with the certificate pin and Bearer token) runs end to end; this service only
///    moves ciphertext.
///
/// usbmuxd is spoken directly (Apple Mobile Device Support on Windows, usbmuxd on Linux, the system daemon on
/// macOS), so no iproxy or helper is needed. When there is no iPhone or no usbmuxd, every attempt fails
/// quietly and is retried; nothing is logged above debug level.
/// </summary>
public sealed class IosUsbBridge(
    ConfigStore config,
    AgentEndpoint endpoint,
    LanConfig lan,
    ILogger<IosUsbBridge> log) : BackgroundService
{
    /// <summary>The loopback port of the iOS app's USB relay (USBLink.hostPort in ios/PhoneBox).</summary>
    internal const int PhonePort = 8765;
    /// <summary>Pipes kept ready: the app's URLSession opens only a handful of connections per host.</summary>
    internal const int PipeCount = 12;
    /// <summary>usbmuxd's PortNumber travels in network byte order (htons).</summary>
    internal static readonly ushort SwappedPhonePort = unchecked((ushort)((PhonePort >> 8) | (PhonePort << 8)));

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AgentConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleRotation = TimeSpan.FromSeconds(30);
    private int _preferredDevice;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Wi-Fi listener owns the certificate the app pins and the token it sends, so it is the target.
        // Without it (off, or set to the agent's own local port) there is nothing to bridge to.
        if (!lan.Enabled || lan.Port == endpoint.Port) return;

        try
        {
            var pipes = Enumerable.Range(0, PipeCount).Select(index => RunLoopAsync(index, stoppingToken)).ToArray();
            await Task.WhenAll(pipes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { log.LogDebug(ex, "iPhone USB relay stopped"); }
    }

    private async Task RunLoopAsync(int index, CancellationToken ct)
    {
        // Stagger the first attempts: with no usbmuxd installed, twelve sockets should not wake at once.
        try { await Task.Delay(TimeSpan.FromMilliseconds(index * 150), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var token = config.Get().Lan.Token;
            if (!string.IsNullOrEmpty(token))
            {
                try { await RunPipeAsync(index, token, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { log.LogDebug("iPhone USB: {Message}", ex.Message); }
            }
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunPipeAsync(int index, string token, CancellationToken ct)
    {
        if (await OpenPipeAsync(ConnectUsbmuxdAsync, token, Volatile.Read(ref _preferredDevice), index, ct) is not { } connected)
            return;
        using var phone = connected.Stream;
        Volatile.Write(ref _preferredDevice, connected.DeviceId);

        using var agent = new TcpClient();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(AgentConnectTimeout);
        await agent.ConnectAsync(IPAddress.Loopback, lan.Port, connect.Token);
        agent.NoDelay = true;
        var agentStream = agent.GetStream();

        using var copy = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = phone.CopyToAsync(agentStream, copy.Token);
        var down = agentStream.CopyToAsync(phone, copy.Token);
        await Task.WhenAny(up, down);
        copy.Cancel();
        try { await Task.WhenAll(up, down); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    internal static async Task<(int DeviceId, NetworkStream Stream)?> OpenPipeAsync(
        Func<CancellationToken, Task<Socket>> connect, string token, int preferredDevice, int index, CancellationToken ct)
    {
        int[] candidates;
        using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            discovery.CancelAfter(HandshakeTimeout);
            using var mux = new NetworkStream(await connect(discovery.Token), ownsSocket: true);
            await SendPlistAsync(mux, ListDevicesXml, tag: 1, discovery.Token);
            if (await ReadMessageAsync(mux, discovery.Token) is not { Type: 8 } list) return null;
            candidates = UsbDevices(Encoding.UTF8.GetString(list.Payload));
        }

        // Reserve a worker's first attempt for each device. Extra workers favor the last device that used a pipe.
        var firstDevice = index < candidates.Length ? candidates[index] : preferredDevice;
        foreach (var deviceId in candidates.OrderByDescending(id => id == firstDevice).ThenByDescending(id => id == preferredDevice))
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(HandshakeTimeout);
            NetworkStream? phone = null;
            try
            {
                phone = new NetworkStream(await connect(handshake.Token), ownsSocket: true);
                await SendPlistAsync(phone, ConnectPlist(deviceId), tag: 2, handshake.Token);
                if (!ConnectSucceeded(await ReadMessageAsync(phone, handshake.Token))) continue;
                var challenge = await ReadLineAsync(phone, 256, handshake.Token);
                if (challenge is null || !TryParseChallenge(challenge, out var nonce)) continue;
                await phone.WriteAsync(Encoding.ASCII.GetBytes($"AUTH {Auth(token, nonce)}\n"), handshake.Token);

                // The phone sends GO when it needs the pipe. Rotate idle pipes only if there are more devices than workers.
                using var idle = candidates.Length > PipeCount ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
                idle?.CancelAfter(IdleRotation);
                if (await ReadLineAsync(phone, 16, idle?.Token ?? ct) != "GO") continue;
                var opened = phone;
                phone = null;
                return (deviceId, opened);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is IOException or SocketException or OperationCanceledException)
            {
                // The next USB device may have the relay even if this one refused or disconnected.
            }
            finally
            {
                phone?.Dispose();
            }
        }
        return null;
    }

    // MARK: - usbmuxd

    private static async Task<Socket> ConnectUsbmuxdAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, 27015), ct);

        Exception? last = null;
        foreach (var path in new[] { "/var/run/usbmuxd", "/run/usbmuxd" })
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private static async Task<Socket> ConnectTcpAsync(IPEndPoint endPoint, CancellationToken ct)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endPoint, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A usbmuxd message: the 16-byte header, then the plist or result payload.</summary>
    private static async Task SendPlistAsync(Stream stream, string xml, uint tag, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)(16 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1); // version 1: XML plist (0 is binary)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 8); // message type: plist
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), tag);
        await stream.WriteAsync(header.AsMemory(), ct);
        await stream.WriteAsync(payload.AsMemory(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(uint Type, byte[] Payload)?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 16u or > 4_194_304u) return null;
        var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var payload = new byte[(int)length - 16];
        if (payload.Length > 0) await stream.ReadExactlyAsync(payload, ct);
        return (type, payload);
    }

    private const string ListDevicesXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>MessageType</key><string>ListDevices</string>
        <key>ClientVersionString</key><string>PhoneBox</string>
        <key>ProgName</key><string>PhoneBox</string>
        <key>kLibUSBMuxVersion</key><integer>3</integer>
        </dict></plist>
        """;

    internal static string ConnectPlist(int deviceId) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>MessageType</key><string>Connect</string>
        <key>DeviceID</key><integer>{deviceId}</integer>
        <key>PortNumber</key><integer>{SwappedPhonePort}</integer>
        <key>ClientVersionString</key><string>PhoneBox</string>
        <key>ProgName</key><string>PhoneBox</string>
        </dict></plist>
        """;

    /// <summary>Explicit USB devices first, then older usbmuxd entries without a transport type.</summary>
    internal static int[] UsbDevices(string xml)
    {
        var root = LoadPlist(xml)?.Element("dict");
        if (root is null || !Pairs(root).TryGetValue("DeviceList", out var devices) || devices.Name != "array")
            return [];

        var usb = new List<int>();
        var fallback = new List<int>();
        foreach (var dict in devices.Elements("dict"))
        {
            var fields = Pairs(dict);
            if (!fields.TryGetValue("DeviceID", out var idNode)
                || !int.TryParse(idNode.Value, out var id) || id <= 0) continue;
            if (fields.TryGetValue("Properties", out var properties) && properties.Name == "dict")
            {
                var props = Pairs(properties);
                if (props.TryGetValue("ConnectionType", out var type))
                {
                    if (type.Value == "USB") usb.Add(id);
                    continue; // explicitly Network (or another transport): not this bridge
                }
            }
            fallback.Add(id);
        }
        return usb.Concat(fallback).Distinct().ToArray();
    }

    /// <summary>A Connect answer: a plist Result with Number 0, or a raw result code of 0.</summary>
    private static bool ConnectSucceeded((uint Type, byte[] Payload)? message)
    {
        if (message is not { } result) return false;
        if (result.Type == 1)
            return result.Payload.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(result.Payload) == 0;
        if (result.Type != 8) return false;

        var root = LoadPlist(Encoding.UTF8.GetString(result.Payload));
        if (root is null) return false;
        foreach (var dict in root.Descendants("dict"))
        {
            var fields = Pairs(dict);
            if (fields.TryGetValue("MessageType", out var type) && type.Value == "Result")
                return fields.TryGetValue("Number", out var number) && number.Value == "0";
        }
        return false;
    }

    // MARK: - Handshake

    internal static bool TryParseChallenge(string line, out string nonce)
    {
        nonce = "";
        const string prefix = "PhoneBox-USB 1 ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var value = line[prefix.Length..].Trim();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) return false;
        nonce = value.ToLowerInvariant();
        return true;
    }

    internal static string Auth(string token, string nonce)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.ASCII.GetBytes(nonce))).ToLowerInvariant();
    }

    /// <summary>
    /// One newline-terminated control line, read a byte at a time so a TLS byte that follows is never swallowed.
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, int limit, CancellationToken ct)
    {
        var one = new byte[1];
        var bytes = new List<byte>(limit);
        while (bytes.Count < limit)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
        return null;
    }

    // MARK: - plist

    private static XElement? LoadPlist(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader).Root;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or IOException) { return null; }
    }

    /// <summary>The direct children of a plist dict, keyed by the key elements before each value.</summary>
    private static Dictionary<string, XElement> Pairs(XElement dict)
    {
        var pairs = new Dictionary<string, XElement>(StringComparer.Ordinal);
        string? key = null;
        foreach (var child in dict.Elements())
        {
            if (child.Name == "key") { key = child.Value; continue; }
            if (key is null) continue;
            pairs[key] = child;
            key = null;
        }
        return pairs;
    }
}
