using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>What the Synced lyrics widget shows for the track playing now.</summary>
/// <param name="Status">ok, notfound, idle (nothing playing), off (the asking widget's lookup is turned off) or error.</param>
/// <param name="Synced">LRC text ("[mm:ss.xx] line") when LRCLIB has timed lyrics; the phone follows the position with it.</param>
public sealed record LyricsResult(string Status, string? Title = null, string? Artist = null, string? Synced = null,
    string? Plain = null, bool Instrumental = false);

/// <summary>
/// Lyrics from LRCLIB (https://lrclib.net), a free and open lyrics database that needs no account or key. The agent asks
/// it, not the phone, and only for a Synced lyrics widget on an enabled page whose "Look up lyrics on LRCLIB" is turned on
/// (off by default, per widget); it
/// sends the title, artist, album and length of the track playing now, nothing else. Answers are kept in memory only
/// (a found track for 6 hours, a miss for 30 minutes, at most <see cref="CacheSize"/> songs), so a song is looked up
/// once however often the phones ask. After a failure no request goes to LRCLIB for a while, longer after each failure
/// in a row (from <see cref="FirstBackoffMs"/> to <see cref="MaxBackoffMs"/>), or as long as its Retry-After asks.
/// </summary>
public sealed partial class LyricsService : IDisposable
{
    public const string Host = "https://lrclib.net";
    internal const int CacheSize = 100;
    internal const long FirstBackoffMs = 60_000, MaxBackoffMs = 30 * 60_000;

    private readonly ConfigStore _config;
    private readonly ILogger<LyricsService> _log;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (LyricsResult Result, long Until)> _cache = [];
    private readonly Dictionary<string, Task<LyricsResult>> _pending = []; // the lookup under way per song, under _gate
    private int _failures;   // failed lookups in a row
    private long _quietUntil; // no request to LRCLIB before this (clock ms), after a failure

    public LyricsService(ConfigStore config, ILogger<LyricsService> log, HttpMessageHandler? handler = null, Func<long>? clock = null)
    {
        _config = config;
        _log = log;
        _clock = clock ?? (() => Environment.TickCount64);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Host),
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 2 * 1024 * 1024, // a song's lyrics are a few KB; a search answer is a short list
        };
        // LRCLIB asks clients to say who they are
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version} (+https://github.com/ersinkoc/phonebox)");
    }

    /// <summary>
    /// The lookup is per widget and off by default: <paramref name="widget"/> ("pageId/widgetId", or "pageId/stackId:cardId"
    /// for a Smart Stack card) must be a Synced lyrics widget on an enabled page this phone shows, with "Look up lyrics on
    /// LRCLIB" explicitly turned on. Another widget's option does not count.
    /// </summary>
    public bool Wanted(string? deviceId, string? widget) =>
        PanelWidgets.Find(_config.Get(), deviceId, widget, "lyrics")?.Bool("lookup") == true;

    /// <summary>The lyrics of the track playing now (or paused), as the asking widget shows them.</summary>
    public async Task<LyricsResult> ForAsync(MediaSnapshot? media, string? deviceId, string? widget, CancellationToken ct)
    {
        if (!Wanted(deviceId, widget)) return new LyricsResult("off");
        if (media is null || string.IsNullOrWhiteSpace(media.Title)) return new LyricsResult("idle");
        var (title, artist) = CleanQuery(media.Title, media.Artist);
        if (title.Length == 0) return new LyricsResult("idle");
        var album = media.Album?.Trim() ?? "";
        var seconds = media.DurMs > 0 ? (int)Math.Round(media.DurMs / 1000.0) : 0;
        var key = string.Join('\n', artist, title, album, seconds).ToLowerInvariant();
        Task<LyricsResult> task;
        lock (_gate)
        {
            var now = _clock();
            Prune(now); // an answer that expired goes now, even when this one comes from the cache
            if (_cache.TryGetValue(key, out var hit) && hit.Until > now) return hit.Result with { Title = title, Artist = artist };
            // one request per song, however many phones or widgets ask at once: the lookup is started under the lock, so
            // only one caller starts it and the others wait for that one. A finished lookup that is still listed has had
            // its answer cached (LookUp caches before it completes), and that answer has expired since: start a new one.
            if (!_pending.TryGetValue(key, out var running) || running.IsCompleted)
            {
                running = Task.Run(() => LookUp(title, artist, album, seconds));
                _pending[key] = running;
                // forgotten once it is done (only this lookup: a newer one for the same song stays)
                _ = running.ContinueWith(done =>
                {
                    lock (_gate)
                        if (_pending.TryGetValue(key, out var listed) && listed == done) _pending.Remove(key);
                }, TaskScheduler.Default);
            }
            task = running;
        }
        // a phone that gives up stops waiting; the lookup itself is not cancelled, the others still get it
        var result = await task.WaitAsync(ct);
        return result with { Title = title, Artist = artist };
    }

    private async Task<LyricsResult> LookUp(string title, string artist, string album, int seconds)
    {
        LyricsResult result;
        long keep;
        // LRCLIB failed a moment ago: no request until the back-off is over; the song is asked again after it
        if (PausedFor() is > 0 and var quiet) return Remember(title, artist, album, seconds, new LyricsResult("error"), quiet);
        try
        {
            result = await Fetch(title, artist, album, seconds);
            keep = result.Status switch { "ok" => 6 * 3600_000L, "off" => 0, _ => 30 * 60_000L };
            lock (_gate) _failures = 0;
        }
        catch (LrclibPausedException paused)
        {
            // another lookup failed while this one was under way: wait for the same back-off, without counting a failure
            result = new LyricsResult("error");
            keep = paused.LeftMs;
        }
        catch (Exception ex)
        {
            _log.LogDebug("lyrics: {Msg}", ex.Message);
            result = new LyricsResult("error");
            lock (_gate)
            {
                // 1, 2, 4… minutes after failures in a row, at most 30; longer when LRCLIB's Retry-After asks for it
                _failures = Math.Min(_failures + 1, 16);
                keep = Math.Min(FirstBackoffMs << (_failures - 1), MaxBackoffMs);
                if (ex is LrclibRefusedException { RetryAfter: { } wait }) keep = Math.Max((long)wait.TotalMilliseconds, keep);
                // two lookups failing at once: the longer wait (an earlier Retry-After, say) stands
                _quietUntil = Math.Max(_quietUntil, _clock() + keep);
            }
        }
        return Remember(title, artist, album, seconds, result, keep);
    }

    /// <summary>How many answers are held now (tests: expired ones are dropped).</summary>
    internal int CachedSongs
    {
        get { lock (_gate) return _cache.Count; }
    }

    /// <summary>How long LRCLIB is still left alone after a failure (ms), or 0.</summary>
    private long PausedFor()
    {
        lock (_gate) return Math.Max(0, _quietUntil - _clock());
    }

    /// <summary>Drops the answers that are no longer valid (call with <see cref="_gate"/> held).</summary>
    private void Prune(long now)
    {
        foreach (var gone in _cache.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) _cache.Remove(gone);
    }

    /// <summary>
    /// Keeps an answer for <paramref name="keep"/> ms, at most <see cref="CacheSize"/> songs. Expired answers are dropped
    /// whenever lyrics are asked for (and here), so memory holds only answers that would still be used.
    /// </summary>
    private LyricsResult Remember(string title, string artist, string album, int seconds, LyricsResult result, long keep)
    {
        var key = string.Join('\n', artist, title, album, seconds).ToLowerInvariant();
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            if (_cache.Count >= CacheSize && !_cache.ContainsKey(key))
                foreach (var old in _cache.OrderBy(kv => kv.Value.Until).Take(_cache.Count - CacheSize + 1).Select(kv => kv.Key).ToList())
                    _cache.Remove(old);
            _cache[key] = (result, now + keep);
        }
        return result;
    }

    private async Task<LyricsResult> Fetch(string title, string artist, string album, int seconds)
    {
        // the exact match first (it needs the album and the length), then a search by title and artist
        if (album.Length > 0 && seconds is > 0 and <= 3600 && artist.Length > 0)
        {
            var url = $"/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}"
                + $"&album_name={Uri.EscapeDataString(album)}&duration={seconds}";
            using var res = await _http.GetAsync(url);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (FromEntry(doc.RootElement) is { } exact) return exact;
            }
            else if (res.StatusCode != HttpStatusCode.NotFound) throw Refused(res);
        }
        // a failure of another lookup may have started a back-off meanwhile: no second request during it
        if (PausedFor() is > 0 and var left) throw new LrclibPausedException(left);
        // no widget wants lyrics any more (its option was turned off while the exact lookup ran): no second request
        if (!_config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "lyrics" && w.Bool("lookup") == true))
            return new LyricsResult("off");
        var search = $"/api/search?track_name={Uri.EscapeDataString(title)}" + (artist.Length > 0 ? $"&artist_name={Uri.EscapeDataString(artist)}" : "");
        using (var res = await _http.GetAsync(search))
        {
            if (!res.IsSuccessStatusCode) throw Refused(res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return Best(doc.RootElement, seconds, title, artist) ?? new LyricsResult("notfound");
        }
    }

    /// <summary>An answer that is not lyrics: its status, and how long LRCLIB asks to wait (Retry-After), if it says.</summary>
    private static LrclibRefusedException Refused(HttpResponseMessage res)
    {
        TimeSpan? wait = res.Headers.RetryAfter?.Delta;
        if (wait is null && res.Headers.RetryAfter?.Date is { } at) wait = at - DateTimeOffset.UtcNow;
        return new LrclibRefusedException((int)res.StatusCode, wait is { Ticks: > 0 } ? wait : null);
    }

    /// <summary>
    /// Of LRCLIB's search results: the same title and artist first (the search matches keywords, so "Home" also finds
    /// "Coming Home"), then timed lyrics before plain ones, then the length closest to the track's.
    /// </summary>
    internal static LyricsResult? Best(JsonElement results, int seconds, string title = "", string artist = "")
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        static string Field(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
        int Same(JsonElement e) =>
            (title.Length > 0 && string.Equals(Field(e, "trackName"), title, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
            + (artist.Length > 0 && string.Equals(Field(e, "artistName"), artist, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        return results.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => (Entry: e, Result: FromEntry(e), Off: e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - seconds) : 999))
            .Where(x => x.Result is not null && (seconds <= 0 || x.Off <= 5))
            .OrderByDescending(x => Same(x.Entry))
            .ThenByDescending(x => x.Result!.Synced is not null)
            .ThenBy(x => seconds > 0 ? x.Off : 0)
            .Select(x => x.Result)
            .FirstOrDefault();
    }

    /// <summary>One LRCLIB record, or null when it holds no lyrics at all.</summary>
    internal static LyricsResult? FromEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        static string? Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        var instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        var synced = Text(e, "syncedLyrics");
        var plain = Text(e, "plainLyrics");
        if (!instrumental && synced is null && plain is null) return null;
        return new LyricsResult("ok", Synced: synced, Plain: plain, Instrumental: instrumental);
    }

    /// <summary>
    /// Title and artist as a lyrics database knows them. A video's title often carries the artist ("Artist - Song") and
    /// extras such as "(Official Video)" or "[Lyrics]", and a YouTube channel name can end in " - Topic" or "VEVO".
    /// </summary>
    internal static (string Title, string Artist) CleanQuery(string title, string? artist)
    {
        var t = Extras().Replace(title, "").Trim();
        var a = (artist ?? "").Trim();
        if (a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) a = a[..^8].Trim();
        if (a.EndsWith("VEVO", StringComparison.Ordinal) && a.Length > 4) a = a[..^4].Trim();
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = t[..dash].Trim();
            var right = t[(dash + 3)..].Trim();
            // "Artist - Song": the part that names the channel's artist (or any part, without an artist) is the artist
            if (right.Length > 0 && (a.Length == 0 || left.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(left, StringComparison.OrdinalIgnoreCase)))
            {
                a = left;
                t = right;
            }
        }
        return (Spaces().Replace(t, " ").Trim(), Spaces().Replace(a, " ").Trim());
    }

    [GeneratedRegex(@"\s*[\(\[](?:official|lyric|lyrics|audio|video|hd|hq|4k|visualizer|visualiser|music video|clip officiel|videoclip|mv)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Extras();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public void Dispose() => _http.Dispose();
}

/// <summary>A lookup that reached a back-off started by another one (LRCLIB is left alone for <see cref="LeftMs"/>).</summary>
internal sealed class LrclibPausedException(long leftMs) : Exception("LRCLIB is paused after a failure")
{
    public long LeftMs { get; } = leftMs;
}

/// <summary>LRCLIB answered with an error status (429 when asked too often, 5xx when it is down).</summary>
public sealed class LrclibRefusedException(int status, TimeSpan? retryAfter) : HttpRequestException($"LRCLIB answered {status}")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
