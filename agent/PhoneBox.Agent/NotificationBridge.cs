using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Shows the phone's notifications on this computer while Settings › Phone bridge allows it. Slow macOS notifications never
/// block a request: <see cref="Receive"/> only queues, and one worker posts. Notification text is held in memory only and
/// never logged (the log gets counts and error types), because it can be a login code or a private message.
/// </summary>
public sealed class NotificationBridge : IHostedService, IDisposable
{
    private const int MaxTitle = 200, MaxText = 2000, MaxApp = 100;
    private const int MaxQueued = 50;
    private const int BurstLimit = 10, BurstWindowMs = 10_000;
    private const int MaxRemembered = 500;
    private const long PhoneFreshMs = 10 * 60_000;

    private sealed record Post(string Title, string Subtitle, string Body, byte[]? Icon = null, string? Id = null);

    /// <summary>Per app: when it was last let through (inside the window), and what was held back since <see cref="HeldSince"/>.</summary>
    private sealed class Window
    {
        public readonly List<long> Times = [];
        public int Held;
        public long HeldSince;
    }

    private readonly IMacNotifier _notifier;
    private readonly ConfigStore _config;
    private readonly ClipboardBridge? _clipboard;
    private readonly Func<long> _clock;
    private readonly ILogger<NotificationBridge> _log;
    private readonly Func<IReadOnlyCollection<PhoneSeen>> _phones;
    private readonly Lock _gate = new();
    private readonly Queue<Post> _queue = new();
    private readonly Dictionary<string, string> _seen = [];       // device|key -> hash of what was shown
    private readonly Queue<string> _seenOrder = new();
    private readonly Dictionary<string, Window> _windows = [];    // per app: the burst limiter
    private readonly Dictionary<string, byte[]> _icons = [];      // package -> the app's icon, in memory only
    private readonly Queue<string> _iconOrder = new();
    private const int MaxIcons = 100;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _drain = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _worker;

    internal NotificationBridge(IMacNotifier notifier, ConfigStore config, ClipboardBridge? clipboard, Func<long> clock,
        ILogger<NotificationBridge> log, Func<IReadOnlyCollection<PhoneSeen>>? phones = null)
    {
        _notifier = notifier;
        _config = config;
        _clipboard = clipboard;
        _clock = clock;
        _log = log;
        _phones = phones ?? PhoneRegistry.All;
    }

    /// <summary>True while Settings › Phone bridge shows phone notifications; the endpoints refuse before reading a body when it is off.</summary>
    public bool Enabled => _config.Get().Bridge.Notifications;

    public NotifyResult Receive(PhoneNotification n, string? deviceId)
    {
        var bridge = _config.Get().Bridge;
        if (!bridge.Notifications) return NotifyResult.Off;
        var isCall = n.Category is "call" or "missed_call";
        var app = Cut(string.IsNullOrWhiteSpace(n.App) ? n.Package ?? "" : n.App, MaxApp);
        var title = Cut(n.Title ?? "", MaxTitle);
        var text = Cut(n.Text ?? "", MaxText);
        var now = _clock();
        var icon = RememberIcon(n);

        lock (_gate)
        {
            var seenKey = (deviceId ?? "") + "|" + n.Key;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + text + "\n" + n.Category)));
            if (_seen.TryGetValue(seenKey, out var old) && old == hash) return NotifyResult.Duplicate;
            if (old is null) { _seenOrder.Enqueue(seenKey); while (_seenOrder.Count > MaxRemembered) _seen.Remove(_seenOrder.Dequeue()); }
            _seen[seenKey] = hash;
        }

        // a login code is copied at once, not after the queue: it is what the owner is waiting for
        string? copied = null;
        if (!isCall && bridge.CodesActive && _clipboard is not null)
        {
            // the title is searched only when the text had no candidate at all: two codes in the text are ambiguous, not a reason to try the title.
            // The whole text is searched, not the part that is shown: a cut can take away the end of a code or a second code
            // that makes the first one doubtful (the request limit, 64 KiB, bounds it).
            var fromText = OneTimeCode.Search(n.Text ?? "");
            var code = fromText.Code ?? (fromText.Ambiguous ? null : OneTimeCode.Find(n.Title ?? ""));
            if (code is not null && _clipboard.WriteLocalOnly(code)) copied = code;
        }

        lock (_gate)
        {
            var window = _windows.TryGetValue(app, out var existing) ? existing : _windows[app] = new Window();
            window.Times.RemoveAll(t => now - t >= BurstWindowMs);
            if (window.Times.Count >= BurstLimit)
            {
                if (window.Held++ == 0) window.HeldSince = now; // one "N more from App" goes out when this window is over
            }
            else
            {
                window.Times.Add(now);
                _queue.Enqueue(Format(app, title, text, n.Category, deviceId, copied is not null, now, icon, isCall ? ReplaceId(deviceId, n.Key) : null));
                while (_queue.Count > MaxQueued) _queue.Dequeue();
            }
        }
        _wake.Release();
        return NotifyResult.Queued;
    }

    private Post Format(string app, string title, string text, string category, string? deviceId, bool codeCopied, long now, byte[]? icon, string? id)
    {
        var parts = new List<string>();
        if (category == "call") parts.Add("Incoming call");
        else if (category == "missed_call") parts.Add(app); // the dialer's own title already says it was missed: not said twice
        else if (title.Length > 0) parts.Add(app);
        // which phone, only when more than one has been around lately
        var recent = _phones().Where(p => now - p.LastSeen <= PhoneFreshMs).ToList();
        if (recent.Count > 1 && recent.FirstOrDefault(p => p.Id == deviceId)?.Model is { Length: > 0 } model) parts.Add(model);
        if (codeCopied) parts.Add("Code copied");
        return new Post(title.Length > 0 ? title : app, string.Join(" · ", parts), text, icon, id);
    }

    /// <summary>
    /// Posts everything queued, then a "N more from App" for each app whose burst window is over. It looks at the switch
    /// before every post: what was queued when the owner turned it off is dropped, never shown.
    /// </summary>
    internal async Task DrainAsync()
    {
        await _drain.WaitAsync();
        try
        {
            while (true)
            {
                if (!Enabled) { DropPending(); return; }
                Post? next;
                lock (_gate) next = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (next is null) break;
                await Show(next);
            }
            List<KeyValuePair<string, int>> due = [];
            var now = _clock();
            lock (_gate)
            {
                foreach (var (app, window) in _windows)
                    if (window.Held > 0 && now - window.HeldSince >= BurstWindowMs) { due.Add(new(app, window.Held)); window.Held = 0; }
                // an app that has been quiet for a window needs no entry
                foreach (var app in _windows.Where(w => w.Value.Held == 0 && w.Value.Times.All(t => now - t >= BurstWindowMs)).Select(w => w.Key).ToList())
                    _windows.Remove(app);
            }
            foreach (var (app, count) in due)
            {
                if (!Enabled) { DropPending(); return; }
                await Show(new Post($"{count} more from {app}", "", ""));
            }
        }
        finally { _drain.Release(); }
    }

    private void DropPending()
    {
        lock (_gate)
        {
            _queue.Clear();
            _windows.Clear();
            _icons.Clear(); // turning the bridge off forgets the apps' icons too
            _iconOrder.Clear();
        }
    }

    /// <summary>How long until the earliest held-back summary is due, or null when none is waiting (the worker sleeps until then).</summary>
    internal TimeSpan? NextSummaryDelay()
    {
        lock (_gate)
        {
            var now = _clock();
            long? soonest = null;
            foreach (var window in _windows.Values)
                if (window.Held > 0) soonest = Math.Min(soonest ?? long.MaxValue, window.HeldSince + BurstWindowMs - now);
            return soonest is { } ms ? TimeSpan.FromMilliseconds(Math.Max(0, ms)) : null;
        }
    }

    private async Task Show(Post post)
    {
        try
        {
            if (!await _notifier.PostAsync(post.Title, post.Subtitle, post.Body, post.Icon, post.Id, _stop?.Token ?? CancellationToken.None))
                _log.LogWarning("a phone notification could not be shown");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning("showing a phone notification failed: {Error}", ex.GetType().Name); }
    }

    /// <summary>The icon this notification came with (and remembered for the app), or the one remembered earlier; none when never seen.</summary>
    private byte[]? RememberIcon(PhoneNotification n)
    {
        if (string.IsNullOrEmpty(n.Package)) return n.IconPng;
        lock (_gate)
        {
            if (n.IconPng is { } fresh)
            {
                if (!_icons.ContainsKey(n.Package)) _iconOrder.Enqueue(n.Package);
                _icons[n.Package] = fresh;
                while (_iconOrder.Count > MaxIcons) _icons.Remove(_iconOrder.Dequeue());
                return fresh;
            }
            return _icons.GetValueOrDefault(n.Package);
        }
    }

    private bool HoldsIcons { get { lock (_gate) return _icons.Count > 0; } }

    /// <summary>How often the worker looks at the switch while it remembers icons (tests make it short).</summary>
    internal TimeSpan IconCheck { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The same call keeps one notification: its key (which carries the time it began) names it, so a later post replaces the first.</summary>
    private static string ReplaceId(string? deviceId, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((deviceId ?? "") + "|" + key)))[..24].ToLowerInvariant();

    private static string Cut(string s, int max)
    {
        // osascript gets its arguments as C strings, so a NUL would silently end the text there
        s = s.Replace('\0', ' ');
        if (s.Length <= max) return s;
        var keep = max - 1;
        if (char.IsHighSurrogate(s[keep - 1])) keep--; // never leave half an emoji
        return s[..keep] + "…";
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (Enabled) (_notifier as IWarmable)?.Warm(); // the PhoneBox icon is ready before the first notification
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _worker = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                // Nothing wakes the worker when the switch goes off (the endpoints refuse without calling in), so while icons are
                // remembered it looks now and then: DrainAsync forgets them once the switch is off, as PRIVACY.md says.
                var wait = NextSummaryDelay() ?? (HoldsIcons ? IconCheck : Timeout.InfiniteTimeSpan);
                try { await _wake.WaitAsync(wait, token); await DrainAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning("the notification worker failed: {Error}", ex.GetType().Name); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stop?.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
    }

    public void Dispose() => _stop?.Dispose();
}
