using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class MacPrivacyProbeTests
{
    /// <summary>Whatever is recording right now, reading it is harmless and never throws (nothing is asked of the apps).</summary>
    [Fact]
    public void ReadingTheMicrophoneAndCameraNeverThrowsOnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (mic, camera) = MacPrivacyProbe.Read();
        Assert.NotNull(mic);
        Assert.NotNull(camera);
        Assert.DoesNotContain(mic, string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(camera, string.IsNullOrWhiteSpace);
    }
}
