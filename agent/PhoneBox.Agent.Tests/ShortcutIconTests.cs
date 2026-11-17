using PhoneBox.Agent;
using Xunit;

public sealed class ShortcutIconTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("builtin:lock", false)]
    [InlineData("builtin:mute", false)]
    [InlineData("BUILTIN:MONITOR-OFF", false)]
    [InlineData("https://github.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("ms-screenclip:", false)]
    [InlineData(@"shell:AppsFolder\Microsoft.VisualStudioCode", false)]
    [InlineData("wt.exe", true)]
    [InlineData("explorer.exe", true)]
    [InlineData("code", true)]
    [InlineData(@"C:\Program Files\App\app.exe", true)]
    [InlineData("/usr/bin/firefox", true)]
    [InlineData("/Applications/Safari.app", true)]
    [InlineData("~/bin/tool", true)]
    public void RecognisesWhetherACommandNamesARealFile(string cmd, bool expected) =>
        Assert.Equal(expected, ShortcutIcons.LooksLikeFilePath(cmd));

    [Theory]
    [InlineData("builtin:lock")]
    [InlineData("https://github.com")]
    [InlineData("ms-settings:")]
    [InlineData(@"shell:AppsFolder\Microsoft.VisualStudioCode")]
    public void CommandsWithoutAFileHaveNoIcon(string cmd) =>
        Assert.Null(ShortcutIcons.Icon(new Shortcut { Cmd = cmd }));

    [Fact]
    public void AMissingProgramHasNoIcon() =>
        Assert.Null(ShortcutIcons.Icon(new Shortcut { Cmd = "this-program-does-not-exist-12345" }));

    [Theory]
    [InlineData("builtin:lock", false)]
    [InlineData("https://github.com", false)]
    [InlineData("this-program-does-not-exist-12345", false)]
    public void HasIconAgreesWithIcon(string cmd, bool expected) =>
        Assert.Equal(expected, ShortcutIcons.HasIcon(new Shortcut { Cmd = cmd }));

    [Fact]
    public void HasIconCacheSurvivesAClearWithTheSameAnswer()
    {
        var sc = new Shortcut { Cmd = "this-cache-test-command-does-not-exist" };
        Assert.False(ShortcutIcons.HasIcon(sc));
        ShortcutIcons.ClearHasIconCache();
        Assert.False(ShortcutIcons.HasIcon(sc));
    }

    [Theory]
    [InlineData("builtin:lock")]
    [InlineData("this-program-does-not-exist-12345")]
    public void RevisionAgreesWithHasIcon(string cmd) =>
        Assert.Null(ShortcutIcons.Revision(new Shortcut { Cmd = cmd }));
}
