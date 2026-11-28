using PhoneBox.Agent;
using Xunit;

/// <summary>builtin:monitor-off on Linux: XWayland has no DPMS, so a Wayland session needs its desktop's own command.</summary>
public sealed class LinuxMonitorOffTests
{
    private static IReadOnlyList<string> Commands(params (string Name, string Value)[] env) =>
        [.. Shortcuts.LinuxMonitorOff(name => env.FirstOrDefault(e => e.Name == name).Value)
            .Select(c => c.Tool + " " + string.Join(' ', c.Args))];

    private const string Mutter = "busctl --user set-property org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig "
        + "org.gnome.Mutter.DisplayConfig PowerSaveMode i 1";

    [Fact]
    public void X11KeepsXset() =>
        Assert.Equal(["xset dpms force off"], Commands(("DISPLAY", ":0"), ("XDG_SESSION_TYPE", "x11"), ("XDG_CURRENT_DESKTOP", "KDE")));

    [Fact]
    public void NoSessionInformationKeepsXset() => Assert.Equal(["xset dpms force off"], Commands());

    [Fact]
    public void KdePlasmaOnWaylandUsesKscreenDoctor() =>
        // what the agent sees as a systemd user service on Plasma 6 (its DISPLAY is XWayland's)
        Assert.Equal(["kscreen-doctor --dpms off"], Commands(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":1"),
            ("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", "KDE")));

    [Theory]
    [InlineData("GNOME")]
    [InlineData("ubuntu:GNOME")] // Ubuntu's session
    [InlineData("gnome")]
    public void GnomeOnWaylandUsesMutter(string desktop) =>
        Assert.Equal([Mutter], Commands(("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", desktop)));

    [Fact]
    public void WaylandIsRecognisedByItsSocketAlone() =>
        Assert.Equal(["kscreen-doctor --dpms off"], Commands(("WAYLAND_DISPLAY", "wayland-1"), ("XDG_CURRENT_DESKTOP", "KDE")));

    [Theory]
    [InlineData("sway")]
    [InlineData("Hyprland")]
    [InlineData("")]
    public void AnotherWaylandDesktopGetsNoCommandRatherThanXset(string desktop) =>
        Assert.Empty(Commands(("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", desktop)));
}
