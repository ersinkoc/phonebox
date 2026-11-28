using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Covers for the games found outside Steam (<see cref="OtherGames"/>), from the Steam store: a game's names (the product
/// name its program carries, then its folder's) are searched in the store, a result is taken only when its words and
/// numbers agree with the name (<see cref="Matches"/>), and that app's 600 × 900 library art is kept on this computer.
/// Each game is looked up once (a game the store does not have is asked again after a week), one request at a time,
/// in the background. The store sees the names searched for and nothing else. Off until a library widget's "Covers from
/// the Steam store" option is turned on, and checked again before every request.
/// After a failure the store is left alone for a while (<see cref="FirstBackoffMs"/>, doubling up to
/// <see cref="MaxBackoffMs"/>, or as long as its Retry-After asks), and the game that failed is asked again only after a
/// wait of its own (<see cref="FirstGameRetryMs"/>, doubling up to <see cref="MaxGameRetryMs"/>), so it does not hold up
/// the others. The covers of the games no longer found are dropped, with their index entries.
/// </summary>
public sealed class GameCovers : IDisposable
{
    internal const string SearchUrl = "https://store.steampowered.com/api/storesearch/";
    internal const string ArtUrl = "https://shared.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg";
    internal const long MaxArtBytes = 4 << 20;
    internal static readonly TimeSpan RetryMissing = TimeSpan.FromDays(7);
    internal const int PauseMs = 1500;
    /// <summary>After failures in a row, nothing is asked for 1, 2, 4… minutes, at most 30 (like LyricsService).</summary>
    internal const long FirstBackoffMs = 60_000, MaxBackoffMs = 30 * 60_000;
    /// <summary>A game whose lookup failed is asked again after 1, 2, 4… hours, at most a day.</summary>
    internal const long FirstGameRetryMs = 3_600_000, MaxGameRetryMs = 24 * 3_600_000;

    private readonly ILogger<GameCovers> _log;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _wanted;
    private readonly int _pauseMs;
    private readonly Lock _gate = new();
    private Dictionary<string, Entry> _index;
    private readonly Queue<(string Key, string[] Names)> _queue = new();
    private readonly HashSet<string> _queued = [];
    private readonly Dictionary<string, (int Count, long Until)> _gameFailures = [];
    private int _failures;
    private long _quietUntil;
    private bool _sweepPending; // art files of dropped games still to delete once no lookup runs
    private Task? _worker;

    internal sealed record Entry(long App, long At);

    /// <summary>Whether a Game library widget on an enabled page has "Covers from the Steam store" on (off by default).</summary>
    internal static bool WantedBy(AgentConfig c) =>
        c.Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library" && w.Bool("storeCovers") == true);

    /// <param name="wanted">Asked before every request: false stops the lookups (the option was turned off).</param>
    public GameCovers(ILogger<GameCovers> log, HttpMessageHandler? handler = null, string? dir = null, Func<DateTimeOffset>? now = null,
        int pauseMs = PauseMs, Func<bool>? wanted = null)
    {
        _log = log;
        _dir = dir ?? Path.Combine(AppPaths.LocalDir, "game-covers");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _wanted = wanted ?? (() => true);
        _pauseMs = pauseMs;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxArtBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version} (+https://github.com/ersinkoc/phonebox)");
        _index = ReadIndex();
    }

    public void Dispose() => _http.Dispose();

    private string IndexPath => Path.Combine(_dir, "index.json");

    private Dictionary<string, Entry> ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath) && new FileInfo(IndexPath).Length < 4 << 20)
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath)) ?? [];
        }
        catch { }
        return [];
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // the folder or the index swapped for a link (a junction to elsewhere): nothing is written through it
            if (SteamLibrary.IsLink(_dir) || (File.Exists(IndexPath) && SteamLibrary.IsLink(IndexPath))) return;
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_index);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, IndexPath, true);
        }
        catch (Exception ex) { _log.LogDebug("could not save the game covers index: {Msg}", ex.Message); }
    }

    /// <summary>The cover kept for a game (its key), or null; never a file reached through a link.</summary>
    public string? CoverFile(string key)
    {
        long app;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var e) || e.App <= 0) return null;
            app = e.App;
        }
        var file = ArtPath(app);
        return File.Exists(file) && !SteamLibrary.IsLink(file) && !SteamLibrary.IsLink(_dir) ? file : null;
    }

    private string ArtPath(long app) => Path.Combine(_dir, app.ToString(CultureInfo.InvariantCulture) + ".jpg");

    private long NowMs() => _now().ToUnixTimeMilliseconds();

    /// <summary>
    /// The games found now: looks up, in the background, those not looked up yet (or not found a week ago), but not while
    /// the store is left alone after a failure, nor a game whose own lookup failed a moment ago. Drops what is kept for
    /// the games that are no longer in <paramref name="games"/>.
    /// </summary>
    public void Want(IEnumerable<(string Key, string[] Names)> games)
    {
        var list = games.ToList();
        bool pruned;
        lock (_gate)
        {
            pruned = Prune(list.Select(g => g.Key).ToHashSet(StringComparer.Ordinal));
            var nowMs = NowMs();
            if (_quietUntil <= nowMs)
            {
                var now = nowMs / 1000;
                foreach (var (key, names) in list)
                {
                    if (names.Length == 0 || _queued.Contains(key)) continue;
                    if (_gameFailures.TryGetValue(key, out var f) && f.Until > nowMs) continue;
                    if (_index.TryGetValue(key, out var e) && (e.App > 0 ? File.Exists(ArtPath(e.App)) : now - e.At < RetryMissing.TotalSeconds)) continue;
                    _queue.Enqueue((key, names));
                    _queued.Add(key);
                }
                if (_queue.Count > 0 && _worker is null) _worker = Task.Run(Work);
            }
        }
        if (pruned) { SaveIndex(); DropUnusedArt(); }
    }

    /// <summary>Drops the index entries (and failure counts) of the games not in <paramref name="keep"/>; call with the lock held.</summary>
    private bool Prune(HashSet<string> keep)
    {
        foreach (var key in _gameFailures.Keys.Where(k => !keep.Contains(k)).ToList()) _gameFailures.Remove(key);
        var gone = _index.Keys.Where(k => !keep.Contains(k)).ToList();
        foreach (var key in gone) _index.Remove(key);
        return gone.Count > 0;
    }

    /// <summary>
    /// Deletes the art files no index entry names any more (only files named like ours: digits and .jpg). While lookups
    /// run (one may be writing a file now) it is left to the worker, which does it once its queue is empty.
    /// </summary>
    private void DropUnusedArt()
    {
        try
        {
            lock (_gate)
            {
                _sweepPending = true;
                if (_queue.Count > 0 || _worker is not null) return;
            }
            if (!Directory.Exists(_dir) || SteamLibrary.IsLink(_dir)) { lock (_gate) _sweepPending = false; return; }
            HashSet<string> used;
            lock (_gate) used = _index.Values.Where(e => e.App > 0).Select(e => Path.GetFileName(ArtPath(e.App))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.jpg", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }).ToList())
            {
                var name = Path.GetFileName(file);
                if (used.Contains(name) || !Path.GetFileNameWithoutExtension(name).All(char.IsAsciiDigit)) continue;
                lock (_gate) if (_queue.Count > 0 || _worker is not null) return; // a lookup started: the worker sweeps after it
                File.Delete(file);
            }
            lock (_gate) _sweepPending = false;
        }
        catch (Exception ex) { _log.LogDebug("could not drop old game covers: {Msg}", ex.Message); }
    }

    /// <summary>Waits for the lookups under way (tests).</summary>
    internal Task Idle() { lock (_gate) return _worker ?? Task.CompletedTask; }

    /// <summary>How long the store is still left alone after a failure (ms), or 0 (tests).</summary>
    internal long PausedFor() { lock (_gate) return Math.Max(0, _quietUntil - NowMs()); }

    private async Task Work()
    {
        while (true)
        {
            (string Key, string[] Names) next;
            bool sweep = false;
            lock (_gate)
            {
                if (_queue.Count == 0) { _worker = null; sweep = _sweepPending; next = ("", []); }
                else next = _queue.Dequeue();
            }
            if (next.Key.Length == 0)
            {
                // the art of the games that were dropped while this ran
                if (sweep) DropUnusedArt();
                return;
            }
            long app = 0;
            var failed = false;
            try { app = await LookUp(next.Names); }
            catch (CoversOffException)
            {
                // the option was turned off: the rest is not asked, and nothing counts as a failure
                bool pending;
                lock (_gate) { _queue.Clear(); _queued.Clear(); _worker = null; pending = _sweepPending; }
                if (pending) DropUnusedArt();
                return;
            }
            catch (Exception ex)
            {
                failed = true;
                _log.LogInformation("game cover lookup failed: {Msg}", ex.Message);
                lock (_gate)
                {
                    var nowMs = NowMs();
                    // this game waits on its own (1, 2, 4… hours), so a game that always fails does not hold up the rest
                    var count = Math.Min((_gameFailures.TryGetValue(next.Key, out var f) ? f.Count : 0) + 1, 16);
                    _gameFailures[next.Key] = (count, nowMs + Math.Min(FirstGameRetryMs << (count - 1), MaxGameRetryMs));
                    // and the store is left alone for 1, 2, 4… minutes, at most 30, or as long as its Retry-After asks
                    _failures = Math.Min(_failures + 1, 16);
                    var keep = Math.Min(FirstBackoffMs << (_failures - 1), MaxBackoffMs);
                    if (ex is StoreRefusedException { RetryAfter: { } wait }) keep = Math.Max((long)wait.TotalMilliseconds, keep);
                    _quietUntil = Math.Max(_quietUntil, nowMs + keep);
                    // the games queued after it are asked again once the wait is over (the next list after it)
                    _queue.Clear();
                    _queued.Clear();
                }
            }
            if (!failed)
            {
                lock (_gate)
                {
                    _queued.Remove(next.Key);
                    // a failure (no network, the store busy) is not "not found": only an answer is kept
                    _index[next.Key] = new Entry(app, _now().ToUnixTimeSeconds());
                    _gameFailures.Remove(next.Key);
                    _failures = 0;
                }
                SaveIndex();
                _log.LogDebug("game cover: {Names} -> {App}", string.Join(" | ", next.Names), app);
            }
            if (_pauseMs > 0) await Task.Delay(_pauseMs);
        }
    }

    /// <summary>The option was turned off while lookups were queued.</summary>
    private sealed class CoversOffException() : Exception("Covers from the Steam store were turned off");

    /// <summary>The store answered with an error: its status, and how long it asks to wait (Retry-After), if it says.</summary>
    internal sealed class StoreRefusedException(int status, TimeSpan? retryAfter) : HttpRequestException($"the Steam store answered {status}")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private static StoreRefusedException Refused(HttpResponseMessage res)
    {
        TimeSpan? wait = res.Headers.RetryAfter?.Delta;
        if (wait is null && res.Headers.RetryAfter?.Date is { } at) wait = at - DateTimeOffset.UtcNow;
        return new StoreRefusedException((int)res.StatusCode, wait is { Ticks: > 0 } ? wait : null);
    }

    /// <summary>A request to the store, made only while the option is still on.</summary>
    private async Task<HttpResponseMessage> Get(string url)
    {
        if (!_wanted()) throw new CoversOffException();
        return await _http.GetAsync(url);
    }

    /// <summary>The store app whose name matches one of <paramref name="names"/>, with its art saved; 0 when none does.</summary>
    private async Task<long> LookUp(string[] names)
    {
        foreach (var name in names)
        {
            var url = $"{SearchUrl}?term={Uri.EscapeDataString(name)}&cc=us&l=en";
            using var res = await Get(url);
            if (!res.IsSuccessStatusCode) throw Refused(res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var hits = new List<(long Id, int Score)>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) continue;
                if (item.TryGetProperty("type", out var type) && type.GetString() is { } t && t != "app") continue;
                if (!item.TryGetProperty("name", out var n) || n.GetString() is not { } found || Match(name, found) is not (> 0 and var score)) continue;
                hits.Add((id, score));
            }
            // the game itself before one sharing its name and a subtitle (an expansion: "…: Episodes from Liberty City")
            foreach (var (id, _) in hits.OrderByDescending(h => h.Score))
                if (await SaveArt(id)) return id;
            if (_pauseMs > 0) await Task.Delay(_pauseMs);
        }
        return 0;
    }

    private async Task<bool> SaveArt(long app)
    {
        using var res = await Get(string.Format(CultureInfo.InvariantCulture, ArtUrl, app));
        // no art for that app (404): another result may have some; a refusal (429, 5xx, a Retry-After) is a failure
        if (res.Headers.RetryAfter is not null || (int)res.StatusCode == 429 || (int)res.StatusCode >= 500) throw Refused(res);
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType != "image/jpeg") return false;
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length is < 1024 or > (int)MaxArtBytes || bytes[0] != 0xFF || bytes[1] != 0xD8) return false; // a JPEG, not a page
        Directory.CreateDirectory(_dir);
        // the folder, or a file in its place, swapped for a link: nothing is written through it
        var path = ArtPath(app);
        if (SteamLibrary.IsLink(_dir) || (File.Exists(path) && SteamLibrary.IsLink(path)))
            throw new IOException("the game covers folder or file is a link");
        await File.WriteAllBytesAsync(path, bytes);
        return true;
    }

    private static readonly Dictionary<string, string> Roman = new()
        { ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10" };

    /// <summary>A name's words for matching: lower case, accents and marks (™ ®) dropped, Roman numerals as digits.</summary>
    internal static List<string> Words(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return OtherGames.Words(sb.ToString()).Select(w => Roman.GetValueOrDefault(w, w)).Where(w => w.Length > 0).ToList();
    }

    // words an edition adds to a game's name ("The Complete Edition", "Game of the Year")
    private static readonly HashSet<string> EditionWords = ["the", "complete", "definitive", "edition", "remastered", "enhanced",
        "deluxe", "ultimate", "gold", "standard", "goty", "game", "of", "year", "collection", "director", "s", "cut", "tm", "r"];

    /// <summary>
    /// Whether a store result is the game named <paramref name="name"/>: the same words, in any order, once the result's
    /// subtitle after a colon or dash ("Grand Theft Auto IV: The Complete Edition") and edition words are set aside on
    /// both sides. So FIFA 23 is not FIFA 22, Portal is not Portal 2 and not Portal Knights.
    /// </summary>
    internal static bool Matches(string name, string result) => Match(name, result) > 0;

    /// <summary>2: the same name (but edition words); 1: the same once the result's subtitle is set aside; 0: another game.</summary>
    internal static int Match(string name, string result)
    {
        static HashSet<string> Core(string n) => Words(n).Where(w => !EditionWords.Contains(w)).ToHashSet();
        var want = Core(name);
        if (want.Count == 0) return 0;
        if (want.SetEquals(Core(result))) return 2;
        var cut = result.IndexOfAny([':', '–', '—']) is var c and > 0 ? c : result.IndexOf(" - ", StringComparison.Ordinal);
        return cut > 0 && want.SetEquals(Core(result[..cut])) ? 1 : 0;
    }

    /// <summary>
    /// The names to look a game up by: its program's product name (what the publisher calls it, "Grand Theft Auto IV" for
    /// a folder named "GTA 4 LCPDFR"), when that is a game's name and not an engine's or the system's, then its folder's.
    /// </summary>
    internal static string[] NamesOf(string folderName, string? productName)
    {
        var names = new List<string>();
        if (productName is { } p && UsefulProduct(p)) names.Add(p.Trim());
        if (!names.Any(n => Words(n).SequenceEqual(Words(folderName)))) names.Add(folderName);
        return names.Where(n => Words(n).Count > 0).ToArray();
    }

    private static bool UsefulProduct(string p)
    {
        var w = Words(p);
        string[] not = ["windows", "microsoft", "unity", "unreal", "engine", "launcher", "setup", "installer", "bootstrap", "game", "application", "player", "client"];
        return w.Count > 0 && p.Length <= 80 && !(w.Count == 1 && not.Contains(w[0])) && !w.Any(x => x is "windows" or "unreal" or "unity" or "launcher" or "bootstrapper");
    }

    /// <summary>The product name a program carries (Windows), or null.</summary>
    internal static string? ProductName(string? exe)
    {
        if (exe is null || !OperatingSystem.IsWindows()) return null;
        try { return FileVersionInfo.GetVersionInfo(exe).ProductName; } catch { return null; }
    }
}
