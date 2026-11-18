using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A malformed LibreHardwareMonitor reply (a null or non-object child, a number where a name or a sensor type belongs)
/// skips that entry instead of losing the whole reading.
/// </summary>
public sealed class CpuTempMalformedTests
{
    private static double? Temp(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SystemMonitor.FindCpuTemp(doc.RootElement);
    }

    [Theory]
    [InlineData("""{"Children":[null,42,"x",{"Text":"CPU Package","Value":"64,0 °C","Type":"Temperature","Children":[]}]}""", 64.0)]
    [InlineData("""{"Children":[{"Text":7,"HardwareId":5,"ImageURL":true,"Children":[null]},{"Text":"CPU","HardwareId":"/intelcpu/0","Children":[null,{"Text":"CPU Core #1","Value":"55,0 °C","Type":"Temperature"}]}]}""", 55.0)]
    public void NonObjectNodesAndNonStringNamesAreSkipped(string json, double expected) => Assert.Equal(expected, Temp(json));

    [Fact]
    public void AnExplicitNonTemperatureTypeIsNotReadAsATemperature()
    {
        const string json = """
            {"Children":[
              {"Text":"CPU Package","Value":"95 °C","Type":42},
              {"Text":"CPU Package","Value":"61,0 °C","Type":"Temperature"}]}
            """;
        Assert.Equal(61.0, Temp(json));
        // an older reply without a type still counts
        Assert.Equal(58.0, Temp("""{"Children":[{"Text":"CPU Package","Value":"58,0 °C"}]}"""));
        Assert.Equal(57.0, Temp("""{"Children":[{"Text":"CPU Package","Value":"57,0 °C","Type":null}]}"""));
    }
}
