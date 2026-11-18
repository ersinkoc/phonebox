namespace PhoneBox.Agent;

/// <summary>
/// Dictation widget: the phone turns speech into text with its own recognizer and sends the text here, to be typed into
/// the app in front. Off unless the widget's own "Type the text on the computer" option is on, on an enabled page of
/// this phone. Control characters are removed here (a line break would be Enter, which sends a chat message or runs a
/// command), so Enter is pressed only with the widget's "Press Enter after the text" option. The text is never logged.
/// </summary>
public sealed class Dictation(ConfigStore config, ILogger<Dictation> log)
{
    public const string WidgetType = "dictate";
    /// <summary>
    /// The longest dictation. Its time budget: typing gets at most 3 s + 20 ms a character (23 s, KeySender) plus up to
    /// 2 s waiting for the previous text, inside the phone's 30 s request deadline.
    /// </summary>
    public const int MaxChars = 1000;

    internal Func<string, CancellationToken, bool> TypeText { get; set; } = KeySender.TryTypeText;
    internal Func<List<string>, bool> SendCombo { get; set; } = KeySender.SendCombo;
    internal Func<FocusedApp?> Focused { get; set; } = AppConsole.FocusedNow;
    internal bool CheckElevation { get; set; } = OperatingSystem.IsWindows();
    internal bool AgentElevated { get; set; } = Environment.IsPrivilegedProcess;

    private readonly SemaphoreSlim _typing = new(1, 1);

    /// <summary>One line of plain text: control characters become spaces, runs of spaces one, at most <see cref="MaxChars"/>.</summary>
    public static string Clean(string? text)
    {
        var chars = (text ?? "").Select(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029' ? ' ' : ch).ToArray();
        var line = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxChars ? line[..MaxChars] : line;
    }

    public async Task<(int Status, string? Error)> TypeAsync(string? deviceId, string? widget, string? text, CancellationToken ct)
    {
        var w = PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType);
        if (w?.Bool("control") != true)
            return (403, "typing is off: turn on \"Type the text on the computer\" in the dictation widget's settings");
        if ((text ?? "").Length > MaxChars * 2) return (413, "the text is too long"); // the phone keeps to MaxChars
        var line = Clean(text);
        if (line.Length == 0) return (400, "nothing to type");
        // Windows drops input to an app running as administrator without a word (UIPI): say so instead
        if (CheckElevation && Focused() is { Elevated: true } && !AgentElevated)
            return (409, "the app in front runs as administrator, so Windows doesn't let the agent type into it");

        if (!await _typing.WaitAsync(TimeSpan.FromSeconds(2), ct)) return (429, "still typing the previous text");
        try
        {
            // the switch can be turned off while this waited for the previous text: decide with the settings of now
            w = PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType);
            if (w?.Bool("control") != true) return (403, "typing was turned off");
            var enter = w.Bool("enter") == true;
            ct.ThrowIfCancellationRequested();
            // a trailing space keeps the next dictation from running into this one
            if (!TypeText(enter ? line : line + " ", ct)) return (500, "the computer refused the typing");
            // a cancelled request never presses Enter: that would send or run what the caller gave up on
            ct.ThrowIfCancellationRequested();
            if (enter && !SendCombo(["enter"])) return (500, "the computer refused the Enter key");
            return (200, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("dictation: typing failed ({Type})", ex.GetType().Name); // never the text
            return (500, "the text could not be typed");
        }
        finally { _typing.Release(); }
    }
}
