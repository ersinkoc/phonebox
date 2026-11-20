using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The builtin:wake shortcut: refused at save time when its MAC is wrong, and an error (not a throw) when run.</summary>
public sealed class WakeShortcutTests
{
    private static string? Save(string shortcutJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-wake-{Guid.NewGuid():N}.json");
        try { return new ConfigStore(path).Save("{ \"shortcuts\": [ " + shortcutJson + " ] }"); }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void A_wake_key_with_a_bad_mac_is_refused_and_names_the_key()
    {
        var error = Save("""{ "label": "NAS", "cmd": "builtin:wake", "args": "not-a-mac" }""");
        Assert.NotNull(error);
        Assert.Contains("NAS", error);
        Assert.Contains("MAC", error);
    }

    [Fact]
    public void A_wake_key_without_a_mac_is_refused()
        => Assert.Contains("MAC", Save("""{ "label": "NAS", "cmd": "builtin:wake" }"""));

    [Fact]
    public void A_wake_key_with_a_valid_mac_and_an_optional_broadcast_address_is_saved()
    {
        Assert.Null(Save("""{ "label": "NAS", "cmd": "builtin:wake", "args": "AA:BB:CC:DD:EE:01" }"""));
        Assert.Null(Save("""{ "label": "NAS", "cmd": "builtin:wake", "args": "aa-bb-cc-dd-ee-01 192.168.1.255" }"""));
    }

    // A toggle's off action runs through the same path when the toggle is on, so a bad MAC there must not save either.
    [Fact]
    public void A_toggles_off_action_that_wakes_with_a_bad_mac_is_refused_and_says_it_is_the_off_action()
    {
        var error = Save("""{ "label": "NAS power", "cmd": "builtin:mute", "toggle": true, "offCmd": "builtin:wake", "offArgs": "not-a-mac" }""");
        Assert.NotNull(error);
        Assert.Contains("NAS power", error);
        Assert.Contains("off action", error);
        Assert.Contains("MAC", error);
    }

    [Fact]
    public void A_toggles_off_action_with_a_valid_mac_or_another_command_is_saved()
    {
        Assert.Null(Save("""{ "label": "NAS power", "cmd": "builtin:mute", "toggle": true, "offCmd": "builtin:wake", "offArgs": "AA:BB:CC:DD:EE:01" }"""));
        Assert.Null(Save("""{ "label": "Lock", "cmd": "builtin:mute", "toggle": true, "offCmd": "builtin:lock" }"""));
    }

    [Fact]
    public void Running_a_wake_key_with_a_bad_mac_returns_the_error_instead_of_throwing()
    {
        var sc = new Shortcut { Id = "nas", Label = "NAS", Cmd = "builtin:wake", Args = "not-a-mac" };
        var error = Shortcuts.Execute(sc, NullLogger.Instance);
        Assert.NotNull(error);
        Assert.Contains("MAC", error);
    }
}
