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
    public void FirstUsbDeviceIgnoresNetworkDevices()
    {
        Assert.Equal(7, IosUsbBridge.FirstUsbDevice(TwoDevices));
    }

    [Fact]
    public void NetworkOnlyDevicesAreNotUsed()
    {
        const string xml = """
            <plist version="1.0"><dict><key>DeviceList</key><array>
            <dict><key>DeviceID</key><integer>4</integer><key>Properties</key>
            <dict><key>ConnectionType</key><string>Network</string></dict></dict>
            </array></dict></plist>
            """;
        Assert.Null(IosUsbBridge.FirstUsbDevice(xml));
    }

    [Fact]
    public void DeviceWithoutAConnectionTypeIsAFallback()
    {
        const string xml = """
            <plist version="1.0"><dict><key>DeviceList</key><array>
            <dict><key>DeviceID</key><integer>9</integer><key>MessageType</key><string>Attached</string></dict>
            </array></dict></plist>
            """;
        Assert.Equal(9, IosUsbBridge.FirstUsbDevice(xml));
    }

    [Fact]
    public void NoDevicesOrGarbageGivesNull()
    {
        Assert.Null(IosUsbBridge.FirstUsbDevice("<plist version=\"1.0\"><dict><key>DeviceList</key><array/></dict></plist>"));
        Assert.Null(IosUsbBridge.FirstUsbDevice("not a plist"));
        Assert.Null(IosUsbBridge.FirstUsbDevice(""));
    }
}
