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

    // The fixtures below follow real ioreg output from an Intel Mac with a Radeon, trimmed to the keys that matter, with
    // the model, classes and registry ids changed. `ioreg -r -d 1 -w 0 -c IOAccelerator` prints one entry per
    // accelerator (sometimes the same one twice); `ioreg -r -d 2 -w 0 -c IOPCIDevice` prints every PCI device with its
    // properties and, indented under it, the headings of its own children, the accelerator among them.

    private const string Radeon5500M = """
        +-o AMDRadeonX6000_AMDNavi14GraphicsAccelerator  <class AMDRadeonX6000_AMDNavi14GraphicsAccelerator, id 0x1000005d5, registered, matched, active, busy 0 (89 ms), retain 125>
            {
              "IOClass" = "AMDRadeonX6000_AMDNavi14GraphicsAccelerator"
              "IOProviderClass" = "IOPCIDevice"
              "PerformanceStatistics" = {"Device Utilization %"=12,"inUseVidMemoryBytes"=1610612736,"vramFreeBytes"=2684354560,"Temperature(C)"=51,"Total Power(W)"=20,"Fan Speed(%)"=0,"Fan Speed(RPM)"=0,"Core Clock(MHz)"=0,"Memory Clock(MHz)"=1730,"inUseSysMemoryBytes"=127045632}
            }

        """;

    private const string Rx580 = """
        +-o AMDRadeonX4000_AMDEllesmereGraphicsAccelerator  <class AMDRadeonX4000_AMDEllesmereGraphicsAccelerator, id 0x100000a21, registered, matched, active, busy 0 (12 ms), retain 98>
            {
              "IOClass" = "AMDRadeonX4000_AMDEllesmereGraphicsAccelerator"
              "PerformanceStatistics" = {"Device Utilization %"=88,"inUseVidMemoryBytes"=6442450944,"Temperature(C)"=74,"Total Power(W)"=140,"Fan Speed(%)"=45,"Core Clock(MHz)"=1340}
            }

        """;

    private const string IntelUhd = """
        +-o IntelAccelerator  <class IntelAccelerator, id 0x100000401, registered, matched, active, busy 0 (3 ms), retain 40>
            {
              "IOClass" = "IntelAccelerator"
              "PerformanceStatistics" = {"Device Utilization %"=77,"inUseVidMemoryBytes"=536870912,"Temperature(C)"=90}
            }

        """;

    private const string MacPci = """
        +-o IGPU@2  <class IOPCIDevice, id 0x100000390, registered, matched, active, busy 0 (210 ms), retain 30>
          | {
          |   "vendor-id" = <86800000>
          |   "model" = <"Intel UHD Graphics 630">
          |   "VRAM,totalMB" = 1536
          | }
          |
          +-o AppleIntelFramebufferController  <class AppleIntelFramebufferController, id 0x100000400, registered, matched, active, busy 0 (5 ms), retain 12>
          +-o IntelAccelerator  <class IntelAccelerator, id 0x100000401, registered, matched, active, busy 0 (3 ms), retain 40>

        +-o pci-bridge@0  <class IOPCIDevice, id 0x10000039a, registered, matched, active, busy 0 (1500 ms), retain 14>
          | {
          |   "vendor-id" = <02100000>
          |   "model" = <"Navi 10 XL Downstream Port of PCI Express Switch">
          | }
          |
          +-o display@0  <class IOPCIDevice, id 0x10000039e, registered, matched, active, busy 0 (1450 ms), retain 22>

        +-o display@0  <class IOPCIDevice, id 0x10000039e, registered, matched, active, busy 0 (1450 ms), retain 22>
          | {
          |   "device-id" = <40730000>
          |   "vendor-id" = <02100000>
          |   "model" = <"AMD Radeon Pro 5500M">
          |   "VRAM,totalMB" = 4080
          |   "attached-gpu-control-path" = "IOService:/AppleACPIPlatformExpert/PC00@0/AppleACPIPCI/PEG0@1/IOPP/display@0/AMDRadeonX6000_AmdRadeonFramebuffer"
          | }
          |
          +-o AMDSupport  <class AMDSupport, id 0x100000598, registered, matched, active, busy 0 (0 ms), retain 5>
          +-o AMDRadeonX6000_AmdRadeonControllerNavi14  <class AMDRadeonX6000_AmdRadeonControllerNavi14, id 0x1000005d4, registered, matched, active, busy 0 (2 ms), retain 9>
          +-o AMDRadeonX6000_AMDNavi14GraphicsAccelerator  <class AMDRadeonX6000_AMDNavi14GraphicsAccelerator, id 0x1000005d5, registered, matched, active, busy 0 (89 ms), retain 125>

        +-o HDAU@0,1  <class IOPCIDevice, id 0x10000039f, registered, matched, active, busy 0 (85 ms), retain 12>
          | {
          |   "vendor-id" = <02100000>
          |   "model" = <"Navi 14 HDMI/DP Audio Controller">
          | }

        """;

    // an RX 580 in an eGPU box, plugged into the same Mac
    private const string EgpuPci = """
        +-o display@0  <class IOPCIDevice, id 0x100000a1c, registered, matched, active, busy 0 (900 ms), retain 20>
          | {
          |   "vendor-id" = <02100000>
          |   "model" = <"Radeon RX 580">
          |   "VRAM,totalMB" = 8192
          | }
          |
          +-o AMDRadeonX4000_AMDEllesmereGraphicsAccelerator  <class AMDRadeonX4000_AMDEllesmereGraphicsAccelerator, id 0x100000a21, registered, matched, active, busy 0 (12 ms), retain 98>

        """;

    private const double SixteenGbRam = 16.0 * 1024 * 1024 * 1024;

    [Fact]
    public void EachPciCardIsFoundByTheIdsOfItsChildren()
    {
        var cards = SystemMonitor.ParseMacPciGpus(MacPci + EgpuPci);
        Assert.Equal(new SystemMonitor.MacPciGpu("AMD Radeon Pro 5500M", 4080.0 * 1024 * 1024, 0x1002), cards["0x1000005d5"]);
        Assert.Equal(new SystemMonitor.MacPciGpu("Intel UHD Graphics 630", 1536.0 * 1024 * 1024, 0x8086), cards["0x100000401"]);
        Assert.Equal("Radeon RX 580", cards["0x100000a21"].Name);
        // the bridge above the card lists display@0 among its children, but has no memory of its own: it is no card
        Assert.False(cards.ContainsKey("0x10000039e"));
    }

    [Fact]
    public void ADiscreteCardShowsItsOwnMemoryNotTheMachinesRam()
    {
        var gpus = SystemMonitor.ParseAppleGpus(Radeon5500M, SixteenGbRam, SystemMonitor.ParseMacPciGpus(MacPci));
        var g = Assert.Single(gpus);
        Assert.Equal("AMD Radeon Pro 5500M", g.Name);
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
    public void AnIntelAndARadeonEachKeepTheirOwnFigures()
    {
        // the Intel GPU is listed first, with its own load, memory and temperature: none of it may reach the Radeon
        var gpus = SystemMonitor.ParseAppleGpus(IntelUhd + Radeon5500M, SixteenGbRam, SystemMonitor.ParseMacPciGpus(MacPci));
        Assert.Equal(2, gpus.Count);
        var intel = gpus.Single(g => g.Vendor == "intel");
        var amd = gpus.Single(g => g.Vendor == "amd");
        Assert.Equal(("Intel UHD Graphics 630", 77d, 0.5, 1.5, 90d), (intel.Name, intel.Util!.Value, intel.VramUsed!.Value, intel.VramTotal!.Value, intel.Temp!.Value));
        Assert.True(intel.Integrated);   // its memory is carved out of RAM
        Assert.Equal(("AMD Radeon Pro 5500M", 12d, 1.5, 4.0, 51d), (amd.Name, amd.Util!.Value, amd.VramUsed!.Value, amd.VramTotal!.Value, amd.Temp!.Value));
        Assert.False(amd.Integrated);
        // the panel shows the discrete card first
        Assert.Equal("AMD Radeon Pro 5500M", Gpus.Compose(null, null, gpus)[0].Name);
    }

    [Fact]
    public void TwoRadeonsNeverMixTheirNumbers()
    {
        // a MacBook Pro's 5500M and an RX 580 eGPU: each accelerator is paired with its own PCI device by registry id,
        // not with the AMD card that has the most memory
        var gpus = SystemMonitor.ParseAppleGpus(Radeon5500M + Rx580, SixteenGbRam, SystemMonitor.ParseMacPciGpus(MacPci + EgpuPci));
        Assert.Equal(2, gpus.Count);
        Assert.Equal(("AMD Radeon Pro 5500M", 12d, 1.5, 4.0, 51d), (gpus[0].Name, gpus[0].Util!.Value, gpus[0].VramUsed!.Value, gpus[0].VramTotal!.Value, gpus[0].Temp!.Value));
        Assert.Equal(("Radeon RX 580", 88d, 6.0, 8.0, 74d), (gpus[1].Name, gpus[1].Util!.Value, gpus[1].VramUsed!.Value, gpus[1].VramTotal!.Value, gpus[1].Temp!.Value));
    }

    [Fact]
    public void AnAcceleratorListedTwiceCountsOnce()
    {
        var gpus = SystemMonitor.ParseAppleGpus(Radeon5500M + Radeon5500M, SixteenGbRam, SystemMonitor.ParseMacPciGpus(MacPci));
        Assert.Single(gpus);
    }

    [Fact]
    public void WithoutItsPciDeviceADiscreteCardStillShowsWhatItReports()
    {
        var g = Assert.Single(SystemMonitor.ParseAppleGpus(Radeon5500M, SixteenGbRam));
        Assert.Equal(1.5, g.VramUsed);
        Assert.Null(g.VramTotal);
    }

    [Fact]
    public void ThePciDevicesAreReadOnlyForGpusWithMemoryOfTheirOwn()
    {
        // Apple silicon: no accelerator with memory of its own, so the agent never starts the PCI read
        Assert.Equal("", SystemMonitor.OwnMemoryAccelerators("""
            +-o AGXAcceleratorG14X  <class AGXAcceleratorG14X, id 0x1000002a0, registered, matched, active, busy 0 (0 ms), retain 30>
                {
                  "model" = "Apple M2 Pro"
                  "PerformanceStatistics" = {"Device Utilization %"=37,"Alloc system memory"=2147483648}
                }
            """));
        // an Intel Mac: read once; the same GPUs listed again (or twice) need no new read, an eGPU plugged in does
        var before = SystemMonitor.OwnMemoryAccelerators(IntelUhd + Radeon5500M);
        Assert.Equal("0x100000401,0x1000005d5", before);
        Assert.Equal(before, SystemMonitor.OwnMemoryAccelerators(Radeon5500M + IntelUhd + Radeon5500M));
        Assert.NotEqual(before, SystemMonitor.OwnMemoryAccelerators(IntelUhd + Radeon5500M + Rx580));
    }

    [Fact]
    public void APciReadThatFindsNoCardIsNotTakenAsDone()
    {
        var own = SystemMonitor.OwnMemoryAccelerators(IntelUhd + Radeon5500M);
        var good = SystemMonitor.ParseMacPciGpus(MacPci);
        Assert.True(SystemMonitor.HasEveryCard(good, own));
        // ioreg printed the headings but no properties: no card, so the good list stays and the read is not done
        var empty = SystemMonitor.ParseMacPciGpus("+-o display@0  <class IOPCIDevice, id 0x10000039e, registered>\n  | {\n  | }\n  +-o AMDRadeonX6000_AMDNavi14GraphicsAccelerator  <class AMDRadeonX6000_AMDNavi14GraphicsAccelerator, id 0x1000005d5, registered>\n");
        Assert.Empty(empty);
        Assert.Same(good, SystemMonitor.MergeCards(good, empty));
        Assert.False(SystemMonitor.HasEveryCard(SystemMonitor.MergeCards(new Dictionary<string, SystemMonitor.MacPciGpu>(), empty), own));
        // a read that finds only the Radeon is not done either, but keeps the Intel GPU found before
        var partial = SystemMonitor.MergeCards(good, SystemMonitor.ParseMacPciGpus(EgpuPci));
        Assert.Equal("Intel UHD Graphics 630", partial["0x100000401"].Name);
        Assert.False(SystemMonitor.HasEveryCard(SystemMonitor.ParseMacPciGpus(EgpuPci), SystemMonitor.OwnMemoryAccelerators(Radeon5500M + Rx580)));
        Assert.True(SystemMonitor.HasEveryCard(partial, SystemMonitor.OwnMemoryAccelerators(Radeon5500M + Rx580)));
    }

    [Fact]
    public void AFailedPciReadIsRetriedWithBackoffAndNeverGivenUp()
    {
        int tries = 0, failures = 0;
        // timeouts (or headings without properties): no card found, retried 1, 2, 4, 8, 16, 30, 30 … minutes later
        var waits = Enumerable.Range(0, 8).Select(_ => SystemMonitor.NextPciRead(false, false, ref tries, ref failures)!.Value.TotalMinutes).ToList();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], waits);
        Assert.Equal(0, tries); // failures don't use up the tries
        // then ioreg recovers and finds every card: done
        Assert.Null(SystemMonitor.NextPciRead(true, true, ref tries, ref failures));
    }

    [Fact]
    public void AnIncompletePciReadGetsThreeTriesAMinuteApart()
    {
        int tries = 0, failures = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), SystemMonitor.NextPciRead(true, false, ref tries, ref failures));
        Assert.Equal(TimeSpan.FromMinutes(1), SystemMonitor.NextPciRead(true, false, ref tries, ref failures));
        Assert.Null(SystemMonitor.NextPciRead(true, false, ref tries, ref failures)); // a GPU whose device has no VRAM,totalMB
    }

    [Theory]
    [InlineData("")]
    [InlineData("+-o pci-bridge@1  <class IOPCIDevice, id 0x100000200, registered>\n  | {\n  |   \"model\" = <\"Some bridge\">\n  | }\n  +-o child  <class IOPCIDevice, id 0x100000201, registered>")]
    public void WithoutVideoMemoryThereIsNoPciCard(string text) => Assert.Empty(SystemMonitor.ParseMacPciGpus(text));
}
