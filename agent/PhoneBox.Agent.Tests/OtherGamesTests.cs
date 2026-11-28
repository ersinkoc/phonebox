using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Games found outside Steam: the launchers' records, game folders and the program chosen for each game.</summary>
public class OtherGamesTests : IDisposable
{
    // on Windows under your own local app data: a program is started only from a path no other account may change, and
    // the temp folder can carry other accounts' rights (a sandbox's, another user's)
    private readonly string _dir = Path.Combine(OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetTempPath(),
        $"phonebox-games-{Guid.NewGuid():N}");

    public OtherGamesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeExe(string relative, int bytes)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void WordsSplitNamesAndDigits()
    {
        Assert.Equal(["fifa", "23"], OtherGames.Words("FIFA23"));
        Assert.Equal(["red", "dead", "redemption", "2"], OtherGames.Words("Red Dead Redemption 2"));
        Assert.Equal(["gta", "4", "lcpdfr"], OtherGames.Words("GTA_4.LCPDFR"));
    }

    [Theory]
    [InlineData(@"C:\G\unins000.exe")]
    [InlineData(@"C:\G\Setup.exe")]
    [InlineData(@"C:\G\UnityCrashHandler64.exe")]
    [InlineData(@"C:\G\_CommonRedist\DirectX\DXSETUP.exe")]
    [InlineData(@"C:\G\__Installer\Touchup.exe")]
    [InlineData(@"C:\G\EasyAntiCheat_Setup.exe")]
    [InlineData(@"C:\G\gta4Browser.exe")]
    [InlineData(@"C:\G\EACLauncher.exe")]
    [InlineData(@"C:\G\BEService_x64.exe")]
    [InlineData(@"C:\G\CrashReportClient.exe")]
    [InlineData(@"C:\G\UE4PrereqSetup_x64.exe")]
    [InlineData(@"C:\G\VC_redist.x64.exe")]
    [InlineData(@"C:\G\DedicatedServer.exe")]
    [InlineData(@"C:\G\AutoUpdater.exe")]
    public void InstallersAndHelpersAreNeverTheGame(string file) => Assert.True(OtherGames.NotTheGame(file));

    // whole words only: a helper's name inside a game's name does not throw the game out
    [Theory]
    [InlineData(@"C:\G\CrashBandicoot.exe")]
    [InlineData(@"C:\G\Crash Bandicoot N. Sane Trilogy.exe")]
    [InlineData(@"C:\G\BeachRacing.exe")]
    [InlineData(@"C:\G\Observer.exe")]
    [InlineData(@"C:\G\ObserverSystemRedux.exe")]
    [InlineData(@"C:\G\Terror.exe")]
    [InlineData(@"C:\G\Toolbox Heroes.exe")]
    [InlineData(@"C:\G\GTAIV.exe")]
    public void AGameNamedLikeAHelperIsStillTheGame(string file) => Assert.False(OtherGames.NotTheGame(file));

    [Fact]
    public void NamePartsSplitAtCaseDigitsAndPunctuation()
    {
        Assert.Equal(["unity", "crash", "handler", "64"], OtherGames.NameParts("UnityCrashHandler64"));
        Assert.Equal(["ea", "anti", "cheat", "game", "service"], OtherGames.NameParts("EAAntiCheat.GameService"));
        Assert.Equal(["beach", "racing"], OtherGames.NameParts("BeachRacing"));
        Assert.Equal(["dxsetup"], OtherGames.NameParts("DXSETUP"));
    }

    [Fact]
    public void ALaunchersRecordStartsOnlyAProgramInsideTheGamesFolder()
    {
        var game = Path.Combine(_dir, "Game");
        MakeExe("Game/bin/game.exe", 100);
        MakeExe("Game/readme.txt", 10);
        MakeExe("Elsewhere/other.exe", 100);
        Assert.Equal(Path.Combine(game, "bin", "game.exe"), OtherGames.ProgramIn(game, @"bin\game.exe".Replace('\\', Path.DirectorySeparatorChar)));
        Assert.Equal(Path.Combine(game, "bin", "game.exe"), OtherGames.ProgramIn(game, Path.Combine(game, "bin", "game.exe")));
        Assert.Null(OtherGames.ProgramIn(game, "readme.txt"));                                   // not a program
        Assert.Null(OtherGames.ProgramIn(game, Path.Combine(_dir, "Elsewhere", "other.exe")));   // outside the game's folder
        Assert.Null(OtherGames.ProgramIn(game, Path.Combine("..", "Elsewhere", "other.exe")));
        Assert.Null(OtherGames.ProgramIn(game, "missing.exe"));
        Assert.Null(OtherGames.ProgramIn(game, null));
    }

    [Fact]
    public void TheProgramNamedLikeTheFolderWinsOverABiggerOne()
    {
        MakeExe("FIFA 23/FIFA23.exe", 2000);
        MakeExe("FIFA 23/Launcher.exe", 900_000);
        MakeExe("FIFA 23/unins000.exe", 5_000_000);
        Assert.EndsWith("FIFA23.exe", OtherGames.PickExe(Path.Combine(_dir, "FIFA 23"), "FIFA 23"));
    }

    [Fact]
    public void WithoutANameMatchTheLargestProgramWinsAndDepthIsBounded()
    {
        MakeExe("GTA 4 LCPDFR/GTA 4 LCPDFR/Grand Theft Auto IV/LaunchGTAIV.exe", 3000);
        MakeExe("GTA 4 LCPDFR/GTA 4 LCPDFR/Grand Theft Auto IV/GTAIV.exe", 900_000);
        MakeExe("GTA 4 LCPDFR/GTA 4 LCPDFR/Grand Theft Auto IV/gta4Browser.exe", 5000);  // the game's tool, named like it
        MakeExe("GTA 4 LCPDFR/a/b/c/d/Huge.exe", 9_000_000); // four folders down: not looked at
        Assert.EndsWith("GTAIV.exe", OtherGames.PickExe(Path.Combine(_dir, "GTA 4 LCPDFR"), "GTA 4 LCPDFR"));
        Assert.Null(OtherGames.PickExe(Path.Combine(_dir, "nothing"), "nothing"));
    }

    [Fact]
    public void EpicManifestsGiveTheirGamesWithTheLauncherAddress()
    {
        var game = Path.Combine(_dir, "Ember Road");
        MakeExe("Ember Road/Ember.exe", 100);
        var manifests = Path.Combine(_dir, "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "a.item"), System.Text.Json.JsonSerializer.Serialize(new
        {
            DisplayName = "Ember Road", InstallLocation = game, LaunchExecutable = "Ember.exe", AppCategories = new[] { "public", "games", "applications" },
            CatalogNamespace = "ns1", CatalogItemId = "item1", AppName = "Ember",
        }));
        // the launcher's own add-ons are not games; a broken manifest is skipped
        File.WriteAllText(Path.Combine(manifests, "b.item"), """{"DisplayName": "Plugin", "InstallLocation": "C:\\x", "AppCategories": ["plugins"]}""");
        File.WriteAllText(Path.Combine(manifests, "c.item"), "{ not json");
        var g = Assert.Single(OtherGames.FromEpic(manifests));
        Assert.Equal(("Ember Road", "epic"), (g.Name, g.Source));
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Aitem1%3AEmber?action=launch&silent=true", g.Url);
        Assert.Equal(Path.Combine(game, "Ember.exe"), g.Exe);
    }

    private string Manifest(string folder, string name, string? location, string? exe)
    {
        var manifests = Path.Combine(_dir, folder);
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, Guid.NewGuid().ToString("N") + ".item"), System.Text.Json.JsonSerializer.Serialize(new
        {
            DisplayName = name, InstallLocation = location, LaunchExecutable = exe, AppCategories = new[] { "games" },
            CatalogNamespace = "ns1", CatalogItemId = "item1", AppName = "App1",
        }));
        return manifests;
    }

    [Fact]
    public void AnEpicGameWhoseFolderIsGoneIsNotListedButOneWithoutAProgramIs()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Kept"));
        var manifests = Manifest("M", "Kept", Path.Combine(_dir, "Kept"), "missing.exe");   // starts by the launcher's address
        Manifest("M", "Gone", Path.Combine(_dir, "Gone"), "gone.exe");
        var g = Assert.Single(OtherGames.FromEpic(manifests));
        Assert.Equal("Kept", g.Name);
        Assert.Null(g.Exe);
        Assert.NotNull(g.Url);
    }

    [Theory]
    [InlineData(@"\\server\share\Games\Ember")]
    [InlineData(@"\\?\UNC\server\share\Games\Ember")]
    [InlineData(@"//server/share/Games/Ember")]
    [InlineData(@"\\.\C:\Games\Ember")]
    [InlineData(@"Games\Ember")]
    [InlineData("")]
    [InlineData(null)]
    public void AnInstallFolderOffThisComputerIsNotLocal(string? dir) => Assert.False(OtherGames.Local(dir));

    [Fact]
    public void OnlyAFixedDiskIsLocal()
    {
        Assert.True(OtherGames.Local(_dir));
        if (!OperatingSystem.IsWindows()) return; // drive letters
        Assert.False(OtherGames.Local(@"C:Games\Ember"));
        foreach (var kind in new[] { DriveType.Network, DriveType.Removable, DriveType.CDRom, DriveType.NoRootDirectory, DriveType.Unknown })
            Assert.False(OtherGames.Local(@"Z:\Games\Ember", _ => kind));
        Assert.True(OtherGames.Local(@"Z:\Games\Ember", root => root == @"Z:\" ? DriveType.Fixed : DriveType.Unknown));
    }

    [Fact]
    public void ALaunchersRecordOfAGameOnANetworkShareIsNotRead()
    {
        // a share's folder would be read (connecting to it) if the check came after; nothing is listed from it. The link
        // check is the first thing that touches a launcher's folder (OtherGames.Usable): it is never asked about a share
        const string share = @"\\phonebox-test.invalid\share\Ember Road";
        var probes = new List<string>();
        bool Probe(string p) { probes.Add(p); return SteamLibrary.IsLink(p); }
        var manifests = Manifest("Share", "Ember Road", share, "Ember.exe");
        Assert.Empty(OtherGames.FromEpic(manifests, Probe));
        Assert.Empty(OtherGames.FromKeys("gog", [Key("1", new() { ["path"] = share, ["gameName"] = "Ember Road", ["exe"] = "Ember.exe" })], Probe));
        Assert.Empty(OtherGames.FromKeys("ubisoft", [Key("635", new() { ["InstallDir"] = share })], Probe));
        Assert.Empty(OtherGames.FromKeys("ea", [Key("Ember Road", new() { ["Install Dir"] = share })], Probe));
        Assert.Empty(OtherGames.FromKeys("rockstar", [Key("Ember Road", new() { ["InstallFolder"] = @"\\?\UNC\phonebox-test.invalid\share\Ember Road" })], Probe));
        Assert.Empty(probes);

        // a folder on this computer is checked, down to the game's folder, before it is listed
        var game = Path.Combine(_dir, "Ember Road");
        Directory.CreateDirectory(game);
        Assert.Single(OtherGames.FromEpic(Manifest("Here", "Ember Road", game, "Ember.exe"), Probe));
        Assert.Equal(game, probes[^1]);
        if (OperatingSystem.IsWindows()) Assert.Contains(_dir, probes);   // and every folder above it
    }

    private static OtherGames.Key Key(string name, Dictionary<string, string> values) => new(name, k => values.GetValueOrDefault(k));

    [Fact]
    public void ALaunchersGameFolderReachedThroughALinkIsNotRead()
    {
        // a junction above the game's folder (on Windows: it could lead to a share or a removable drive), and the game's
        // folder itself a link; the same game in a plain folder is listed by every launcher
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_dir, "Linked");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "Ember Road"));
            File.WriteAllBytes(Path.Combine(outside, "Ember Road", "Ember.exe"), new byte[100]);
            MakeExe("Plain/Ember Road/Ember.exe", 100);
            Junction(link, outside);
            var above = Path.Combine(link, "Ember Road");
            var plain = Path.Combine(_dir, "Plain", "Ember Road");
            Assert.True(File.Exists(Path.Combine(above, "Ember.exe")));
            IEnumerable<OtherGames.Found> All(string dir) =>
                OtherGames.FromEpic(Manifest(Guid.NewGuid().ToString("N"), "Ember Road", dir, "Ember.exe"))
                .Concat(OtherGames.FromKeys("gog", [Key("1", new() { ["path"] = dir, ["gameName"] = "Ember Road", ["exe"] = "Ember.exe" })]))
                .Concat(OtherGames.FromKeys("ubisoft", [Key("635", new() { ["InstallDir"] = dir })]))
                .Concat(OtherGames.FromKeys("ea", [Key("Ember Road", new() { ["Install Dir"] = dir })]))
                .Concat(OtherGames.FromKeys("rockstar", [Key("Ember Road", new() { ["InstallFolder"] = dir })]));
            Assert.Equal(["epic", "gog", "ubisoft", "ea", "rockstar"], All(plain).Select(g => g.Source));
            Assert.True(OtherGames.Usable(plain));
            Assert.Empty(All(link));                                    // the game's folder is the link
            Assert.False(OtherGames.Usable(link));
            if (!OperatingSystem.IsWindows()) return;                   // above the game's folder: checked on Windows
            Assert.Empty(All(above));
            Assert.False(OtherGames.Usable(above));
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void ALaunchersFolderMustBeAFolderOfThisComputerThatIsStillThere()
    {
        Assert.False(OtherGames.Usable(Path.Combine(_dir, "Gone")));
        Assert.False(OtherGames.Usable(@"\\phonebox-test.invalid\share\Ember Road"));
        Assert.True(OtherGames.Usable(_dir));
        Assert.True(OtherGames.Usable(_dir + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows()) Assert.False(OtherGames.Usable(Path.GetPathRoot(_dir)));   // a drive's root is no game's folder
    }

    [Fact]
    public void AGogRecordNamingAnInstallerGetsTheGamesProgramInstead()
    {
        var game = Path.Combine(_dir, "Ember Road");
        MakeExe("Ember Road/Ember.exe", 100);
        MakeExe("Ember Road/setup_ember_road.exe", 9000);
        var named = OtherGames.FromKeys("gog", [Key("1", new() { ["path"] = game, ["gameName"] = "Ember Road", ["exe"] = "setup_ember_road.exe" })]);
        Assert.Equal(Path.Combine(game, "Ember.exe"), Assert.Single(named).Exe);
        // a program that is the game still wins over the one picked
        MakeExe("Ember Road/bin/Different.exe", 100);
        var right = OtherGames.FromKeys("gog", [Key("1", new() { ["path"] = game, ["gameName"] = "Ember Road", ["exe"] = @"bin\Different.exe".Replace('\\', Path.DirectorySeparatorChar) })]);
        Assert.Equal(Path.Combine(game, "bin", "Different.exe"), Assert.Single(right).Exe);
    }

    [Fact]
    public void NoEpicManifestsFolderIsNoGamesButAFolderThatCannotBeListedFailsTheLook()
    {
        Assert.Empty(OtherGames.FromEpic(Path.Combine(_dir, "no-such-folder", "Manifests")));
        if (!OperatingSystem.IsWindows()) return; // a file where the folder should be: a listing error on Windows
        var file = MakeExe("Manifests", 10);
        Assert.ThrowsAny<IOException>(() => OtherGames.FromEpic(file).ToList());

        // a look whose Epic listing fails keeps the last list (and is tried again after RetryMs)
        var game = Program("Old Fort");
        var now = 1_000_000L;
        var fail = false;
        var others = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
        {
            Clock = () => Interlocked.Read(ref now),
            Launchers = () => Volatile.Read(ref fail) ? OtherGames.FromEpic(file) : [game],
            Disks = () => [],
        };
        others.List();
        Assert.True(SpinWait.SpinUntil(() => others.Looks == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(["Old Fort"], others.List().Select(g => g.Name));
        Volatile.Write(ref fail, true);
        Interlocked.Add(ref now, OtherGames.RescanMs + 1);
        others.List();
        Assert.True(SpinWait.SpinUntil(() => others.Looks == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(["Old Fort"], others.List().Select(g => g.Name));
    }

    [Fact]
    public void AProgramNamedThroughALinkInTheGamesFolderIsNotTaken()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var game = Path.Combine(_dir, "Ember Road");
        var link = Path.Combine(game, "bin");
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "Ember.exe"), new byte[100]);
            MakeExe("Ember Road/Ember.exe", 100);
            Junction(link, outside);
            Assert.True(File.Exists(Path.Combine(link, "Ember.exe")));
            Assert.Null(OtherGames.ProgramIn(game, Path.Combine("bin", "Ember.exe")));
            Assert.True(OtherGames.LinkBelow(game, Path.Combine(link, "Ember.exe")));
            Assert.Equal(Path.Combine(game, "Ember.exe"), OtherGames.ProgramIn(game, "Ember.exe"));   // the program in the folder itself
            Assert.False(OtherGames.LinkBelow(game, Path.Combine(game, "Ember.exe")));
            // an Epic record naming the linked program: listed by the launcher's address only
            var g = Assert.Single(OtherGames.FromEpic(Manifest("Linked", "Ember Road", game, Path.Combine("bin", "Ember.exe"))));
            Assert.Null(g.Exe);
            Assert.NotNull(g.Url);
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void LauncherKeysGiveTheirGames()
    {
        var bf = Path.Combine(_dir, "Battlefield V");
        MakeExe("Battlefield V/bfv.exe", 5000);
        MakeExe("Battlefield V/__Installer/Touchup.exe", 9000);
        var key = (string name, Dictionary<string, string> v) => new OtherGames.Key(name, k => v.GetValueOrDefault(k));
        var ea = OtherGames.FromKeys("ea", [key("Battlefield V", new() { ["Install Dir"] = bf + Path.DirectorySeparatorChar }), key("Gone", new() { ["Install Dir"] = @"Z:\nope" })]).ToList();
        Assert.Equal("Battlefield V", Assert.Single(ea).Name);
        Assert.EndsWith("bfv.exe", ea[0].Exe);

        // Rockstar: the launcher itself and a game without a folder (bought on Steam) are not listed
        Assert.Empty(OtherGames.FromKeys("rockstar", [key("Launcher", new() { ["InstallFolder"] = bf }), key("GTA V Enhanced", new())]));

        var ubi = Assert.Single(OtherGames.FromKeys("ubisoft", [key("635", new() { ["InstallDir"] = bf }), key("x/../1", new() { ["InstallDir"] = bf })]));
        Assert.Equal("uplay://launch/635/0", ubi.Url);
    }

    [Fact]
    public void ScanKeepsOneGamePerFolderAndLeavesSteamGamesToSteam()
    {
        OtherGames.Found F(string name, string dir) => new(OtherGames.IdOf("t", dir), name, "t", dir, dir + @"\a.exe", null);
        var list = OtherGames.Scan([F("Battlefield V", @"D:\Epic Games\Battlefield V\"), F("BF V again", @"d:/epic games/battlefield v"),
            F("FC 26", @"D:\SteamLibrary\steamapps\common\FC 26")], []);
        Assert.Equal("Battlefield V", Assert.Single(list).Name);
    }

    [Fact]
    public void AnIdIsStableAndTellsNothingOfThePath()
    {
        var a = OtherGames.IdOf("folder", @"D:\Games\FIFA 23\FIFA23.exe");
        Assert.Equal(a, OtherGames.IdOf("folder", @"d:\games\fifa 23\fifa23.exe"));
        Assert.Matches("^x:[0-9a-f]{16}$", a);
        Assert.NotEqual(a, OtherGames.IdOf("epic", @"D:\Games\FIFA 23\FIFA23.exe"));
    }

    [Fact]
    public void TheDrivesAreLookedThroughForWhatOnlyGamesCarry()
    {
        MakeExe("Games/Red Dead Redemption 2/RDR2.exe", 5000);
        MakeExe("Games/Red Dead Redemption 2/x64/data.rpf", 10);          // a Rockstar archive
        MakeExe("GTA 4 LCPDFR/GTA 4 LCPDFR/Grand Theft Auto IV/GTAIV.exe", 9000);
        MakeExe("GTA 4 LCPDFR/GTA 4 LCPDFR/Grand Theft Auto IV/GTAIVGDF.dll", 10); // registers the game with Windows
        MakeExe("Program Files/Electronic Arts/EA Desktop/EADesktop.exe", 9000);   // the launcher itself
        MakeExe("Program Files/Electronic Arts/EA Desktop/steam_api64.dll", 10);
        MakeExe("Program Files/Electronic Arts/Battlefield V/bfv.exe", 9000);
        MakeExe("Program Files/Electronic Arts/Battlefield V/bink2w64.dll", 10);
        MakeExe("CorelDRAW/corel.exe", 9000);                               // a program: no game sign
        MakeExe("CorelDRAW/openal32.dll", 10);                              // one weak sign is not enough
        MakeExe("uv-cache/archive/pygame/game.exe", 10);                    // a package cache is never looked into
        MakeExe("uv-cache/archive/pygame/steam_api.dll", 10);
        MakeExe("SteamLibrary/steamapps/common/FC 26/fc26.exe", 10);        // Steam's games come from Steam
        MakeExe("SteamLibrary/steamapps/common/FC 26/steam_api64.dll", 10);
        var found = OtherGames.FromDrives([_dir]).OrderBy(g => g.Name).ToList();
        Assert.Equal(["Battlefield V", "GTA 4 LCPDFR", "Red Dead Redemption 2"], found.Select(g => g.Name));
        Assert.All(found, g => Assert.Equal("drive", g.Source));
        Assert.EndsWith("GTAIV.exe", found[1].Exe);
    }

    [Fact]
    public void TwoWeakSignsMakeAGameButOneDoesNot()
    {
        var old = Path.Combine(_dir, "Old");
        MakeExe("Old/old.exe", 100);
        MakeExe("Old/d3dx9_43.dll", 10);
        Assert.False(OtherGames.LooksLikeAGame(old, old));
        MakeExe("Old/xinput1_3.dll", 10);
        Assert.True(OtherGames.LooksLikeAGame(old, old));
    }

    [Fact]
    public void WhatLooksLikeAGameButIsNotOneIsLeftOut()
    {
        // an anti-cheat installed as a service of its own (C:\Program Files\EA\AC)
        MakeExe("EA/AC/EAAntiCheat.GameService.exe", 9000);
        MakeExe("EA/AC/EAAntiCheat.GameService.dll", 10);
        // a project folder keeping a copy of a game's files next to its own programs, and a source tree
        MakeExe("Projects/tool/tool.exe", 9000);
        MakeExe("Projects/fifa-mod/backup/amd_ags_x64.dll", 10);
        MakeExe("Repo/.git/HEAD", 10);
        MakeExe("Repo/bin/app.exe", 9000);
        MakeExe("Repo/bin/intro.bik", 10);    // a game's sign next to its program: only the .git folder keeps it out
        // a launcher's own parts; its games folder is looked into
        MakeExe("Ubisoft Game Launcher/shareplay/stream.exe", 9000);
        MakeExe("Ubisoft Game Launcher/shareplay/bink2w64.dll", 10);
        MakeExe("Ubisoft Game Launcher/games/Wild Lands/WildLands.exe", 9000);
        MakeExe("Ubisoft Game Launcher/games/Wild Lands/upc_r2_loader64.dll", 10);
        Assert.Equal(["Wild Lands"], OtherGames.FromDrives([_dir]).Select(g => g.Name));
    }

    [Fact]
    public void AFolderInsideAGameIsNotAnotherGame()
    {
        OtherGames.Found F(string name, string dir) => new(OtherGames.IdOf("t", dir), name, "t", dir, dir + @".exe", null);
        var list = OtherGames.Scan([F("Battlefield V", @"D:\Epic Games\Battlefield V")], [F("Battlefield V tools", @"D:\Epic Games\Battlefield V\Tools"), F("Other", @"D:\Other")]);
        Assert.Equal(["Battlefield V", "Other"], list.Select(g => g.Name));
    }

    [Fact]
    public void AGameStartedByOneOfYourOwnShortcutsNamesThatShortcut()
    {
        OtherGames.Found F(string name, string dir) => new(OtherGames.IdOf("t", dir), name, "drive", dir, dir + "/a.exe", null);
        var programs = new[] { ("gta", @"d:\gta 4 lcpdfr\GTA 4 LCPDFR\Grand Theft Auto IV\LaunchGTAIV.exe"), ("other", @"D:\GTA 4 LCPDFR2\x.exe") };
        // the phone leaves the found game out only in a widget that shows that shortcut as one of its own games
        Assert.Equal(["gta"], OtherGames.StartedBy(F("GTA 4 LCPDFR", "D:/GTA 4 LCPDFR"), programs));
        Assert.Empty(OtherGames.StartedBy(F("Red Dead Redemption 2", "D:/Games/Red Dead Redemption 2"), programs));
    }

    [Fact]
    public void AGameOnADriveIsKnownByItsFolderAndWeakSignsNeedACompanion()
    {
        MakeExe("Racer/racer.exe", 5000);
        MakeExe("Racer/data/level.toc", 10);   // LaTeX writes .toc too: one weak sign alone is not a game
        Assert.Empty(OtherGames.FromDrives([_dir]));
        MakeExe("Racer/data/level.sb", 10);
        var g = Assert.Single(OtherGames.FromDrives([_dir]));
        // the id is the folder's, so another program picked later keeps it (and a favorite with it)
        Assert.Equal(OtherGames.IdOf("drive", Path.Combine(_dir, "Racer")), g.Id);
        MakeExe("Racer/Racer2.exe", 9_000_000);
        Assert.Equal(g.Id, Assert.Single(OtherGames.FromDrives([_dir])).Id);
    }

    [Fact]
    public void AFolderBudgetStopsTakingAtItsEndAndAtItsDrives()
    {
        var drive = new OtherGames.Budget(3);
        var game = new OtherGames.Budget(2, drive);
        Assert.True(game.Take());
        Assert.True(game.Take());
        Assert.False(game.Take());   // the game's own budget is spent
        var next = new OtherGames.Budget(5, drive);
        Assert.True(next.Take());
        Assert.False(next.Take());   // the drive's is spent
    }

    [Fact]
    public void EveryFolderLookedIntoTakesFromTheBudgetTheLastLevelToo()
    {
        MakeExe("Racer/racer.exe", 5000);
        MakeExe("Racer/a/b/c/steam_api.dll", 10);   // three folders down: the last level looked into
        var racer = Path.Combine(_dir, "Racer");
        // a spent budget reads nothing, not even the game's own folder
        Assert.Null(OtherGames.PickExe(racer, "Racer", new OtherGames.Budget(0)));
        Assert.False(OtherGames.LooksLikeAGame(racer, racer, new OtherGames.Budget(0)));
        Assert.EndsWith("racer.exe", OtherGames.PickExe(racer, "Racer", new OtherGames.Budget(1)));
        // Racer, a, b and c: four folders; with three the last one is not read
        Assert.False(OtherGames.LooksLikeAGame(racer, racer, new OtherGames.Budget(3)));
        Assert.True(OtherGames.LooksLikeAGame(racer, racer, new OtherGames.Budget(4)));
        // a drive's budget: its top folder alone spends it, so no game is looked into
        var drive = new OtherGames.Budget(1);
        Assert.True(drive.Take());
        Assert.True(drive.Spent);
        Assert.True(new OtherGames.Budget(5, drive).Spent);
    }

    private static IEnumerable<string> Endless(Action<int> pulled, int failAt = -1)
    {
        for (var i = 0; ; i++)
        {
            pulled(i);
            if (i == failAt) throw new IOException("the folder went away");
            yield return "e" + i;
        }
    }

    [Fact]
    public void AListingIsReadOnlyAsFarAsItsLimitAndEndsAtAnError()
    {
        var pulled = 0;
        Assert.Equal(["e0", "e1", "e2"], OtherGames.Entries(() => Endless(i => pulled = i + 1), 3));
        Assert.Equal(3, pulled);                     // not one entry more than the limit is read
        Assert.Equal(["e0", "e1"], OtherGames.Entries(() => Endless(_ => { }, failAt: 2), 10));
        Assert.Empty(OtherGames.Entries(() => throw new DirectoryNotFoundException(), 10));
    }

    [Fact]
    public void ALinkedGamesFolderOfALauncherIsNotWalked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_dir, "Ubisoft Game Launcher", "games");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "Far Game"));
            File.WriteAllBytes(Path.Combine(outside, "Far Game", "far.exe"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(outside, "Far Game", "upc_r2_loader64.dll"), new byte[10]);
            Directory.CreateDirectory(Path.Combine(_dir, "Ubisoft Game Launcher"));
            Junction(link, outside);
            Assert.True(Directory.Exists(Path.Combine(link, "Far Game")));
            Assert.Empty(OtherGames.FromDrives([_dir]));
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void WindowsOldAndInetpubAreNotLookedInto()
    {
        MakeExe("Windows.old/Games/Old Game/old.exe", 5000);
        MakeExe("Windows.old/Games/Old Game/steam_api.dll", 10);
        MakeExe("inetpub/site/site.exe", 5000);
        MakeExe("inetpub/site/bink2w64.dll", 10);
        Assert.Empty(OtherGames.FromDrives([_dir]));
    }

    // ------------------------------------------------------------------ starting a found game from the phone

    private readonly List<ProcessStartInfo> _started = [];

    private ConfigStore Config(string options)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$$"""{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{{{options}}}}]}]}""");
        return new ConfigStore(path);
    }

    private OtherGames Games(ConfigStore config, params OtherGames.Found[] found) =>
        new(config, NullLogger<OtherGames>.Instance) { Launchers = () => found, Disks = () => [], StartProcess = _started.Add };

    private OtherGames.Found Program(string name)
    {
        var exe = MakeExe($"{name}/{name}.exe", 100);
        var dir = Path.GetDirectoryName(exe)!;
        return new(OtherGames.IdOf("drive", dir), name, "drive", dir, exe, null);
    }

    // both options: "Let the phone start games" and "Let the phone start the games found outside Steam"
    private const string BothOn = """{"allowLaunch":true,"allowLaunchOthers":true}""";

    [Fact]
    public void StartingAGameFoundOutsideSteamIsItsOwnOptionOffByDefault()
    {
        var game = Program("Old Fort");
        // the Steam option alone does not let the phone start a program found here
        foreach (var options in new[] { """{"allowLaunch":true}""", """{"allowLaunch":true,"allowLaunchOthers":false}""" })
            Assert.Equal("othersoff", Games(Config(options), game).Launch(null, "p/w", game.Id)?.Code);
        // nor the option of another widget, or none named
        var on = Games(Config(BothOn), game);
        Assert.Equal("off", on.Launch(null, "p/other", game.Id)?.Code);
        Assert.Equal("off", on.Launch(null, null, game.Id)?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void WithStartingGamesOffNoGameFoundOutsideSteamStarts()
    {
        // "Let the phone start games" off: the library is only shown, whatever the option for the games found here says
        var game = Program("Old Fort");
        foreach (var options in new[] { "{}", """{"allowLaunchOthers":true}""", """{"allowLaunch":false,"allowLaunchOthers":true}""" })
            Assert.Equal("off", Games(Config(options), game).Launch(null, "p/w", game.Id)?.Code);
        Assert.Empty(_started);
        Assert.Null(Games(Config(BothOn), game).Launch(null, "p/w", game.Id));
        Assert.Single(_started);
    }

    [Fact]
    public void OnlyAGameOfTheListStartsAndOnlyByItsId()
    {
        var game = Program("Old Fort");
        var on = Games(Config(BothOn), game);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", "x:0000000000000000")?.Code);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", game.Exe)?.Code);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", null)?.Code);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", game.Id + " & calc")?.Code);
        Assert.Empty(_started);

        Assert.Null(on.Launch(null, "p/w", game.Id));
        var psi = Assert.Single(_started);
        Assert.Equal(Path.GetFullPath(game.Exe!), psi.FileName);
        Assert.Equal(game.Dir, psi.WorkingDirectory);
        Assert.Empty(psi.ArgumentList);
        Assert.True(string.IsNullOrEmpty(psi.Arguments));
    }

    [Fact]
    public void ALaunchersGameStartsByItsAddressAndAProgramThatIsGoneDoesNot()
    {
        var epic = new OtherGames.Found(OtherGames.IdOf("epic", Path.Combine(_dir, "Ember")), "Ember Road", "epic", Path.Combine(_dir, "Ember"), null,
            "com.epicgames.launcher://apps/ns%3Aitem%3AEmber?action=launch&silent=true");
        var gone = new OtherGames.Found(OtherGames.IdOf("gog", Path.Combine(_dir, "Gone")), "Gone", "gog", Path.Combine(_dir, "Gone"), Path.Combine(_dir, "Gone", "gone.exe"), null);
        var doc = Program("Doc") with { Exe = MakeExe("Doc/readme.txt", 10) };
        Directory.CreateDirectory(epic.Dir);
        var on = Games(Config(BothOn), epic, gone, doc);
        Assert.Null(on.Launch(null, "p/w", epic.Id));
        Assert.Equal(epic.Url, Assert.Single(_started).FileName);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", gone.Id)?.Code);
        Assert.Equal("notinstalled", on.Launch(null, "p/w", doc.Id)?.Code); // not a program: never handed to the shell
        Assert.Single(_started);
    }

    [Fact]
    public void ALaunchersGameRemovedSinceItWasFoundIsNotStartedByItsAddress()
    {
        // the list is up to ten minutes old: the game's folder is looked at again before the launcher's address is used
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_dir, "Linked");
        OtherGames.Found Epic(string dir) => new(OtherGames.IdOf("epic", dir), "Ember Road", "epic", dir, null, "com.epicgames.launcher://apps/ns%3Aitem%3AEmber?action=launch&silent=true");
        try
        {
            var kept = Epic(Path.Combine(_dir, "Ember Road"));
            Directory.CreateDirectory(kept.Dir);
            var on = Games(Config(BothOn), kept);
            Assert.Null(on.Launch(null, "p/w", kept.Id));
            Assert.Single(_started);
            Directory.Delete(kept.Dir);                                   // uninstalled since
            Assert.Equal("notinstalled", on.Launch(null, "p/w", kept.Id)?.Code);

            // its folder now reached through a link (above it on Windows; elsewhere the folder itself)
            Directory.CreateDirectory(Path.Combine(outside, "Ember Road"));
            Junction(link, outside);
            var linked = Epic(OperatingSystem.IsWindows() ? Path.Combine(link, "Ember Road") : link);
            Assert.Equal("notinstalled", Games(Config(BothOn), linked).Launch(null, "p/w", linked.Id)?.Code);
            Assert.Single(_started);
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void AFoundGamesIconIsNotReadThroughALinkAboveItsFolder()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_dir, "Linked");
        try
        {
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "Ember Road")).FullName, "Ember.exe"), new byte[100]);
            MakeExe("Plain/Ember Road/Ember.exe", 100);
            Junction(link, outside);
            OtherGames.Found Gog(string dir) => new(OtherGames.IdOf("gog", dir), "Ember Road", "gog", dir, Path.Combine(dir, "Ember.exe"), null);
            var plain = Gog(Path.Combine(_dir, "Plain", "Ember Road"));
            // a link above the game's folder (Windows; elsewhere the game's folder itself is the link)
            var linked = Gog(OperatingSystem.IsWindows() ? Path.Combine(link, "Ember Road") : link) with { Exe = Path.Combine(link, "Ember Road", "Ember.exe") };
            var read = new List<string>();
            var games = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
            {
                Launchers = () => [plain, linked], Disks = () => [], ReadIcon = exe => { read.Add(exe); return [1]; },
            };
            Assert.NotNull(games.Cover(plain.Id));
            Assert.Null(games.Cover(linked.Id));
            Assert.Equal([plain.Exe!], read);
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void ASourceTreeWithManyFilesAtItsTopIsNotAGame()
    {
        // more files at the top than the walk reads: the .git folder, listed after them, is looked for first
        var repo = Path.Combine(_dir, "Repo");
        MakeExe("Repo/app.exe", 9000);
        MakeExe("Repo/unityplayer.dll", 10);
        for (var i = 0; i < OtherGames.MaxFilesPerGame * 2; i++) File.WriteAllBytes(Path.Combine(repo, $"zz-{i:D5}.txt"), []);
        if (OperatingSystem.IsWindows()) Assert.True(OtherGames.LooksLikeAGame(repo, repo));   // NTFS lists by name: the game's sign is read
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Assert.False(OtherGames.LooksLikeAGame(repo, repo));
        Directory.Delete(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: elsewhere");   // a worktree's or submodule's .git file
        Assert.False(OtherGames.LooksLikeAGame(repo, repo));
    }

    [Fact]
    public async Task TurningStartingOffWhileTheFirstLookRunsStopsTheStart()
    {
        var game = Program("Old Fort");
        var config = Config(BothOn);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var others = new OtherGames(config, NullLogger<OtherGames>.Instance)
        {
            Launchers = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return [game]; },
            Disks = () => [], StartProcess = _started.Add,
        };
        var start = Task.Run(() => others.Launch(null, "p/w", game.Id));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(config.Save("""{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunch":true,"allowLaunchOthers":false}}]}]}"""));
        release.Set();
        Assert.Equal("othersoff", (await start.WaitAsync(TimeSpan.FromSeconds(10)))?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void OnlyTheFirstLookIsWaitedFor()
    {
        var game = Program("Old Fort");
        using var release = new ManualResetEventSlim();
        var others = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
        {
            Launchers = () => { release.Wait(TimeSpan.FromSeconds(20)); return [game]; },
            Disks = () => [], FirstWaitMs = 1500,
        };
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Empty(others.List());                          // the first request waits for the look, then gives up
            Assert.True(clock.ElapsedMilliseconds >= 1000, $"waited {clock.ElapsedMilliseconds} ms");
            clock.Restart();
            Assert.Empty(others.List());                          // later ones do not wait: they get what is there
            Assert.Empty(others.List());
            Assert.True(clock.ElapsedMilliseconds < 1000, $"waited {clock.ElapsedMilliseconds} ms");
        }
        finally { release.Set(); }
    }

    [Fact]
    public void TheDrivesAreWalkedAtMostOnceAnHourAndTheLaunchersEveryTenMinutes()
    {
        var now = 1_000_000L;
        int launchers = 0, disks = 0;
        var others = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
        {
            Clock = () => Interlocked.Read(ref now),
            Launchers = () => { Interlocked.Increment(ref launchers); return []; },
            Disks = () => { Interlocked.Increment(ref disks); return []; },
        };
        void Look(int expected)
        {
            others.List();
            // the look has set its time (after reading the launchers) before the clock moves on
            Assert.True(SpinWait.SpinUntil(() => others.Looks == expected, TimeSpan.FromSeconds(10)));
            Assert.Equal(expected, Volatile.Read(ref launchers));
        }
        Look(1);
        Assert.Equal(1, disks);
        others.List();                                         // within ten minutes: nothing is read again
        Interlocked.Add(ref now, 11 * 60_000);
        Look(2);
        Assert.Equal(1, disks);                                // the launchers again, not the drives
        Interlocked.Add(ref now, 50 * 60_000);
        Look(3);
        Assert.Equal(2, disks);                                // an hour after the last walk
    }

    [Fact]
    public void ALookThatFailsKeepsTheLastListAndIsNotTriedOnEveryRequest()
    {
        var game = Program("Old Fort");
        var now = 1_000_000L;
        int launchers = 0;
        var fail = false;
        var others = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
        {
            Clock = () => Interlocked.Read(ref now),
            Launchers = () =>
            {
                Interlocked.Increment(ref launchers);
                if (Volatile.Read(ref fail)) throw new IOException("the registry could not be read");
                return [game];
            },
            Disks = () => [],
        };
        void Look(int expected)
        {
            others.List();
            Assert.True(SpinWait.SpinUntil(() => others.Looks == expected, TimeSpan.FromSeconds(10)));
        }
        Look(1);
        Assert.Equal(["Old Fort"], others.List().Select(g => g.Name));

        Volatile.Write(ref fail, true);
        Interlocked.Add(ref now, OtherGames.RescanMs + 1);
        Look(2);                                               // the look fails: the last list stays
        Assert.Equal(["Old Fort"], others.List().Select(g => g.Name));
        Interlocked.Add(ref now, OtherGames.RetryMs - 1);
        others.List();                                         // within the retry time: nothing is read again
        Assert.Equal(2, others.Looks);
        Assert.Equal(2, Volatile.Read(ref launchers));

        Volatile.Write(ref fail, false);
        Interlocked.Add(ref now, 1);
        Look(3);                                               // then it is tried again
        Assert.Equal(3, Volatile.Read(ref launchers));
        Assert.Equal(["Old Fort"], others.List().Select(g => g.Name));
    }

    [Fact]
    public void ALauncherRecordThatCannotBeReadDoesNotHideTheGamesOnTheDrives()
    {
        var onDrive = new OtherGames.Found("x:drive1", "Wild Lands", "drive", @"D:\Games\Wild Lands", @"D:\Games\Wild Lands\wild.exe", null);
        var now = 1_000_000L;
        int launchers = 0;
        var others = new OtherGames(Config("{}"), NullLogger<OtherGames>.Instance)
        {
            Clock = () => Interlocked.Read(ref now),
            Launchers = () => { Interlocked.Increment(ref launchers); throw new UnauthorizedAccessException("a key that is never readable"); },
            Disks = () => [onDrive],
        };
        others.List();
        Assert.True(SpinWait.SpinUntil(() => others.Looks == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(["Wild Lands"], others.List().Select(g => g.Name)); // the drives' games are listed all the same
        Interlocked.Add(ref now, OtherGames.RetryMs - 1);
        others.List();
        Assert.Equal(1, Volatile.Read(ref launchers));           // not read again on every request
        Interlocked.Add(ref now, 2);
        others.List();
        Assert.True(SpinWait.SpinUntil(() => others.Looks == 2, TimeSpan.FromSeconds(10))); // but again after the retry time
    }

    [Fact]
    public void ALinkedFolderIsNotWalked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "Far Game"));
            File.WriteAllBytes(Path.Combine(outside, "Far Game", "far.exe"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(outside, "Far Game", "steam_api64.dll"), new byte[10]);
            Directory.CreateDirectory(Path.Combine(_dir, "Games"));
            var link = Path.Combine(_dir, "Games", "Linked");
            if (OperatingSystem.IsWindows())
            {
                // a junction (a network share or another disk mounted in a folder looks the same): no special rights needed
                using var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, outside }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
                p.WaitForExit(10_000);
            }
            else Directory.CreateSymbolicLink(link, outside);
            Assert.True(Directory.Exists(Path.Combine(link, "Far Game")));
            Assert.Empty(OtherGames.FromDrives([_dir]));
        }
        finally { try { Directory.Delete(Path.Combine(_dir, "Games", "Linked")); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    // ------------------------------------------------------------------ a program other accounts could have put there

    private void Junction(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            p.WaitForExit(10_000);
        }
        else Directory.CreateSymbolicLink(link, target);
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void AProgramInAFolderOnlyThisUserCanChangeIsStarted()
    {
        var game = Program("Old Fort");
        Assert.Null(OtherGames.Unsafe(game.Dir, game.Exe!));
        Assert.Null(Games(Config(BothOn), game).Launch(null, "p/w", game.Id));
        Assert.Equal(Path.GetFullPath(game.Exe!), Assert.Single(_started).FileName);
    }

    [Fact]
    public void AProgramReachedThroughALinkIsNotStarted()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"phonebox-games-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_dir, "Linked");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "Far"));
            File.WriteAllBytes(Path.Combine(outside, "Far", "far.exe"), new byte[100]);
            Junction(link, outside);
            // the link above the game's folder, and the game's folder itself a link
            var below = new OtherGames.Found(OtherGames.IdOf("gog", Path.Combine(link, "Far")), "Far", "gog", Path.Combine(link, "Far"), Path.Combine(link, "Far", "far.exe"), null);
            var at = new OtherGames.Found(OtherGames.IdOf("gog", link), "Far 2", "gog", link, Path.Combine(link, "Far", "far.exe"), null);
            Assert.Equal("is reached through a link", OtherGames.Unsafe(at.Dir, at.Exe!));
            if (OperatingSystem.IsWindows()) Assert.Equal("is reached through a link", OtherGames.Unsafe(below.Dir, below.Exe!));
            var config = Config(BothOn);
            Assert.Equal("unsafe", Games(config, at).Launch(null, "p/w", at.Id)?.Code);
            if (OperatingSystem.IsWindows()) Assert.Equal("unsafe", Games(config, below).Launch(null, "p/w", below.Id)?.Code);
            Assert.Empty(_started);
        }
        finally { try { Directory.Delete(link); } catch { } try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void AProgramInAFolderEveryoneMayChangeIsNotStarted()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows permissions
        var game = Program("Shared Game");
        var sub = Program("Shared Game/bin/Shared Game") with { Dir = game.Dir };
        var other = Program("Users Game");
        Assert.Null(OtherGames.Unsafe(game.Dir, game.Exe!));

        // Everyone may modify the game's folder (and, inherited, the program in it)
        var dir = new DirectoryInfo(game.Dir);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        dir.SetAccessControl(acl);
        // Users may write the program itself only
        var file = new FileInfo(other.Exe!);
        var facl = file.GetAccessControl();
        facl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-5-32-545"),
            System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
        file.SetAccessControl(facl);

        Assert.Equal("can be changed by other accounts", OtherGames.Unsafe(game.Dir, game.Exe!));
        Assert.Equal("can be changed by other accounts", OtherGames.Unsafe(sub.Dir, sub.Exe!)); // a program further down, by inheritance
        Assert.Equal("can be changed by other accounts", OtherGames.Unsafe(other.Dir, other.Exe!));
        var on = Games(Config(BothOn), game, other);
        Assert.Equal("unsafe", on.Launch(null, "p/w", game.Id)?.Code);
        Assert.Equal("unsafe", on.Launch(null, "p/w", other.Id)?.Code);
        Assert.Empty(_started);
    }

    // the rules for each file and folder on the path, deny by default (OtherGames.Judge)
    private const string Me = "S-1-5-21-1000-2000-3000-1001", Another = "S-1-5-21-1000-2000-3000-1002";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static OtherGames.Allow A(string sid, long rights, bool inheritOnly = false) => new(sid, rights, inheritOnly);
    private const long Modify = 0x1301BF, ReadExecute = 0x1200A9, FullControl = 0x1F01FF;

    [Fact]
    public void OnlyTrustedAccountsMayOwnOrChangeAProgramsPath()
    {
        // Program Files: TrustedInstaller owns it; SYSTEM and Administrators may change it; Users read; CREATOR OWNER's
        // rule only reaches what is created inside
        var programFiles = new[] { A(TrustedInstaller, FullControl), A("S-1-5-18", Modify), A("S-1-5-32-544", Modify), A("S-1-5-32-545", ReadExecute),
            A("S-1-3-0", 0x10000000, inheritOnly: true), A("S-1-5-32-545", unchecked((uint)-1610612736), inheritOnly: true) };
        Assert.Null(OtherGames.Judge(TrustedInstaller, programFiles, false, Me));
        // your own folder
        Assert.Null(OtherGames.Judge(Me, [A(Me, FullControl), A("S-1-5-18", FullControl), A("S-1-5-32-544", FullControl)], false, Me));

        // a folder another account owns (it can give itself any right)
        Assert.Equal("is owned by another account", OtherGames.Judge(Another, [A(Me, FullControl)], false, Me));
        Assert.Equal("is owned by another account", OtherGames.Judge("S-1-5-32-545", [], false, Me));
        // a specific standard account (not only Everyone, Authenticated Users or Users) that may change it
        Assert.Equal("can be changed by other accounts", OtherGames.Judge(Me, [A(Me, FullControl), A(Another, Modify)], false, Me));
        foreach (var right in new long[] { 0x2, 0x4, 0x40, 0x10000, 0x40000, 0x80000, 0x40000000, 0x10000000 })
            Assert.Equal("can be changed by other accounts", OtherGames.Judge(Me, [A(Another, right)], false, Me));
        Assert.Null(OtherGames.Judge(Me, [A(Another, ReadExecute)], false, Me)); // reading is fine
    }

    [Fact]
    public void ADrivesRootMayLetAuthenticatedUsersCreateFoldersThereButNothingMore()
    {
        // C:\ and a data drive's root, as Windows sets them up
        var system = new[] { A("S-1-5-11", 0x4), A("S-1-5-11", unchecked((uint)-536805376), inheritOnly: true), A("S-1-5-18", FullControl), A("S-1-5-32-544", FullControl), A("S-1-5-32-545", ReadExecute) };
        var data = new[] { A("S-1-5-11", Modify), A("S-1-5-11", unchecked((uint)-536805376), inheritOnly: true), A("S-1-5-18", FullControl), A("S-1-5-32-544", FullControl) };
        Assert.Null(OtherGames.Judge(TrustedInstaller, system, true, Me));
        Assert.Null(OtherGames.Judge("S-1-5-18", data, true, Me));
        // only at the root itself, only Authenticated Users, and not the rights that move or re-permission what is there
        Assert.Equal("can be changed by other accounts", OtherGames.Judge("S-1-5-18", [A("S-1-5-11", 0x4)], false, Me));
        Assert.Equal("can be changed by other accounts", OtherGames.Judge("S-1-5-18", [A("S-1-1-0", 0x4)], true, Me));
        foreach (var right in new long[] { 0x40, 0x40000, 0x80000, 0x40000000, 0x10000000 })
            Assert.Equal("can be changed by other accounts", OtherGames.Judge("S-1-5-18", [A("S-1-5-11", 0x4 | right)], true, Me));
    }

    [Fact]
    public void ASystemProgramPassesAndAFolderAnotherAccountMayChangeDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows permissions
        // the drive's root, Windows and System32 are owned and changed by trusted accounts only
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Null(OtherGames.Unsafe(Environment.SystemDirectory, cmd));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet)) Assert.Null(OtherGames.Unsafe(Path.GetDirectoryName(dotnet)!, dotnet));

        // a folder in your own profile where one other standard account (not a broad group) may modify
        var game = Program("Lent Game");
        Assert.Null(OtherGames.Unsafe(game.Dir, game.Exe!));
        var dir = new DirectoryInfo(game.Dir);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(Another),
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.InheritanceFlags.None,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        dir.SetAccessControl(acl);
        Assert.Equal("can be changed by other accounts", OtherGames.Unsafe(game.Dir, game.Exe!));
        Assert.Equal("unsafe", Games(Config(BothOn), game).Launch(null, "p/w", game.Id)?.Code);
        Assert.Empty(_started);
    }
}