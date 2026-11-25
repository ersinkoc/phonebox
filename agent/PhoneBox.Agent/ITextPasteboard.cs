namespace PhoneBox.Agent;

/// <summary>The computer's clipboard as the phone bridge sees it: plain text only.</summary>
internal interface ITextPasteboard
{
    /// <summary>
    /// Goes up whenever a new item is put on the clipboard, by any program, including this agent. It does not move when a
    /// program adds another type to the item it already put there.
    /// </summary>
    long ChangeCount { get; }

    /// <summary>
    /// The plain text of the current item. Null when the clipboard is empty, holds no text, or the item is marked
    /// concealed, transient or auto-generated (password managers mark what they copy that way), because the bridge
    /// must not share those; the marks are checked before and again after the text is read. May also be null for a text
    /// longer than <see cref="ClipboardBridge.MaxBytes"/> could hold, which the bridge would not send anyway.
    /// </summary>
    string? ReadShareableText();

    /// <summary>Rechecks the same item's change count and privacy markers immediately before releasing its text.</summary>
    bool CanShare(long changeCount);

    /// <summary>
    /// True when the last <see cref="ReadShareableText"/> found plain text among the item's types but the system gave none
    /// for it: what a read that macOS's paste privacy denied looks like (see MacPasteboard).
    /// </summary>
    bool LastReadRefused { get; }

    /// <summary>True when the system is set to deny this program the clipboard (macOS 15.4 and later: Paste from Other Apps).</summary>
    bool AccessDenied { get; }

    /// <summary>Replaces the clipboard and returns the change count assigned to the write, or null on failure.</summary>
    long? WriteText(string text);
}
