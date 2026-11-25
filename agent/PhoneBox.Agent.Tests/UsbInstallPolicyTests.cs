using PhoneBox.Agent;
using Xunit;

public sealed class UsbInstallPolicyTests
{
    [Fact]
    public void SetupReadsTheAgentsJsoncAndWritesOnlyNormalizedSerials()
    {
        var folder = Directory.CreateTempSubdirectory("pb-usb-policy-");
        try
        {
            var config = Path.Combine(folder.FullName, "config.json");
            var output = Path.Combine(folder.FullName, "policy");
            File.WriteAllText(config, """
                {
                    // Legacy comments and trailing commas are accepted by the agent.
                    "IgnoredDeviceSerials": [" device-a ", "device-a", "DEVICE-A", "device\nb", null, ""],
                }
                """);
            Assert.True(AppPaths.TryWriteIgnoredUsb(output, config));
            Assert.Equal(["device-a", "DEVICE-A"], File.ReadAllLines(output));
            Assert.False(AppPaths.TryWriteIgnoredUsb(output, config));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void MissingConfigAllowsSetupAndMalformedConfigReturnsNoPolicy()
    {
        var folder = Directory.CreateTempSubdirectory("pb-usb-policy-");
        try
        {
            var config = Path.Combine(folder.FullName, "config.json");
            var first = Path.Combine(folder.FullName, "first");
            Assert.True(AppPaths.TryWriteIgnoredUsb(first, config));
            Assert.Empty(File.ReadAllLines(first));
            File.WriteAllText(config, """{"ignoredDeviceSerials": "device-a"}""");
            var second = Path.Combine(folder.FullName, "second");
            Assert.False(AppPaths.TryWriteIgnoredUsb(second, config));
            Assert.False(File.Exists(second));
        }
        finally { folder.Delete(recursive: true); }
    }
}
