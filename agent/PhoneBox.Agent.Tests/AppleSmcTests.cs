using System.Runtime.InteropServices;
using PhoneBox.Agent;
using Xunit;

/// <summary>macOS CPU and GPU temperature from the SMC: which keys count, and how the readings are averaged.</summary>
public sealed class AppleSmcTests
{
    [Theory]
    [InlineData("Tp00", true)]
    [InlineData("Tp0W", true)]
    [InlineData("Te05", true)]
    [InlineData("Ts0S", true)]
    [InlineData("Ts1P", false)] // "Ts*P" doesn't follow the CPU load
    [InlineData("Tg0C", false)]
    [InlineData("TPD0", false)]
    [InlineData("Tp0", false)]
    [InlineData("tp00", false)]
    public void CpuKeys(string key, bool expected) => Assert.Equal(expected, AppleSmc.IsCpuKey(key));

    [Theory]
    [InlineData("Tg0C", true)]
    [InlineData("Tg1z", true)]
    [InlineData("Tp00", false)]
    [InlineData("TG0C", false)]
    [InlineData("Tg0", false)]
    public void GpuKeys(string key, bool expected) => Assert.Equal(expected, AppleSmc.IsGpuKey(key));

    [Fact]
    public void TheAverageLeavesOutSensorsThatAreOffOrNonsense()
    {
        Assert.Equal(65.0, AppleSmc.Average([60.0, 70.0, 0.0, 3.5, null, 400.0, double.NaN]));
        Assert.Equal(150.0, AppleSmc.Average([150.0]));
        Assert.Equal(61.7, AppleSmc.Average([61.66, 61.74]));
    }

    [Fact]
    public void WithoutAValidReadingThereIsNoTemperature()
    {
        Assert.Null(AppleSmc.Average([]));
        Assert.Null(AppleSmc.Average([null, 0.0, 10.0, double.PositiveInfinity]));
    }

    [Theory]
    [InlineData("#KEY")]
    [InlineData("Tp0W")]
    [InlineData("flt ")]
    public void KeyNamesRoundTrip(string key) => Assert.Equal(key, AppleSmc.KeyName(AppleSmc.FourCc(key)));

    [Fact]
    public void FourCcIsBigEndian() => Assert.Equal(0x666C7420u, AppleSmc.FourCc("flt "));

    // Only a Mac with Apple silicon has these sensors; anywhere else Open() must say so instead of throwing.
    [Fact]
    public void OnAnAppleSiliconMacTheReadingsArePlausible()
    {
        using var smc = AppleSmc.Open();
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Null(smc);
            return;
        }
        if (smc is null) return; // macOS 13 and older expose no temperature keys
        var (cpu, gpu) = smc.Read();
        Assert.True(cpu is null or (> 10 and <= 150), $"cpu {cpu}");
        Assert.True(gpu is null or (> 10 and <= 150), $"gpu {gpu}");
        Assert.True(cpu is not null || gpu is not null);
    }
}
