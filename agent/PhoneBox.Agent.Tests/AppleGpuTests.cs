using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The macOS GPU comes from ioreg text (no nvidia-smi there): on Apple silicon load, memory the GPU holds and the
/// machine's RAM as total; on an Intel Mac with a Radeon the card's own video memory, temperature and power.
/// </summary>
public sealed class AppleGpuTests
{
    private const double SixteenGb = 16.0 * 1024 * 1024 * 1024;

    [Fact]
    public void LoadAndMemoryAreReadFromTheAcceleratorEntry()
    {
        var g = SystemMonitor.ParseAppleGpu("""
            +-o AGXAcceleratorG14X  <class AGXAcceleratorG14X>
              {
                "model" = "Apple M2 Pro"
                "PerformanceStatistics" = {"Device Utilization %"=37,"Alloc system memory"=2147483648,"Renderer Utilization %"=30}
              }
            """, SixteenGb);
        Assert.NotNull(g);
        Assert.Equal("Apple M2 Pro", g!.Name);
        Assert.Equal(37, g.Util);
        Assert.Equal(2.0, g.VramUsed);
        Assert.Equal(16.0, g.VramTotal);
        Assert.Null(g.Temp);   // no sensors: the panel must not show 0 °C
        Assert.Null(g.Power);
    }

    [Fact]
    public void SpacesAroundTheEqualsSignAreFine()
    {
        var g = SystemMonitor.ParseAppleGpu("\"Device Utilization %\" = 5\n\"Alloc system memory\" = 1073741824", SixteenGb);
        Assert.Equal(5, g!.Util);
        Assert.Equal(1.0, g.VramUsed);
        Assert.Equal("Apple GPU", g.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothing useful here")]
    public void WithoutAnAcceleratorThereIsNoGpu(string text) => Assert.Null(SystemMonitor.ParseAppleGpu(text, SixteenGb));

    [Fact]
    public void WithoutAMemoryFigureTheMemoryStaysEmpty()
    {
        var g = SystemMonitor.ParseAppleGpu("\"Device Utilization %\"=9", 0);
        Assert.Equal(9, g!.Util);
        Assert.Null(g.VramUsed);
        Assert.Null(g.VramTotal);
    }

    // An Intel Mac with a Radeon (here a Radeon Pro 5500M with 4 GB): IOAccelerator, then its PCI device.
    private const string RadeonAccelerator = """
        +-o AMDRadeonX6000_AMDNavi14GraphicsAccelerator  <class AMDRadeonX6000_AMDNavi14GraphicsAccelerator>
          {
            "IOClass" = "AMDRadeonX6000_AMDNavi14GraphicsAccelerator"
            "PerformanceStatistics" = {"Device Utilization %"=12,"inUseVidMemoryBytes"=1610612736,"vramFreeBytes"=2684354560,"Temperature(C)"=51,"Total Power(W)"=20,"Fan Speed(%)"=0,"Fan Speed(RPM)"=0,"Core Clock(MHz)"=89,"Memory Clock(MHz)"=1730,"inUseSysMemoryBytes"=127045632}
          }
        """;

    private const string RadeonPci = """
        +-o pci8086,1901@1  <class IOPCIDevice>
          {
            "model" = <"PCI Express Root Port">
          }
        +-o display@0  <class IOPCIDevice>
          {
            "model" = <"AMD Radeon Pro 5500M">
            "VRAM,totalMB" = 4080
          }
        """;

    [Fact]
    public void ADiscreteCardShowsItsOwnMemoryNotTheMachinesRam()
    {
        var card = SystemMonitor.ParseMacPciGpu(RadeonPci);
        Assert.Equal("AMD Radeon Pro 5500M", card!.Value.Name);

        var g = SystemMonitor.ParseAppleGpu(RadeonAccelerator, 16.0 * 1024 * 1024 * 1024, card);
        Assert.Equal("AMD Radeon Pro 5500M", g!.Name);
        Assert.Equal(12, g.Util);
        Assert.Equal(1.5, g.VramUsed);
        Assert.Equal(4.0, g.VramTotal);  // 4080 MB, not the 16 GB of RAM
        Assert.Equal(51, g.Temp);
        Assert.Equal(20, g.Power);
        Assert.Null(g.Fan);              // a stopped fan reports 0: shown as nothing, like a missing sensor
        Assert.Equal("amd", g.Vendor);
        Assert.False(g.Integrated);
    }

    [Fact]
    public void WithoutItsPciDeviceADiscreteCardStillShowsWhatItReports()
    {
        var g = SystemMonitor.ParseAppleGpu(RadeonAccelerator, 16.0 * 1024 * 1024 * 1024);
        Assert.Equal(1.5, g!.VramUsed);
        Assert.Null(g.VramTotal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+-o pci8086,1901@1  <class IOPCIDevice>\n  {\n    \"model\" = <\"Some bridge\">\n  }")]
    public void AppleSiliconHasNoPciGraphicsCard(string text) => Assert.Null(SystemMonitor.ParseMacPciGpu(text));
}
