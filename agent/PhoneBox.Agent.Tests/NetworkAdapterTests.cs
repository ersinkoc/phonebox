using System.Net.NetworkInformation;
using PhoneBox.Agent;
using Xunit;

/// <summary>#190 / #169: the network meters count the adapters the user picked, or else the automatic choice.</summary>
public class NetworkAdapterTests
{
    [Fact]
    public void AutomaticCountsEthernetAndWifiButNotVirtualOrVpn()
    {
        Assert.True(SystemMonitor.Counted("Ethernet", "Realtek Gaming USB 2.5GbE Family Controller", NetworkInterfaceType.Ethernet, []));
        Assert.True(SystemMonitor.Counted("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, []));
        Assert.False(SystemMonitor.Counted("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, []));
        Assert.False(SystemMonitor.Counted("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet, []));
        Assert.False(SystemMonitor.Counted("WireGuard", "WireGuard Tunnel", NetworkInterfaceType.Unknown, [])); // not Ethernet / Wi-Fi
    }

    [Fact]
    public void ChosenAdaptersReplaceTheAutomaticChoice()
    {
        string[] chosen = ["ethernet"]; // names match without case
        Assert.True(SystemMonitor.Counted("Ethernet", "Realtek Gaming USB 2.5GbE Family Controller", NetworkInterfaceType.Ethernet, chosen));
        Assert.False(SystemMonitor.Counted("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, chosen));
        // a pick may be anything the user wants counted, a VPN tunnel too
        Assert.True(SystemMonitor.Counted("WireGuard", "WireGuard Tunnel", NetworkInterfaceType.Unknown, ["WireGuard"]));
    }

    [Fact]
    public void TheSettingIsTidiedOnSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-net-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{ "ping": { "host": "1.1.1.1", "adapters": [" Ethernet ", "ethernet", "", "Wi-Fi"] } }"""));
            Assert.Equal(["Ethernet", "Wi-Fi"], store.Get().Ping.Adapters);
        }
        finally { File.Delete(path); }
    }
}
