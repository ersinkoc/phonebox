using PhoneBox.Agent;
using Xunit;

public class NativeCameraTests
{
    [Theory]
    [InlineData("D:/Program Files/PhoneBox/Camera/PhoneBoxCamera.dll", true)]
    [InlineData("D:/Program Files/PhoneBox/Camera/../Camera/PhoneBoxCamera.dll", true)]
    [InlineData("D:/Program Files/PhoneBox/Camera/PhoneBoxCamera.dll.attacker", false)]
    [InlineData("D:/Program Files/PhoneBox/CameraElse/PhoneBoxCamera.dll", false)]
    [InlineData("D:/Users/person/PhoneBoxCamera.dll", false)]
    [InlineData("PhoneBoxCamera.dll", false)]
    [InlineData(null, false)]
    public void NativeLibraryMustHaveExactProtectedInstallLocation(string? path, bool valid)
    {
        if (!OperatingSystem.IsWindows()) return; // platform-specific absolute path syntax
        Assert.Equal(valid, NativeCamera.IsExpectedPath(path, "D:/Program Files/PhoneBox/Camera"));
    }

    [Fact]
    public async Task DoesNotEnableCameraOnConstructionAndRejectsInvalidPort()
    {
        using var camera = new NativeCamera();
        Assert.False(camera.Status().Running);
        Assert.NotNull(await camera.Enable(80));
        Assert.False(camera.Status().Running);
        Assert.False(camera.Status().Busy);
        Assert.Null(await camera.Disable());
    }
}
