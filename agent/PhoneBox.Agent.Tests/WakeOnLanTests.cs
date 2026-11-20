using System.Net;
using System.Net.NetworkInformation;
using PhoneBox.Agent;
using Xunit;

/// <summary>Wake-on-LAN: the packet, MAC parsing, adapter selection and the send loop (never a real network send).</summary>
public sealed class WakeOnLanTests
{
    private static readonly byte[] Mac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01];

    [Fact]
    public void Packet_is_six_ff_then_the_mac_sixteen_times()
    {
        var p = WakeOnLan.BuildPacket(Mac);
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(Mac, p.Skip(6 + i * 6).Take(6).ToArray());
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    [InlineData("AABBCCDDEE01")]
    [InlineData("  AA:BB:CC:DD:EE:01  ")]
    public void A_mac_is_accepted_in_every_common_format(string text)
    {
        Assert.True(WakeOnLan.TryParseMac(text, out var mac));
        Assert.Equal(Mac, mac);
        Assert.Equal("AA:BB:CC:DD:EE:01", WakeOnLan.FormatMac(mac));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:01:02")]
    [InlineData("GG:BB:CC:DD:EE:01")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    public void A_bad_mac_is_rejected(string? text) => Assert.False(WakeOnLan.TryParseMac(text, out _));

    [Fact]
    public void Args_are_a_mac_and_an_optional_broadcast_address()
    {
        Assert.Null(WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01", out var mac, out var none));
        Assert.Equal(Mac, mac);
        Assert.Null(none);
        Assert.Null(WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 192.168.1.255", out _, out var bc));
        Assert.Equal(IPAddress.Parse("192.168.1.255"), bc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mac")]
    public void Args_with_a_bad_mac_say_so(string args)
        => Assert.Contains("MAC", WakeOnLan.ParseArgs(args, out _, out _));

    [Fact]
    public void Args_with_a_bad_broadcast_address_say_so()
        => Assert.Contains("address", WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 nope", out _, out _));

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void An_ethernet_adapter_with_a_private_address_is_eligible()
        => Assert.True(WakeOnLan.IsEligible(NetworkInterfaceType.Ethernet, "Intel I225-V", true, Ip("192.168.1.20"), Ip("255.255.255.0")));

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback", true, "192.168.1.20", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo", true, "192.168.1.20", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Ppp, "VPN", true, "10.8.0.2", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Tailscale", true, "100.101.1.2", "255.192.0.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel I225-V", true, "169.254.3.4", "255.255.0.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel I225-V", true, "10.0.0.5", "255.255.255.255")]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", false, "172.20.0.1", "255.255.240.0")]
    public void Other_adapters_are_not_eligible(NetworkInterfaceType type, string desc, bool gateway, string address, string mask)
        => Assert.False(WakeOnLan.IsEligible(type, desc, gateway, Ip(address), Ip(mask)));

    [Fact]
    public void A_virtual_switch_that_owns_the_gateway_stays_eligible()
        => Assert.True(WakeOnLan.IsEligible(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", true, Ip("192.168.1.20"), Ip("255.255.255.0")));

    // A VPN or container adapter can own a default gateway just as a Hyper-V external switch does, so it is told apart
    // by what it is called, not by whether it has a gateway.
    [Theory]
    [InlineData("TAP-Windows Adapter V9 Local Area Connection 3", "10.8.0.2", "255.255.255.0")]
    [InlineData("WireGuard Tunnel wg0", "10.64.0.2", "255.255.255.0")]
    [InlineData("Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64 Ethernet 4", "192.168.50.2", "255.255.255.0")]
    [InlineData("Fortinet SSL VPN Virtual Ethernet Adapter Ethernet 5", "10.212.134.200", "255.255.255.0")]
    [InlineData("NordLynx Tunnel NordLynx", "10.5.0.2", "255.255.0.0")]
    [InlineData("ZeroTier One Virtual Port ZeroTier One [8056c2e21c000001]", "192.168.191.5", "255.255.255.0")]
    [InlineData("tun0 tun0", "10.8.0.1", "255.255.255.0")]
    [InlineData("utun3 utun3", "10.9.0.2", "255.255.255.0")]
    [InlineData("docker0 docker0", "172.17.0.1", "255.255.0.0")]
    [InlineData("virbr0 virbr0", "192.168.122.1", "255.255.255.0")]
    [InlineData("veth12ab34 veth12ab34", "172.18.0.1", "255.255.0.0")]
    [InlineData("br-0a1b2c3d4e5f br-0a1b2c3d4e5f", "172.19.0.1", "255.255.0.0")]
    public void VPN_and_container_adapters_stay_out_even_with_a_gateway(string name, string address, string mask)
        => Assert.False(WakeOnLan.IsEligible(NetworkInterfaceType.Ethernet, name, true, Ip(address), Ip(mask)));

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Connection (7) I219-V Ethernet")]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller Ethernet 2")]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Controller I225-V enp5s0")]
    [InlineData(NetworkInterfaceType.Ethernet, "en0 en0")]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6E AX211 160MHz Wi-Fi")]
    // what EligibleNics passes for a Hyper-V external switch that took over the physical adapter: description + name.
    // Its name starts with "vEth", which must not be mistaken for a Linux "veth" container interface.
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter vEthernet (External)")]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter #2 vEthernet (External Switch)")]
    public void Ordinary_wired_and_wifi_adapters_stay_eligible(NetworkInterfaceType type, string name)
        => Assert.True(WakeOnLan.IsEligible(type, name, true, Ip("192.168.1.20"), Ip("255.255.255.0")));

    private static WakeOnLan.LanNic Nic(string addr, string bcast, bool wired = true)
        => new("eth", Ip(addr), Ip(bcast), Mac, wired);

    [Fact]
    public void Wake_with_no_eligible_adapter_is_an_error_not_a_silent_success()
    {
        var sent = 0;
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01", [], (_, _, _) => sent++);
        Assert.Contains("no network interface", err);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Wake_sends_three_packets_to_the_subnet_and_the_limited_broadcast_from_the_adapter()
    {
        var sends = new List<(IPAddress From, IPEndPoint To, byte[] Packet)>();
        var err = WakeOnLan.Wake("aa-bb-cc-dd-ee-01", [Nic("192.168.1.20", "192.168.1.255")], (f, t, p) => sends.Add((f, t, p)));
        Assert.Null(err);
        Assert.Equal(6, sends.Count);
        Assert.All(sends, s => Assert.Equal(Ip("192.168.1.20"), s.From));
        Assert.All(sends, s => Assert.Equal(9, s.To.Port));
        Assert.All(sends, s => Assert.Equal(WakeOnLan.BuildPacket(Mac), s.Packet));
        Assert.Equal(3, sends.Count(s => s.To.Address.Equals(Ip("192.168.1.255"))));
        Assert.Equal(3, sends.Count(s => s.To.Address.Equals(IPAddress.Broadcast)));
    }

    [Fact]
    public void A_broadcast_override_that_is_the_networks_broadcast_replaces_the_default_targets()
    {
        var sends = new List<IPEndPoint>();
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01 192.168.1.255", [Nic("192.168.1.20", "192.168.1.255")], (_, t, _) => sends.Add(t));
        Assert.Null(err);
        Assert.Equal(3, sends.Count);
        Assert.All(sends, t => Assert.Equal(Ip("192.168.1.255"), t.Address));
    }

    [Fact]
    public void The_limited_broadcast_is_an_allowed_override()
    {
        var sends = new List<IPEndPoint>();
        Assert.Null(WakeOnLan.Wake("AA:BB:CC:DD:EE:01 255.255.255.255", [Nic("192.168.1.20", "192.168.1.255")], (_, t, _) => sends.Add(t)));
        Assert.All(sends, t => Assert.Equal(IPAddress.Broadcast, t.Address));
    }

    // The packet holds the target's MAC: it must never be aimed at an address outside this computer's own networks.
    [Fact]
    public void A_broadcast_override_that_is_not_a_broadcast_of_this_computers_networks_sends_nothing()
    {
        var sent = 0;
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01 192.168.9.255", [Nic("192.168.1.20", "192.168.1.255")], (_, _, _) => sent++);
        Assert.Contains("broadcast", err);
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.101.1.2")]
    [InlineData("169.254.1.1")]
    [InlineData("192.168.1")]
    [InlineData("1")]
    [InlineData("224.0.0.1")]
    public void A_public_or_odd_broadcast_address_is_refused_when_the_arguments_are_checked(string address)
        => Assert.Contains("broadcast", WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 " + address, out _, out _));

    [Theory]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.1.255")]
    [InlineData("10.0.0.255")]
    [InlineData("172.16.5.255")]
    public void A_local_broadcast_address_is_accepted_when_the_arguments_are_checked(string address)
        => Assert.Null(WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 " + address, out _, out _));

    private static WakeOnLan.LanNic NicWithMac(byte last, bool wired)
        => new("n" + last, Ip("192.168.1.20"), Ip("192.168.1.255"), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, last], wired);

    [Fact]
    public void Reported_macs_put_wired_adapters_first_without_duplicates_and_stop_at_four()
    {
        var nics = new[]
        {
            NicWithMac(1, wired: false), NicWithMac(2, wired: true), NicWithMac(2, wired: true), NicWithMac(3, wired: false),
            NicWithMac(4, wired: true), NicWithMac(5, wired: false), NicWithMac(6, wired: true),
        };
        Assert.Equal(["AA:BB:CC:DD:EE:02", "AA:BB:CC:DD:EE:04", "AA:BB:CC:DD:EE:06", "AA:BB:CC:DD:EE:01"], WakeOnLan.LocalMacs(nics));
    }

    [Fact]
    public void The_phone_may_ask_for_the_macs_over_wifi_but_not_for_anything_else_under_wake()
    {
        Assert.True(Lan.AllowedOverLan("/api/wake/macs"));
        Assert.False(Lan.AllowedOverLan("/api/wake"));
        Assert.False(Lan.AllowedOverLan("/api/wake/send"));
    }

    [Fact]
    public void A_failing_sender_is_reported()
    {
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01", [Nic("192.168.1.20", "192.168.1.255")], (_, _, _) => throw new System.Net.Sockets.SocketException());
        Assert.NotNull(err);
    }
}
