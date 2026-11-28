using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Games found outside Steam: the launchers' records, game folders and the program chosen for each game.</summary>
public class OtherGamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-games-{Guid.NewGuid():N}");

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
    [InlineData(@"C:\G\BEService_x64.exe")]
    [InlineData(@"C:\G\BattlEye\BEService.exe")]
    [InlineData(@"C:\G\bin\steamerrorreporter.exe")]
    [InlineData(@"C:\G\unitycrashhandler64.exe")]
    [InlineData(@"C:\G\CrashReportClient.exe")]
    [InlineData(@"C:\G\crashsender1403.exe")]
    public void InstallersAndHelpersAreNeverTheGame(string file) => Assert.True(OtherGames.NotTheGame(file));

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
        MakeExe("Projects/mymod/mymod.exe", 9000);
        MakeExe("Projects/fifa-mod/backup/amd_ags_x64.dll", 10);
        MakeExe("Repo/.git/HEAD", 10);
        MakeExe("Repo/bin/app.exe", 9000);
        MakeExe("Repo/assets/intro.bik", 10);
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
    public void AFoundGameNamesTheShortcutsThatStartAProgramInItsFolder()
    {
        OtherGames.Found F(string name, string dir) => new(OtherGames.IdOf("t", dir), name, "drive", dir, dir + "/a.exe", null);
        var programs = new[] { ("mod", @"d:\gta 4 lcpdfr\GTA 4 LCPDFR\Grand Theft Auto IV\LaunchGTAIV.exe"), ("notes", @"C:\Windows\notepad.exe") };
        Assert.Equal(["mod"], OtherGames.ShortcutsIn(F("GTA 4 LCPDFR", "D:/GTA 4 LCPDFR"), programs));
        Assert.Empty(OtherGames.ShortcutsIn(F("Red Dead Redemption 2", "D:/Games/Red Dead Redemption 2"), programs));
    }

    [Theory]
    [InlineData(@"C:\G\BeachRacing.exe")]
    [InlineData(@"C:\G\CrashBandicoot.exe")]
    [InlineData(@"C:\G\Toolbox Wars.exe")]
    [InlineData(@"C:\G\Speedrunners.exe")]
    public void AGameWhoseNameHoldsAHelpersLettersIsStillTheGame(string file) => Assert.False(OtherGames.NotTheGame(file));

    [Fact]
    public void TokensSplitAtCapitalsDigitsAndMarks()
    {
        Assert.Equal(["unity", "crash", "handler", "64"], OtherGames.Tokens("UnityCrashHandler64"));
        Assert.Equal(["ea", "anti", "cheat", "game", "service"], OtherGames.Tokens("EAAntiCheat.GameService"));
        Assert.Equal(["gta", "4", "browser"], OtherGames.Tokens("gta4Browser"));
        Assert.Equal(["beach", "racing"], OtherGames.Tokens("BeachRacing"));
    }

    [Fact]
    public void ATableOfContentsAloneIsNotAGame()
    {
        var doc = Path.Combine(_dir, "Thesis");
        MakeExe("Thesis/thesis.exe", 100);
        MakeExe("Thesis/thesis.toc", 10);
        Assert.False(OtherGames.LooksLikeAGame(doc, doc));
        MakeExe("Thesis/data.sb", 10);
        Assert.True(OtherGames.LooksLikeAGame(doc, doc));
    }

    [Fact]
    public void AGameFoundOnADriveIsKnownByItsFolder()
    {
        MakeExe("Wild/Wild.exe", 9000);
        MakeExe("Wild/bink2w64.dll", 10);
        var game = Assert.Single(OtherGames.FromDrives([_dir]));
        Assert.Equal(OtherGames.IdOf("drive", Path.Combine(_dir, "Wild")), game.Id);
    }

    [Fact]
    public void ALinkedFolderIsNotFollowed()
    {
        MakeExe("Real/Wild/Wild.exe", 9000);
        MakeExe("Real/Wild/bink2w64.dll", 10);
        var drive = Path.Combine(_dir, "Drive");
        Directory.CreateDirectory(drive);
        try { Directory.CreateSymbolicLink(Path.Combine(drive, "Wild"), Path.Combine(_dir, "Real", "Wild")); }
        catch (Exception) { return; } // no right to make links here (Windows without developer mode)
        Assert.Empty(OtherGames.FromDrives([drive]));
        Assert.Single(OtherGames.FromDrives([Path.Combine(_dir, "Real")]));
    }

    [Theory]
    [InlineData("Windows.old")]
    [InlineData("inetpub")]
    public void SystemLeftoversAreNotLookedInto(string folder)
    {
        MakeExe(folder + "/Wild/Wild.exe", 9000);
        MakeExe(folder + "/Wild/bink2w64.dll", 10);
        MakeExe(folder + "/bink2w64.dll", 10);
        Assert.Empty(OtherGames.FromDrives([_dir]));
    }

    // ------------------------------------------------------------------ starting a found game

    private readonly List<ProcessStartInfo> _started = [];

    private OtherGames Games(bool allowLaunch, bool allowOthers, params OtherGames.Found[] found)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        var options = $$"""{"allowLaunch":{{(allowLaunch ? "true" : "false")}},"allowLaunchOthers":{{(allowOthers ? "true" : "false")}}}""";
        File.WriteAllText(path, """{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":"""
            + options + "}]}]}");
        return new OtherGames(new ConfigStore(path), NullLogger<OtherGames>.Instance) { Launchers = () => found, Disks = () => [], StartProcess = _started.Add };
    }

    private OtherGames.Found FoundProgram(string name)
    {
        var exe = MakeExe(name + "/" + name + ".exe", 9000);
        var dir = Path.GetDirectoryName(exe)!;
        return new OtherGames.Found(OtherGames.IdOf("drive", dir), name, "drive", dir, exe, null);
    }

    [Fact]
    public void AFoundGameStartsOnlyWithItsOwnOption()
    {
        var game = FoundProgram("Wild");
        // "Let the phone start games" is Steam's: it does not start other programs
        Assert.Equal("off", Games(allowLaunch: true, allowOthers: false, game).Launch(null, "p/w", game.Id)?.Code);
        // another widget's option does not count
        Assert.Equal("off", Games(allowLaunch: true, allowOthers: true, game).Launch(null, "p/other", game.Id)?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void AnUnknownIdStartsNothing()
    {
        var games = Games(false, true, FoundProgram("Wild"));
        Assert.Equal("notinstalled", games.Launch(null, "p/w", "x:0000000000000000")?.Code);
        Assert.Equal("notinstalled", games.Launch(null, "p/w", @"C:\Windows\System32\cmd.exe")?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void AFoundGamesProgramStartsDirectlyInItsFolderWithNoShell()
    {
        var game = FoundProgram("Wild");
        Assert.Null(Games(false, true, game).Launch(null, "p/w", game.Id));
        var psi = Assert.Single(_started);
        Assert.Equal(game.Exe, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(game.Dir, psi.WorkingDirectory);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void AFoundGameWithALauncherAddressStartsThroughItsLauncher()
    {
        var dir = Path.Combine(_dir, "Epic");
        Directory.CreateDirectory(dir);
        var game = new OtherGames.Found(OtherGames.IdOf("epic", dir), "Epic one", "epic", dir, null, "com.epicgames.launcher://apps/a%3Ab%3Ac?action=launch&silent=true");
        Assert.Null(Games(false, true, game).Launch(null, "p/w", game.Id));
        var psi = Assert.Single(_started);
        Assert.Equal(game.Url, psi.FileName);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void AProgramThatIsGoneOrNotAProgramIsNotStarted()
    {
        var game = FoundProgram("Wild");
        File.Delete(game.Exe!);
        Assert.Equal("notinstalled", Games(false, true, game).Launch(null, "p/w", game.Id)?.Code);
        var script = MakeExe("Bat/run.bat", 10);
        var bat = game with { Id = OtherGames.IdOf("drive", Path.GetDirectoryName(script)!), Dir = Path.GetDirectoryName(script)!, Exe = script };
        Assert.Equal("notinstalled", Games(false, true, bat).Launch(null, "p/w", bat.Id)?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void TurningTheOptionOffDuringTheFirstLookRefusesTheLaunch()
    {
        var game = FoundProgram("Wild");
        var path = Path.Combine(_dir, "config-off.json");
        static string Config(bool on) => """{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunchOthers":"""
            + (on ? "true" : "false") + "}}]}]}";
        File.WriteAllText(path, Config(true));
        var store = new ConfigStore(path);
        var games = new OtherGames(store, NullLogger<OtherGames>.Instance)
        {
            // the option is turned off while the drives are read
            Launchers = () => { Assert.Null(store.Save(Config(false))); return [game]; }, Disks = () => [], StartProcess = _started.Add,
        };
        Assert.Equal("off", games.Launch(null, "p/w", game.Id)?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void ALaunchersProgramOutsideTheGamesFolderIsNotTaken()
    {
        var dir = Path.Combine(_dir, "GOG", "Wild");
        MakeExe("GOG/Wild/Wild.exe", 9000);
        var outside = MakeExe("Elsewhere/cmd.exe", 9000);
        var key = new OtherGames.Key("1", k => k switch { "path" => dir, "gameName" => "Wild", "exe" => outside, _ => null });
        Assert.Equal(Path.Combine(dir, "Wild.exe"), Assert.Single(OtherGames.FromKeys("gog", [key])).Exe);
        var climbing = new OtherGames.Key("2", k => k switch { "path" => dir, "gameName" => "Wild", "exe" => Path.Combine(dir, "..", "..", "Elsewhere", "cmd.exe"), _ => null });
        Assert.Equal(Path.Combine(dir, "Wild.exe"), Assert.Single(OtherGames.FromKeys("gog", [climbing])).Exe);

        var manifests = Path.Combine(_dir, "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "a.item"), System.Text.Json.JsonSerializer.Serialize(new
        {
            DisplayName = "Wild", InstallLocation = dir, LaunchExecutable = "../../Elsewhere/cmd.exe", AppCategories = new[] { "games" },
        }));
        Assert.Empty(OtherGames.FromEpic(manifests)); // no program inside, and no launcher address to start it by
    }

    [Fact]
    public void OnlyTheFirstRequestWaitsForTheFirstLook()
    {
        using var gate = new ManualResetEventSlim();
        var game = FoundProgram("Wild");
        var path = Path.Combine(_dir, "config-slow.json");
        File.WriteAllText(path, """{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4}]}]}""");
        var games = new OtherGames(new ConfigStore(path), NullLogger<OtherGames>.Instance)
        {
            Launchers = () => { gate.Wait(TimeSpan.FromSeconds(20)); return [game]; }, Disks = () => [], FirstWaitMs = 300,
        };
        var clock = Stopwatch.StartNew();
        Assert.Empty(games.List()); // waited up to 300 ms, then served what there is
        Assert.InRange(clock.ElapsedMilliseconds, 250, 5000);
        clock.Restart();
        Assert.Empty(games.List()); // the look is still running: no second wait
        Assert.InRange(clock.ElapsedMilliseconds, 0, 1000); // well under a wait, even on a busy runner
        gate.Set();
        var until = Stopwatch.StartNew();
        while (games.List().Count == 0 && until.ElapsedMilliseconds < 10_000) Thread.Sleep(20);
        Assert.Equal(["Wild"], games.List().Select(g => g.Name));
    }
}
