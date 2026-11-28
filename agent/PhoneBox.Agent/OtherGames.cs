using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A game found outside Steam, as the phone sees it: an id ("x:" and a hash of its program), name and source.</summary>
public sealed record OtherGame(string Id, string Name, string Source, bool Cover);

/// <summary>
/// Games installed outside Steam, shown by the game library widget next to Steam's: found on their own, with nothing to
/// set up, no account and no network. Read from what the launchers leave on this computer (the Epic Games Launcher's
/// manifests; the registry keys of the EA app, GOG Galaxy, Ubisoft Connect and the Rockstar Games Launcher), and from the
/// drives, for the games no launcher lists (a portable copy, a mod, an old disc install), told from other programs by what
/// only games carry (<see cref="LooksLikeAGame"/>). A game's program is chosen from its folder (<see cref="PickExe"/>);
/// installers, uninstallers, crash reporters and redistributables are never chosen. Nothing is read while no library
/// widget is on an enabled page. Windows only: on macOS and Linux nothing is found.
/// </summary>
public sealed class OtherGames(ConfigStore config, ILogger<OtherGames> log)
{
    /// <summary>A found game and how it starts: a launcher's own address (Epic, Ubisoft), or its program in its folder.</summary>
    internal sealed record Found(string Id, string Name, string Source, string Dir, string? Exe, string? Url);

    internal const int RescanMs = 10 * 60_000;
    // bounds for a folder that is not what it seems
    internal const int MaxGamesPerFolder = 200, MaxFilesPerGame = 2000, MaxDepth = 3;

    private readonly Lock _gate = new();
    private List<Found> _found = [];
    private long _scannedAt = -(1L << 40);

    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    /// <summary>Overridable for tests: the launchers' games (Windows registry and Epic manifests).</summary>
    internal Func<IEnumerable<Found>> Launchers { get; init; } = DefaultLaunchers;
    /// <summary>Overridable for tests: hands the program or address to the system.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; init; } = psi => { using var p = Process.Start(psi); };

    /// <summary>A game library widget is on an enabled page.</summary>
    private bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library");

    /// <summary>The asking widget may start games (its own option; see SteamLibrary.LaunchAllowed).</summary>
    public bool LaunchAllowed(string? deviceId, string? widget) =>
        PanelWidgets.Find(config.Get(), deviceId, widget, "steam-library") is { } w && w.Bool("allowLaunch") == true;

    /// <summary>The games found, by name; empty while no library widget asks for them.</summary>
    public IReadOnlyList<OtherGame> List()
    {
        var games = Games();
        if (games.Count == 0) return [];
        var c = config.Get();
        // a game already shown as one of your own (a shortcut ticked in "Other games" that starts a program in its folder)
        // is shown once, as yours: your shortcut may start it another way (a mod's launcher, a different program)
        var ticked = c.Widgets(enabledPagesOnly: true).Where(w => w.Type == "steam-library")
            .SelectMany(w => w.Options?.TryGetValue("games", out var v) == true && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : [])
            .ToHashSet(StringComparer.Ordinal);
        var mine = c.Shortcuts.Where(s => ticked.Contains(s.Id) && ShortcutIcons.LooksLikeFilePath(s.Cmd)).Select(s => s.Cmd.Trim().Trim('"')).ToList();
        return Shown(games, mine).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OtherGame(g.Id, g.Name, g.Source, g.Exe is not null)).ToList();
    }

    /// <summary>The found games but those whose folder holds one of <paramref name="mine"/> (programs your own games start).</summary>
    internal static IEnumerable<Found> Shown(IEnumerable<Found> games, IReadOnlyList<string> mine) =>
        games.Where(g => !mine.Any(p => Norm(p).StartsWith(Norm(g.Dir), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The last list found. Looking through the drives takes a few seconds, so it runs in the background: a request gets
    /// the last list at once (the very first one waits up to <see cref="FirstWaitMs"/>), and a list older than
    /// <see cref="RescanMs"/> starts a new look.
    /// </summary>
    private List<Found> Games()
    {
        if (!OperatingSystem.IsWindows() && Launchers == DefaultLaunchers) return [];
        if (!Wanted()) return [];
        Task running;
        bool first;
        lock (_gate)
        {
            if (Clock() - _scannedAt < RescanMs) return _found;
            first = _scannedAt < 0;
            running = _scanning ??= Task.Run(Refresh);
        }
        running.Wait(first ? FirstWaitMs : 0);
        lock (_gate) return _found;
    }

    private void Refresh()
    {
        List<Found> found;
        try { found = Scan(Launchers(), Disks()); }
        catch (Exception ex) { log.LogWarning("could not look for other games: {Msg}", ex.Message); found = []; }
        lock (_gate) { _found = found; _scannedAt = Clock(); _scanning = null; }
    }

    private Task? _scanning;
    internal const int FirstWaitMs = 8000;
    /// <summary>Overridable for tests: the games found on the drives (<see cref="FromDrives"/>).</summary>
    internal Func<IEnumerable<Found>> Disks { get; init; } = () => OperatingSystem.IsWindows()
        ? FromDrives(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)) : [];

    /// <summary>The launchers' games, then the others; one game per folder (the first source wins, and a folder inside a
    /// game's folder is not another game), none from Steam's own libraries.</summary>
    internal static List<Found> Scan(IEnumerable<Found> launchers, IEnumerable<Found> others)
    {
        var dirs = new List<string>();
        var list = new List<Found>();
        foreach (var g in launchers.Concat(others))
        {
            var dir = Norm(g.Dir);
            if (dir.Length <= 1 || dir.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)
                || dirs.Any(d => d.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
            dirs.Add(dir);
            list.Add(g);
        }
        return list;
    }

    // ------------------------------------------------------------------ games on the drives, with no folder listed

    /// <summary>Folders that hold games rather than being one: looked into (up to three levels: Program Files › Ubisoft › Ubisoft Game Launcher › games).</summary>
    private static readonly HashSet<string> Holders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Games", "Jogos", "Juegos", "Jeux", "Spiele", "Giochi", "Gry", "Игры", "Program Files", "Program Files (x86)", "XboxGames",
        "Epic Games", "GOG Games", "GOG Galaxy", "Origin Games", "EA Games", "Electronic Arts", "Rockstar Games", "Ubisoft",
        "Ubisoft Game Launcher", "Riot Games", "Battle.net", "Blizzard Entertainment", "Bethesda.net Launcher", "Amazon Games",
    };

    /// <summary>Folders that are never a game: the system's, the launchers themselves, Steam (its games come from Steam), tools.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "Users", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "MSOCache", "Intel", "AMD",
        "NVIDIA", "NVIDIA Corporation", "Common Files", "WindowsApps", "Microsoft", "Windows Defender", "dotnet", "Steam", "SteamLibrary",
        "Launcher", "EA Desktop", "EA app", "Origin", "Social Club", "Epic Games Launcher", "Epic Online Services", "DirectXRedist",
        "GalaxyClient", "Battle.net Launcher", "cache", "data", "locales", "logs", "node_modules", "site-packages", "Python", "Git", "Java",
    };

    /// <summary>The games on these drives: every folder at a drive's top, or inside a <see cref="Holders"/> folder, that looks like a game (<see cref="LooksLikeAGame"/>).</summary>
    internal static IEnumerable<Found> FromDrives(IEnumerable<string> roots)
    {
        var candidates = new List<string>();
        void Look(string dir, int level)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var sub in subs.Take(MaxGamesPerFolder))
            {
                var name = Path.GetFileName(sub);
                // a package cache or a Python environment carries game libraries (pygame) without being a game
                if (NotGames.Contains(name) || name.StartsWith('.') || name.StartsWith('$') || name.Contains("cache", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("venv", StringComparison.OrdinalIgnoreCase) || name.Contains("conda", StringComparison.OrdinalIgnoreCase)) continue;
                // a launcher's own folder: only the games folder inside it (Ubisoft Game Launcher › games), not its parts
                if (LauncherHomes.Contains(name)) { if (level < 3 && Directory.Exists(Path.Combine(sub, "games"))) Look(Path.Combine(sub, "games"), level + 1); }
                else if (Holders.Contains(name) && level < 3) Look(sub, level + 1);
                else candidates.Add(sub);
            }
        }
        foreach (var root in roots) Look(root, 0);
        foreach (var dir in candidates)
            if (Game(dir) is { } g && LooksLikeAGame(dir, Path.GetDirectoryName(g.Exe!)!)) yield return g;
    }

    /// <summary>Launchers that keep their games in a "games" folder of their own, next to their parts.</summary>
    private static readonly HashSet<string> LauncherHomes = new(StringComparer.OrdinalIgnoreCase) { "Ubisoft Game Launcher" };

    // What only a game brings along: a game store's or engine's library, a game's archives, videos and mod loaders.
    private static readonly string[] SureFiles = ["steam_api.dll", "steam_api64.dll", "unityplayer.dll", "gameuxinstallhelper.dll", "amd_ags_x64.dll",
        "mss32.dll", "mss64.dll", "physxloader.dll", "xlive.dll", "eossdk-win64-shipping.dll", "eossdk-win32-shipping.dll"];
    private static readonly string[] SureStarts = ["bink", "uplay_r", "upc_r", "nvphysx", "galaxy64", "galaxy.dll"];
    private static readonly HashSet<string> SureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".rpf", ".bik", ".bk2", ".vpk", ".ucas", ".utoc", ".forge", ".bsa", ".ba2", ".ggpk", ".asi", ".toc", ".sb", ".wad", ".gcf", ".usm" };
    // also in other programs (or, for an anti-cheat, installed as a service of its own), so two are needed
    private static readonly string[] MaybeFiles = ["xinput1_3.dll", "d3dx9_43.dll", "d3dx9_42.dll", "openal32.dll", "fmod.dll", "fmod64.dll", "fmodex.dll", "fmodex64.dll", "fmodstudio.dll"];
    private static readonly string[] MaybeStarts = ["eaanticheat", "easyanticheat", "battleye"];

    /// <summary>
    /// Whether a folder holds a game whose program is in <paramref name="exeDir"/>: at least one sign only games carry (a
    /// game store's or engine's library, a game archive or video, a mod loader, a file ending in GDF.dll that registers it
    /// with Windows), or two weaker ones, in the program's folder or below it, so a copy of a game's files kept somewhere
    /// else in the folder does not count. A source tree (a .git folder) is not a game. Looked at most three folders down
    /// and a few thousand files in.
    /// </summary>
    internal static bool LooksLikeAGame(string dir, string exeDir)
    {
        int sure = 0, files = 0;
        var weak = new HashSet<string>();
        var under = Norm(exeDir);
        foreach (var file in Files(dir, 0))
        {
            if (++files > MaxFilesPerGame * 2) break;
            var path = file.Replace('/', '\\');
            if (path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)) return false;
            if (!path.StartsWith(under, StringComparison.OrdinalIgnoreCase)) continue;
            var n = path[(path.LastIndexOf('\\') + 1)..].ToLowerInvariant();
            if (SureFiles.Contains(n) || SureStarts.Any(s => n.StartsWith(s, StringComparison.Ordinal)) || n.EndsWith("gdf.dll", StringComparison.Ordinal)
                || SureExtensions.Contains(Path.GetExtension(n))) sure++;
            else if (MaybeFiles.Contains(n)) weak.Add(n);
            else if (MaybeStarts.FirstOrDefault(s => n.StartsWith(s, StringComparison.Ordinal)) is { } m) weak.Add(m);
        }
        return sure > 0 || weak.Count > 1;
    }

    private static IEnumerable<string> Files(string dir, int depth)
    {
        string[] files, dirs;
        try { files = Directory.GetFiles(dir); dirs = depth < MaxDepth ? Directory.GetDirectories(dir) : []; }
        catch { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.Equals("site-packages", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            // a source tree: its .git folder is reported (and ends the look) without reading it
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { yield return Path.Combine(d, "HEAD"); continue; }
            foreach (var f in Files(d, depth + 1)) yield return f;
        }
    }

    private static string Norm(string dir) => dir.Replace('/', '\\').TrimEnd('\\') + "\\";

    internal static string IdOf(string source, string where) =>
        "x:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + where.ToLowerInvariant())))[..16];

    private static Found? Game(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.StartsWith('.') || name.StartsWith('$') || name.StartsWith('_')) return null;
        return PickExe(dir, name) is { } exe ? new Found(IdOf("drive", exe), name, "drive", dir, exe, null) : null;
    }

    /// <summary>Programs that come with a game but are not it.</summary>
    internal static bool NotTheGame(string file)
    {
        file = file.Replace('/', '\\');
        var n = Path.GetFileNameWithoutExtension(file[(file.LastIndexOf('\\') + 1)..]).ToLowerInvariant();
        string[] words = ["unins", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "directx", "crash", "report",
            "helper", "updat", "patch", "prereq", "anticheat", "eac", "battleye", "beservice", "cleanup", "dotnet", "touchup",
            "cefprocess", "cef", "activation", "register", "uploader", "benchmark", "config", "settings", "server", "dedicated",
            "unity", "ue4", "ue5", "subprocess", "error", "debug", "tool", "editor", "7z", "unrar", "dxsetup", "physx", "oalinst", "nvidia",
            "browser", "encoder", "diagnos", "profiler", "viewer", "securom"];
        return words.Any(w => n.Contains(w, StringComparison.Ordinal)) || file.Contains(@"\_CommonRedist\", StringComparison.OrdinalIgnoreCase)
            || file.Contains(@"\Redist\", StringComparison.OrdinalIgnoreCase) || file.Contains(@"\__Installer\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The game's program in its folder: of the programs at most three folders down (not an installer or helper: a game's
    /// tools carry its name too, gta4Browser.exe next to GTAIV.exe), the one whose name shares the most with the folder's,
    /// then the largest, then the shallowest. Null when there is none.
    /// </summary>
    internal static string? PickExe(string dir, string name)
    {
        var want = Words(name);
        (string File, double Score)? best = null;
        var count = 0;
        foreach (var (file, depth) in Programs(dir, 0))
        {
            if (++count > MaxFilesPerGame) break;
            if (NotTheGame(file)) continue;
            long size;
            try { size = new FileInfo(file).Length; } catch { continue; }
            var have = Words(Path.GetFileNameWithoutExtension(file));
            var shared = want.Count == 0 ? 0 : (double)want.Count(have.Contains) / want.Count;
            var joined = string.Concat(want);
            if (joined.Length > 2 && string.Concat(have).Contains(joined, StringComparison.Ordinal)) shared = Math.Max(shared, 1);
            var score = shared * 10 + Math.Log10(Math.Max(size, 1)) - depth * 0.5;
            if (best is null || score > best.Value.Score) best = (file, score);
        }
        return best?.File;
    }

    private static IEnumerable<(string File, int Depth)> Programs(string dir, int depth)
    {
        string[] files, dirs;
        try { files = Directory.GetFiles(dir, "*.exe"); dirs = depth < MaxDepth ? Directory.GetDirectories(dir) : []; }
        catch { yield break; }
        foreach (var f in files) yield return (f, depth);
        foreach (var d in dirs)
            foreach (var x in Programs(d, depth + 1)) yield return x;
    }

    /// <summary>A name's words in lower case, with the digits split off ("GTAIV" stays one word, "FIFA23" gives fifa and 23).</summary>
    internal static List<string> Words(string name)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); } }
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetter(c)) { if (sb.Length > 0 && char.IsDigit(sb[^1])) Flush(); sb.Append(c); }
            else if (char.IsDigit(c)) { if (sb.Length > 0 && !char.IsDigit(sb[^1])) Flush(); sb.Append(c); }
            else Flush();
        }
        Flush();
        return words;
    }

    /// <summary>The Epic Games Launcher's manifests (one JSON .item per installed game).</summary>
    internal static IEnumerable<Found> FromEpic(string manifests)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(manifests, "*.item").ToList(); } catch { yield break; }
        foreach (var f in files)
        {
            Found? g = null;
            try
            {
                if (new FileInfo(f).Length > 1 << 20) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var cats = r.TryGetProperty("AppCategories", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                if (!cats.Contains("games") || S("DisplayName") is not { Length: > 0 } name || S("InstallLocation") is not { Length: > 0 } dir) continue;
                var exe = S("LaunchExecutable") is { Length: > 0 } le ? Path.Combine(dir, le) : null;
                string? url = null;
                if (S("CatalogNamespace") is { } ns && S("CatalogItemId") is { } item && S("AppName") is { } app
                    && new[] { ns, item, app }.All(x => x.Length > 0 && x.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
                    url = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true";
                if (exe is null && url is null) continue;
                g = new Found(IdOf("epic", dir), name, "epic", dir, exe, url);
            }
            catch { }
            if (g is not null) yield return g;
        }
    }

    /// <summary>One registry key of a launcher: its name and a reader of its values.</summary>
    internal sealed record Key(string Name, Func<string, string?> Value);

    /// <summary>GOG Galaxy, Ubisoft Connect, the EA app and Rockstar from their keys (see <see cref="DefaultLaunchers"/>).</summary>
    internal static IEnumerable<Found> FromKeys(string source, IEnumerable<Key> keys)
    {
        foreach (var k in keys)
        {
            switch (source)
            {
                case "gog":
                    if (k.Value("path") is { Length: > 0 } gp && k.Value("gameName") is { Length: > 0 } gn)
                    {
                        var exe = k.Value("exe") is { Length: > 0 } ge && File.Exists(ge) ? ge : PickExe(gp, gn);
                        if (exe is not null) yield return new Found(IdOf("gog", gp), gn, "gog", gp, exe, null);
                    }
                    break;
                case "ubisoft":
                    // the Installs key holds only the folder; Ubisoft Connect starts the game by its number
                    if (k.Value("InstallDir") is { Length: > 0 } ud && k.Name.All(char.IsAsciiDigit) && Directory.Exists(ud))
                        yield return new Found(IdOf("ubisoft", ud), Path.GetFileName(ud.TrimEnd('/', '\\')), "ubisoft", ud, PickExe(ud, Path.GetFileName(ud.TrimEnd('/', '\\'))), $"uplay://launch/{k.Name}/0");
                    break;
                case "ea":
                case "rockstar":
                    var dir = k.Value("Install Dir") ?? k.Value("InstallFolder") ?? k.Value("InstallDir");
                    if (dir is { Length: > 0 } && Directory.Exists(dir) && !k.Name.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                        && !k.Name.Contains("Social Club", StringComparison.OrdinalIgnoreCase) && PickExe(dir, k.Name) is { } x)
                        yield return new Found(IdOf(source, dir), k.Name, source, dir, x, null);
                    break;
            }
        }
    }

    private static IEnumerable<Found> DefaultLaunchers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return FromEpic(Path.Combine(data, "Epic", "EpicGamesLauncher", "Data", "Manifests"))
            .Concat(FromKeys("ea", Keys(@"SOFTWARE\WOW6432Node\EA Games")))
            .Concat(FromKeys("gog", Keys(@"SOFTWARE\WOW6432Node\GOG.com\Games")))
            .Concat(FromKeys("ubisoft", Keys(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs")))
            .Concat(FromKeys("rockstar", Keys(@"SOFTWARE\WOW6432Node\Rockstar Games")));
    }

    private static List<Key> Keys(string path)
    {
        var list = new List<Key>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k is null) continue;
                var values = k.GetValueNames().ToDictionary(v => v, v => k.GetValue(v)?.ToString(), StringComparer.OrdinalIgnoreCase);
                list.Add(new Key(name, v => values.TryGetValue(v, out var s) ? s : null));
            }
        }
        catch { }
        return list;
    }

    /// <summary>The icon of a found game's program, as a PNG, or null.</summary>
    public byte[]? Cover(string? id)
    {
        if (id is null || !id.StartsWith("x:", StringComparison.Ordinal)) return null;
        var g = Games().FirstOrDefault(x => x.Id == id);
        if (g?.Exe is null) return null;
        return ShortcutIcons.Icon(new Shortcut { Id = id, Cmd = g.Exe });
    }

    /// <summary>
    /// Starts a found game for a widget that may start games (checked by the caller): by its launcher's address, or its
    /// program in its folder. Only a game of the list read right now can start, so no text from the request is run.
    /// </summary>
    public SteamLibrary.LaunchError? Launch(string? id)
    {
        var g = Games().FirstOrDefault(x => x.Id == id);
        // the list is a few minutes old at most: a game removed since is not started
        if (g is null || (g.Exe is not null && g.Url is null && !File.Exists(g.Exe))) return new("notinstalled", "That game is not installed");
        try
        {
            StartProcess(g.Url is not null
                ? new ProcessStartInfo { FileName = g.Url, UseShellExecute = true }
                : new ProcessStartInfo { FileName = g.Exe!, WorkingDirectory = Path.GetDirectoryName(g.Exe!)!, UseShellExecute = true });
            log.LogInformation("game started from the phone: {Name} ({Source})", g.Name, g.Source);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("could not start {Name}: {Msg}", g.Name, ex.Message);
            return new("failed", "The game could not be started");
        }
    }
}
