using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PhoneBox.Agent;
using Xunit;

public sealed class IosUsbBridgeTests
{
    [Fact]
    public void PortIsSwappedIntoNetworkOrder()
    {
        Assert.Equal(8765, IosUsbBridge.PhonePort);
        Assert.Equal((ushort)15650, IosUsbBridge.SwappedPhonePort); // 0x223D -> 0x3D22
    }

    [Fact]
    public void AuthMatchesTheRfc4231Vector()
    {
        Assert.Equal(
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            IosUsbBridge.Auth("key", "The quick brown fox jumps over the lazy dog"));
    }

    [Fact]
    public void ConnectPlistCarriesTheDeviceAndNetworkOrderPort()
    {
        var xml = IosUsbBridge.ConnectPlist(7);
        Assert.Contains("<key>MessageType</key><string>Connect</string>", xml);
        Assert.Contains("<key>DeviceID</key><integer>7</integer>", xml);
        Assert.Contains("<key>PortNumber</key><integer>15650</integer>", xml);
    }

    [Theory]
    [InlineData("PhoneBox-USB 1 " + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef", true)]
    [InlineData("PhoneBox-USB 1 " + "ABCDEF" + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef" + "0123456789", true)]
    [InlineData("PhoneBox-USB 2 " + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef", false)]
    [InlineData("PhoneBox-USB 1 short", false)]
    [InlineData("AUTH " + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef", false)]
    [InlineData("PhoneBox-USB 1 " + "g123456789abcdef" + "0123456789abcdef" + "0123456789abcdef" + "0123456789abcdef", false)]
    public void ChallengeIsVersionedAndA64CharacterHexNonce(string line, bool accepted)
    {
        var ok = IosUsbBridge.TryParseChallenge(line, out var nonce);
        Assert.Equal(accepted, ok);
        Assert.Equal(accepted ? 64 : 0, nonce.Length);
    }

    [Fact]
    public void UppercaseNonceIsNormalised()
    {
        var line = "PhoneBox-USB 1 " + new string('A', 64);
        Assert.True(IosUsbBridge.TryParseChallenge(line, out var nonce));
        Assert.Equal(new string('a', 64), nonce);
    }

    private const string TwoDevices = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>DeviceList</key><array>
        <dict>
        <key>DeviceID</key><integer>4</integer>
        <key>MessageType</key><string>Attached</string>
        <key>Properties</key><dict><key>ConnectionType</key><string>Network</string></dict>
        </dict>
        <dict>
        <key>DeviceID</key><integer>7</integer>
        <key>MessageType</key><string>Attached</string>
        <key>Properties</key><dict><key>ConnectionType</key><string>USB</string></dict>
        </dict>
        </array>
        </dict></plist>
        """;

    [Fact]
    public void UsbDevicesIgnoreExplicitNetworkDevices()
    {
        Assert.Equal([7], IosUsbBridge.UsbDevices(TwoDevices));
    }

    [Fact]
    public void NetworkOnlyDevicesAreNotUsed()
    {
        const string xml = """
            <plist version="1.0"><dict><key>DeviceList</key><array>
            <dict><key>DeviceID</key><integer>4</integer><key>Properties</key>
            <dict><key>DeviceID</key><integer>4</integer><key>ConnectionType</key><string>Network</string></dict></dict>
            </array></dict></plist>
            """;
        Assert.Empty(IosUsbBridge.UsbDevices(xml));
    }

    [Fact]
    public void DeviceWithoutAConnectionTypeIsAFallback()
    {
        const string xml = """
            <plist version="1.0"><dict><key>DeviceList</key><array>
            <dict><key>DeviceID</key><integer>9</integer><key>MessageType</key><string>Attached</string></dict>
            </array></dict></plist>
            """;
        Assert.Equal([9], IosUsbBridge.UsbDevices(xml));
    }

    [Fact]
    public void NoDevicesOrGarbageGivesAnEmptyList()
    {
        Assert.Empty(IosUsbBridge.UsbDevices("<plist version=\"1.0\"><dict><key>DeviceList</key><array/></dict></plist>"));
        Assert.Empty(IosUsbBridge.UsbDevices("not a plist"));
        Assert.Empty(IosUsbBridge.UsbDevices(""));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(8, 2, false)]
    [InlineData(0, 1, false)]
    public async Task RelayTriesAnAvailableDeviceAndReservesAWorkerForOthers(int preferred, int index, bool firstRefuses)
    {
        const string token = "phonebox-test-token";
        var nonce = new string('a', 64);
        var devices = TwoDevices.Replace("</array>", """
            <dict><key>DeviceID</key><integer>8</integer>
            <key>Properties</key><dict><key>ConnectionType</key><string>USB</string></dict></dict>
            </array>
            """, StringComparison.Ordinal);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        async Task RunDaemon()
        {
            using (var list = await listener.AcceptTcpClientAsync(ct))
            {
                var peer = list.GetStream();
                Assert.Contains("<string>ListDevices</string>", await ReceivePlist(peer, ct));
                await SendMessage(peer, type: 8, Encoding.UTF8.GetBytes(devices), ct);
            }
            if (firstRefuses)
            {
                using var first = await listener.AcceptTcpClientAsync(ct);
                var peer = first.GetStream();
                Assert.Contains("<key>DeviceID</key><integer>7</integer>", await ReceivePlist(peer, ct));
                await SendMessage(peer, type: 1, [2, 0, 0, 0], ct);
            }

            using var relay = await listener.AcceptTcpClientAsync(ct);
            var stream = relay.GetStream();
            Assert.Contains("<key>DeviceID</key><integer>8</integer>", await ReceivePlist(stream, ct));
            await SendMessage(stream, type: 1, [0, 0, 0, 0], ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"PhoneBox-USB 1 {nonce}\n"), ct);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert.Equal($"AUTH {IosUsbBridge.Auth(token, nonce)}", await reader.ReadLineAsync(ct));
            await stream.WriteAsync("GO\n"u8.ToArray(), ct);
        }

        async Task<Socket> Connect(CancellationToken cancel)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(listener.LocalEndpoint, cancel);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        var daemon = RunDaemon();
        var opened = await IosUsbBridge.OpenPipeAsync(Connect, token, preferred, index, ct);
        using var pipe = opened?.Stream;
        Assert.Equal(8, opened?.DeviceId);
        await daemon;
    }

    private static async Task<string> ReceivePlist(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header) - header.Length];
        await stream.ReadExactlyAsync(payload, ct);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task SendMessage(NetworkStream stream, uint type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, header.Length + payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), type);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }
}
