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
/// the Steam store" option is turned on.
/// </summary>
public sealed class GameCovers : IDisposable
{
    internal const string SearchUrl = "https://store.steampowered.com/api/storesearch/";
    internal const string ArtUrl = "https://shared.steamstatic.com/store_item_assets/steam/apps/{0}/library_600x900.jpg";
    internal const long MaxArtBytes = 4 << 20;
    internal static readonly TimeSpan RetryMissing = TimeSpan.FromDays(7);
    internal const int PauseMs = 1500;

    private readonly ILogger<GameCovers> _log;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _pauseMs;
    private readonly Lock _gate = new();
    private Dictionary<string, Entry> _index;
    private readonly Queue<(string Key, string[] Names)> _queue = new();
    private readonly HashSet<string> _queued = [];
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
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_index);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, IndexPath, true);
        }
        catch (Exception ex) { _log.LogDebug("could not save the game covers index: {Msg}", ex.Message); }
    }

    /// <summary>The cover kept for a game (its key), or null.</summary>
    public string? CoverFile(string key)
    {
        lock (_gate)
            if (!_index.TryGetValue(key, out var e) || e.App <= 0) return null;
        var file = ArtPath(_index[key].App);
        return File.Exists(file) ? file : null;
    }

    private string ArtPath(long app) => Path.Combine(_dir, app.ToString(CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>Looks up, in the background, the games not looked up yet (or not found a week ago).</summary>
    public void Want(IEnumerable<(string Key, string[] Names)> games)
    {
        lock (_gate)
        {
            var now = _now().ToUnixTimeSeconds();
            foreach (var (key, names) in games)
            {
                if (names.Length == 0 || _queued.Contains(key)) continue;
                if (_index.TryGetValue(key, out var e) && (e.App > 0 ? File.Exists(ArtPath(e.App)) : now - e.At < RetryMissing.TotalSeconds)) continue;
                _queue.Enqueue((key, names));
                _queued.Add(key);
            }
            if (_queue.Count > 0 && _worker is null) _worker = Task.Run(Work);
        }
    }

    /// <summary>Waits for the lookups under way (tests).</summary>
    internal Task Idle() { lock (_gate) return _worker ?? Task.CompletedTask; }

    private async Task Work()
    {
        while (true)
        {
            (string Key, string[] Names) next;
            lock (_gate)
            {
                if (_queue.Count == 0) { _worker = null; return; }
                next = _queue.Dequeue();
            }
            long app = 0;
            var failed = false;
            try { app = await LookUp(next.Names); }
            catch (Exception ex) { failed = true; _log.LogInformation("game cover lookup failed: {Msg}", ex.Message); }
            lock (_gate)
            {
                _queued.Remove(next.Key);
                // a failure (no network, the store busy) is not "not found": the game is asked again next time
                if (!failed) _index[next.Key] = new Entry(app, _now().ToUnixTimeSeconds());
                if (failed) { _queue.Clear(); _queued.Clear(); }
            }
            if (!failed) { SaveIndex(); _log.LogDebug("game cover: {Names} -> {App}", string.Join(" | ", next.Names), app); }
            if (_pauseMs > 0) await Task.Delay(_pauseMs);
        }
    }

    /// <summary>The store app whose name matches one of <paramref name="names"/>, with its art saved; 0 when none does.</summary>
    private async Task<long> LookUp(string[] names)
    {
        foreach (var name in names)
        {
            var url = $"{SearchUrl}?term={Uri.EscapeDataString(name)}&cc=us&l=en";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
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
        using var res = await _http.GetAsync(string.Format(CultureInfo.InvariantCulture, ArtUrl, app));
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType != "image/jpeg") return false;
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length is < 1024 or > (int)MaxArtBytes || bytes[0] != 0xFF || bytes[1] != 0xD8) return false; // a JPEG, not a page
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(ArtPath(app), bytes);
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
