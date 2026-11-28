using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// GPUs from every source: nvidia-smi lines, LibreHardwareMonitor's data.json (AMD / Intel cards, issue #49),
/// Linux sysfs, and how they are merged into one list with the main GPU first.
/// </summary>
public sealed class GpuSourceTests
{
    private static GpuInfo G(string name, bool integrated = false, double? vram = null, string? source = null) =>
        new(name, null, null, null, null, null, vram, null, null, Source: source, Integrated: integrated);

    [Fact]
    public void TheDiscreteCardComesFirstEvenWhenTheIntegratedOneIsListedFirst()
    {
        var list = Gpus.Compose(null, null, [G("AMD Radeon(TM) Graphics", integrated: true, vram: 2), G("NVIDIA GeForce RTX 5080", vram: 15.6)]);
        Assert.Equal(["NVIDIA GeForce RTX 5080", "AMD Radeon(TM) Graphics"], list.Select(g => g.Name));
    }

    [Fact]
    public void TheSameCardFromTwoSourcesIsOneEntryAndTheFirstSourceWins()
    {
        var smi = new GpuInfo("NVIDIA GeForce RTX 5080", 3, 42.3, 40.7, 360, 3.1, 15.9, 0, 210, Vendor: "nvidia", Source: "nvidia-smi", Driver: "621.01");
        var os = new GpuInfo("NVIDIA GeForce RTX 5080", 5, 42.1, null, null, 3.14, 15.6, null, null, Vendor: "nvidia", Source: "windows",
            SharedUsed: 0.24, SharedTotal: 100.5, PowerPct: 11, TempMax: 88, Driver: "32.0.16.2101");
        var g = Assert.Single(Gpus.Compose([smi], null, [os]));
        Assert.Equal(3, g.Util);          // nvidia-smi's
        Assert.Equal(15.9, g.VramTotal);
        Assert.Equal("621.01", g.Driver);
        Assert.Equal(0.24, g.SharedUsed); // only the OS knows these
        Assert.Equal(88, g.TempMax);
        Assert.Equal("nvidia-smi+windows", g.Source);
    }

    [Fact]
    public void WithoutNvidiaSmiTheOsEntriesStay()
    {
        var list = Gpus.Compose(null, null, [G("AMD Radeon RX 7900 XTX", vram: 24)]);
        Assert.Equal("AMD Radeon RX 7900 XTX", Assert.Single(list).Name);
    }

    [Fact]
    public void TwoIdenticalCardsStayTwoEntries()
    {
        var list = Gpus.Compose([G("NVIDIA GeForce RTX 3090", vram: 24, source: "nvidia-smi"), G("NVIDIA GeForce RTX 3090", vram: 24, source: "nvidia-smi")],
            null, [G("NVIDIA GeForce RTX 3090", vram: 24, source: "windows"), G("NVIDIA GeForce RTX 3090", vram: 24, source: "windows")]);
        Assert.Equal(2, list.Count);
        Assert.All(list, g => Assert.Equal("nvidia-smi+windows", g.Source));
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics", true)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("AMD Radeon RX 6600 Graphics", false)]
    [InlineData("AMD Radeon Pro W7900", false)]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false)]
    public void IntegratedGraphicsAreToldApartByName(string name, bool integrated) => Assert.Equal(integrated, Gpus.LooksIntegrated(name));

    [Fact]
    public void NvidiaSmiLinesCarryTheIndexNameAndMemoryClock()
    {
        var names = Gpus.ParseNvidiaNames("0, NVIDIA GeForce RTX 5080, 621.01\r\n1, NVIDIA GeForce RTX 3060, 621.01\r\n");
        var row = Gpus.ParseNvidiaLine("1, 37, 61, 120.5, 170.00, 2048, 12288, 30, 1800, 7501", names);
        Assert.NotNull(row);
        Assert.Equal(1, row!.Value.Index);
        var g = row.Value.Gpu;
        Assert.Equal("NVIDIA GeForce RTX 3060", g.Name);
        Assert.Equal(37, g.Util);
        Assert.Equal(2.0, g.VramUsed);
        Assert.Equal(12.0, g.VramTotal);
        Assert.Equal(7501, g.MemClock);
        Assert.Equal("621.01", g.Driver);
        Assert.Equal("nvidia", g.Vendor);
    }

    [Fact]
    public void NvidiaSmiNotSupportedFieldsAreEmptyNotZero()
    {
        var row = Gpus.ParseNvidiaLine("0, 5, 40, [N/A], [N/A], 512, 4096, [N/A], 300, 405", new Dictionary<int, (string, string?)>());
        Assert.Null(row!.Value.Gpu.Power);
        Assert.Null(row.Value.Gpu.Fan);
        Assert.Equal("NVIDIA GPU", row.Value.Gpu.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.")]
    public void NvidiaSmiNoiseIsSkipped(string line) => Assert.Null(Gpus.ParseNvidiaLine(line, new Dictionary<int, (string, string?)>()));

    // A data.json in the shape LibreHardwareMonitor's HttpServer writes (GenerateJsonForNode): Text/Value strings in the
    // user's culture, Type and SensorId on sensors, HardwareId on hardware nodes.
    private const string LhmAmd = """
        {"id":0,"Text":"Sensor","Min":"Min","Value":"Value","Max":"Max","ImageURL":"","Children":[
          {"id":1,"Text":"DESKTOP","Min":"","Value":"","Max":"","ImageURL":"images_icon/computer.png","Children":[
            {"id":2,"Text":"AMD Ryzen 9 7950X","HardwareId":"/amdcpu/0","ImageURL":"images_icon/cpu.png","Min":"","Value":"","Max":"","Children":[
              {"id":3,"Text":"Temperatures","ImageURL":"images_icon/temperature.png","Min":"","Value":"","Max":"","Children":[
                {"id":4,"Text":"Core (Tctl/Tdie)","Min":"40,0 °C","Value":"61,5 °C","Max":"80,0 °C","SensorId":"/amdcpu/0/temperature/2","Type":"Temperature","RawValue":61.5,"ImageURL":"images/transparent.png","Children":[]}]}]},
            {"id":5,"Text":"AMD Radeon RX 7900 XTX","HardwareId":"/gpu-amd/0","ImageURL":"images_icon/ati.png","Min":"","Value":"","Max":"","Children":[
              {"id":6,"Text":"Temperatures","ImageURL":"images_icon/temperature.png","Min":"","Value":"","Max":"","Children":[
                {"id":7,"Text":"GPU Core","Min":"","Value":"52,0 °C","Max":"","SensorId":"/gpu-amd/0/temperature/0","Type":"Temperature","Children":[]},
                {"id":8,"Text":"GPU Memory","Min":"","Value":"58,0 °C","Max":"","SensorId":"/gpu-amd/0/temperature/1","Type":"Temperature","Children":[]},
                {"id":9,"Text":"GPU Hot Spot","Min":"","Value":"71,0 °C","Max":"","SensorId":"/gpu-amd/0/temperature/7","Type":"Temperature","Children":[]}]},
              {"id":10,"Text":"Load","ImageURL":"images_icon/load.png","Min":"","Value":"","Max":"","Children":[
                {"id":11,"Text":"GPU Core","Min":"","Value":"87,0 %","Max":"","SensorId":"/gpu-amd/0/load/0","Type":"Load","Children":[]},
                {"id":12,"Text":"GPU Memory","Min":"","Value":"40,0 %","Max":"","SensorId":"/gpu-amd/0/load/1","Type":"Load","Children":[]}]},
              {"id":13,"Text":"Clocks","ImageURL":"images_icon/clock.png","Min":"","Value":"","Max":"","Children":[
                {"id":14,"Text":"GPU Core","Min":"","Value":"2482,0 MHz","Max":"","SensorId":"/gpu-amd/0/clock/0","Type":"Clock","Children":[]},
                {"id":15,"Text":"GPU Memory","Min":"","Value":"2500,0 MHz","Max":"","SensorId":"/gpu-amd/0/clock/2","Type":"Clock","Children":[]}]},
              {"id":16,"Text":"Powers","ImageURL":"images_icon/power.png","Min":"","Value":"","Max":"","Children":[
                {"id":17,"Text":"GPU Core","Min":"","Value":"250,0 W","Max":"","SensorId":"/gpu-amd/0/power/0","Type":"Power","Children":[]},
                {"id":18,"Text":"GPU Package","Min":"","Value":"312,4 W","Max":"","SensorId":"/gpu-amd/0/power/3","Type":"Power","Children":[]}]},
              {"id":19,"Text":"Fans","ImageURL":"images_icon/fan.png","Min":"","Value":"","Max":"","Children":[
                {"id":20,"Text":"GPU Fan","Min":"","Value":"1450 RPM","Max":"","SensorId":"/gpu-amd/0/fan/0","Type":"Fan","Children":[]}]},
              {"id":21,"Text":"Controls","ImageURL":"images_icon/control.png","Min":"","Value":"","Max":"","Children":[
                {"id":22,"Text":"GPU Fan","Min":"","Value":"42,0 %","Max":"","SensorId":"/gpu-amd/0/control/0","Type":"Control","Children":[]}]},
              {"id":23,"Text":"Data","ImageURL":"images_icon/data.png","Min":"","Value":"","Max":"","Children":[
                {"id":24,"Text":"GPU Memory Used","Min":"","Value":"6,0 GB","Max":"","SensorId":"/gpu-amd/0/data/0","Type":"Data","Children":[]},
                {"id":25,"Text":"GPU Memory Total","Min":"","Value":"24,0 GB","Max":"","SensorId":"/gpu-amd/0/data/2","Type":"Data","Children":[]},
                {"id":26,"Text":"D3D Shared Memory Used","Min":"","Value":"512,0 MB","Max":"","SensorId":"/gpu-amd/0/data/6","Type":"Data","Children":[]}]}]}]}]}
        """;

    [Fact]
    public void AnAmdCardIsReadFromLibreHardwareMonitor()
    {
        using var doc = JsonDocument.Parse(LhmAmd);
        var g = Assert.Single(Gpus.ParseLhm(doc.RootElement)); // the CPU node is not a GPU
        Assert.Equal("AMD Radeon RX 7900 XTX", g.Name);
        Assert.Equal("amd", g.Vendor);
        Assert.Equal("lhm", g.Source);
        Assert.False(g.Integrated);
        Assert.Equal(87, g.Util);
        Assert.Equal(52, g.Temp);
        Assert.Equal(71, g.HotSpot);
        Assert.Equal(58, g.MemTemp);
        Assert.Equal(2482, g.Clock);
        Assert.Equal(2500, g.MemClock);
        Assert.Equal(312.4, g.Power); // the board (package), not the core
        Assert.Equal(1450, g.FanRpm);
        Assert.Equal(42, g.Fan);      // the fan duty is the control, not a load
        Assert.Equal(6, g.VramUsed);
        Assert.Equal(24, g.VramTotal);
        Assert.Equal(0.5, g.SharedUsed);
    }

    [Fact]
    public void OlderLibreHardwareMonitorWithoutIdsIsRecognisedByItsIcon()
    {
        // no HardwareId / SensorId / Type; memory as SmallData in MB; °F chosen in its settings
        const string json = """
            {"Text":"Sensor","Children":[{"Text":"PC","ImageURL":"images_icon/computer.png","Children":[
              {"Text":"Intel(R) Arc(TM) A770 Graphics","ImageURL":"images_icon/intel.png","Children":[
                {"Text":"Temperatures","Children":[{"Text":"GPU Core","Value":"140.0 °F","Children":[]}]},
                {"Text":"Load","Children":[{"Text":"GPU Core","Value":"12.0 %","Children":[]}]},
                {"Text":"SmallData","Children":[{"Text":"GPU Memory Used","Value":"8192.0 MB","Children":[]},{"Text":"GPU Memory Total","Value":"16384.0 MB","Children":[]}]}]}]}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var g = Assert.Single(Gpus.ParseLhm(doc.RootElement));
        Assert.Equal("intel", g.Vendor);
        Assert.False(g.Integrated);
        Assert.Equal(60, g.Temp);
        Assert.Equal(12, g.Util);
        Assert.Equal(8, g.VramUsed);
        Assert.Equal(16, g.VramTotal);
    }

    [Fact]
    public void AnLhmIntelIntegratedGpuIsMarkedIntegrated()
    {
        const string json = """
            {"Text":"Sensor","Children":[{"Text":"Intel(R) UHD Graphics 770","HardwareId":"/gpu-intel-integrated/0x4680","ImageURL":"images_icon/intel.png","Children":[
              {"Text":"Powers","Children":[{"Text":"GPU Power","Value":"3,1 W","Type":"Power","SensorId":"/gpu-intel-integrated/0x4680/power/0","Children":[]}]}]}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var g = Assert.Single(Gpus.ParseLhm(doc.RootElement));
        Assert.True(g.Integrated);
        Assert.Equal(3.1, g.Power);
        Assert.Null(g.Util); // the OS counters fill this in
    }

    [Fact]
    public void LhmFillsTheSensorsTheOsCannotRead()
    {
        using var doc = JsonDocument.Parse(LhmAmd);
        var os = new GpuInfo("AMD Radeon RX 7900 XTX", 85, 51, null, null, 6.1, 23.98, null, null, Vendor: "amd", Source: "windows",
            SharedUsed: 0.4, SharedTotal: 31.9, PowerPct: 88);
        var g = Assert.Single(Gpus.Compose(null, Gpus.ParseLhm(doc.RootElement), [os]));
        Assert.Equal(71, g.HotSpot);
        Assert.Equal(312.4, g.Power);
        Assert.Equal(31.9, g.SharedTotal);
        Assert.Equal(88, g.PowerPct);
        Assert.Equal("lhm+windows", g.Source);
    }

    [Theory]
    [InlineData("45,0 °C", null, "temp", 45.0)]
    [InlineData("113.0 °F", null, "temp", 45.0)]
    [InlineData("12,5 %", "Load", "load", 12.5)]
    [InlineData("40,0 %", "Control", "control", 40.0)]
    [InlineData("40,0 %", "Controls", "control", 40.0)]
    [InlineData("1,5 GB", "Data", "data", 1.5)]
    [InlineData("512,0 MB", "Data", "data", 0.5)]
    [InlineData("1 RPM", null, "rpm", 1.0)]
    [InlineData("1,2 GHz", null, "clock", 1200.0)]
    public void LhmValuesAreReadWithTheirUnit(string value, string? type, string kind, double expected)
    {
        var r = Gpus.ParseLhmValue(value, type);
        Assert.NotNull(r);
        Assert.Equal(kind, r!.Value.Kind);
        Assert.Equal(expected, r.Value.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1,0 V")] // voltages are not used
    public void OtherLhmValuesAreIgnored(string value) => Assert.Null(Gpus.ParseLhmValue(value, null));

    [Fact]
    public void AnAmdCardIsReadFromLinuxSysfs()
    {
        var root = Directory.CreateTempSubdirectory("pb-drm-");
        try
        {
            var dev = Directory.CreateDirectory(Path.Combine(root.FullName, "card1", "device")).FullName;
            Directory.CreateDirectory(Path.Combine(root.FullName, "card1-DP-1")); // a connector, not a card
            var hw = Directory.CreateDirectory(Path.Combine(dev, "hwmon", "hwmon3")).FullName;
            void W(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text + "\n");
            W(dev, "vendor", "0x1002");
            W(dev, "device", "0x744c");
            W(dev, "gpu_busy_percent", "64");
            W(dev, "mem_info_vram_used", (6L << 30).ToString());
            W(dev, "mem_info_vram_total", (24L << 30).ToString());
            W(dev, "mem_info_gtt_used", (1L << 29).ToString());
            W(dev, "mem_info_gtt_total", (16L << 30).ToString());
            W(dev, "pp_dpm_sclk", "0: 500Mhz\n1: 2482Mhz *\n2: 2620Mhz");
            W(dev, "pp_dpm_mclk", "0: 96Mhz\n3: 1250Mhz *");
            W(hw, "temp1_input", "52000"); W(hw, "temp1_label", "edge");
            W(hw, "temp2_input", "71000"); W(hw, "temp2_label", "junction");
            W(hw, "temp3_input", "58000"); W(hw, "temp3_label", "mem");
            W(hw, "temp1_crit", "100000");
            W(hw, "power1_average", "312400000");
            W(hw, "power1_cap", "339000000");
            W(hw, "fan1_input", "1450");
            W(hw, "fan1_max", "3300");
            var ids = Path.Combine(root.FullName, "pci.ids");
            File.WriteAllText(ids, "1000  Broadcom\n\t0001  Something\n1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t744c  Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]\n\t\t1002 0e3b  RX 7900 XTX\n");

            var g = Assert.Single(Gpus.ReadSysfs(root.FullName, ids));
            Assert.Equal("AMD Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M", g.Name);
            Assert.Equal("sysfs", g.Source);
            Assert.False(g.Integrated);
            Assert.Equal(64, g.Util);
            Assert.Equal(52, g.Temp);
            Assert.Equal(71, g.HotSpot);
            Assert.Equal(58, g.MemTemp);
            Assert.Equal(100, g.TempMax);
            Assert.Equal(312.4, g.Power);
            Assert.Equal(339, g.PowerLimit);
            Assert.Equal(6, g.VramUsed);
            Assert.Equal(24, g.VramTotal);
            Assert.Equal(0.5, g.SharedUsed);
            Assert.Equal(16, g.SharedTotal);
            Assert.Equal(2482, g.Clock);
            Assert.Equal(1250, g.MemClock);
            Assert.Equal(1450, g.FanRpm);
            Assert.Equal(44, g.Fan);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void LinuxNvidiaCardsAreLeftToNvidiaSmiAndAnApuIsIntegrated()
    {
        var root = Directory.CreateTempSubdirectory("pb-drm-");
        try
        {
            void Card(string name, string vendor, long vram)
            {
                var dev = Directory.CreateDirectory(Path.Combine(root.FullName, name, "device")).FullName;
                File.WriteAllText(Path.Combine(dev, "vendor"), vendor);
                File.WriteAllText(Path.Combine(dev, "device"), "0x15bf");
                File.WriteAllText(Path.Combine(dev, "mem_info_vram_total"), vram.ToString());
            }
            Card("card0", "0x10de", 8L << 30);
            Card("card1", "0x1002", 512L << 20);
            var g = Assert.Single(Gpus.ReadSysfs(root.FullName, Path.Combine(root.FullName, "missing.ids")));
            Assert.True(g.Integrated);
            Assert.Equal("AMD GPU", g.Name);
            Assert.Null(g.Util); // no file, no value: not 0
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void WithoutSysfsThereAreNoGpus() => Assert.Empty(Gpus.ReadSysfs(Path.Combine(Path.GetTempPath(), "pb-no-such-dir-" + Guid.NewGuid())));

    [Fact]
    public void TheCurrentDpmLevelIsTheStarredOne()
    {
        Assert.Equal(2482, Gpus.DpmCurrent("0: 500Mhz\n1: 2482Mhz *\n"));
        Assert.Null(Gpus.DpmCurrent("0: 500Mhz\n1: 2482Mhz\n"));
        Assert.Null(Gpus.DpmCurrent(null));
    }

    [Fact]
    public void UnicodeArcTrademarksFourGigRadeonSysfsMergePriorityAndCommaNvidiaNamesAreHandledCleanly()
    {
        Assert.False(Gpus.LooksIntegrated("Intel® Arc™ A770 Graphics"));
        Assert.False(Gpus.LooksIntegrated("Intel® Arc™ B580 Graphics"));
        Assert.True(Gpus.LooksIntegrated("Intel® Arc™ Graphics"));
        Assert.True(Gpus.LooksIntegrated("Intel® Arc™ 140V GPU"));

        var root = Directory.CreateTempSubdirectory("pb-drm-f392-");
        try
        {
            var dev = Directory.CreateDirectory(Path.Combine(root.FullName, "card0", "device")).FullName;
            File.WriteAllText(Path.Combine(dev, "vendor"), "0x1002\n");
            File.WriteAllText(Path.Combine(dev, "device"), "0x743f\n");
            File.WriteAllText(Path.Combine(dev, "product_name"), "AMD Radeon RX 6500 XT\n");
            File.WriteAllText(Path.Combine(dev, "mem_info_vram_total"), ((4L << 30) - (32L << 20)).ToString() + "\n");

            var sysfsCard = Assert.Single(Gpus.ReadSysfs(root.FullName));
            Assert.False(sysfsCard.Integrated);

            var primary = new GpuInfo("AMD Radeon RX 6500 XT", 40, 55, 80, 107, 2.0, 3.98, 35, 2600, Vendor: "amd", Source: "lhm", Integrated: false);
            var secondary = new GpuInfo("AMD Radeon RX 6500 XT", null, null, null, null, null, 3.98, null, null, Vendor: "amd", Source: "sysfs", Integrated: true);
            Assert.False(Gpus.Merge(primary, secondary).Integrated);

            var nv = Gpus.ParseNvidiaNames("0, NVIDIA Tegra, Orin, 540.3.0\n1, NVIDIA Tegra, Xavier, [N/A]\n");
            Assert.Equal(("NVIDIA Tegra, Orin", "540.3.0"), nv[0]);
            Assert.Equal(("NVIDIA Tegra, Xavier", null), nv[1]);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(1100u, 110.0)]   // tenths of a degree, as documented
    [InlineData(880u, 88.0)]
    [InlineData(110u, 110.0)]    // whole degrees, as the AMD Adrenalin driver reports it (#736)
    [InlineData(95u, 95.0)]
    public void ATemperatureLimitIsReadInTenthsOrWholeDegreesWhicheverIsPlausible(uint raw, double expected) =>
        Assert.Equal(expected, Gpus.TempMaxFromCaps(raw));

    [Theory]
    [InlineData(0u)]
    [InlineData(11u)]
    [InlineData(300u)]
    [InlineData(2000u)]
    [InlineData(uint.MaxValue)]
    public void AnImplausibleTemperatureLimitIsNoLimit(uint raw) => Assert.Null(Gpus.TempMaxFromCaps(raw));
}
