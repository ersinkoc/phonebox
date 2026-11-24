using PhoneBox.Agent;
using Xunit;

public class KeySenderTests
{
    [Fact]
    public void ParsesCombosAndSequences()
    {
        var combos = KeySender.Parse("Ctrl+Shift+M, cmd+Space,F13, play");
        Assert.Equal([["ctrl", "shift", "m"], ["win", "space"], ["f13"], ["playpause"]], combos);
    }

    [Theory]
    [InlineData("ctrl+a+b")]      // two keys besides modifiers
    [InlineData("ctrl+banana")]   // unknown key
    [InlineData("f25")]
    [InlineData(" , ")]           // nothing
    public void RejectsBadSpecs(string spec) => Assert.Throws<ArgumentException>(() => KeySender.Parse(spec));

    [Fact]
    public void BracketsAreKeys() // Photoshop's brush size
    {
        Assert.Equal([["bracketleft"], ["ctrl", "bracketright"]], KeySender.Parse("[, ctrl+]"));
        Assert.Equal(0xDB, KeySender.WinVk("bracketleft"));
        Assert.Equal(0xDD, KeySender.WinVk("bracketright"));
    }

    [Fact]
    public void ModifiersAloneAreAllowed() => Assert.Equal([["win"]], KeySender.Parse("Win"));

    // #98: the generic VK_SHIFT/VK_CONTROL/VK_MENU left raw-input apps (Discord) guessing which side was pressed
    [Theory]
    [InlineData("shift", 0xA0)]
    [InlineData("ctrl", 0xA2)]
    [InlineData("alt", 0xA4)]
    [InlineData("win", 0x5B)]
    [InlineData("f24", 0x87)]
    [InlineData("m", 0x4D)]
    [InlineData("playpause", 0xB3)]
    public void WindowsKeysAreTheLeftHandOnes(string key, int vk) => Assert.Equal(vk, KeySender.WinVk(key));

    // #270: the keypad is its own set of keys (Discord's Ctrl+Alt+Numpad3 is not Ctrl+Alt+3), under the names people know
    [Theory]
    [InlineData("Ctrl+Alt+Numpad3", "numpad3")]
    [InlineData("ctrl+alt+num3", "numpad3")]
    [InlineData("ctrl+alt+kp3", "numpad3")]
    [InlineData("Ctrl+Alt+KP_3", "numpad3")]
    [InlineData("ctrl+alt+numpad_3", "numpad3")]
    [InlineData("ctrl+alt+NumpadEnter", "numpadenter")]
    [InlineData("ctrl+alt+KP_Enter", "numpadenter")]
    [InlineData("ctrl+alt+numpadplus", "numpadadd")]
    [InlineData("ctrl+alt+KP_Add", "numpadadd")]
    [InlineData("ctrl+alt+NumpadSubtract", "numpadsubtract")]
    [InlineData("ctrl+alt+kpminus", "numpadsubtract")]
    [InlineData("ctrl+alt+numpad*", "numpadmultiply")]
    [InlineData("ctrl+alt+NumpadDivide", "numpaddivide")]
    [InlineData("ctrl+alt+NumpadDecimal", "numpaddecimal")]
    public void NumpadKeysHaveTheirOwnNames(string spec, string key) => Assert.Equal([["ctrl", "alt", key]], KeySender.Parse(spec));

    [Theory]
    [InlineData("numpad10")]
    [InlineData("numpad")]
    [InlineData("numlock")]
    [InlineData("kp_banana")]
    public void UnknownNumpadNamesAreRejected(string spec) => Assert.Throws<ArgumentException>(() => KeySender.Parse(spec));

    [Theory]
    [InlineData("numpad0", 0x60, false)]
    [InlineData("numpad9", 0x69, false)]
    [InlineData("numpadmultiply", 0x6A, false)]
    [InlineData("numpadadd", 0x6B, false)]
    [InlineData("numpadsubtract", 0x6D, false)]
    [InlineData("numpaddecimal", 0x6E, false)]
    [InlineData("numpaddivide", 0x6F, true)]
    [InlineData("numpadenter", 0x0D, true)]  // VK_RETURN, told apart from the main Enter only by the extended flag
    [InlineData("enter", 0x0D, false)]
    [InlineData("3", 0x33, false)]           // the number row stays the number row
    public void WindowsKeypadCodes(string key, int vk, bool extended)
    {
        Assert.Equal(vk, KeySender.WinVk(key));
        Assert.Equal(extended, KeySender.WinExtended(key));
    }

#if !WINDOWS
    // names from ydotool 0.1.x's tables: it reads an unknown name as its first letter, so "leftctrl" would press L
    [Theory]
    [InlineData("ctrl+shift+m", "ctrl+shift+m")]
    [InlineData("win+period", "super+.")]
    [InlineData("enter", "enter")]
    [InlineData("ctrl+]", "ctrl+]")]
    [InlineData("alt+f4", "alt+f4")]
    [InlineData("ctrl+space", "ctrl+ ")]
    [InlineData("printscreen", "sysrq")]
    public void YdotoolUsesItsOwnKeyNames(string spec, string expected) => Assert.Equal(expected, KeySender.YdotoolCombo(KeySender.Parse(spec)[0]));

    [Theory]
    [InlineData("next")]
    [InlineData("ctrl+alt+numpad3")]
    [InlineData("f13")]
    [InlineData("volumeup")]
    public void YdotoolIsSkippedForKeysItCannotName(string spec) => Assert.Null(KeySender.YdotoolCombo(KeySender.Parse(spec)[0]));

    [Theory]
    [InlineData("Hello, world!", true)]
    [InlineData("a\tb\nc ~{}|", true)]
    [InlineData("café", false)]
    [InlineData("🦊", false)]
    public void YdotoolTypesOnlyWhatItCanTypeInFull(string text, bool ok) => Assert.Equal(ok, KeySender.YdotoolCanType(text));
#endif
}
