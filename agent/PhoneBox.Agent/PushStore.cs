using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record PushItem(JsonElement Data, long At, long? ExpiresAt);

/// <param name="Kind">notify | goto | back | refresh | reload | approval | approval-done | push</param>
public sealed record PanelEvent(long Seq, string Kind, JsonElement? Data, long At);

/// <summary>
/// Data pushed into the agent from outside (scripts, CI, Home Assistant, cron jobs…) through the webhook API,
/// plus a short queue of events for the phone (notifications, page switches, forced refreshes).
/// Values survive restarts (data.json next to config.json); events are kept in memory only.
/// </summary>
public sealed partial class PushStore : IDisposable
{
    public const int MaxBytes = 64 * 1024;
    /// <summary>
    /// Arrays and objects inside one value. The serializer stops at 64, and a value travels wrapped: in a change event
    /// inside /api/state's event list, in /api/push's item list. The rest is the room those envelopes need.
    /// </summary>
    public const int MaxDepth = 32;
    private const int MaxItems = 200;
    private const int MaxEvents = 50;
    private const int SaveDelayMs = 2000;
    private const int MaxSaveRetryMs = 30_000;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dataPath;
    private readonly ConcurrentDictionary<string, PushItem> _items = new();
    private readonly Lock _setLock = new();
    private readonly Lock _evLock = new();
    private readonly LinkedList<PanelEvent> _events = new();
    private readonly ILogger<PushStore> _log;
    private readonly System.Threading.Timer _saveTimer;
    private long _seq;
    private int _savePending;
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <param name="dataPath">Where values are kept (tests pass a temporary file); default: data.json next to config.json.</param>
    public PushStore(ILogger<PushStore> log, string? dataPath = null)
    {
        _log = log;
        _dataPath = Path.GetFullPath(dataPath ?? Path.Combine(AppPaths.ConfigDir, "data.json"));
        _saveTimer = new System.Threading.Timer(_ => Persist(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (File.Exists(_dataPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_dataPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    // one record at a time: a damaged one (a null time, a wrong type) must not take the others with it
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (!ValidKey(p.Name) || p.Value.ValueKind != JsonValueKind.Object) continue;
                        PushItem? v;
                        try { v = p.Value.Deserialize<PushItem>(); }
                        catch (JsonException) { continue; }
                        if (v is not null && v.Data.ValueKind != JsonValueKind.Undefined) _items[p.Name] = v with { Data = v.Data.Clone() };
                    }
            }
        }
        catch (Exception ex) { log.LogWarning("could not read {Path}: {Msg}", _dataPath, ex.Message); }
    }

    /// <summary>
    /// Changes whenever a value is set, removed or expires. Change events carry the update; the phone refetches
    /// /api/push when it misses one. Phones read it every second (in /api/state), which is also when expired values are dropped.
    /// </summary>
    public long Version
    {
        get { PurgeExpired(); return Interlocked.Read(ref _version); }
    }

    /// <summary>Last event number, so a freshly started phone does not replay old notifications.</summary>
    public long Seq => Interlocked.Read(ref _seq);

    public static bool ValidKey(string key) => KeyRegex().IsMatch(key);

    public IReadOnlyDictionary<string, PushItem> Snapshot()
    {
        PurgeExpired();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _items.Where(kv => kv.Value.ExpiresAt is null || kv.Value.ExpiresAt > now).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public PushItem? Get(string key) => Snapshot().GetValueOrDefault(key);

    /// <returns>An error message, or null on success.</returns>
    public string? Set(string key, JsonElement data, int? ttlSec)
    {
        if (!ValidKey(key)) return "key must be 1-64 characters: letters, digits, '.', '_' or '-'";
        if (Depth(data) > MaxDepth) return $"value nested too deeply (max {MaxDepth} levels of arrays and objects)";
        PurgeExpired(); // expired values do not count against the limit
        // {"ttl": 300, ...} inside the payload works too
        if (ttlSec is null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ttl", out var t)
            && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var tt)) ttlSec = tt;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = new PushItem(data.Clone(), now, ttlSec is > 0 ? now + ttlSec.Value * 1000L : null);
        // ConcurrentDictionary does not make the capacity check and insertion atomic. The version belongs to the
        // mutation itself: a change event is applied only through this unbroken chain, so concurrent writers cannot
        // make the phone apply the same key in the wrong order.
        lock (_setLock)
        {
            if (!_items.ContainsKey(key) && _items.Count >= MaxItems) return $"too many keys (max {MaxItems}); delete some first";
            // the event is built before anything changes: a value it cannot carry leaves the store as it was
            var change = NextVersion();
            JsonElement ev;
            try { ev = JsonSerializer.SerializeToElement(new { baseVersion = change.BaseVersion, version = change.Version, key, item }, WebJson); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return "value cannot be sent to the phone: " + ex.Message; }
            _items[key] = item;
            Interlocked.Exchange(ref _version, change.Version);
            Enqueue("push", ev);
        }
        // outside _setLock: scheduling can wait for a save in progress, and other writers must not wait behind it
        ScheduleSave();
        Emitted?.Invoke();
        return null;
    }

    public bool Remove(string key)
    {
        (long BaseVersion, long Version) change;
        lock (_setLock)
        {
            if (!_items.TryRemove(key, out _)) return false;
            change = AdvanceVersion();
            EnqueuePushChange(new { baseVersion = change.BaseVersion, version = change.Version, key, item = (PushItem?)null });
        }
        ScheduleSave();
        Emitted?.Invoke();
        return true;
    }

    public void Clear()
    {
        (long BaseVersion, long Version) change;
        lock (_setLock)
        {
            _items.Clear();
            change = AdvanceVersion();
            EnqueuePushChange(new { baseVersion = change.BaseVersion, version = change.Version, clear = true });
        }
        ScheduleSave();
        Emitted?.Invoke();
    }

    /// <summary>Raised after an event is queued, so live streams can deliver it right away.</summary>
    public event Action? Emitted;

    public PanelEvent Emit(string kind, JsonElement? data = null)
    {
        var e = Enqueue(kind, data);
        Emitted?.Invoke();
        return e;
    }

    private PanelEvent Enqueue(string kind, JsonElement? data = null)
    {
        lock (_evLock)
        {
            var e = new PanelEvent(++_seq, kind, data?.Clone(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _events.AddLast(e);
            while (_events.Count > MaxEvents)
            {
                // pushed values can change every second; drop those before notifications and page switches, because
                // the phone can always refill them from /api/push when it notices the broken chain
                var victim = _events.FirstOrDefault(x => x.Kind == "push");
                if (victim is null) _events.RemoveFirst();
                else _events.Remove(victim);
            }
            return e;
        }
    }

    /// <summary>Events after <paramref name="seq"/>, at most a minute old (a phone that was away does not get a flood).</summary>
    public IReadOnlyList<PanelEvent> Since(long seq)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        lock (_evLock) return _events.Where(e => e.Seq > seq && e.At > cutoff).ToList();
    }

    /// <summary>Drops values whose TTL has passed; that is a change too, so phones reload and drop them as well.</summary>
    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var removed = new List<string>();
        lock (_setLock)
        {
            foreach (var kv in _items)
                if (kv.Value.ExpiresAt <= now && _items.TryRemove(kv)) removed.Add(kv.Key); // only this exact value, not a newer one
            if (removed.Count == 0) return;
            var change = AdvanceVersion();
            EnqueuePushChange(new { baseVersion = change.BaseVersion, version = change.Version, remove = removed });
        }
        ScheduleSave();
        Emitted?.Invoke();
    }

    private readonly Lock _persistLock = new();
    private bool _disposed;
    private int _saveRetryMs = SaveDelayMs;

    // Every version change happens under _setLock, so the next version can be worked out before it is taken.
    private (long BaseVersion, long Version) NextVersion()
    {
        var previous = Interlocked.Read(ref _version);
        return (previous, Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), previous + 1));
    }

    private (long BaseVersion, long Version) AdvanceVersion()
    {
        var change = NextVersion();
        Interlocked.Exchange(ref _version, change.Version);
        return change;
    }

    /// <summary>How many arrays and objects are nested in <paramref name="e"/>; a scalar is 0.</summary>
    private static int Depth(JsonElement e)
    {
        var deepest = 0;
        if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) deepest = Math.Max(deepest, Depth(p.Value));
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) deepest = Math.Max(deepest, Depth(x));
        else return 0;
        return deepest + 1;
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 0)
            lock (_persistLock)
                if (!_disposed) _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
    }

    private void EnqueuePushChange(object payload) => Enqueue("push", JsonSerializer.SerializeToElement(payload, WebJson));

    private void Persist()
    {
        lock (_persistLock)
            if (!_disposed) SavePending();
    }

    public void Dispose()
    {
        lock (_persistLock)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Dispose();
            SavePending();
        }
    }

    // Timer writes and the final flush share this lock, so a restart waits for any active write.
    private void SavePending()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            var tmp = _dataPath + ".tmp";
            AppPaths.WriteAllTextPrivate(tmp, JsonSerializer.Serialize(Snapshot()));
            File.Move(tmp, _dataPath, overwrite: true);
            _saveRetryMs = SaveDelayMs;
        }
        catch (Exception ex)
        {
            // re-armed before the warning: this runs on the timer thread, where an exception from a logger (its own
            // disk may be the full one) would end the process and the retry would never be scheduled
            if (!_disposed)
            {
                Interlocked.Exchange(ref _savePending, 1);
                _saveTimer.Change(_saveRetryMs, Timeout.Infinite);
                _saveRetryMs = Math.Min(2 * _saveRetryMs, MaxSaveRetryMs);
            }
            try { _log.LogWarning("could not save pushed data: {Msg}", ex.Message); }
            catch { /* the warning is best effort */ }
        }
    }

    /// <summary>Reads a webhook body: JSON as-is, anything else as a trimmed string; ?value= wins when given.</summary>
    public static async Task<(JsonElement? Data, string? Error)> ReadBody(HttpRequest req)
    {
        if (req.Query.TryGetValue("value", out var qv)) return (Parse(qv.ToString()), null);
        if (req.ContentLength > MaxBytes) return (null, $"body too large (max {MaxBytes / 1024} KB)");
        // Content-Length can be absent (chunked requests); enforce the same byte limit in either case.
        var buf = new byte[MaxBytes + 1];
        var n = 0;
        while (n < buf.Length)
        {
            var count = await req.Body.ReadAsync(buf.AsMemory(n), req.HttpContext.RequestAborted);
            if (count == 0) break;
            n += count;
        }
        if (n > MaxBytes) return (null, $"body too large (max {MaxBytes / 1024} KB)");
        using var r = new StreamReader(new MemoryStream(buf, 0, n));
        var text = (await r.ReadToEndAsync()).Trim();
        if (req.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try { return (JsonDocument.Parse(text == "" ? "null" : text).RootElement.Clone(), null); }
            catch (JsonException ex) { return (null, "invalid JSON: " + ex.Message); }
        }
        return (Parse(text), null);
    }

    /// <summary>"21.5" becomes a number, "true" a boolean, anything else a string.</summary>
    public static JsonElement ParseValue(string text) => Parse(text);

    private static JsonElement Parse(string text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return JsonSerializer.SerializeToElement(d);
        if (text is "true" or "false") return JsonSerializer.SerializeToElement(text == "true");
        return JsonSerializer.SerializeToElement(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex KeyRegex();
}

/// <summary>Lets the webhook API wake up slow monitors (quotas, GitHub) immediately.</summary>
public static class Wake
{
    private static TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long _count;

    /// <summary>Goes up on every <see cref="Now"/>, so a loop can tell a wake-up from its own timer.</summary>
    public static long Count => Interlocked.Read(ref _count);

    /// <summary>Current wake signal; capture before long work so a <see cref="Now"/> during that work still ends <see cref="Delay"/>.</summary>
    public static Task Pending => _tcs.Task;

    public static void Now()
    {
        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _tcs, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Like Task.Delay, but returns early when <see cref="Now"/> is called.</summary>
    public static Task Delay(TimeSpan delay, CancellationToken ct) => Delay(delay, ct, _tcs.Task);

    /// <summary>Like <see cref="Delay(TimeSpan, CancellationToken)"/> using a wake task captured earlier in the iteration.</summary>
    public static async Task Delay(TimeSpan delay, CancellationToken ct, Task wake)
    {
        await Task.WhenAny(Task.Delay(delay, ct), wake);
        ct.ThrowIfCancellationRequested();
    }
}
