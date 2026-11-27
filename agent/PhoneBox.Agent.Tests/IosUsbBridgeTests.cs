using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task DoesNotStartWhenNoWifiListenerIsActive(int? lanPort)
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-ios-usb-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                lan = new { enabled = true, port = 8766, token = "phonebox-test-token" },
            }));
            var connectAttempts = 0;
            using var bridge = new IosUsbBridge(
                new ConfigStore(path),
                lanPort,
                NullLogger<IosUsbBridge>.Instance,
                _ =>
                {
                    Interlocked.Increment(ref connectAttempts);
                    throw new InvalidOperationException("must not dial usbmuxd");
                });

            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await bridge.StartAsync(ct.Token);
            await bridge.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), ct.Token);
            await bridge.StopAsync(CancellationToken.None);

            Assert.True(bridge.ExecuteTask.IsCompletedSuccessfully);
            Assert.Equal(0, connectAttempts);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotOpenPipesWhileWifiIsDisabledInConfig()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-ios-usb-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                lan = new { enabled = false, port = 8766, token = "phonebox-test-token" },
            }));
            var connectAttempts = 0;
            using var bridge = new IosUsbBridge(
                new ConfigStore(path),
                8766,
                NullLogger<IosUsbBridge>.Instance,
                _ =>
                {
                    Interlocked.Increment(ref connectAttempts);
                    throw new InvalidOperationException("must not dial usbmuxd");
                });

            await bridge.StartAsync(CancellationToken.None);
            await Task.Delay(300);
            await bridge.StopAsync(CancellationToken.None);

            Assert.Equal(0, connectAttempts);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ForwardsAuthenticatedPipeToActiveLanPort()
    {
        const string token = "phonebox-test-token";
        var dir = Directory.CreateTempSubdirectory("phonebox-ios-usb-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                lan = new { enabled = true, port = 8766, token },
            }));

            using var activeTarget = new TcpListener(IPAddress.Loopback, 0);
            activeTarget.Start();
            var activePort = ((IPEndPoint)activeTarget.LocalEndpoint).Port;

            using var fakeMux = new TcpListener(IPAddress.Loopback, 0);
            fakeMux.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            var replyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var targetTask = Task.Run(async () =>
            {
                using var client = await activeTarget.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                var buffer = new byte[4];
                await stream.ReadExactlyAsync(buffer, ct);
                Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
                await stream.WriteAsync("pong"u8.ToArray(), ct);
            }, ct);

            const string singleUsbDevice = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                <key>DeviceList</key><array>
                <dict>
                <key>DeviceID</key><integer>7</integer>
                <key>Properties</key><dict><key>ConnectionType</key><string>USB</string></dict>
                </dict>
                </array>
                </dict></plist>
                """;
            var nonce = new string('b', 64);
            var claimedConnect = 0;

            var muxTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient peer;
                    try { peer = await fakeMux.AcceptTcpClientAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    _ = Task.Run(async () =>
                    {
                        using var client = peer;
                        var stream = client.GetStream();
                        var plist = await ReceivePlist(stream, ct);
                        if (plist.Contains("<string>ListDevices</string>", StringComparison.Ordinal))
                        {
                            await SendMessage(stream, type: 8, Encoding.UTF8.GetBytes(singleUsbDevice), ct);
                            return;
                        }
                        if (Interlocked.CompareExchange(ref claimedConnect, 1, 0) != 0)
                        {
                            await SendMessage(stream, type: 1, [2, 0, 0, 0], ct);
                            return;
                        }

                        await SendMessage(stream, type: 1, [0, 0, 0, 0], ct);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PhoneBox-USB 1 {nonce}\n"), ct);
                        var authLine = await ReadAsciiLineAsync(stream, ct);
                        Assert.Equal($"AUTH {IosUsbBridge.Auth(token, nonce)}", authLine);
                        await stream.WriteAsync("GO\n"u8.ToArray(), ct);
                        await stream.WriteAsync("ping"u8.ToArray(), ct);

                        var buffer = new byte[4];
                        await stream.ReadExactlyAsync(buffer, ct);
                        replyTcs.TrySetResult(Encoding.ASCII.GetString(buffer));
                    }, ct);
                }
            }, ct);

            async Task<Socket> ConnectFakeUsbmuxd(CancellationToken cancel)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(fakeMux.LocalEndpoint, cancel);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            using var bridge = new IosUsbBridge(
                new ConfigStore(path),
                activePort,
                NullLogger<IosUsbBridge>.Instance,
                ConnectFakeUsbmuxd);

            await bridge.StartAsync(ct);
            var reply = await replyTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal("pong", reply);
            await targetTask;

            await bridge.StopAsync(CancellationToken.None);
            timeout.Cancel();
            try { await muxTask; } catch (OperationCanceledException) { }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DropsOpenedPipeWithoutDialingWhenWifiIsDisabledBeforeGo()
    {
        const string token = "phonebox-test-token";
        var dir = Directory.CreateTempSubdirectory("phonebox-ios-usb-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                lan = new { enabled = true, port = 8766, token },
            }));
            var store = new ConfigStore(path);

            using var activeTarget = new TcpListener(IPAddress.Loopback, 0);
            activeTarget.Start();
            var activePort = ((IPEndPoint)activeTarget.LocalEndpoint).Port;

            using var fakeMux = new TcpListener(IPAddress.Loopback, 0);
            fakeMux.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            var targetAccepted = 0;
            var pipeClosedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var targetAcceptTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await activeTarget.AcceptTcpClientAsync(ct);
                        Interlocked.Increment(ref targetAccepted);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                }
            }, ct);

            const string singleUsbDevice = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                <key>DeviceList</key><array>
                <dict>
                <key>DeviceID</key><integer>7</integer>
                <key>Properties</key><dict><key>ConnectionType</key><string>USB</string></dict>
                </dict>
                </array>
                </dict></plist>
                """;
            var nonce = new string('c', 64);
            var claimedConnect = 0;

            var muxTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient peer;
                    try { peer = await fakeMux.AcceptTcpClientAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    _ = Task.Run(async () =>
                    {
                        using var client = peer;
                        var stream = client.GetStream();
                        var plist = await ReceivePlist(stream, ct);
                        if (plist.Contains("<string>ListDevices</string>", StringComparison.Ordinal))
                        {
                            await SendMessage(stream, type: 8, Encoding.UTF8.GetBytes(singleUsbDevice), ct);
                            return;
                        }
                        if (Interlocked.CompareExchange(ref claimedConnect, 1, 0) != 0)
                        {
                            await SendMessage(stream, type: 1, [2, 0, 0, 0], ct);
                            return;
                        }

                        await SendMessage(stream, type: 1, [0, 0, 0, 0], ct);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PhoneBox-USB 1 {nonce}\n"), ct);
                        var authLine = await ReadAsciiLineAsync(stream, ct);
                        Assert.Equal($"AUTH {IosUsbBridge.Auth(token, nonce)}", authLine);

                        // Turn Wi-Fi off before the phone asks for the pipe with GO.
                        Assert.Null(store.Save(JsonSerializer.Serialize(new
                        {
                            lan = new { enabled = false, port = 8766, token },
                        })));
                        await stream.WriteAsync("GO\n"u8.ToArray(), ct);

                        try
                        {
                            var buf = new byte[1];
                            while (await stream.ReadAsync(buf, ct) > 0) { }
                            pipeClosedTcs.TrySetResult(true);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException)
                        {
                            pipeClosedTcs.TrySetResult(true);
                        }
                    }, ct);
                }
            }, ct);

            async Task<Socket> ConnectFakeUsbmuxd(CancellationToken cancel)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(fakeMux.LocalEndpoint, cancel);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            using var bridge = new IosUsbBridge(
                store,
                activePort,
                NullLogger<IosUsbBridge>.Instance,
                ConnectFakeUsbmuxd);

            await bridge.StartAsync(ct);
            var closed = await pipeClosedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await Task.Delay(100, ct);
            Assert.True(closed);
            Assert.Equal(0, Volatile.Read(ref targetAccepted));

            await bridge.StopAsync(CancellationToken.None);
            timeout.Cancel();
            try { await muxTask; } catch (OperationCanceledException) { }
            try { await targetAcceptTask; } catch (OperationCanceledException) { }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<string> ReadAsciiLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        var bytes = new List<byte>(128);
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0) break;
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
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
