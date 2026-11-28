using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>How macOS text typing splits a text into keyboard events (#706); posting them needs a Mac and is not tested.</summary>
public class MacKeyboardTests
{
    private static string Joined(IEnumerable<MacKeyboard.Piece> pieces) =>
        string.Concat(pieces.Select(p => p.Text ?? (p.Key == MacKeyboard.ReturnKey ? "⏎" : p.Key == MacKeyboard.TabKey ? "⇥" : "?")));

    [Fact]
    public void AccentedTextGoesAsTheCharactersThemselves()
    {
        var pieces = MacKeyboard.Pieces("não, você está lá");
        Assert.Equal("não, você está lá", Joined(pieces));
        Assert.All(pieces, p => Assert.NotNull(p.Text));
    }

    [Fact]
    public void ADecomposedLetterStaysWithItsMark()
    {
        // "a" + combining tilde: one character on screen, so it must never be split between two events
        var text = new string('x', MacKeyboard.MaxUnitsPerEvent - 1) + "ão";
        var pieces = MacKeyboard.Pieces(text);
        Assert.DoesNotContain(pieces, p => p.Text is { Length: > 0 } t && t[0] == '̃');
        Assert.Equal(text.Normalize(System.Text.NormalizationForm.FormC), Joined(pieces).Normalize(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public void LongTextIsSplitWithoutBreakingASurrogatePair()
    {
        var text = new string('a', MacKeyboard.MaxUnitsPerEvent - 1) + "😀" + new string('b', 30);
        var pieces = MacKeyboard.Pieces(text);
        Assert.Equal(text, Joined(pieces));
        Assert.All(pieces, p => Assert.InRange(p.Text!.Length, 1, MacKeyboard.MaxUnitsPerEvent));
        Assert.All(pieces, p => Assert.False(char.IsLowSurrogate(p.Text![0])));
    }

    [Fact]
    public void ALetterWithMoreMarksThanOneEventHoldsIsSplitWithoutLosingAny()
    {
        // one text element longer than an event: q + 20 combining tildes, and one with astral combining marks
        foreach (var text in new[] { "q" + new string('̃', 20), "x" + string.Concat(Enumerable.Repeat("\U0001D167", 15)) + "y" })
        {
            var pieces = MacKeyboard.Pieces(text);
            Assert.Equal(text, Joined(pieces));
            Assert.All(pieces, p => Assert.InRange(p.Text!.Length, 1, MacKeyboard.MaxUnitsPerEvent));
            Assert.All(pieces, p => Assert.False(char.IsLowSurrogate(p.Text![0])));
        }
    }

    [Fact]
    public void LineBreaksAndTabsArePressedAsKeys()
    {
        var pieces = MacKeyboard.Pieces("a\r\nb\nc\rd\te");
        Assert.Equal("a⏎b⏎c⏎d⇥e", Joined(pieces));
        Assert.Equal(3, pieces.Count(p => p.Key == MacKeyboard.ReturnKey && p.Text is null));
        Assert.Single(pieces, p => p.Key == MacKeyboard.TabKey && p.Text is null);
    }

    [Fact]
    public void NothingToTypeSendsNoEvent() => Assert.Empty(MacKeyboard.Pieces(""));
}
