using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

public enum ClipboardResult { Stored, Off, TooLarge, Failed }

/// <summary>
/// Shares the clipboard's plain text with the paired phones, both ways, while Settings › Phone bridge allows it.
/// Text is held in memory only and never logged: the log gets counts and error types, nothing a person copied.
/// </summary>
public sealed class ClipboardBridge : IHostedService, IDisposable
{
    /// <summary>The most text the bridge moves, in UTF-8 bytes (not characters).</summary>
    public const int MaxBytes = 64 * 1024;
    private const int PollMs = 400;

    private readonly ITextPasteboard _pasteboard;
    private readonly ConfigStore _config;
    private readonly BridgeStream _stream;
    private readonly ILogger<ClipboardBridge> _log;
    private readonly Lock _gate = new();
    private System.Threading.Timer? _timer;
    private bool _primed;
    private bool _settling; // a change was seen; it is read on the next pass if the counter has not moved again
    private bool _warnedRefused;
    private long _lastCount;
    private (long Count, byte[] Hash)? _lastReceived; // the exact change written for a phone
    private volatile bool _stopped;

    internal ClipboardBridge(ITextPasteboard pasteboard, ConfigStore config, BridgeStream stream, ILogger<ClipboardBridge> log)
    {
        _pasteboard = pasteboard;
        _config = config;
        _stream = stream;
        _log = log;
    }

    /// <summary>Text from a phone: put it on this computer's clipboard and hand it to the other phones.</summary>
    public ClipboardResult ReceiveFromPhone(string text, string? deviceId)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return ClipboardResult.TooLarge;
        lock (_gate)
        {
            // inside the lock, like the poll's look at it: a phone's write that races the switch being turned off either lands
            // before the poll that sees it off (which then clears the kept events) or is refused, never after it
            if (!_config.Get().Bridge.Clipboard) return ClipboardResult.Off;
            if (_pasteboard.WriteText(text) is not { } count) return ClipboardResult.Failed;
            _lastReceived = (count, Hash(text));
            // inside the lock: two phones writing at once publish in the order their text reached the pasteboard, so a phone that applies
            // events in sequence ends with what the Mac holds
            _stream.Publish("clipboard", text, deviceId);
        }
        return ClipboardResult.Stored;
    }

    /// <summary>
    /// Puts text on this computer's clipboard for the owner (a login code from a message) without handing it to the phones:
    /// it is remembered like text received from a phone, so the next poll does not publish it. Needs no clipboard switch.
    /// </summary>
    public bool WriteLocalOnly(string text)
    {
        lock (_gate)
        {
            if (_pasteboard.WriteText(text) is not { } count) return false;
            _lastReceived = (count, Hash(text));
            return true;
        }
    }

    /// <summary>
    /// One look at the Mac's clipboard (the timer calls this every 400 ms). A change is read one poll after it is seen, once the
    /// change counter has stayed put for that poll: the counter moves on clearContents, not on each setString:forType:, so a
    /// password manager that writes the password first and its concealed marker a moment later would otherwise be read in
    /// between. The pasteboard then checks the types again after reading the text (see MacPasteboard).
    /// </summary>
    internal void PollOnce()
    {
        lock (_gate)
        {
            if (!_config.Get().Bridge.Clipboard)
            {
                _primed = false; // when it is turned on again, what is on the clipboard then must not be sent
                _settling = false;
                _lastReceived = null;
                _warnedRefused = false; // a warning again after the next switch-on, if macOS still refuses
                _refused = false;
                _stream.Clear(); // nothing copied stays in memory once sharing is off
                return;
            }
            _stream.PruneExpired();
            var count = _pasteboard.ChangeCount;
            if (!_primed)
            {
                // what is on the clipboard now is not sent, and no echo is expected for it: a later deliberate copy of the same words must go out
                _primed = true; _lastCount = count; _lastReceived = null; _settling = false;
                if (_pasteboard.AccessDenied) WarnRefused("macOS is set to deny PhoneBox the clipboard (Privacy & Security, Paste from Other Apps)");
                return;
            }
            if (count != _lastCount)
            {
                _lastCount = count;
                _settling = true; // read on the next pass, if nothing else has been copied by then
                return;
            }
            if (!_settling) return;
            // Leave this change pending if the pasteboard fails temporarily. A later poll can retry it.
            var text = _pasteboard.ReadShareableText();
            var refused = _pasteboard.LastReadRefused;
            // a copy during the read (the types and the text are separate calls) makes it a mixture, so nothing is shared from it;
            // the next pass sees the new count and reads that copy once it has settled
            if (_pasteboard.ChangeCount != count) return;
            if (text is not null && Encoding.UTF8.GetByteCount(text) <= MaxBytes &&
                !(_lastReceived is { } echo && echo.Count == count && Hash(text).AsSpan().SequenceEqual(echo.Hash)) &&
                _pasteboard.CanShare(count))
                _stream.Publish("clipboard", text, null);
            _settling = false;
            _lastReceived = null;
            if (text is not null) _refused = false;
            else if (refused) WarnRefused("the clipboard holds text but macOS gave PhoneBox none (Privacy & Security, Paste from Other Apps may deny it)");
        }
    }

    /// <summary>Once per switch-on: sharing nothing must not be silent. The log line carries no clipboard text.</summary>
    private void WarnRefused(string why)
    {
        _refused = true;
        if (_warnedRefused) return;
        _warnedRefused = true;
        _log.LogWarning("clipboard bridge: {Why}; nothing copied on this Mac reaches the phone", why);
    }

    /// <summary>
    /// True while macOS refuses the agent the clipboard's text: from a refused read (or a setting that denies it) until a read
    /// succeeds or the switch is turned off. The settings card shows it, so sharing nothing is never silent.
    /// </summary>
    public bool Refused => _refused;
    private volatile bool _refused;

    internal void Tick()
    {
        try { PollOnce(); }
        catch (Exception ex) { _log.LogWarning("clipboard poll failed: {Error}", ex.GetType().Name); }
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new System.Threading.Timer(_ =>
        {
            Tick();
            if (!_stopped) try { _timer?.Change(PollMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }, null, PollMs, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopped = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopped = true;
        _timer?.Dispose();
    }
}
