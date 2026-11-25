using PhoneBox.Agent;

/// <summary>A pasteboard for tests of the clipboard bridge: nothing here touches a real clipboard.</summary>
internal sealed class FakePasteboard : ITextPasteboard
{
    public long Count;
    public string? Text;
    public int Writes;
    public bool Fail;
    public bool ThrowOnCount;
    /// <summary>The current item carries a password manager's marker (it reads as null, like MacPasteboard's).</summary>
    public bool Marked;
    /// <summary>macOS denies the text of the current item (its types say text, the read gives none).</summary>
    public bool Deny;
    public bool AccessDenied { get; set; }
    public bool LastReadRefused { get; private set; }
    public long ChangeCount => ThrowOnCount ? throw new InvalidOperationException("boom") : Count;
    /// <summary>Runs once the text has been read, to put a copy between the two reads of one poll.</summary>
    public Action? AfterRead;
    public string? ReadShareableText()
    {
        LastReadRefused = false;
        if (Marked) return null;
        if (Deny) { LastReadRefused = Text is not null; return null; }
        var text = Text;
        AfterRead?.Invoke();
        return Marked ? null : text; // the marks are checked again after the text, as MacPasteboard does
    }
    public Action? BeforeShare;
    public bool CanShare(long changeCount)
    {
        BeforeShare?.Invoke();
        return changeCount == Count && !Marked;
    }

    public long? WriteText(string text)
    {
        if (Fail) return null;
        Writes++;
        Text = text;
        Marked = false;
        return ++Count;
    }
    /// <summary>Somebody copies something on the Mac (null: an item the bridge must not read).</summary>
    public void Copy(string? text) { Text = text; Marked = false; Count++; }

    /// <summary>A password manager's setString("", concealed type) after its text: the marker arrives, the count stays.</summary>
    public void Mark() => Marked = true;
}
