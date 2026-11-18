using PhoneBox.Agent;
using Xunit;

// #73: the default Terminal shortcut starts wt.exe (not on Windows 10) or x-terminal-emulator (Debian family only)
// #238: the default Calculator, Settings and System Monitor shortcuts start GNOME's apps, which other desktops lack
public class TerminalStandInTests
{
    [Theory]
    [InlineData("wt.exe")]
    [InlineData("WT")]
    public void WindowsTerminalFallsBackToPowerShell(string cmd) => Assert.Equal(["powershell.exe", "cmd.exe"], Shortcuts.TerminalStandIns(cmd));

    [Fact]
    public void LinuxTriesTheTerminalVariableFirst()
    {
        var alts = Shortcuts.TerminalStandIns("x-terminal-emulator", " alacritty ");
        Assert.Equal("alacritty", alts[0]);
        Assert.Contains("konsole", alts);
        Assert.Equal("xterm", alts[^1]);
    }

    [Fact]
    public void LinuxWithoutTheVariableStartsWithTheDesktopTerminals() =>
        Assert.Equal("kgx", Shortcuts.TerminalStandIns("x-terminal-emulator", null)[0]);

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("code")]
    [InlineData("C:\\Tools\\wt.exe")] // an explicit path is the user's own choice
    public void OtherCommandsAreLeftAlone(string cmd) => Assert.Empty(Shortcuts.TerminalStandIns(cmd));

    [Theory]
    [InlineData("gnome-calculator", "kcalc")]
    [InlineData("gnome-control-center", "systemsettings")]
    [InlineData("gnome-system-monitor", "plasma-systemmonitor")]
    public void TheGnomeDefaultsFallBackToKdeFirst(string cmd, string first) => Assert.Equal(first, Shortcuts.TerminalStandIns(cmd)[0]);

    [Theory]
    [InlineData("gnome-calculator")]
    [InlineData("gnome-control-center")]
    [InlineData("gnome-system-monitor")]
    public void EveryStandInIsAPlainProgramName(string cmd)
    {
        // looked up by name in PATH and started without a shell or arguments: no path, space or option in any of them
        foreach (var alt in Shortcuts.TerminalStandIns(cmd))
            Assert.Matches("^[a-z0-9][a-z0-9-]*$", alt);
    }

    [Theory]
    [InlineData("/usr/bin/gnome-calculator")] // an explicit path is the user's own choice
    [InlineData("gnome-terminal")]
    [InlineData("calc")]
    public void OtherGnomeNamesAreLeftAlone(string cmd) => Assert.Empty(Shortcuts.TerminalStandIns(cmd));
}
