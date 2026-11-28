using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// A game found outside Steam, as the phone sees it: an id ("x:" and a hash of its folder), name and source, and the ids
/// of your shortcuts that start a program in its folder (a widget that shows one of them as its own game leaves this one
/// out). No path reaches the phone. <c>Cover</c>: a picture can be asked for; <c>Art</c>: it is the game's store art
/// (portrait), not its program's icon.
/// </summary>
public sealed record OtherGame(string Id, string Name, string Source, bool Cover, IReadOnlyList<string> Shortcuts, bool Art = false);

/// <summary>
/// Games installed outside Steam, shown by the game library widget next to Steam's: found on their own, with nothing to
/// set up, no account and no network. Read from what the launchers leave on this computer (the Epic Games Launcher's
/// manifests; the registry keys of the EA app, GOG Galaxy, Ubisoft Connect and the Rockstar Games Launcher), and from the
/// drives, for the games no launcher lists (a portable copy, a mod, an old disc install), told from other programs by what
/// only games carry (<see cref="LooksLikeAGame"/>). A game's program is chosen from its folder (<see cref="PickExe"/>);
/// installers, uninstallers, crash reporters and redistributables are never chosen. Nothing is read while no library
/// widget is on an enabled page. Windows only: on macOS and Linux nothing is found.
/// Starting one from the phone is a widget option of its own ("allowLaunchOthers", off by default), apart from the Steam
/// one: it runs a program this scan picked, not only Steam's URL handler.
/// </summary>
public sealed class OtherGames(ConfigStore config, ILogger<OtherGames> log, GameCovers? covers = null)
{
    /// <summary>A found game and how it starts: a launcher's own address (Epic, Ubisoft), or its program in its folder.</summary>
    internal sealed record Found(string Id, string Name, string Source, string Dir, string? Exe, string? Url);

    /// <summary>How often the launchers' records are read again (the registry and Epic's manifests: quick).</summary>
    internal const int RescanMs = 10 * 60_000;
    /// <summary>How often the drives are looked through again: a walk wakes idle disks, so at most once an hour.</summary>
    internal const int DriveRescanMs = 60 * 60_000;
    // bounds for a folder that is not what it seems
    internal const int MaxGamesPerFolder = 200, MaxFilesPerGame = 2000, MaxDepth = 3;
    /// <summary>Folders looked into for one game (its program, its signs), and on one drive in all.</summary>
    internal const int MaxFoldersPerGame = 400, MaxFoldersPerDrive = 20_000;

    /// <summary>Links (junctions, symbolic links, a network share mounted in a folder) and the system's own files are not walked.</summary>
    private static readonly EnumerationOptions Walk = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private readonly Lock _gate = new();
    private List<Found> _found = [];
    private Dictionary<string, string[]> _names = []; // the names each found game is looked up by in the store
    private long _scannedAt = -(1L << 40);
    private List<Found> _onDrives = [];
    private long _drivesAt = -(1L << 40);
    private long? _firstDeadline; // when the requests made during the first look stop waiting for it

    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    /// <summary>Overridable for tests: the launchers' games (Windows registry and Epic manifests).</summary>
    internal Func<IEnumerable<Found>> Launchers { get; init; } = DefaultLaunchers;
    /// <summary>Overridable for tests: hands the program or address to the system.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; init; } = psi => { using var p = Process.Start(psi); };

    /// <summary>A game library widget is on an enabled page.</summary>
    private bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library");

    /// <summary>A library widget on an enabled page has "Covers from the Steam store" turned on (off by default).</summary>
    private bool CoversWanted() => covers is not null && GameCovers.WantedBy(config.Get());

    /// <summary>
    /// The asking widget may start the games found outside Steam: its own "allowLaunchOthers" option, off by default and
    /// apart from the Steam one ("allowLaunch"), on a Game library widget of an enabled page this phone shows (see
    /// SteamLibrary.LaunchAllowed).
    /// </summary>
    public bool LaunchAllowed(string? deviceId, string? widget) =>
        PanelWidgets.Find(config.Get(), deviceId, widget, "steam-library") is { } w && w.Bool("allowLaunchOthers") == true;

    internal static readonly SteamLibrary.LaunchError LaunchOff =
        new("othersoff", "Turn on \"Let the phone start the games found outside Steam\" in this Game library widget's settings");

    /// <summary>The games found, by name; empty while no library widget asks for them.</summary>
    public IReadOnlyList<OtherGame> List()
    {
        var games = Games();
        if (games.Count == 0) return [];
        // a game your own shortcut starts (a program in its folder) is shown once, as yours, by a widget that has that
        // shortcut in its "Other games": the phone leaves it out per widget, by these shortcut ids
        var programs = config.Get().Shortcuts.Where(s => ShortcutIcons.LooksLikeFilePath(s.Cmd))
            .Select(s => (s.Id, Path: s.Cmd.Trim().Trim('"'))).ToList();
        var sorted = games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var art = CoversWanted();
        if (art)
        {
            Dictionary<string, string[]> names;
            lock (_gate) names = _names;
            // every game found: the covers of the games that are gone are dropped
            covers!.Want(sorted.Select(g => (g.Id, names.GetValueOrDefault(g.Id) ?? [g.Name])));
        }
        return sorted.Select(g => art && covers!.CoverFile(g.Id) is not null
            ? new OtherGame(g.Id, g.Name, g.Source, true, StartedBy(g, programs), true)
            : new OtherGame(g.Id, g.Name, g.Source, g.Exe is not null, StartedBy(g, programs))).ToList();
    }

    /// <summary>The ids of the shortcuts whose program is in the game's folder.</summary>
    internal static IReadOnlyList<string> StartedBy(Found game, IEnumerable<(string Id, string Path)> programs) =>
        programs.Where(p => Norm(p.Path).StartsWith(Norm(game.Dir), StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Distinct().ToList();

    /// <summary>
    /// The last list found. Looking through the drives takes a few seconds, so it runs in the background: a request gets
    /// the last list at once, and a list older than <see cref="RescanMs"/> starts a new look. Only the requests made while
    /// the very first look runs wait for it, up to <see cref="FirstWaitMs"/> after it started; after that nothing waits.
    /// </summary>
    private List<Found> Games()
    {
        if (!OperatingSystem.IsWindows() && Launchers == DefaultLaunchers) return [];
        if (!Wanted()) return [];
        Task running;
        long wait;
        lock (_gate)
        {
            if (Clock() - _scannedAt < RescanMs) return _found;
            running = _scanning ??= Task.Run(Refresh);
            _firstDeadline ??= Environment.TickCount64 + FirstWaitMs;
            wait = Math.Max(0, _firstDeadline.Value - Environment.TickCount64);
        }
        if (wait > 0) running.Wait((int)wait);
        lock (_gate) return _found;
    }

    private void Refresh()
    {
        List<Found> found;
        Dictionary<string, string[]> names = [];
        try
        {
            // the drives are walked again only once an hour; the launchers' records every time
            List<Found>? drives;
            lock (_gate) drives = Clock() - _drivesAt < DriveRescanMs ? _onDrives : null;
            if (drives is null)
            {
                drives = Disks().ToList();
                lock (_gate) { _onDrives = drives; _drivesAt = Clock(); }
            }
            found = Scan(Launchers(), drives);
            // the publisher's name for the game, carried by its program ("Grand Theft Auto IV" in a folder "GTA 4 LCPDFR")
            if (covers is not null)
                foreach (var g in found) names[g.Id] = GameCovers.NamesOf(g.Name, g.Source == "epic" ? null : GameCovers.ProductName(g.Exe));
        }
        catch (Exception ex) { log.LogWarning("could not look for other games: {Msg}", ex.Message); found = []; }
        lock (_gate) { _found = found; _names = names; _scannedAt = Clock(); _scanning = null; }
    }

    private Task? _scanning;
    /// <summary>How long the requests made while the first look runs wait for it.</summary>
    internal int FirstWaitMs { get; init; } = 8000;
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
        "Windows.old", "inetpub",
    };

    /// <summary>
    /// A number of folders that may still be looked into: one game's (<see cref="MaxFoldersPerGame"/>), taken from its
    /// drive's (<see cref="MaxFoldersPerDrive"/>), so a large tree of photos or backups near a drive's top is not walked whole.
    /// </summary>
    internal sealed class Budget(int folders, Budget? drive = null)
    {
        private int _left = folders;
        public bool Take()
        {
            if (_left <= 0 || drive?.Take() == false) return false;
            _left--;
            return true;
        }
    }

    /// <summary>The folders in a folder (no links, no system folders), or none when it cannot be read or the budget is spent.</summary>
    private static string[] Folders(string dir, Budget budget)
    {
        if (!budget.Take()) return [];
        try { return Directory.EnumerateDirectories(dir, "*", Walk).ToArray(); } catch { return []; }
    }

    /// <summary>The games on these drives: every folder at a drive's top, or inside a <see cref="Holders"/> folder, that looks like a game (<see cref="LooksLikeAGame"/>).</summary>
    internal static IEnumerable<Found> FromDrives(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var drive = new Budget(MaxFoldersPerDrive);
            var candidates = new List<string>();
            void Look(string dir, int level)
            {
                foreach (var sub in Folders(dir, drive).Take(MaxGamesPerFolder))
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
            Look(root, 0);
            foreach (var dir in candidates)
                if (Game(dir, new Budget(MaxFoldersPerGame, drive)) is { } g
                    && LooksLikeAGame(dir, Path.GetDirectoryName(g.Exe!)!, new Budget(MaxFoldersPerGame, drive))) yield return g;
        }
    }

    /// <summary>Launchers that keep their games in a "games" folder of their own, next to their parts.</summary>
    private static readonly HashSet<string> LauncherHomes = new(StringComparer.OrdinalIgnoreCase) { "Ubisoft Game Launcher" };

    // What only a game brings along: a game store's or engine's library, a game's archives, videos and mod loaders.
    private static readonly string[] SureFiles = ["steam_api.dll", "steam_api64.dll", "unityplayer.dll", "gameuxinstallhelper.dll", "amd_ags_x64.dll",
        "mss32.dll", "mss64.dll", "physxloader.dll", "xlive.dll", "eossdk-win64-shipping.dll", "eossdk-win32-shipping.dll"];
    private static readonly string[] SureStarts = ["bink", "uplay_r", "upc_r", "nvphysx", "galaxy64", "galaxy.dll"];
    private static readonly HashSet<string> SureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".rpf", ".bik", ".bk2", ".vpk", ".ucas", ".utoc", ".forge", ".bsa", ".ba2", ".ggpk", ".asi", ".gcf", ".usm" };
    // also in other programs (or, for an anti-cheat, installed as a service of its own), so two are needed
    private static readonly string[] MaybeFiles = ["xinput1_3.dll", "d3dx9_43.dll", "d3dx9_42.dll", "openal32.dll", "fmod.dll", "fmod64.dll", "fmodex.dll", "fmodex64.dll", "fmodstudio.dll"];
    private static readonly string[] MaybeStarts = ["eaanticheat", "easyanticheat", "battleye"];
    // game archives by other programs' extensions too (LaTeX writes .toc): a weak sign each
    private static readonly HashSet<string> MaybeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".toc", ".sb", ".wad" };

    /// <summary>
    /// Whether a folder holds a game whose program is in <paramref name="exeDir"/>: at least one sign only games carry (a
    /// game store's or engine's library, a game archive or video, a mod loader, a file ending in GDF.dll that registers it
    /// with Windows), or two weaker ones, in the program's folder or below it, so a copy of a game's files kept somewhere
    /// else in the folder does not count. A source tree (a .git folder) is not a game. Looked at most three folders down
    /// and a few thousand files in.
    /// </summary>
    internal static bool LooksLikeAGame(string dir, string exeDir, Budget? budget = null)
    {
        int sure = 0, files = 0;
        var weak = new HashSet<string>();
        var under = Norm(exeDir);
        foreach (var file in Files(dir, 0, budget ?? new Budget(MaxFoldersPerGame)))
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
            else if (MaybeExtensions.Contains(Path.GetExtension(n))) weak.Add(Path.GetExtension(n));
        }
        return sure > 0 || weak.Count > 1;
    }

    private static IEnumerable<string> Files(string dir, int depth, Budget budget)
    {
        string[] files, dirs;
        try { files = Directory.EnumerateFiles(dir, "*", Walk).ToArray(); } catch { yield break; }
        dirs = depth < MaxDepth ? Folders(dir, budget) : [];
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.Equals("site-packages", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            // a source tree: its .git folder is reported (and ends the look) without reading it
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { yield return Path.Combine(d, "HEAD"); continue; }
            foreach (var f in Files(d, depth + 1, budget)) yield return f;
        }
    }

    private static string Norm(string dir) => dir.Replace('/', '\\').TrimEnd('\\') + "\\";

    /// <summary>A found game's id: a hash of where it is (its folder), so a different program picked later keeps the id.</summary>
    internal static string IdOf(string source, string where) =>
        "x:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + Norm(where).ToLowerInvariant())))[..16];

    private static Found? Game(string dir, Budget? budget = null)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.StartsWith('.') || name.StartsWith('$') || name.StartsWith('_')) return null;
        return PickExe(dir, name, budget) is { } exe ? new Found(IdOf("drive", dir), name, "drive", dir, exe, null) : null;
    }

    /// <summary>
    /// <paramref name="file"/> (relative to <paramref name="dir"/>, or a full path) when it is an existing program (.exe)
    /// inside <paramref name="dir"/>; null for anything else (a document, a script, a path outside the game's folder).
    /// What a launcher's record names is checked this way before it can be started.
    /// </summary>
    internal static string? ProgramIn(string dir, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(dir, file.Trim().Trim('"')));
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Norm(full).StartsWith(Norm(Path.GetFullPath(dir)), StringComparison.OrdinalIgnoreCase)
                && File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    // Programs that come with a game but are not it, matched against whole words of the program's name (so "eac" does
    // not throw out BeachRacing, nor "server" Observer), or a few words run together ("crash handler", "anti cheat").
    private static readonly HashSet<string> HelperWords = new(StringComparer.Ordinal)
    {
        "setup", "redist", "vcredist", "directx", "report", "reporter", "helper", "patch", "patcher", "anticheat", "eac", "battleye",
        "beservice", "cleanup", "dotnet", "touchup", "cef", "cefprocess", "activation", "register", "uploader", "benchmark", "config",
        "configurator", "settings", "server", "dedicated", "unity", "ue4", "ue5", "subprocess", "error", "debug", "debugger", "tool",
        "tools", "editor", "7z", "unrar", "physx", "oalinst", "nvidia", "browser", "encoder", "profiler", "viewer", "securom",
    };
    // the same, for words that come in several forms (uninstall, uninstaller, unins000; update, updater, autoupdate…)
    private static readonly string[] HelperStems = ["unins", "uninstall", "install", "updat", "autoupdat", "prereq", "dxsetup", "dxwebsetup",
        "diagnos", "crashhandl", "crashreport", "crashsend", "crashpad", "crashdump", "crashupload", "bugreport", "errorreport"];

    /// <summary>Programs that come with a game but are not it.</summary>
    internal static bool NotTheGame(string file)
    {
        file = file.Replace('/', '\\');
        if (file.Contains(@"\_CommonRedist\", StringComparison.OrdinalIgnoreCase) || file.Contains(@"\Redist\", StringComparison.OrdinalIgnoreCase)
            || file.Contains(@"\__Installer\", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = NameParts(Path.GetFileNameWithoutExtension(file[(file.LastIndexOf('\\') + 1)..]));
        for (var i = 0; i < parts.Count; i++)
        {
            var run = "";
            for (var j = i; j < parts.Count && j < i + 3; j++)
            {
                run += parts[j];
                if (HelperWords.Contains(run) || HelperStems.Any(s => run.StartsWith(s, StringComparison.Ordinal))) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A program's name in words, lower case: split at anything not a letter or digit, between letters and digits, and
    /// where the case changes (UnityCrashHandler64 gives unity, crash, handler and 64; EAAntiCheat gives ea, anti, cheat).
    /// </summary>
    internal static List<string> NameParts(string name)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); } }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (sb.Length > 0)
            {
                var p = name[i - 1];
                if (char.IsDigit(c) != char.IsDigit(p) || (char.IsUpper(c) && char.IsLower(p))
                    || (char.IsUpper(c) && char.IsUpper(p) && i + 1 < name.Length && char.IsLower(name[i + 1]))) Flush();
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return parts;
    }

    /// <summary>
    /// The game's program in its folder: of the programs at most three folders down (not an installer or helper: a game's
    /// tools carry its name too, gta4Browser.exe next to GTAIV.exe), the one whose name shares the most with the folder's,
    /// then the largest, then the shallowest. Null when there is none.
    /// </summary>
    internal static string? PickExe(string dir, string name, Budget? budget = null)
    {
        var want = Words(name);
        (string File, double Score)? best = null;
        var count = 0;
        foreach (var (file, depth) in Programs(dir, 0, budget ?? new Budget(MaxFoldersPerGame)))
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

    private static IEnumerable<(string File, int Depth)> Programs(string dir, int depth, Budget budget)
    {
        string[] files, dirs;
        try { files = Directory.EnumerateFiles(dir, "*.exe", Walk).ToArray(); } catch { yield break; }
        dirs = depth < MaxDepth ? Folders(dir, budget) : [];
        foreach (var f in files) yield return (f, depth);
        foreach (var d in dirs)
            foreach (var x in Programs(d, depth + 1, budget)) yield return x;
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
                // a program inside the game's folder only; the launcher's address starts it otherwise
                var exe = ProgramIn(dir, S("LaunchExecutable"));
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
                        var exe = ProgramIn(gp, k.Value("exe")) ?? PickExe(gp, gn);
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
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in k.GetValueNames()) values[v] = k.GetValue(v)?.ToString();
                list.Add(new Key(name, v => values.TryGetValue(v, out var s) ? s : null));
            }
        }
        catch { }
        return list;
    }

    /// <summary>A found game's picture: its store art (JPEG) when kept, else its program's icon (PNG); or null.</summary>
    public (byte[] Bytes, string Type)? Cover(string? id)
    {
        if (id is null || !id.StartsWith("x:", StringComparison.Ordinal)) return null;
        var g = Games().FirstOrDefault(x => x.Id == id);
        if (g is null) return null;
        if (CoversWanted() && covers!.CoverFile(id) is { } file)
            try { return (File.ReadAllBytes(file), "image/jpeg"); } catch { }
        return g.Exe is not null && ShortcutIcons.Icon(new Shortcut { Id = id, Cmd = g.Exe }) is { } png ? (png, "image/png") : null;
    }

    /// <summary>
    /// Starts a found game for the asking <paramref name="widget"/> when its "Let the phone start the games found outside
    /// Steam" is on (<see cref="LaunchAllowed"/>): by its launcher's address, or its program in its folder. Only a game of
    /// the list found here can start, by its id, so no text from the request is run or passed on.
    /// </summary>
    public SteamLibrary.LaunchError? Launch(string? deviceId, string? widget, string? id)
    {
        if (!LaunchAllowed(deviceId, widget)) return LaunchOff;
        if (id is null || !id.StartsWith("x:", StringComparison.Ordinal)) return new("notinstalled", "That game is not installed");
        var g = Games().FirstOrDefault(x => x.Id == id);
        // the option can be turned off while this waited for the first look: decide with the settings of now
        if (!LaunchAllowed(deviceId, widget)) return LaunchOff;
        if (g is null) return new("notinstalled", "That game is not installed");
        // a launcher's address goes to its URL handler; a path only when it is still a program (the list is up to ten
        // minutes old, and a launcher's record could name any file) that no other account could have put there (see
        // Unsafe): the shell starts it so a game that asks for administrator rights still gets the prompt
        string? exe = null;
        if (g.Url is null)
        {
            if (g.Exe is null) return new("notinstalled", "That game is not installed");
            switch (Unsafe(g.Dir, g.Exe))
            {
                case null: exe = Path.GetFullPath(g.Exe); break;
                case "missing": return new("notinstalled", "That game is not installed");
                case var why:
                    log.LogWarning("not starting {Name} from the phone: its program {Why}", g.Name, why);
                    return Unprotected;
            }
        }
        try
        {
            StartProcess(g.Url is not null
                ? new ProcessStartInfo { FileName = g.Url, UseShellExecute = true }
                : new ProcessStartInfo { FileName = exe!, WorkingDirectory = Path.GetDirectoryName(exe!)!, UseShellExecute = true });
            log.LogInformation("game started from the phone: {Name} ({Source})", g.Name, g.Source);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("could not start {Name}: {Msg}", g.Name, ex.Message);
            return new("failed", "The game could not be started");
        }
    }

    internal static readonly SteamLibrary.LaunchError Unprotected =
        new("unsafe", "That game's folder can be changed by other accounts on this computer, so it is not started from the phone");

    // Everyone, Authenticated Users and the local Users group
    private static readonly HashSet<string> BroadGroups = ["S-1-1-0", "S-1-5-11", "S-1-5-32-545"];

    // what lets another account put a different program in place: write or append data (create files and folders),
    // delete, delete a child, change the permissions or the owner, and the generic write and all rights
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private const FileSystemRights ReplaceRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;

    /// <summary>
    /// Why a game's program (by path: found on a drive, or named by a launcher's record) must not be started from the
    /// phone, or null when it may:
    /// - "missing": not an existing .exe inside the game's folder (any more);
    /// - "is reached through a link": the path, resolved again now, has a junction or symbolic link in it, from the drive's
    ///   root down to the program (on Windows; elsewhere from the game's folder down);
    /// - "can be changed by other accounts": on Windows, the game's folder, a folder between it and the program, or the
    ///   program itself lets Everyone, Authenticated Users or Users write, delete or change permissions (inherited
    ///   rules count). Another account could otherwise plant a program there and have it run as this user. Games under
    ///   Program Files or in this user's own folders pass; a folder made at the top of a data drive usually does not.
    /// </summary>
    internal static string? Unsafe(string dir, string exe)
    {
        string full, root;
        try { full = Path.GetFullPath(exe); root = Path.GetFullPath(dir); } catch { return "missing"; }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)
            || !Norm(full).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase)) return "missing";
        // the program and its folders up to the game's folder, then (Windows) on to the drive's root
        var chain = new List<string> { full };
        for (var d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
        {
            var mine = Norm(d).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase);
            if (!mine && !OperatingSystem.IsWindows()) break;
            chain.Add(d);
        }
        if (chain.Any(SteamLibrary.IsLink)) return "is reached through a link";
        if (OperatingSystem.IsWindows())
            foreach (var p in chain.Where(p => Norm(p).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase)))
                if (WritableByOthers(p)) return "can be changed by other accounts";
        return null;
    }

    /// <summary>Everyone, Authenticated Users or Users may write, delete or re-permission this file or folder (an unreadable one counts).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool WritableByOthers(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var folder = security is DirectorySecurity;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || !BroadGroups.Contains(rule.IdentityReference.Value)) continue;
                // a rule only handed down to what is created inside does not apply to the folder itself
                if (folder && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                if ((rule.FileSystemRights & ReplaceRights) != 0) return true;
            }
            return false;
        }
        catch { return true; }
    }
}
