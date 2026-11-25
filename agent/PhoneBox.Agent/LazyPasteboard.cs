namespace PhoneBox.Agent;

/// <summary>
/// Creates the real pasteboard on first use. If that fails (no window server, AppKit missing) the bridge sees an empty
/// pasteboard that refuses writes, and one warning is logged, instead of the agent failing to start. While the bridge is
/// off nothing asks for the pasteboard, so nothing is created.
/// </summary>
internal sealed class LazyPasteboard(Func<ITextPasteboard> create, ILogger<LazyPasteboard> log) : ITextPasteboard
{
    private readonly Lock _gate = new();
    private ITextPasteboard? _inner;
    private bool _failed;

    private ITextPasteboard? Inner()
    {
        lock (_gate)
        {
            if (_inner is null && !_failed)
            {
                try { _inner = create(); }
                catch (Exception ex)
                {
                    _failed = true;
                    log.LogWarning("the clipboard bridge cannot reach the pasteboard: {Error}", ex.GetType().Name);
                }
            }
            return _inner;
        }
    }

    public long ChangeCount => Inner()?.ChangeCount ?? 0;
    public string? ReadShareableText() => Inner()?.ReadShareableText();
    public bool CanShare(long changeCount) => Inner()?.CanShare(changeCount) ?? false;
    public bool LastReadRefused => Inner()?.LastReadRefused ?? false;
    public bool AccessDenied => Inner()?.AccessDenied ?? false;
    public long? WriteText(string text) => Inner()?.WriteText(text);
}
