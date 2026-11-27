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

    // An Intel Mac with a Radeon (here a Radeon Pro 5500M with 4 GB): IOAccelerator, then its PCI devices.
    private const string RadeonAccelerator = """
        +-o AMDRadeonX6000_AMDNavi14GraphicsAccelerator  <class AMDRadeonX6000_AMDNavi14GraphicsAccelerator>
          {
            "IOClass" = "AMDRadeonX6000_AMDNavi14GraphicsAccelerator"
            "PerformanceStatistics" = {"Device Utilization %"=12,"inUseVidMemoryBytes"=1610612736,"vramFreeBytes"=2684354560,"Temperature(C)"=51,"Total Power(W)"=20,"Fan Speed(%)"=0,"Fan Speed(RPM)"=0,"Core Clock(MHz)"=0,"Memory Clock(MHz)"=1730,"inUseSysMemoryBytes"=127045632}
          }
        """;

    // The same Mac's Intel GPU, which ioreg can list first.
    private const string IntelAccelerator = """
        +-o IntelAccelerator  <class IntelAccelerator>
          {
            "IOClass" = "IntelAccelerator"
            "PerformanceStatistics" = {"Device Utilization %"=77,"inUseVidMemoryBytes"=536870912,"Temperature(C)"=90}
          }
        """;

    private const string RadeonPci = """
        +-o pci8086,1901@1  <class IOPCIDevice>
          {
            "vendor-id" = <86800000>
            "model" = <"PCI Express Root Port">
          }
        +-o IGPU@2  <class IOPCIDevice>
          {
            "vendor-id" = <86800000>
            "model" = <"Intel UHD Graphics 630">
            "VRAM,totalMB" = 1536
          }
        +-o display@0  <class IOPCIDevice>
          {
            "vendor-id" = <02100000>
            "model" = <"AMD Radeon Pro 5500M">
            "VRAM,totalMB" = 4080
          }
        """;

    private const double SixteenGbRam = 16.0 * 1024 * 1024 * 1024;

    [Fact]
    public void PciCardsCarryNameSizeAndVendor()
    {
        var cards = SystemMonitor.ParseMacPciGpus(RadeonPci);
        Assert.Equal(2, cards.Count);
        Assert.Equal(new SystemMonitor.MacPciGpu("Intel UHD Graphics 630", 1536.0 * 1024 * 1024, 0x8086), cards[0]);
        Assert.Equal(new SystemMonitor.MacPciGpu("AMD Radeon Pro 5500M", 4080.0 * 1024 * 1024, 0x1002), cards[1]);
    }

    [Fact]
    public void ADiscreteCardShowsItsOwnMemoryNotTheMachinesRam()
    {
        var g = SystemMonitor.ParseAppleGpu(RadeonAccelerator, SixteenGbRam, SystemMonitor.ParseMacPciGpus(RadeonPci));
        Assert.Equal("AMD Radeon Pro 5500M", g!.Name);
        Assert.Equal(12, g.Util);
        Assert.Equal(1.5, g.VramUsed);
        Assert.Equal(4.0, g.VramTotal);  // 4080 MB, not the 16 GB of RAM
        Assert.Equal(51, g.Temp);
        Assert.Equal(20, g.Power);
        Assert.Null(g.Fan);              // a stopped fan reports 0: shown as nothing, like a missing sensor
        Assert.Null(g.FanRpm);
        Assert.Null(g.Clock);            // so does an idle clock
        Assert.Equal(1730, g.MemClock);
        Assert.Equal("amd", g.Vendor);
        Assert.False(g.Integrated);
    }

    [Fact]
    public void WithTwoGpusEveryFigureComesFromTheRadeon()
    {
        // the Intel GPU comes first, with its own load, memory and temperature: none of it may leak in
        var g = SystemMonitor.ParseAppleGpu(IntelAccelerator + RadeonAccelerator, SixteenGbRam, SystemMonitor.ParseMacPciGpus(RadeonPci));
        Assert.Equal("AMD Radeon Pro 5500M", g!.Name);
        Assert.Equal(12, g.Util);
        Assert.Equal(1.5, g.VramUsed);
        Assert.Equal(4.0, g.VramTotal);
        Assert.Equal(51, g.Temp);
        Assert.Equal("amd", g.Vendor);
    }

    [Fact]
    public void AnIntelGpuAloneIsPairedWithItsOwnPciDevice()
    {
        var g = SystemMonitor.ParseAppleGpu(IntelAccelerator, SixteenGbRam, SystemMonitor.ParseMacPciGpus(RadeonPci));
        Assert.Equal("Intel UHD Graphics 630", g!.Name);
        Assert.Equal(77, g.Util);
        Assert.Equal(0.5, g.VramUsed);
        Assert.Equal(1.5, g.VramTotal);
        Assert.Equal("intel", g.Vendor);
    }

    [Fact]
    public void WithoutItsPciDeviceADiscreteCardStillShowsWhatItReports()
    {
        var g = SystemMonitor.ParseAppleGpu(RadeonAccelerator, SixteenGbRam);
        Assert.Equal(1.5, g!.VramUsed);
        Assert.Null(g.VramTotal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+-o pci8086,1901@1  <class IOPCIDevice>\n  {\n    \"model\" = <\"Some bridge\">\n  }")]
    public void AppleSiliconHasNoPciGraphicsCard(string text) => Assert.Empty(SystemMonitor.ParseMacPciGpus(text));
}
