using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class BatteryParsingTests
{
    [Fact]
    public void MacBluetoothKeepsValidLevelsAndDevicesAroundMalformedEntries()
    {
        using var doc = JsonDocument.Parse("""
            {
              "SPBluetoothDataType": [
                {"device_connected": [
                  {"AirPods A":{"device_batteryLevelMain":"72%"}},
                  {"Unknown":{"device_batteryLevelMain":{}}},
                  {"AirPods B":{"device_batteryLevelLeft":false,"device_batteryLevelRight":"48%","device_batteryLevelMain":"60%"}},
                  null,
                  {"Malformed":0},
                  {"MX Keys":{"device_batteryLevelMain":"18%"}}
                ]},
                {"device_connected":null},
                {"device_connected":[{"Headset Z":{"device_batteryLevelMain":"25%"}}]}
              ]
            }
            """);
        var devices = new List<DeviceBattery> { new("MX Keys", 80, "keyboard", null) };

        BatteryMonitor.AddMacBluetoothBatteries(doc.RootElement, devices);

        Assert.Equal([
            new DeviceBattery("MX Keys", 80, "keyboard", null),
            new DeviceBattery("AirPods A", 72, "headphones", null),
            new DeviceBattery("AirPods B", 48, "headphones", null),
            new DeviceBattery("Headset Z", 25, "headphones", null)
        ], devices);
    }
}
