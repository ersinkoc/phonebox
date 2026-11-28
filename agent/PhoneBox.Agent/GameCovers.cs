using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Covers for the games found outside Steam (<see cref="OtherGames"/>), from the Steam store: a game's names (the product
/// name its program carries, then its folder's) are searched in the store, a result is taken only when its words and
/// numbers agree with the name (<see cref="Matches"/>), and that app's 600 × 900 library art is kept on this computer.
/// A game is looked up until it is found or the store answers that it has none (then again after a week), one request at
/// a time, in the background, and only while a library widget's "Covers from the Steam store" option is on: it is checked
/// again before every request, and turning it off cancels the request under way and drops what was waiting. After a
/// failure (no network, a timeout, the store refusing or busy, an answer that is not the store's) nothing is asked for a
/// while: 1 minute, doubling up to 30 for failures in a row, or as long as the store's Retry-After asks (like
/// <see cref="LyricsService"/>). Each failure also counts against the game being looked up: the games with the fewest go
/// first, and one that failed <see cref="MaxGameFailures"/> times waits a week like a missing one, so it never holds up
/// the others. The store sees the names searched for and the user agent, nothing else.
/// </summary>
public sealed class GameCovers : IDisposable
{
    internal const string SearchUrl = "https://store.steampowered.com/api/storesearch/";
    internal const string ArtUrl = "https://shared.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg";
    internal const long MaxArtBytes = 4 << 20;
    internal static readonly TimeSpan RetryMissing = TimeSpan.FromDays(7);
    /// <summary>How long a game may be missing from the list (a drive not ready, unplugged) before its cover is deleted.</summary>
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);
    internal const int PauseMs = 1500;
    internal const long FirstBackoffMs = 60_000, MaxBackoffMs = 30 * 60_000;
    /// <summary>Failed lookups of one game before it waits like a missing one.</summary>
    internal const int MaxGameFailures = 3;

    private readonly ILogger<GameCovers> _log;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _pauseMs;
    private readonly Lock _gate = new();
    private readonly Lock _saving = new();
    private Dictionary<string, Entry> _index;
    private readonly Queue<(string Key, string[] Names)> _queue = new();
    private readonly HashSet<string> _queued = [];
    private readonly Dictionary<string, int> _gameFailures = [];
    private readonly Dictionary<string, long> _absentSince = []; // unix s: a game with a cover first missing from the list
    private Func<bool> _wanted = () => false;
    private CancellationTokenSource _cancel = new();
    private string? _inFlight;      // the game being looked up now
    private int _failures;          // failures in a row (the store or the network)
    private long _pausedUntil;      // unix ms: nothing is asked before
    private Task? _worker;

    internal sealed record Entry(long App, long At);

    public GameCovers(ILogger<GameCovers> log, HttpMessageHandler? handler = null, string? dir = null, Func<DateTimeOffset>? now = null, int pauseMs = PauseMs)
    {
        _log = log;
        _dir = dir ?? Path.Combine(AppPaths.LocalDir, "game-covers");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pauseMs = pauseMs;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxArtBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version} (+https://github.com/ersinkoc/phonebox)");
        _index = ReadIndex();
    }

    public void Dispose() { _cancel.Cancel(); _http.Dispose(); }

    private string IndexPath => Path.Combine(_dir, "index.json");

    private Dictionary<string, Entry> ReadIndex()
    {
        try
        {
            if (!IsLink(_dir) && File.Exists(IndexPath) && !IsLink(IndexPath) && new FileInfo(IndexPath).Length < 4 << 20)
                return (JsonSerializer.Deserialize<Dictionary<string, Entry?>>(File.ReadAllText(IndexPath)) ?? [])
                    .Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
        }
        catch { }
        return [];
    }

    /// <summary>Writes the index, one save at a time, from a copy taken in the save (a later save never loses to an earlier one).</summary>
    private void SaveIndex()
    {
        lock (_saving)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = IndexPath + ".tmp";
                if (IsLink(_dir) || IsLink(IndexPath) || IsLink(tmp)) return;
                string json;
                lock (_gate) json = JsonSerializer.Serialize(_index);
                File.WriteAllText(tmp, json);
                File.Move(tmp, IndexPath, true);
            }
            catch (Exception ex) { _log.LogDebug("could not save the game covers index: {Msg}", ex.Message); }
        }
    }

    /// <summary>The cover kept for a game (its key), or null. A file or folder that is a link is never served.</summary>
    public string? CoverFile(string key)
    {
        long app;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var e) || e.App <= 0) return null;
            app = e.App;
        }
        var file = ArtPath(app);
        return File.Exists(file) && !IsLink(file) && !IsLink(_dir) ? file : null;
    }

    private string ArtPath(long app) => Path.Combine(_dir, app.ToString(CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>
    /// The games found now: looks up, in the background, those not looked up yet (or not found a week ago), while
    /// <paramref name="wanted"/> says the covers are still wanted, and forgets the covers of the games gone for a day.
    /// </summary>
    public void Want(IReadOnlyCollection<(string Key, string[] Names)> games, Func<bool> wanted)
    {
        bool pruned;
        List<string> unused;
        lock (_gate)
        {
            _wanted = wanted;
            if (_cancel.IsCancellationRequested) { _cancel.Dispose(); _cancel = new(); }
            var now = _now();
            var secs = now.ToUnixTimeSeconds();
            // the games that failed least go first, so one that keeps failing comes after the others
            foreach (var (key, names) in games.OrderBy(g => _gameFailures.GetValueOrDefault(g.Key)))
            {
                if (names.Length == 0 || _queued.Contains(key) || key == _inFlight) continue;
                if (_index.TryGetValue(key, out var e) && (e.App > 0 ? File.Exists(ArtPath(e.App)) : secs - e.At < RetryMissing.TotalSeconds)) continue;
                _queue.Enqueue((key, names));
                _queued.Add(key);
            }
            (pruned, unused) = Prune(games.Select(g => g.Key).ToHashSet(StringComparer.Ordinal), secs);
            if (_queue.Count > 0 && _worker is null && now.ToUnixTimeMilliseconds() >= _pausedUntil) _worker = Task.Run(Work);
        }
        if (!pruned) return;
        if (!IsLink(_dir))
            foreach (var file in unused)
                try { if (File.Exists(file) && !IsLink(file)) File.Delete(file); } catch { }
        SaveIndex();
    }

    /// <summary>
    /// Drops the entries of games missing from <paramref name="keys"/> for <see cref="ForgetAfter"/>, and returns the art
    /// files no game uses any more (under the lock).
    /// </summary>
    private (bool Changed, List<string> Files) Prune(HashSet<string> keys, long now)
    {
        foreach (var k in _absentSince.Keys.Where(keys.Contains).ToList()) _absentSince.Remove(k);
        var gone = new List<string>();
        foreach (var k in _index.Keys.Where(k => !keys.Contains(k)))
        {
            if (!_absentSince.TryGetValue(k, out var since)) _absentSince[k] = since = now;
            if (now - since >= ForgetAfter.TotalSeconds) gone.Add(k);
        }
        // a lookup under way may be saving a cover one of them shares: the next prune does it
        if (gone.Count == 0 || _inFlight is not null) return (false, []);
        var apps = gone.Select(k => _index[k].App).Where(a => a > 0).ToHashSet();
        foreach (var k in gone) { _index.Remove(k); _gameFailures.Remove(k); _absentSince.Remove(k); }
        apps.ExceptWith(_index.Values.Select(e => e.App));
        return (true, apps.Select(ArtPath).ToList());
    }

    /// <summary>The covers are no longer wanted (the option was turned off, or no library widget is shown): the request under way is cancelled, what waits is dropped.</summary>
    public void Stop() { lock (_gate) StopLocked(); }

    private void StopLocked()
    {
        _wanted = () => false;
        _cancel.Cancel();
        _queue.Clear();
        _queued.Clear();
    }

    /// <summary>Waits for the lookups under way (tests).</summary>
    internal Task Idle() { lock (_gate) return _worker ?? Task.CompletedTask; }

    private bool StillWanted()
    {
        Func<bool> wanted;
        lock (_gate) wanted = _wanted;
        try { return wanted(); } catch { return false; }
    }

    private async Task Work()
    {
        while (true)
        {
            (string Key, string[] Names) next;
            CancellationToken token;
            lock (_gate)
            {
                if (_queue.Count == 0) { _worker = null; return; }
                next = _queue.Dequeue();
                _queued.Remove(next.Key);
                _inFlight = next.Key;
                token = _cancel.Token;
            }
            if (!StillWanted()) { lock (_gate) { StopLocked(); _inFlight = null; _worker = null; } return; }
            long app = 0;
            Exception? failure = null;
            try { app = await LookUp(next.Names, token); }
            catch (Exception ex) { failure = ex; }
            // turned off during the lookup: nothing more is asked or kept
            if (token.IsCancellationRequested || !StillWanted() || failure is OperationCanceledException and not TaskCanceledException)
            {
                lock (_gate) { StopLocked(); _inFlight = null; _worker = null; }
                return;
            }
            var storeFault = failure is StoreBusyException or HttpRequestException or TaskCanceledException or JsonException;
            lock (_gate)
            {
                _inFlight = null;
                if (failure is null)
                {
                    _failures = 0;
                    _gameFailures.Remove(next.Key);
                    _index[next.Key] = new Entry(app, _now().ToUnixTimeSeconds());
                }
                else
                {
                    // counted against the game too: the next round starts with the others, and after a few it waits a week
                    var n = _gameFailures[next.Key] = _gameFailures.GetValueOrDefault(next.Key) + 1;
                    if (n >= MaxGameFailures) { _index[next.Key] = new Entry(0, _now().ToUnixTimeSeconds()); _gameFailures.Remove(next.Key); }
                }
                if (storeFault)
                {
                    // the store or the network: wait before asking anything again, and ask again later
                    _failures++;
                    var wait = Math.Min(FirstBackoffMs << Math.Min(_failures - 1, 10), MaxBackoffMs);
                    if (failure is StoreBusyException { RetryAfter: { } asked }) wait = Math.Max(wait, (long)asked.TotalMilliseconds);
                    _pausedUntil = Math.Max(_pausedUntil, _now().ToUnixTimeMilliseconds() + wait);
                    _queue.Clear();
                    _queued.Clear();
                    _worker = null;
                }
            }
            if (failure is null) _log.LogDebug("game cover: {Key} -> {App}", next.Key, app);
            else _log.LogInformation("game cover lookup failed{Paused}: {Msg}", storeFault ? ", paused" : "", failure.Message);
            SaveIndex();
            if (storeFault) return;
            if (_pauseMs > 0) await Task.Delay(_pauseMs, CancellationToken.None);
        }
    }

    /// <summary>The store app whose name matches one of <paramref name="names"/>, with its art saved; 0 when none does.</summary>
    private async Task<long> LookUp(string[] names, CancellationToken token)
    {
        foreach (var name in names)
        {
            if (!StillWanted()) throw new OperationCanceledException("covers turned off");
            using var res = await _http.GetAsync($"{SearchUrl}?term={Uri.EscapeDataString(name)}&cc=us&l=en", token);
            Refused(res, [HttpStatusCode.NotFound]);
            if (res.StatusCode == HttpStatusCode.NotFound) continue;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var hits = new List<(long Id, int Score)>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) continue;
                if (item.TryGetProperty("type", out var type) && type.GetString() is { } t && t != "app") continue;
                if (!item.TryGetProperty("name", out var n) || n.GetString() is not { } found || Match(name, found) is not (> 0 and var score)) continue;
                hits.Add((id, score));
            }
            // the game itself first; a name with a subtitle only when it is the one result that has one (two of them: the
            // game and one of its own expansions, maybe, so neither is taken)
            if (hits.Count(h => h.Score == 1) > 1) hits.RemoveAll(h => h.Score == 1);
            foreach (var (id, _) in hits.OrderByDescending(h => h.Score))
            {
                if (!StillWanted()) throw new OperationCanceledException("covers turned off");
                if (await SaveArt(id, token)) return id;
            }
            if (_pauseMs > 0) await Task.Delay(_pauseMs, token);
        }
        return 0;
    }

    /// <summary>
    /// A refusal (any status but success and <paramref name="missing"/>: 429, 403, a redirect, 5xx) or a Retry-After on
    /// a failed answer: thrown as <see cref="StoreBusyException"/>, never taken as "not found". Like LyricsService.
    /// </summary>
    private static void Refused(HttpResponseMessage res, HttpStatusCode[] missing)
    {
        if (res.IsSuccessStatusCode || (missing.Contains(res.StatusCode) && res.Headers.RetryAfter is null)) return;
        TimeSpan? wait = res.Headers.RetryAfter?.Delta;
        if (wait is null && res.Headers.RetryAfter?.Date is { } at) wait = at - DateTimeOffset.UtcNow;
        throw new StoreBusyException((int)res.StatusCode, wait is { } w && w > TimeSpan.Zero ? w : null);
    }

    private async Task<bool> SaveArt(long app, CancellationToken token)
    {
        using var res = await _http.GetAsync(string.Format(CultureInfo.InvariantCulture, ArtUrl, app), token);
        // no art for this app (the store's CDN answers 404, or 403 for a file it does not have): try the next result
        Refused(res, [HttpStatusCode.NotFound, HttpStatusCode.Forbidden]);
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType != "image/jpeg") return false;
        var bytes = await res.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length is < 1024 or > (int)MaxArtBytes || bytes[0] != 0xFF || bytes[1] != 0xD8) return false; // a JPEG, not a page
        Directory.CreateDirectory(_dir);
        var file = ArtPath(app);
        if (IsLink(_dir) || IsLink(file) || token.IsCancellationRequested) return false;
        await File.WriteAllBytesAsync(file, bytes, CancellationToken.None);
        return true;
    }

    /// <summary>A link (symbolic link, junction): never read, written or served. Unreadable counts as one (SteamLibrary, #684).</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            // a link that points nowhere is still a link: writing through it would create its target
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is not null) return true;
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
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

    // words of a subtitle that names something added to a game, not the game ("Episodes from Liberty City", "Season Pass")
    private static readonly HashSet<string> AddOnWords = ["episode", "episodes", "dlc", "expansion", "pack", "soundtrack", "ost",
        "season", "pass", "bundle", "demo", "chapter", "chapters", "addon", "add", "on", "upgrade", "content", "costume", "skin",
        "skins", "artbook", "prologue", "playtest", "server", "tool", "tools", "sdk", "editor", "benchmark", "trial"];

    /// <summary>
    /// Whether a store result is the game named <paramref name="name"/>: the same words, in any order, once edition words
    /// are set aside on both sides ("Grand Theft Auto IV: The Complete Edition"), or once the result's subtitle after a
    /// colon or dash is set aside ("The Witcher 3: Wild Hunt" for "Witcher 3") when that subtitle does not name an add-on
    /// ("Grand Theft Auto IV: Episodes from Liberty City" is not GTA IV). So FIFA 23 is not FIFA 22, Portal is not Portal 2
    /// and not Portal Knights.
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
        if (cut <= 0 || !want.SetEquals(Core(result[..cut]))) return 0;
        return Words(result[cut..]).Any(AddOnWords.Contains) ? 0 : 1;
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

/// <summary>The Steam store asked to slow down or could not answer: its status and how long it asked to wait, if it said.</summary>
internal sealed class StoreBusyException(int status, TimeSpan? retryAfter) : Exception($"the store answered {status}")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
