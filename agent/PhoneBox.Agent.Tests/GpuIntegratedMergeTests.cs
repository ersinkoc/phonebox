using PhoneBox.Agent;
using Xunit;

/// <summary>The Windows adapter's hybrid-integrated flag survives the merge with a LibreHardwareMonitor row for the same GPU.</summary>
public sealed class GpuIntegratedMergeTests
{
    private static GpuInfo Gpu(string name, double vram, string source, bool integrated) =>
        new(name, null, null, null, null, null, vram, null, null, Source: source, Integrated: integrated);

    [Fact]
    public void TheWindowsIntegratedFlagIsKeptWhenAnEarlierSourceMergesTheSameGpu()
    {
        // a name the heuristic doesn't recognise, so only Windows knows it is the integrated GPU of a hybrid laptop
        var lhm = new[] { Gpu("Vendor iGPU 600", 2, "lhm", false), Gpu("Vendor dGPU 9000", 1, "lhm", false) };
        var os = new[] { Gpu("Vendor iGPU 600", 2, "windows", true), Gpu("Vendor dGPU 9000", 1, "windows", false) };

        var gpus = Gpus.Compose(null, lhm, os);

        Assert.Equal(["Vendor dGPU 9000", "Vendor iGPU 600"], gpus.Select(g => g.Name));
        Assert.True(gpus[1].Integrated);
        Assert.False(gpus[0].Integrated);
    }
}
