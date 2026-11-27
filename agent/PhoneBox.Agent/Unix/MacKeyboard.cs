using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Types text on macOS by posting Quartz keyboard events that carry the text itself (CGEventKeyboardSetUnicodeString),
/// in-process. What is typed then does not depend on the keyboard layout: System Events' <c>keystroke</c> looks each
/// character up as a key of the current layout and typed every accented vowel as "a" on Brazilian ABNT2 (#706). It also
/// started osascript for every text, and each start costs WindowServer a Mach port (#695). Like the mouse, posting
/// events needs the Accessibility permission.
/// </summary>
internal static class MacKeyboard
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint HidTap = 0;
    internal const ushort ReturnKey = 36, TabKey = 48;
    /// <summary>An event carries at most this many UTF-16 units; macOS ignores the rest of a longer string.</summary>
    internal const int MaxUnitsPerEvent = 20;
    /// <summary>A short pause between events, so a busy app reads them in order instead of dropping some.</summary>
    private const int GapMs = 2;

    [DllImport(CoreGraphics)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    [DllImport(CoreGraphics)] private static extern void CGEventKeyboardSetUnicodeString(nint ev, nuint length, [In] ushort[] chars);
    [DllImport(CoreGraphics)] private static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint ev);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(ApplicationServices)] private static extern byte AXIsProcessTrusted();

    /// <summary>One event: a run of text (<see cref="Text"/>), or a key of its own (Return, Tab) when Text is null.</summary>
    internal readonly record struct Piece(string? Text, ushort Key);

    /// <summary>
    /// The text as events: runs of at most <see cref="MaxUnitsPerEvent"/> UTF-16 units that never split a surrogate pair
    /// or a letter from its combining marks, and line breaks (\r\n, \r, \n) and tabs as their own keys, which apps
    /// read as keys, not as characters. The text is composed first (NFC), so "a" + combining tilde types as "ã".
    /// </summary>
    internal static List<Piece> Pieces(string text)
    {
        var pieces = new List<Piece>();
        var run = new StringBuilder();
        void Flush() { if (run.Length > 0) { pieces.Add(new Piece(run.ToString(), 0)); run.Clear(); } }
        var e = StringInfo.GetTextElementEnumerator(Composed(text));
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (element is "\r\n" or "\r" or "\n") { Flush(); pieces.Add(new Piece(null, ReturnKey)); continue; }
            if (element == "\t") { Flush(); pieces.Add(new Piece(null, TabKey)); continue; }
            if (run.Length + element.Length > MaxUnitsPerEvent) Flush();
            if (element.Length <= MaxUnitsPerEvent) { run.Append(element); continue; }
            // a letter with more combining marks than one event holds: split it between code points, so nothing is
            // lost and no surrogate pair is broken (the marks after the cut may then draw on their own)
            for (var i = 0; i < element.Length;)
            {
                var n = Math.Min(MaxUnitsPerEvent, element.Length - i);
                if (i + n < element.Length && char.IsLowSurrogate(element[i + n])) n--;
                pieces.Add(new Piece(element.Substring(i, n), 0));
                i += n;
            }
        }
        Flush();
        return pieces;
    }

    /// <summary>
    /// NFC where the runtime can compose. The agent runs in invariant globalization mode; if that cannot normalize, the
    /// text goes as it came, and a combining mark still travels in the same event as its letter.
    /// </summary>
    private static string Composed(string text)
    {
        try { return text.Normalize(NormalizationForm.FormC); }
        catch (PlatformNotSupportedException) { return text; }
    }

    /// <summary>Types the text into the app in front; false when <paramref name="ct"/> stopped it part way.</summary>
    public static bool TypeText(string text, CancellationToken ct)
    {
        // macOS drops posted events silently without the permission, so say so instead
        if (AXIsProcessTrusted() == 0) throw new InvalidOperationException(KeySender.AccessibilityHint);
        foreach (var piece in Pieces(text))
        {
            if (ct.IsCancellationRequested) return false;
            var chars = piece.Text?.Select(c => (ushort)c).ToArray();
            Post(piece.Key, true, chars);
            Post(piece.Key, false, chars);
            Thread.Sleep(GapMs);
        }
        return true;
    }

    private static void Post(ushort key, bool down, ushort[]? chars)
    {
        var ev = CGEventCreateKeyboardEvent(0, key, down);
        if (ev == 0) throw new InvalidOperationException("macOS could not create the keyboard event");
        try
        {
            // no modifier from a key held on the Mac's own keyboard: Shift or Command would change what the app does
            CGEventSetFlags(ev, 0);
            if (chars is not null) CGEventKeyboardSetUnicodeString(ev, (nuint)chars.Length, chars);
            CGEventPost(HidTap, ev);
        }
        finally { CFRelease(ev); }
    }
}
