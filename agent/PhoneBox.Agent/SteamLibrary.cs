using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>A node of Valve's KeyValues text format (.vdf, .acf): a string value, or a block of named children.</summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VdfNode? this[string key] => Children.TryGetValue(key, out var n) ? n : null;
    public string? Str(string key) => this[key]?.Value;
    public long Long(string key) => long.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// Reads KeyValues text: <c>"key" "value"</c> pairs and <c>"key" { … }</c> blocks, <c>//</c> comments, <c>\\</c> and
    /// <c>\"</c> escapes inside quotes. Platform conditions such as <c>[$WIN32]</c> are skipped. Keys are case-insensitive.
    /// </summary>
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? key = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                i++;
                var block = new VdfNode();
                if (key is not null) { stack.Peek().Children[key] = block; key = null; }
                stack.Push(block);
                continue;
            }
            if (c == '}')
            {
                i++;
                if (stack.Count > 1) stack.Pop();
                key = null;
                continue;
            }
            string token;
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1] switch { 'n' => '\n', 't' => '\t', var other => other });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i++]);
                }
                i++; // the closing quote
                token = sb.ToString();
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
                token = text[start..i];
                if (token.StartsWith('[') && token.EndsWith(']')) continue; // a platform condition, not a key or value
            }
            if (key is null) key = token;
            else
            {
                stack.Peek().Children[key] = new VdfNode { Value = token };
                key = null;
            }
        }
        return root;
    }
}

/// <summary>An installed Steam game, as the Steam library widget shows it.</summary>
/// <param name="State">ready, update (waiting for an update), updating (downloading or installing) or paused.</param>
/// <param name="Progress">0–1 while an update or download is known to be under way, else null.</param>
/// <param name="LastPlayed">Unix seconds; 0 = never.</param>
public sealed record SteamGame(string Id, string Name, long LastPlayed, double SizeGb, string State, double? Progress, bool Cover);

/// <summary>An installed game in the settings page's favorites picker.</summary>
public sealed record SteamPick(string Id, string Name);

/// <param name="Enabled">False while no Steam library widget is on an enabled page: then nothing is read.</param>
/// <param name="Running">The app id Steam runs now (RunningAppID), or null.</param>
/// <param name="CanLaunch">A Steam library widget on an enabled page has "Let the phone start games" on.</param>
public sealed record SteamLibraryView(bool Enabled, bool Found, string? Error, string? Running, IReadOnlyList<SteamGame> Games, bool CanLaunch);

/// <summary>
/// The Steam library on this computer, read from Steam's own files: no Steam Web API key, no account and no internet.
/// Installed games come from the appmanifest_*.acf files of every library folder (libraryfolders.vdf), when they were
/// last played from the manifests (or localconfig.vdf), covers from Steam's library cache, and the game being played from
/// Steam's RunningAppID (the registry on Windows, registry.vdf elsewhere).
/// <para>
/// Files are read only while a Steam library widget is on an enabled page, at most every <see cref="MinScanMs"/>, and a
/// file is parsed again only when its modification time or size changed. Every read has a size cap.
/// </para>
/// <para>
/// Starting a game is off by default (the widget's "Let the phone start games"). The phone sends an app id; the agent
/// starts only an installed game from the list it reads right then, and hands Steam a URL built from that list's own
/// numeric id, never from the request.
/// </para>
/// </summary>
public sealed class SteamLibrary(ConfigStore config, ILogger<SteamLibrary> log)
{
    /// <summary>Steam tools, runtimes and redistributables: installed like games, but nothing to play.</summary>
    private static readonly HashSet<string> Tools = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100", "2348590", "250820", "1826330"];

    /// <summary>The widget asks every 5 s while an update runs and every 30 s otherwise; several phones share one read.</summary>
    internal const int MinScanMs = 2000;
    // Size caps: a manifest or libraryfolders.vdf is a few KB, localconfig.vdf a few MB on a big account, a cover ~100 KB.
    internal const long MaxManifestBytes = 1 << 20, MaxLocalConfigBytes = 32L << 20, MaxRegistryBytes = 4 << 20, MaxCoverBytes = 4 << 20;
    private static readonly string[] CoverNames = ["library_600x900.jpg", "library_600x900_2x.jpg", "library_capsule.jpg", "header.jpg"];

    private readonly Lock _gate = new();
    private string? _root;
    private long _rootAt = Never;
    private const long Never = -(1L << 40); // far enough back that "now minus it" cannot overflow
    private List<SteamGame> _games = [];
    private long _scannedAt = Never;
    private string? _error;
    private readonly Dictionary<string, (DateTime Stamp, long Length, SteamGame? Game)> _manifests = new(PathComparer);
    private (string Path, DateTime Stamp, long Length, List<string> Folders)? _folders;
    private (string Path, DateTime Stamp, long Length, Dictionary<string, long> Played)? _localConfig;
    private readonly Dictionary<string, bool> _covers = [];
    private DateTime _coverStamp;
    private long _coversAt = Never;
    private (string Path, DateTime Stamp, long Length, long Id)? _registryVdf;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Overridable for tests: the folder Steam is installed in, or null.</summary>
    internal Func<string?> FindRoot { get; init; } = () => DefaultRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    /// <summary>Overridable for tests: Steam's RunningAppID (0 = none).</summary>
    internal Func<string?, long>? RunningAppId { get; init; }
    /// <summary>Overridable for tests: the clock (ms).</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    /// <summary>Overridable for tests: hands the steam:// URL to the system (tests record it instead of starting Steam).</summary>
    internal Action<ProcessStartInfo> StartProcess { get; init; } = psi => { using var p = Process.Start(psi); };
    /// <summary>How many manifests were parsed (tests: an unchanged file is not read again).</summary>
    internal int ManifestReads { get; private set; }

    public bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library");

    /// <summary>Starting games is opt-in: a Steam library widget on an enabled page with "Let the phone start games" on.</summary>
    public bool LaunchAllowed() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library" && w.Bool("allowLaunch") == true);

    /// <summary>The library for the phone: installed games, the one being played, and whether the phone may start one.</summary>
    public SteamLibraryView View()
    {
        if (!Wanted()) return new SteamLibraryView(false, false, null, null, [], false);
        var root = Root();
        if (root is null) return new SteamLibraryView(true, false, "Steam was not found on this computer", null, [], false);
        var games = Games(force: false);
        string? error;
        lock (_gate) error = _error;
        var running = (RunningAppId ?? ReadRunningAppId)(root);
        var runningId = running > 0 ? running.ToString(CultureInfo.InvariantCulture) : null;
        // most recently played first, then by name; never played games last
        var sorted = games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new SteamLibraryView(true, true, error, runningId, sorted, LaunchAllowed());
    }

    /// <summary>
    /// The installed games by name, for the favorites picker in the settings page on this computer (not reachable over
    /// Wi-Fi: <c>/api/steam/games</c> is not in <see cref="Lan"/>). Read when the widget's editor opens, not on a timer.
    /// </summary>
    public IReadOnlyList<SteamPick> Installed() =>
        Root() is null ? [] : Games(force: false).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new SteamPick(g.Id, g.Name)).ToList();

    /// <summary>
    /// The cover of an installed game, as a file under Steam's appcache/librarycache that belongs to that app id, or null.
    /// The id must be a number of an installed game; the file must resolve inside the cache, with no link on the way out.
    /// </summary>
    public string? CoverPath(string? id)
    {
        if (!IsAppId(id) || !Wanted()) return null;
        var root = Root();
        if (root is null || !Games(force: false).Any(g => g.Id == id)) return null;
        return FindCover(root, id!);
    }

    /// <summary>A refused start: a code the phone translates (off, nosteam, notinstalled, failed) and an English text.</summary>
    public sealed record LaunchError(string Code, string Error);

    /// <summary>Starts an installed game through Steam; returns why it did not, or null when Steam was asked to start it.</summary>
    public LaunchError? Launch(string? id)
    {
        if (!IsAppId(id)) return new("notinstalled", "That game is not installed");
        if (!LaunchAllowed()) return new("off", "Turn on \"Let the phone start games\" in a Steam library widget on an enabled page");
        if (Root() is null) return new("nosteam", "Steam was not found on this computer");
        // the installed list read right now (unchanged files come from the cache), not a list the phone saw a while ago
        var game = Games(force: true).FirstOrDefault(g => g.Id == id);
        if (game is null) return new("notinstalled", "That game is not installed");
        // the URL is built from the manifest's own number, so no text from the request reaches the URL handler
        var url = "steam://rungameid/" + long.Parse(game.Id, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows()) psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
            else
            {
                psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(url);
            }
            StartProcess(psi);
            log.LogInformation("steam game started from the phone: {Id} {Name}", game.Id, game.Name);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("could not start steam game {Id}: {Msg}", game.Id, ex.Message);
            return new("failed", "Steam could not be started");
        }
    }

    internal static bool IsAppId(string? id) => id is { Length: > 0 and <= 10 } && id.All(char.IsAsciiDigit);

    private string? Root()
    {
        lock (_gate)
        {
            var now = Clock();
            if (now - _rootAt < 60_000) return _root;
            _rootAt = now;
            try { _root = FindRoot(); } catch { _root = null; }
            return _root;
        }
    }

    /// <summary>The installed games, read again at most every <see cref="MinScanMs"/> unless <paramref name="force"/>.</summary>
    private List<SteamGame> Games(bool force)
    {
        var root = Root();
        lock (_gate)
        {
            var now = Clock();
            if (!force && now - _scannedAt < MinScanMs) return _games;
            _scannedAt = now;
            if (root is null) { _games = []; _error = null; return _games; }
            try
            {
                _games = Scan(root);
                _error = null;
            }
            catch (Exception ex)
            {
                log.LogDebug("steam library: {Msg}", ex.Message);
                _error = "The Steam library could not be read";
            }
            return _games;
        }
    }

    /// <summary>Every library folder's manifests; a file whose time and size did not change is not parsed again.</summary>
    private List<SteamGame> Scan(string root)
    {
        var played = LastPlayedFromLocalConfig(root);
        var games = new Dictionary<string, SteamGame>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var folder in Folders(root))
        {
            var apps = Path.Combine(folder, "steamapps");
            if (!Directory.Exists(apps)) continue;
            List<string> files;
            try { files = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); }
            catch { continue; }
            foreach (var file in files)
            {
                seen.Add(file);
                SteamGame? game;
                try
                {
                    var info = new FileInfo(file);
                    if (_manifests.TryGetValue(file, out var known) && known.Stamp == info.LastWriteTimeUtc && known.Length == info.Length) game = known.Game;
                    else
                    {
                        game = null;
                        if (info.Length <= MaxManifestBytes)
                        {
                            game = ReadManifest(File.ReadAllText(file));
                            ManifestReads++;
                        }
                        _manifests[file] = (info.LastWriteTimeUtc, info.Length, game);
                    }
                }
                catch { continue; }
                if (game is null) continue;
                if (played.TryGetValue(game.Id, out var last) && last > game.LastPlayed) game = game with { LastPlayed = last };
                game = game with { Cover = HasCover(root, game.Id) };
                // a game in two libraries (a copy left behind): keep the one that is ready to play
                if (!games.TryGetValue(game.Id, out var other) || (other.State != "ready" && game.State == "ready")) games[game.Id] = game;
            }
        }
        foreach (var gone in _manifests.Keys.Where(k => !seen.Contains(k)).ToList()) _manifests.Remove(gone);
        return [.. games.Values];
    }

    /// <summary>
    /// Whether a game has a cover, looked up again after Steam's cache folder changed (a new game's folder appears in it)
    /// or after five minutes (a cover added inside a game's own folder does not change the cache folder's time).
    /// </summary>
    private bool HasCover(string root, string id)
    {
        DateTime stamp;
        try { stamp = Directory.GetLastWriteTimeUtc(Path.Combine(root, "appcache", "librarycache")); }
        catch { stamp = DateTime.MinValue; }
        var now = Clock();
        if (stamp != _coverStamp || now - _coversAt > 300_000) { _covers.Clear(); _coverStamp = stamp; _coversAt = now; }
        if (!_covers.TryGetValue(id, out var has)) _covers[id] = has = FindCover(root, id) is not null;
        return has;
    }

    private List<string> Folders(string root)
    {
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) file = Path.Combine(root, "config", "libraryfolders.vdf");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return [root];
            if (_folders is { } f && f.Path == file && f.Stamp == info.LastWriteTimeUtc && f.Length == info.Length) return f.Folders;
            var folders = info.Length <= MaxManifestBytes ? LibraryFolders(root, File.ReadAllText(file)) : [root];
            _folders = (file, info.LastWriteTimeUtc, info.Length, folders);
            return folders;
        }
        catch { return [root]; }
    }

    private Dictionary<string, long> LastPlayedFromLocalConfig(string root)
    {
        var file = LocalConfigPath(root);
        if (file is null) return [];
        try
        {
            var info = new FileInfo(file);
            if (_localConfig is { } c && c.Path == file && c.Stamp == info.LastWriteTimeUtc && c.Length == info.Length) return c.Played;
            var played = info.Length <= MaxLocalConfigBytes ? LastPlayed(VdfNode.Parse(File.ReadAllText(file))) : [];
            _localConfig = (file, info.LastWriteTimeUtc, info.Length, played);
            return played;
        }
        catch { return []; }
    }

    // ---------------------------------------------------------------- reading Steam's files (static: the tests use them)

    /// <summary>Where Steam is installed: the registry on Windows, the usual folders on Linux (also Flatpak and Snap) and macOS.</summary>
    internal static string? DefaultRoot(string home)
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && path.Length > 0) candidates.Add(path.Replace('/', '\\'));
            }
            catch { }
            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
                if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } pf) candidates.Add(Path.Combine(pf, "Steam"));
        }
        else if (OperatingSystem.IsMacOS()) candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        else
        {
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        }
        return candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "steamapps")));
    }

    /// <summary>Every library folder: Steam's own folder first, then the ones libraryfolders.vdf lists (old and new format).</summary>
    internal static List<string> LibraryFolders(string root, string libraryFoldersVdf)
    {
        var folders = new List<string> { root };
        var list = VdfNode.Parse(libraryFoldersVdf)["libraryfolders"];
        if (list is not null)
        {
            foreach (var (key, node) in list.Children)
            {
                // new format: "0" { "path" "D:\\SteamLibrary" … }; old format: "1" "D:\\SteamLibrary"
                if (!key.All(char.IsAsciiDigit)) continue;
                var path = node.Value ?? node.Str("path");
                if (!string.IsNullOrWhiteSpace(path)) folders.Add(path);
            }
        }
        return folders.Select(f => f.TrimEnd('/', '\\')).Distinct(PathComparer).ToList();
    }

    /// <summary>One appmanifest_*.acf, or null for a tool or runtime that is not a game.</summary>
    internal static SteamGame? ReadManifest(string text)
    {
        var app = VdfNode.Parse(text)["AppState"];
        var id = app?.Str("appid");
        if (app is null || !IsAppId(id) || Tools.Contains(id!)) return null;
        var name = app.Str("name");
        if (string.IsNullOrWhiteSpace(name)) name = $"App {id}";
        if (name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase)) return null;
        var (state, progress) = StateOf(app.Long("StateFlags"), app.Long("BytesToDownload"), app.Long("BytesDownloaded"));
        return new SteamGame(id!, name, app.Long("LastPlayed"), Math.Round(app.Long("SizeOnDisk") / 1e9, 1), state, progress, false);
    }

    /// <summary>A manifest's StateFlags as the widget shows them, with the download's progress when Steam reports one.</summary>
    internal static (string State, double? Progress) StateOf(long flags, long toDownload, long downloaded)
    {
        const long UpdateRequired = 2, FullyInstalled = 4, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024,
            Validating = 16384, AddingFiles = 32768, Preallocating = 65536, Downloading = 131072, Staging = 262144, Committing = 524288;
        double? progress = toDownload > 0 ? Math.Round(Math.Clamp((double)downloaded / toDownload, 0, 1), 3) : null;
        if ((flags & UpdatePaused) != 0) return ("paused", progress);
        if ((flags & (UpdateRunning | UpdateStarted | Validating | AddingFiles | Preallocating | Downloading | Staging | Committing)) != 0)
            return ("updating", progress);
        if ((flags & UpdateRequired) != 0 || (flags & FullyInstalled) == 0) return ("update", progress);
        return ("ready", null);
    }

    /// <summary>The newest localconfig.vdf of the accounts that signed in to Steam on this computer.</summary>
    internal static string? LocalConfigPath(string root)
    {
        var users = Path.Combine(root, "userdata");
        if (!Directory.Exists(users)) return null;
        try
        {
            return Directory.EnumerateDirectories(users)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(f => File.Exists(f))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>LastPlayed (Unix seconds) per app id from localconfig.vdf.</summary>
    internal static Dictionary<string, long> LastPlayed(VdfNode localConfig)
    {
        var apps = localConfig["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"];
        var result = new Dictionary<string, long>();
        if (apps is null) return result;
        foreach (var (id, node) in apps.Children)
            if (IsAppId(id) && node.Long("LastPlayed") is > 0 and var t) result[id] = t;
        return result;
    }

    /// <summary>
    /// A game's cover in Steam's library cache: the portrait capsule when there is one, else the header. Only files under
    /// appcache/librarycache that belong to this app id: <c>&lt;id&gt;/…/name</c> (current Steam, sometimes in a sub-folder
    /// named by a hash) or <c>&lt;id&gt;_name</c> (older Steam). Links are not followed, and the result is checked to resolve
    /// inside the cache with no link on the way; a file over <see cref="MaxCoverBytes"/> is skipped.
    /// </summary>
    internal static string? FindCover(string root, string appId)
    {
        if (!IsAppId(appId)) return null;
        string cache;
        try { cache = Path.GetFullPath(Path.Combine(root, "appcache", "librarycache")); }
        catch { return null; }
        if (!Directory.Exists(cache)) return null;
        var dir = Path.Combine(cache, appId);
        if (Directory.Exists(dir) && !IsLink(dir))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive,
            };
            foreach (var name in CoverNames)
            {
                var direct = Path.Combine(dir, name);
                if (SafeCover(cache, direct)) return direct;
                try
                {
                    foreach (var nested in Directory.EnumerateFiles(dir, name, options))
                        if (SafeCover(cache, nested)) return nested;
                }
                catch { }
            }
        }
        foreach (var name in CoverNames)
        {
            var flat = Path.Combine(cache, $"{appId}_{name}");
            if (SafeCover(cache, flat)) return flat;
        }
        return null;
    }

    /// <summary>
    /// The file exists, is small enough, and resolves inside <paramref name="cache"/> without a symbolic link or junction
    /// on the way (the file itself and every folder between it and the cache).
    /// </summary>
    internal static bool SafeCover(string cache, string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var prefix = cache.EndsWith(Path.DirectorySeparatorChar) ? cache : cache + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison)) return false;
            var info = new FileInfo(full);
            if (!info.Exists || info.Length == 0 || info.Length > MaxCoverBytes || IsLink(full)) return false;
            for (var d = info.Directory; d is not null && d.FullName.Length >= prefix.Length; d = d.Parent)
                if (IsLink(d.FullName)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>A symbolic link or a junction (both are reparse points; a symbolic link also has a target).</summary>
    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
        }
        catch { return true; } // unreadable: treat as unsafe
    }

    /// <summary>Steam's RunningAppID: the registry on Windows, registry.vdf on Linux and macOS; 0 when no game runs.</summary>
    internal long ReadRunningAppId(string? root)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("RunningAppID") is int id ? id : 0;
            }
            catch { return 0; }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var file in new[] { Path.Combine(home, ".steam", "registry.vdf"), root is null ? "" : Path.Combine(root, "registry.vdf") })
        {
            if (file.Length == 0) continue;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                lock (_gate)
                {
                    if (_registryVdf is { } r && r.Path == file && r.Stamp == info.LastWriteTimeUtc && r.Length == info.Length) return r.Id;
                    var id = info.Length <= MaxRegistryBytes ? RunningAppIdFromRegistryVdf(File.ReadAllText(file)) : 0;
                    _registryVdf = (file, info.LastWriteTimeUtc, info.Length, id);
                    return id;
                }
            }
            catch { }
        }
        return 0;
    }

    internal static long RunningAppIdFromRegistryVdf(string text) =>
        VdfNode.Parse(text)["Registry"]?["HKCU"]?["Software"]?["Valve"]?["Steam"]?.Long("RunningAppID") ?? 0;
}
