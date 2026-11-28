using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>The microphone and camera are probed only for what reads them: an On air widget or a rule.</summary>
public sealed class PrivacyWantedTests
{
    private static AgentConfig Config(string widgets, string rules = "[]", bool pageEnabled = true) =>
        JsonSerializer.Deserialize<AgentConfig>($$"""
            {"pages":[{"id":"p","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":{{widgets}}}],"rules":{{rules}}}
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Theory]
    [InlineData("""[{"type":"onair"}]""", true, true)]                                     // default: both
    [InlineData("""[{"type":"onair","options":{"source":"both"}}]""", true, true)]
    [InlineData("""[{"type":"onair","options":{"source":"mic"}}]""", true, false)]
    [InlineData("""[{"type":"onair","options":{"source":"camera"}}]""", false, true)]
    [InlineData("""[{"type":"onair","options":{"source":"obs"}}]""", false, false)]        // OBS only
    [InlineData("""[{"type":"onair","options":{"source":"push"}}]""", false, false)]       // a pushed value only
    [InlineData("""[{"type":"stack","options":{"widgets":[{"type":"onair","options":{"source":"camera"}}]}}]""", false, true)]
    [InlineData("""[{"type":"stack","options":{"widgets":[{"type":"onair","enabled":false}]}}]""", false, false)]
    [InlineData("""[{"type":"meeting-status"}]""", true, true)]                            // drawn by the On air code
    [InlineData("""[{"type":"clock"},{"type":"audio-mixer"}]""", false, false)]
    [InlineData("[]", false, false)]
    public void OnAirWidgetsAskForWhatTheyLightUpFor(string widgets, bool mic, bool camera) =>
        Assert.Equal((mic, camera), PrivacyMonitor.Wanted(Config(widgets)));

    [Fact]
    public void AWidgetOnADisabledPageAsksForNothing() =>
        Assert.Equal((false, false), PrivacyMonitor.Wanted(Config("""[{"type":"onair"}]""", pageEnabled: false)));

    [Theory]
    [InlineData("""[{"when":"mic-in-use"}]""", true, false)]
    [InlineData("""[{"when":"camera-in-use"}]""", false, true)]
    [InlineData("""[{"when":"mic-in-use"},{"when":"camera-in-use"}]""", true, true)]
    [InlineData("""[{"when":"camera-in-use","enabled":false}]""", false, false)]
    [InlineData("""[{"when":"media-playing"},{"when":"cpu-above"}]""", false, false)]
    public void RulesAskForTheirTrigger(string rules, bool mic, bool camera) =>
        Assert.Equal((mic, camera), PrivacyMonitor.Wanted(Config("[]", rules)));

    [Fact]
    public void AFolderThisUserMayNotListIsToldWithoutAnException()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(Sh.CanList(Path.GetTempPath()));
        if (Sh.Uid != 0) Assert.False(Sh.CanList("/proc/1/fd")); // init's open files belong to root (unless the tests run as root)
        Assert.False(Sh.CanList("/no/such/folder"));
    }
}
