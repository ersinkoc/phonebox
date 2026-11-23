using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The CPU temperature from LibreHardwareMonitor's data.json: the package or Tctl/Tdie reading first, and on older Intel
/// processors that only report per-core sensors, the hottest core of the CPU (idea from #164).
/// </summary>
public sealed class CpuTempTests
{
    private static double? Temp(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SystemMonitor.FindCpuTemp(doc.RootElement);
    }

    private static string Sensor(string text, string value, string type = "Temperature") =>
        $$"""{"Text":"{{text}}","Value":"{{value}}","Type":"{{type}}","Children":[]}""";

    private static string Hardware(string text, string hardwareId, string image, params string[] groups) =>
        $$"""{"Text":"{{text}}","HardwareId":"{{hardwareId}}","ImageURL":"{{image}}","Children":[{{string.Join(",", groups)}}]}""";

    private static string Group(string text, params string[] sensors) =>
        $$"""{"Text":"{{text}}","Children":[{{string.Join(",", sensors)}}]}""";

    private static string Root(params string[] hardware) =>
        $$"""{"Text":"Sensor","Children":[{"Text":"PC","Children":[{{string.Join(",", hardware)}}]}]}""";

    [Fact]
    public void ThePackageReadingWinsWhenThereIsOne()
    {
        var json = Root(Hardware("Intel Core i7-12700K", "/intelcpu/0", "images_icon/cpu.png",
            Group("Temperatures", Sensor("CPU Core #1", "71,0 °C"), Sensor("CPU Package", "64,0 °C"))));
        Assert.Equal(64.0, Temp(json));
    }

    [Fact]
    public void OlderIntelCpusUseTheHottestCore()
    {
        // an i5-3570K reports no package sensor in some LHM versions, only the cores
        var json = Root(Hardware("Intel Core i5-3570K", "/intelcpu/0", "images_icon/cpu.png",
            Group("Clocks", Sensor("CPU Core #1", "3400,0 MHz", "Clock")),
            Group("Temperatures", Sensor("CPU Core #1", "48,0 °C"), Sensor("CPU Core #2", "53,5 °C"), Sensor("Core #3", "50,0 °C")),
            Group("Load", Sensor("CPU Core #2", "97,0 %", "Load"))));
        Assert.Equal(53.5, Temp(json));
    }

    [Fact]
    public void CoreSensorsOutsideTheCpuNeverCount()
    {
        // a GPU or a disk with a "Core #1"-like name must not become the CPU temperature
        var json = Root(
            Hardware("NVIDIA GeForce RTX 4070", "/gpu-nvidia/0", "images_icon/nvidia.png", Group("Temperatures", Sensor("Core #1", "88,0 °C"))),
            Hardware("Samsung SSD 980", "/nvme/0", "images_icon/hdd.png", Group("Temperatures", Sensor("Core #2", "79,0 °C"))));
        Assert.Null(Temp(json));
    }

    [Fact]
    public void ACpuIsRecognisedByItsIconWhenItHasNoHardwareId()
    {
        var json = Root(Hardware("Intel Core 2 Quad Q9550", "", "images_icon/cpu.png",
            Group("Temperatures", Sensor("CPU Core #1", "45,0 °C"), Sensor("CPU Core #4", "47,0 °C"))));
        Assert.Equal(47.0, Temp(json));
    }

    [Fact]
    public void AmdTctlStillWorks()
    {
        var json = Root(Hardware("AMD Ryzen 7 7800X3D", "/amdcpu/0", "images_icon/cpu.png",
            Group("Temperatures", Sensor("Core (Tctl/Tdie)", "61,3 °C"), Sensor("CCD1 (Tdie)", "58,0 °C"))));
        Assert.Equal(61.3, Temp(json));
    }

    [Fact]
    public void NoCpuSensorsMeansNoTemperature() => Assert.Null(Temp(Root()));

    [Fact]
    public void FahrenheitSensorsConvertToCelsiusAndNullChildrenAreTolerated()
    {
        var json = """
        {
          "Text": "Sensor",
          "Children": [
            { "Text": "Motherboard", "Children": null },
            {
              "Text": "Intel Core i7",
              "HardwareId": "/intelcpu/0",
              "Children": [
                { "Text": "Temperatures", "Children": [{ "Text": "CPU Package", "Value": "140,0 °F", "Type": "Temperature", "Children": null }] }
              ]
            }
          ]
        }
        """;
        Assert.Equal(60.0, Temp(json));
    }

    [Fact]
    public void A_slow_read_now_and_then_keeps_the_temperature()
    {
        // #442: LibreHardwareMonitor answered half of the reads in 600-640 ms; each failure blanked the temperature for 30 s
        var failures = 0;
        Assert.Equal((false, TimeSpan.FromSeconds(2)), SystemMonitor.LhmFailed(ref failures));
        failures = 0; // a read that worked in between
        Assert.Equal((false, TimeSpan.FromSeconds(2)), SystemMonitor.LhmFailed(ref failures));
        Assert.Equal((false, TimeSpan.FromSeconds(2)), SystemMonitor.LhmFailed(ref failures));
        // not running at all: cleared after a few tries, then asked again only every 30 s
        Assert.Equal((true, TimeSpan.FromSeconds(30)), SystemMonitor.LhmFailed(ref failures));
        Assert.Equal((true, TimeSpan.FromSeconds(30)), SystemMonitor.LhmFailed(ref failures));
    }
}
