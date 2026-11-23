using System.Diagnostics;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>The CPU package power from LibreHardwareMonitor's data.json, for the CPU tile and the CPU power metric (#442).</summary>
public sealed class CpuPowerTests
{
    private static double? Power(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SystemMonitor.FindCpuPower(doc.RootElement);
    }

    private static string Sensor(string text, string value, string type) =>
        $$"""{"Text":"{{text}}","Value":"{{value}}","Type":"{{type}}","Children":[]}""";

    private static string Hardware(string text, string hardwareId, string image, params string[] groups) =>
        $$"""{"Text":"{{text}}","HardwareId":"{{hardwareId}}","ImageURL":"{{image}}","Children":[{{string.Join(",", groups)}}]}""";

    private static string Group(string text, params string[] sensors) =>
        $$"""{"Text":"{{text}}","Children":[{{string.Join(",", sensors)}}]}""";

    private static string Root(params string[] hardware) =>
        $$"""{"Text":"Sensor","Children":[{"Text":"PC","Children":[{{string.Join(",", hardware)}}]}]}""";

    [Fact]
    public void IntelPackagePowerIsTakenNotThePackageTemperature()
    {
        var json = Root(Hardware("Intel Core i7-12700H", "/intelcpu/0", "images_icon/cpu.png",
            Group("Temperatures", Sensor("CPU Package", "92,0 °C", "Temperature")),
            Group("Powers", Sensor("CPU Package", "42,0 W", "Power"), Sensor("CPU Cores", "30,5 W", "Power"))));
        Assert.Equal(42.0, Power(json));
    }

    [Fact]
    public void AmdReportsItAsPackage()
    {
        var json = Root(Hardware("AMD Ryzen 5 5500U", "/amdcpu/0", "images_icon/cpu.png",
            Group("Powers", Sensor("Core #1 (SMU)", "1,8 W", "Power"), Sensor("Package", "15.2 W", "Power"))));
        Assert.Equal(15.2, Power(json));
    }

    [Fact]
    public void AGpuPackageNeverCounts()
    {
        // an integrated Radeon or a graphics card reports its own package power, which isn't the CPU's
        var json = Root(
            Hardware("AMD Radeon(TM) Graphics", "/gpu-amd/0", "images_icon/ati.png",
                Group("Powers", Sensor("GPU Package", "9,0 W", "Power"), Sensor("Package", "9,0 W", "Power"))),
            Hardware("AMD Ryzen 5 5500U", "/amdcpu/0", "images_icon/cpu.png",
                Group("Temperatures", Sensor("Core (Tctl/Tdie)", "61,3 °C", "Temperature"))));
        Assert.Null(Power(json));
    }

    [Fact]
    public void NoReadablePowerMeansNoValue()
    {
        Assert.Null(Power(Root()));
        Assert.Null(Power(Root(Hardware("Intel Core i5", "/intelcpu/0", "images_icon/cpu.png",
            Group("Powers", Sensor("CPU Package", "-", "Power"))))));
        Assert.Null(Power("""{"Text":"Sensor","Children":[null,{"Text":"Intel Core i5","HardwareId":"/intelcpu/0","Children":null}]}"""));
    }
}

/// <summary>Linux: the CPU package power from RAPL's energy counter, against fake powercap files (#442).</summary>
public sealed class RaplPowerTests : IDisposable
{
    private readonly DirectoryInfo _zone = Directory.CreateTempSubdirectory("phonebox-rapl-");

    public void Dispose() => _zone.Delete(recursive: true);

    private void Energy(long microjoules) => File.WriteAllText(Path.Combine(_zone.FullName, "energy_uj"), microjoules + "\n");

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void WattsAreTheEnergyUsedBetweenTwoReads()
    {
        var rapl = new RaplPower(_zone.FullName);
        Energy(5_000_000);
        Assert.Null(rapl.Read(At(10))); // nothing to compare with yet
        Energy(15_000_000);
        Assert.Equal(5.0, rapl.Read(At(12))); // 10 J in 2 s
    }

    [Fact]
    public void ACounterThatWrapsAroundStillGivesTheRightWatts()
    {
        const long range = 262_143_328_850;
        File.WriteAllText(Path.Combine(_zone.FullName, "max_energy_range_uj"), range + "\n");
        var rapl = new RaplPower(_zone.FullName);
        Energy(range - 1_000_000);
        rapl.Read(At(1));
        Energy(3_000_000);
        Assert.Equal(4.0, rapl.Read(At(2)));
    }

    [Fact]
    public void ACounterThatWasResetIsNotTakenForAWrap()
    {
        const long range = 262_143_328_850;
        File.WriteAllText(Path.Combine(_zone.FullName, "max_energy_range_uj"), range + "\n");
        var rapl = new RaplPower(_zone.FullName);
        Energy(90_000_000_000);
        rapl.Read(At(1));
        Energy(2_000_000); // back near zero, as after a resume: not a wrap of 172 kJ in 1 s
        Assert.Null(rapl.Read(At(2)));
        Energy(7_000_000);
        Assert.Equal(5.0, rapl.Read(At(3))); // and the next read is normal again
    }

    [Fact]
    public void AnUnreadableCounterGivesNothingAndStartsOver()
    {
        // root-only energy_uj (most distributions since 2020) reads like a missing file
        var rapl = new RaplPower(_zone.FullName);
        Assert.Null(rapl.Read(At(1)));
        Energy(1_000_000);
        Assert.Null(rapl.Read(At(2)));
        Energy(2_000_000);
        Assert.Equal(1.0, rapl.Read(At(3)));
    }
}
