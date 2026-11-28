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
/// out). No path reaches the phone.
/// </summary>
public sealed record OtherGame(string Id, string Name, string Source, bool Cover, IReadOnlyList<string> Shortcuts);

/// <summary>
/// Games installed outside Steam, shown by the game library widget next to Steam's: found on their own, with nothing to
/// set up, no account and no network. Read from what the launchers leave on this computer (the Epic Games Launcher's
/// manifests; the registry keys of the EA app, GOG Galaxy, Ubisoft Connect and the Rockstar Games Launcher), and from the
/// drives, for the games no launcher lists (a portable copy, a mod, an old disc install), told from other programs by what
/// only games carry (<see cref="LooksLikeAGame"/>). A game's program is chosen from its folder (<see cref="PickExe"/>);
/// installers, uninstallers, crash reporters and redistributables are never chosen. Nothing is read while no library
/// widget is on an enabled page. Windows only: on macOS and Linux nothing is found.
/// Starting one from the phone needs a widget option of its own ("allowLaunchOthers", off by default) on top of the
/// Steam one ("allowLaunch"): it runs a program this scan picked, not only Steam's URL handler.
/// </summary>
public sealed class OtherGames(ConfigStore config, ILogger<OtherGames> log)
{
    /// <summary>A found game and how it starts: a launcher's own address (Epic, Ubisoft), or its program in its folder.</summary>
    internal sealed record Found(string Id, string Name, string Source, string Dir, string? Exe, string? Url);

    /// <summary>How often the launchers' records are read again (the registry and Epic's manifests: quick).</summary>
    internal const int RescanMs = 10 * 60_000;
    /// <summary>How often the drives are looked through again: a walk wakes idle disks, so at most once an hour.</summary>
    internal const int DriveRescanMs = 60 * 60_000;
    /// <summary>How soon a look that failed is tried again; the last list found stays shown meanwhile.</summary>
    internal const int RetryMs = 60_000;
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
    private long _scannedAt = -(1L << 40);
    private List<Found> _onDrives = [];
    private long _drivesAt = -(1L << 40);
    private long _retryAt = -(1L << 40); // after a look failed: not tried again before this
    private List<Found> _lastLaunchers = []; // the launchers' games of the last look that could read them
    private long? _firstDeadline; // when the requests made during the first look stop waiting for it
    private int _looks;

    /// <summary>How many looks have ended (found a list or failed): what a test waits for.</summary>
    internal int Looks => Volatile.Read(ref _looks);

    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    /// <summary>Overridable for tests: the launchers' games (Windows registry and Epic manifests).</summary>
    internal Func<IEnumerable<Found>> Launchers { get; init; } = DefaultLaunchers;
    /// <summary>Overridable for tests: hands the program or address to the system.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; init; } = psi => { using var p = Process.Start(psi); };

    /// <summary>A game library widget is on an enabled page.</summary>
    private bool Wanted() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "steam-library");

    /// <summary>
    /// Why the asking widget may not start the games found outside Steam, or null when it may: both its "Let the phone
    /// start games" ("allowLaunch": with it off the library is only shown, for every game) and its own
    /// "allowLaunchOthers" must be on (both off by default), on a Game library widget of an enabled page this phone
    /// shows (see SteamLibrary.LaunchAllowed).
    /// </summary>
    internal SteamLibrary.LaunchError? LaunchRefused(string? deviceId, string? widget) =>
        PanelWidgets.Find(config.Get(), deviceId, widget, "steam-library") is not { } w || w.Bool("allowLaunch") != true ? StartingOff
        : w.Bool("allowLaunchOthers") != true ? LaunchOff : null;

    internal static readonly SteamLibrary.LaunchError StartingOff =
        new("off", "Turn on \"Let the phone start games\" in this Game library widget's settings");
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
        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OtherGame(g.Id, g.Name, g.Source, g.Exe is not null, StartedBy(g, programs))).ToList();
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
            if (Clock() - _scannedAt < RescanMs || Clock() < _retryAt) return _found;
            running = _scanning ??= Task.Run(Refresh);
            _firstDeadline ??= Environment.TickCount64 + FirstWaitMs;
            wait = Math.Max(0, _firstDeadline.Value - Environment.TickCount64);
        }
        if (wait > 0) running.Wait((int)wait);
        lock (_gate) return _found;
    }

    /// <summary>One look; a look that fails keeps the last list found and is tried again after <see cref="RetryMs"/>.</summary>
    private void Refresh()
    {
        List<Found>? found = null;
        var launchersFailed = false;
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
            // A launcher's record that cannot be read keeps that part of the last list (none on a first look); it does not
            // hold back the games on the drives, and it is read again after RetryMs.
            List<Found> launchers;
            try
            {
                launchers = Launchers().ToList();
                lock (_gate) _lastLaunchers = launchers;
            }
            catch (PartialLaunchers partial)
            {
                log.LogWarning("could not read one launcher's games; its last ones are kept");
                launchersFailed = true;
                launchers = partial.Games;
                lock (_gate) _lastLaunchers = launchers;
            }
            catch (Exception ex)
            {
                log.LogWarning("could not read the launchers' games: {Msg}", ex.Message);
                launchersFailed = true;
                lock (_gate) launchers = _lastLaunchers;
            }
            found = Scan(launchers, drives);
        }
        catch (Exception ex) { log.LogWarning("could not look for other games: {Msg}", ex.Message); }
        lock (_gate)
        {
            if (found is not null) _found = found;
            if (found is not null && !launchersFailed) _scannedAt = Clock();
            else _retryAt = Clock() + RetryMs;
            _scanning = null;
            _looks++;
        }
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
        /// <summary>Nothing more may be looked into: this budget, or its drive's, is spent.</summary>
        public bool Spent => _left <= 0 || drive?.Spent == true;
        public bool Take()
        {
            if (_left <= 0 || drive?.Take() == false) return false;
            _left--;
            return true;
        }
    }

    /// <summary>
    /// At most <paramref name="max"/> entries of a folder's listing, read one by one as they are asked for, so a folder
    /// with a huge number of entries is not read whole. A listing that cannot be read (any more) ends there.
    /// </summary>
    internal static IEnumerable<string> Entries(Func<IEnumerable<string>> listing, int max)
    {
        IEnumerator<string> e;
        try { e = listing().GetEnumerator(); } catch { yield break; }
        using (e)
        {
            for (var n = 0; n < max; n++)
            {
                string next;
                try
                {
                    if (!e.MoveNext()) yield break;
                    next = e.Current;
                }
                catch { yield break; }
                yield return next;
            }
        }
    }

    /// <summary>The folders in a folder (no links, no system folders), at most <paramref name="max"/>.</summary>
    private static IEnumerable<string> Folders(string dir, int max) => Entries(() => Directory.EnumerateDirectories(dir, "*", Walk), max);

    /// <summary>The games on these drives: every folder at a drive's top, or inside a <see cref="Holders"/> folder, that looks like a game (<see cref="LooksLikeAGame"/>).</summary>
    internal static IEnumerable<Found> FromDrives(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var drive = new Budget(MaxFoldersPerDrive);
            var candidates = new List<string>();
            void Look(string dir, int level)
            {
                if (!drive.Take()) return;
                foreach (var sub in Folders(dir, MaxGamesPerFolder))
                {
                    var name = Path.GetFileName(sub);
                    // a package cache or a Python environment carries game libraries (pygame) without being a game
                    if (NotGames.Contains(name) || name.StartsWith('.') || name.StartsWith('$') || name.Contains("cache", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("venv", StringComparison.OrdinalIgnoreCase) || name.Contains("conda", StringComparison.OrdinalIgnoreCase)) continue;
                    // a launcher's own folder: only the games folder inside it (Ubisoft Game Launcher › games), not its parts
                    // (that folder looked up by its name, so a link there is skipped here, as the listing skips the others)
                    if (LauncherHomes.Contains(name))
                    {
                        var games = Path.Combine(sub, "games");
                        if (level < 3 && Directory.Exists(games) && !SteamLibrary.IsLink(games)) Look(games, level + 1);
                    }
                    else if (Holders.Contains(name) && level < 3) Look(sub, level + 1);
                    else candidates.Add(sub);
                }
            }
            Look(root, 0);
            foreach (var dir in candidates)
            {
                if (drive.Spent) break; // the drive's folders are spent: the rest of its candidates are not looked into
                // one budget for both looks at a candidate: its program and its signs share the per-game limit
                var budget = new Budget(MaxFoldersPerGame, drive);
                if (Game(dir, budget) is { } g && LooksLikeAGame(dir, Path.GetDirectoryName(g.Exe!)!, budget)) yield return g;
            }
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
        budget ??= new Budget(MaxFoldersPerGame);
        if (budget.Spent) return false;
        // a source tree: its .git at the top is looked for before the walk, which may stop at its file limit before
        // reaching the folders (a tree with thousands of files at its top)
        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return false;
        int sure = 0, files = 0;
        var weak = new HashSet<string>();
        var under = Norm(exeDir);
        foreach (var file in Files(dir, 0, budget))
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

    // Every folder looked into, the last level too, takes one from the budget before it is read; a spent budget ends the look.
    private static IEnumerable<string> Files(string dir, int depth, Budget budget)
    {
        if (!budget.Take()) yield break;
        foreach (var f in Entries(() => Directory.EnumerateFiles(dir, "*", Walk), MaxFilesPerGame * 2)) yield return f;
        if (depth >= MaxDepth) yield break;
        foreach (var d in Folders(dir, MaxFoldersPerGame))
        {
            var name = Path.GetFileName(d);
            if (name.Equals("site-packages", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            // a source tree: its .git folder is reported (and ends the look) without reading it
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { yield return Path.Combine(d, "HEAD"); continue; }
            if (budget.Spent) yield break;
            foreach (var f in Files(d, depth + 1, budget)) yield return f;
        }
    }

    // resolved first ("D:\Games\.\Racer" and "D:\Games\\Racer" are D:\Games\Racer), so one folder has one id and one record
    private static string Norm(string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir); } catch { full = dir; }
        return full.Replace('/', '\\').TrimEnd('\\') + "\\";
    }

    /// <summary>A found game's id: a hash of where it is (its folder), so a different program picked later keeps the id.</summary>
    // the folder alone, not the source: a game first found on a drive and later named by its launcher keeps its id, and
    // a favourite saved under it (Scan keeps one record per folder, so the id stays unique)
    internal static string IdOf(string where) =>
        "x:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Norm(where).ToLowerInvariant())))[..16];

    private static Found? Game(string dir, Budget? budget = null)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.StartsWith('.') || name.StartsWith('$') || name.StartsWith('_')) return null;
        return PickExe(dir, name, budget) is { } exe ? new Found(IdOf(dir), name, "drive", dir, exe, null) : null;
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
                && !LinkBelow(dir, full) && File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether a junction or symbolic link is on the way from the game's folder down to its program, the program
    /// included: such a path can lead out of the folder. A program found by a walk never is (links are not walked); one
    /// named by a launcher's record is checked here, and again before its icon is read (the game's folder and the ones
    /// above it are checked by <see cref="Usable"/> before, from the drive's root down). Before a start,
    /// <see cref="Unsafe"/> checks the whole path again, from the drive's root down.
    /// </summary>
    internal static bool LinkBelow(string dir, string exe)
    {
        try
        {
            // from the game's folder down, stopping at the first link, so nothing below a link is looked at
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            var full = Path.GetFullPath(exe);
            if (Norm(full).Length <= Norm(root).Length || !Norm(full).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase)) return false;
            var path = root;
            foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                path = Path.Combine(path, part);
                if (SteamLibrary.IsLink(path)) return true;
            }
            return false;
        }
        catch { return true; }
    }

    /// <summary>
    /// Whether a launcher's record names a folder on a local disk, judged from its text before anything there is read
    /// (reading a folder on a network share would connect to it): never a path starting with two separators (a share,
    /// \\server\share or \\?\UNC\server\share, or a device path), never a relative one, and on Windows only a drive
    /// letter of a fixed disk, not a mapped network drive (nor a disc or a removable drive).
    /// </summary>
    internal static bool Local(string? dir, Func<string, DriveType>? driveType = null)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var d = dir.Trim();
        if (d.Length >= 2 && (d[0] is '\\' or '/') && (d[1] is '\\' or '/')) return false;
        if (!Path.IsPathFullyQualified(d)) return false;
        if (!OperatingSystem.IsWindows()) return true;
        if (d.Length < 3 || !char.IsAsciiLetter(d[0]) || d[1] != ':') return false;
        try { return (driveType ?? (root => new DriveInfo(root).DriveType))(d[0] + @":\") == DriveType.Fixed; }
        catch { return false; }
    }

    /// <summary>
    /// Whether a game's folder named by a launcher's record may be read: on a local disk (<see cref="Local"/>, judged
    /// from the text), with no junction or symbolic link on the way from the drive's root down to the folder, the folder
    /// included, and still there. The path is checked from the top down and the check stops at the first link, so
    /// nothing is read through one (a link above the folder could lead to a network share, or to a disc or removable
    /// drive that <see cref="Local"/> keeps out). The drive's root itself is not checked. Elsewhere than Windows (where
    /// nothing is found outside the tests) only the folder itself is. Every probe, enumeration and icon read of such a
    /// folder comes after this. <paramref name="isLink"/>: the link check (<see cref="SteamLibrary.IsLink"/>), the
    /// first thing that touches the folder's path; a test counts it.
    /// </summary>
    internal static bool Usable(string? dir, Func<string, bool>? isLink = null)
    {
        if (!Local(dir)) return false;
        isLink ??= SteamLibrary.IsLink;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir!.Trim()));
            if (!OperatingSystem.IsWindows()) return !isLink(full) && Directory.Exists(full);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || full.Length <= root.Length) return false; // a drive's root is no game's folder
            var path = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                path = Path.Combine(path, part);
                if (isLink(path)) return false;
            }
            return Directory.Exists(full);
        }
        catch { return false; }
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

    // like Files: each folder looked into takes one from the budget first
    private static IEnumerable<(string File, int Depth)> Programs(string dir, int depth, Budget budget)
    {
        if (!budget.Take()) yield break;
        foreach (var f in Entries(() => Directory.EnumerateFiles(dir, "*.exe", Walk), MaxFilesPerGame)) yield return (f, depth);
        if (depth >= MaxDepth) yield break;
        foreach (var d in Folders(dir, MaxFoldersPerGame))
        {
            if (budget.Spent) yield break;
            foreach (var x in Programs(d, depth + 1, budget)) yield return x;
        }
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

    /// <summary>
    /// The Epic Games Launcher's manifests (one JSON .item per installed game). No manifests folder: the launcher is not
    /// installed, so no games. A folder that cannot be listed (no access, a disk error) fails the look, so the last list
    /// found stays and the look is tried again soon (<see cref="RetryMs"/>) rather than dropping the Epic games.
    /// <paramref name="isLink"/>: see <see cref="Usable"/>.
    /// </summary>
    internal static IEnumerable<Found> FromEpic(string manifests, Func<string, bool>? isLink = null)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(manifests, "*.item").ToList(); }
        catch (DirectoryNotFoundException) { yield break; }
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
                // a folder on a network share, or reached through a link, is never read; a game whose folder is gone is
                // not installed (any more)
                if (!Usable(dir, isLink)) continue;
                // a program inside the game's folder only; the launcher's address starts it otherwise
                // the program the manifest names, unless it is an installer or a helper (the launcher's link still works)
                var exe = ProgramIn(dir, S("LaunchExecutable")) is { } named && !NotTheGame(named) ? named : null;
                string? url = null;
                if (S("CatalogNamespace") is { } ns && S("CatalogItemId") is { } item && S("AppName") is { } app
                    && new[] { ns, item, app }.All(x => x.Length > 0 && x.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
                    url = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true";
                if (exe is null && url is null) continue;
                g = new Found(IdOf(dir), name, "epic", dir, exe, url);
            }
            catch { }
            if (g is not null) yield return g;
        }
    }

    /// <summary>One registry key of a launcher: its name and a reader of its values.</summary>
    internal sealed record Key(string Name, Func<string, string?> Value);

    /// <summary>GOG Galaxy, Ubisoft Connect, the EA app and Rockstar from their keys (see <see cref="DefaultLaunchers"/>).
    /// <paramref name="isLink"/>: see <see cref="Usable"/>.</summary>
    internal static IEnumerable<Found> FromKeys(string source, IEnumerable<Key> keys, Func<string, bool>? isLink = null)
    {
        // each folder named is checked (Usable: a local disk, no link from the drive's root down, still there) before
        // anything there is read
        foreach (var k in keys)
        {
            switch (source)
            {
                case "gog":
                    if (k.Value("path") is { Length: > 0 } gp && k.Value("gameName") is { Length: > 0 } gn && Usable(gp, isLink))
                    {
                        // the program the record names, unless it is an installer or a helper: then the one picked here
                        var exe = ProgramIn(gp, k.Value("exe")) is { } named && !NotTheGame(named) ? named : PickExe(gp, gn);
                        if (exe is not null) yield return new Found(IdOf(gp), gn, "gog", gp, exe, null);
                    }
                    break;
                case "ubisoft":
                    // the Installs key holds only the folder; Ubisoft Connect starts the game by its number
                    if (k.Value("InstallDir") is { Length: > 0 } ud && k.Name.All(char.IsAsciiDigit) && Usable(ud, isLink))
                        yield return new Found(IdOf(ud), Path.GetFileName(ud.TrimEnd('/', '\\')), "ubisoft", ud, PickExe(ud, Path.GetFileName(ud.TrimEnd('/', '\\'))), $"uplay://launch/{k.Name}/0");
                    break;
                case "ea":
                case "rockstar":
                    var dir = k.Value("Install Dir") ?? k.Value("InstallFolder") ?? k.Value("InstallDir");
                    if (dir is { Length: > 0 } && !k.Name.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                        && !k.Name.Contains("Social Club", StringComparison.OrdinalIgnoreCase) && Usable(dir, isLink) && PickExe(dir, k.Name) is { } x)
                        yield return new Found(IdOf(dir), k.Name, source, dir, x, null);
                    break;
            }
        }
    }

    private static IEnumerable<Found> DefaultLaunchers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var sources = new (string Name, Func<IEnumerable<Found>> Read)[]
        {
            ("epic", () => FromEpic(Path.Combine(data, "Epic", "EpicGamesLauncher", "Data", "Manifests"))),
            ("ea", () => FromKeys("ea", Keys(@"SOFTWARE\WOW6432Node\EA Games"))),
            ("gog", () => FromKeys("gog", Keys(@"SOFTWARE\WOW6432Node\GOG.com\Games"))),
            ("ubisoft", () => FromKeys("ubisoft", Keys(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))),
            ("rockstar", () => FromKeys("rockstar", Keys(@"SOFTWARE\WOW6432Node\Rockstar Games"))),
        };
        // Each launcher on its own: one whose records cannot be read keeps its own last games, and the others' are current.
        var all = new List<Found>();
        var failed = false;
        foreach (var (name, read) in sources)
        {
            List<Found> games;
            try
            {
                games = read().ToList();
                lock (LastBySource) LastBySource[name] = games;
            }
            catch (Exception)
            {
                failed = true;
                lock (LastBySource) games = LastBySource.GetValueOrDefault(name) ?? [];
            }
            all.AddRange(games);
        }
        return failed ? throw new PartialLaunchers(all) : all;
    }

    private static readonly Dictionary<string, List<Found>> LastBySource = [];

    /// <summary>Some launcher's records could not be read: <see cref="Games"/> holds the others' and that one's last games.</summary>
    internal sealed class PartialLaunchers(List<Found> games) : Exception("a launcher's records could not be read")
    {
        public List<Found> Games { get; } = games;
    }

    /// <summary>
    /// A launcher's keys. No key: the launcher is not installed, so no games. A read that fails (no access, a key
    /// changing under it) fails the look rather than giving part of the list, so the last list found stays and the look
    /// is tried again soon (<see cref="RetryMs"/>). A game's key removed between the listing and its reading is skipped.
    /// </summary>
    private static List<Key> Keys(string path)
    {
        var list = new List<Key>();
        if (!OperatingSystem.IsWindows()) return list;
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
        return list;
    }

    /// <summary>The icon of a found game's program, as a PNG, or null.</summary>
    public byte[]? Cover(string? id)
    {
        if (id is null || !id.StartsWith("x:", StringComparison.Ordinal)) return null;
        var g = Games().FirstOrDefault(x => x.Id == id);
        // the list is up to ten minutes old: a link put on the program's path since (above the game's folder, at it, or
        // below it) is not followed
        if (g?.Exe is null || !Usable(g.Dir) || LinkBelow(g.Dir, g.Exe)) return null;
        return ReadIcon(g.Exe);
    }

    /// <summary>Overridable for tests: reads a program's icon, as a PNG.</summary>
    internal Func<string, byte[]?> ReadIcon { get; init; } = exe => ShortcutIcons.Icon(new Shortcut { Id = "game", Cmd = exe });

    /// <summary>
    /// Starts a found game for the asking <paramref name="widget"/> when both its "Let the phone start games" and its "Let
    /// the phone start the games found outside Steam" are on (<see cref="LaunchRefused"/>): by its launcher's address, or
    /// its program in its folder. Only a game of the list found here can start, by its id, so no text from the request is
    /// run or passed on.
    /// </summary>
    public SteamLibrary.LaunchError? Launch(string? deviceId, string? widget, string? id)
    {
        if (LaunchRefused(deviceId, widget) is { } off) return off;
        if (id is null || !id.StartsWith("x:", StringComparison.Ordinal)) return new("notinstalled", "That game is not installed");
        var g = Games().FirstOrDefault(x => x.Id == id);
        // an option can be turned off while this waited for the first look: decide with the settings of now
        if (LaunchRefused(deviceId, widget) is { } now) return now;
        if (g is null) return new("notinstalled", "That game is not installed");
        // a launcher's address goes to its URL handler, only while the game's folder is still there, on a local disk
        // with no link on its way (the list is up to ten minutes old: the game may have been removed since); a path only
        // when it is still a program (a launcher's record could name any file) that no other account could have put
        // there (see Unsafe): the shell starts it so a game that asks for administrator rights still gets the prompt
        string? exe = null;
        if (g.Url is not null)
        {
            if (!Usable(g.Dir)) return new("notinstalled", "That game is not installed");
        }
        else
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

    // The accounts trusted to own and change a program's path besides the agent's own user: SYSTEM, Administrators and
    // TrustedInstaller (Program Files belongs to them).
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static readonly HashSet<string> TrustedSids = ["S-1-5-18", "S-1-5-32-544", TrustedInstaller];
    private const string AuthenticatedUsers = "S-1-5-11";

    // The rights that let an account put another program in place: write data (create files), append data (create
    // folders), delete, delete a child, change the permissions (WRITE_DAC), change the owner (WRITE_OWNER), and the
    // generic write and all rights.
    internal const long ReplaceRights = 0x2 | 0x4 | 0x10000 | 0x40 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;
    // Drive-root exception: a drive's root lets Authenticated Users create files and folders in it, and on a data drive
    // also modify the root itself (write data, append data, delete), by Windows' default. None of these lets anyone
    // rename or replace a folder that is already there (that needs delete-a-child on the root, or rights on the folder
    // itself, which are checked on the folder), so at the root itself, and only there, they are accepted.
    internal const long RootCreateRights = 0x2 | 0x4 | 0x10000;

    /// <summary>An allow rule of a file's or folder's permissions: who, which rights, and whether it only applies to what is created inside.</summary>
    internal readonly record struct Allow(string Sid, long Rights, bool InheritOnly);

    /// <summary>
    /// Why a game's program (by path: found on a drive, or named by a launcher's record) must not be started from the
    /// phone, or null when it may. Deny by default:
    /// - "missing": not an existing .exe inside the game's folder (any more);
    /// - "is reached through a link": the path, resolved again now, has a junction or symbolic link in it, from the drive's
    ///   root down to the program (on Windows; elsewhere from the game's folder down);
    /// - on Windows, the program and every folder above it up to the drive's root (whoever controls a parent can move the
    ///   game's folder away and put another in its place) are judged by <see cref="Judge"/>: owned by a trusted account,
    ///   and changeable by trusted accounts only. Permissions that cannot be read refuse the start.
    /// </summary>
    internal static string? Unsafe(string dir, string exe)
    {
        string full, root;
        try { full = Path.GetFullPath(exe); root = Path.GetFullPath(dir); } catch { return "missing"; }
        // the text first, then the links from the drive's root down, and only then the file: a parent that became a link
        // since the list was made is not followed even to see whether the program is there
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Norm(full).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase)) return "missing";
        // the program and its folders up to the game's folder, then (Windows) on to the drive's root
        var chain = new List<string> { full };
        for (var d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
        {
            var mine = Norm(d).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase);
            if (!mine && !OperatingSystem.IsWindows()) break;
            chain.Add(d);
        }
        for (var i = chain.Count - 1; i >= 1; i--)
        {
            // a folder that is gone is a game that is gone (its parent, checked just before, is not a link)
            if (!Directory.Exists(chain[i])) return "missing";
            if (SteamLibrary.IsLink(chain[i])) return "is reached through a link";
        }
        if (!File.Exists(full)) return "missing";
        if (SteamLibrary.IsLink(full)) return "is reached through a link";
        if (OperatingSystem.IsWindows())
        {
            if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is not { } me) return "permissions could not be read";
            foreach (var p in chain)
                if (Permissions(p, me) is { } why) return why;
        }
        return null;
    }

    /// <summary>The owner and allow rules of a file or folder, judged by <see cref="Judge"/>; unreadable ones refuse.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? Permissions(string path, string me)
    {
        try
        {
            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return "permissions could not be read";
            var allows = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow)
                .Select(r => new Allow(r.IdentityReference.Value, (uint)(int)r.FileSystemRights, r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)))
                .ToList();
            var isRoot = Path.GetPathRoot(path) is { } r && Norm(r).Equals(Norm(path), StringComparison.OrdinalIgnoreCase);
            return Judge(owner.Value, allows, isRoot, me);
        }
        catch { return "permissions could not be read"; }
    }

    /// <summary>
    /// The rules for one file or folder on a program's path, deny by default:
    /// - its owner is the agent's user (<paramref name="me"/>), SYSTEM, Administrators or TrustedInstaller;
    /// - every allow rule with a right in <see cref="ReplaceRights"/> belongs to one of those four, except
    ///   - a rule that only applies to what is created inside (inherit-only, such as CREATOR OWNER's): it is not a right on
    ///     this folder, and where such a rule reaches a folder or the program on the path it is judged there, as an
    ///     inherited rule;
    ///   - at a drive's root only, Authenticated Users with nothing beyond <see cref="RootCreateRights"/>.
    /// Deny rules are not needed for this: they only take rights away.
    /// </summary>
    internal static string? Judge(string owner, IEnumerable<Allow> allows, bool root, string me)
    {
        bool Trusted(string sid) => sid == me || TrustedSids.Contains(sid);
        if (!Trusted(owner)) return "is owned by another account";
        foreach (var a in allows)
        {
            var rights = a.Rights & ReplaceRights;
            if (rights == 0 || Trusted(a.Sid) || a.InheritOnly) continue;
            if (root && a.Sid == AuthenticatedUsers && (rights & ~RootCreateRights) == 0) continue;
            return "can be changed by other accounts";
        }
        return null;
    }
}
