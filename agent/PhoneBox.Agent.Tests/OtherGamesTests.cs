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
        MakeExe("Projects/tool/tool.exe", 9000);
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
    public void AGameStartedByOneOfYourOwnShortcutsIsShownOnceAsYours()
    {
        OtherGames.Found F(string name, string dir) => new(OtherGames.IdOf("t", dir), name, "drive", dir, dir + "/a.exe", null);
        var games = new[] { F("GTA 4 LCPDFR", "D:/GTA 4 LCPDFR"), F("Red Dead Redemption 2", "D:/Games/Red Dead Redemption 2") };
        var shown = OtherGames.Shown(games, [@"d:\gta 4 lcpdfr\GTA 4 LCPDFR\Grand Theft Auto IV\LaunchGTAIV.exe"]);
        Assert.Equal(["Red Dead Redemption 2"], shown.Select(g => g.Name));
    }
}
