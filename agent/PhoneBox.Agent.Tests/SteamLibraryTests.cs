using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Steam library widget: Steam's own files, the cover path checks, the read cache and starting a game.</summary>
public sealed class SteamLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-steam-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "Steam");
    private string Other => Path.Combine(_dir, "Games");
    private string Cache => Path.Combine(Root, "appcache", "librarycache");
    /// <summary>What the library would have handed to the system: no test ever starts Steam.</summary>
    private readonly List<ProcessStartInfo> _started = [];

    public SteamLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(Other, "steamapps"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Quote(string path) => path.Replace("\\", "\\\\");

    private string Manifest(string folder, string id, string name, long flags = 4, long lastPlayed = 0, long toDownload = 0, long downloaded = 0, string extra = "")
    {
        var path = Path.Combine(folder, "steamapps", $"appmanifest_{id}.acf");
        File.WriteAllText(path, $$"""
            "AppState"
            {
            	"appid"		"{{id}}"
            	"Universe"		"1"
            	"name"		"{{name}}"
            	"StateFlags"		"{{flags}}"
            	"installdir"		"{{name}}"
            	"LastPlayed"		"{{lastPlayed}}"
            	"SizeOnDisk"		"52428800000"
            	"BytesToDownload"		"{{toDownload}}"
            	"BytesDownloaded"		"{{downloaded}}"
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            }
            {{extra}}
            """);
        return path;
    }

    private void LibraryFolders() => File.WriteAllText(Path.Combine(Root, "steamapps", "libraryfolders.vdf"), $$"""
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"{{Quote(Root)}}"
        		"label"		""
        		"apps"
        		{
        			"228980"		"1"
        		}
        	}
        	"1"
        	{
        		"path"		"{{Quote(Other)}}"
        	}
        }
        """);

    /// <summary>
    /// A config with one widget of <paramref name="widget"/> type (null: no widget) on a page. A disabled page gets an
    /// enabled page with a clock beside it: the config turns the first page back on when every page is disabled.
    /// </summary>
    private ConfigStore Config(string? widget = "steam-library", bool allowLaunch = true, bool pageEnabled = true)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        var on = allowLaunch ? "true" : "false";
        var enabled = pageEnabled ? "true" : "false";
        var other = pageEnabled ? "" : """,{"id":"o","title":"O","enabled":true,"widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":4,"h":2}]}""";
        File.WriteAllText(path, widget is null ? "{}" :
            $$$"""{"pages":[{"id":"p","title":"P","enabled":{{{enabled}}},"widgets":[{"id":"w","type":"{{{widget}}}","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunch":{{{on}}}}}]}{{{other}}}]}""");
        var store = new ConfigStore(path);
        if (widget is not null) Assert.Equal(pageEnabled, store.Get().Pages.Single(p => p.Id == "p").Enabled);
        return store;
    }

    private ConfigStore ConfigJson(string json)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new ConfigStore(path);
    }

    private SteamLibrary Library(ConfigStore config, Func<long>? clock = null, long running = 0) =>
        new(config, NullLogger<SteamLibrary>.Instance)
        {
            FindRoot = () => Root, RunningAppId = _ => running, Clock = clock ?? (() => Environment.TickCount64), StartProcess = _started.Add,
        };

    private void Cover(string relative, int bytes = 1)
    {
        var path = Path.Combine(Cache, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // ---------------------------------------------------------------- Steam's files

    [Fact]
    public void VdfReadsBlocksEscapesCommentsAndConditions()
    {
        var doc = VdfNode.Parse("""
            // a comment
            "Root"
            {
                "Path"  "C:\\Games\\Steam"   [$WIN32]
                "quote" "say \"hi\""
                "Nested" { "n" "42" }
                bare value
            }
            """);
        var root = doc["root"]!; // keys are case-insensitive
        Assert.Equal("C:\\Games\\Steam", root.Str("path"));
        Assert.Equal("say \"hi\"", root.Str("quote"));
        Assert.Equal(42, root["nested"]!.Long("N"));
        Assert.Equal("value", root.Str("bare"));
        Assert.Equal(0, root.Long("missing"));
        Assert.Empty(root["quote"]!.Children); // a value has no children (and no dictionary)
        VdfNode.Parse("\"a\" { \"b\" \"c\" } // the end", out var complete);
        Assert.True(complete);
    }

    [Theory]
    [InlineData("\"AppState\" {")]                                  // ends inside a block
    [InlineData("\"AppState\" { \"appid\" \"570\" \"name\" \"Dota")] // ends inside a quoted string
    [InlineData("\"AppState\" { \"appid\" \"570\" } }")]             // a } with no {
    [InlineData("\"AppState\" { \"appid\" \"570\" \"name\" }")]      // a key with no value
    [InlineData("{ \"appid\" \"570\" }")]                            // a block with no key
    [InlineData("\u0001\uFFFD garbage \"\\")]                        // not KeyValues at all
    public void ABrokenManifestIsSkippedWithoutThrowing(string text)
    {
        VdfNode.Parse(text, out var complete);
        Assert.False(complete);
        Assert.Null(SteamLibrary.ReadManifest(text));
    }

    [Fact]
    public void AManifestStillBeingWrittenIsReadOnceItIsComplete()
    {
        Manifest(Root, "570", "Dota 2");
        var path = Path.Combine(Root, "steamapps", "appmanifest_620.acf");
        File.WriteAllText(path, "\"AppState\"\n{\n\t\"appid\"\t\t\"620\"\n\t\"name\"\t\t\"Port");
        long now = 1_000_000;
        var library = Library(Config(), () => now);
        Assert.Equal(new[] { "570" }, library.View().Games.Select(g => g.Id).ToArray());

        Manifest(Root, "620", "Portal 2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        now += SteamLibrary.MinScanMs + 1;
        Assert.Equal(new[] { "570", "620" }, library.View().Games.Select(g => g.Id).Order().ToArray());
        Assert.Empty(VdfNode.Parse("").Children);
        Assert.Null(SteamLibrary.ReadManifest(""));
    }

    [Fact]
    public void EveryLibraryFolderIsReadNewestFirstAndToolsAreLeftOut()
    {
        LibraryFolders();
        Manifest(Root, "570", "Dota 2", lastPlayed: 1_700_000_000);
        Manifest(Other, "1245620", "ELDEN RING", lastPlayed: 1_800_000_000);
        Manifest(Other, "400", "Portal", lastPlayed: 0);
        Manifest(Root, "228980", "Steamworks Common Redistributables");
        Manifest(Other, "1007", "Steamworks SDK Redist");
        Manifest(Other, "1493710", "Proton Experimental");

        var view = Library(Config()).View();

        Assert.True(view.Enabled);
        Assert.True(view.Found);
        Assert.Null(view.Error);
        Assert.Equal(new[] { "1245620", "570", "400" }, view.Games.Select(g => g.Id).ToArray());
        var elden = view.Games[0];
        Assert.Equal("ELDEN RING", elden.Name);
        Assert.Equal("ready", elden.State);
        Assert.Null(elden.Progress);
        Assert.Equal(52.4, elden.SizeGb);
        Assert.Equal(1_800_000_000, elden.LastPlayed);
        Assert.False(elden.Cover);
    }

    [Fact]
    public void OldLibraryFolderFormatIsRead()
    {
        var folders = SteamLibrary.LibraryFolders(Root, $$"""
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1234"
            	"ContentStatsID"		"-123"
            	"1"		"{{Quote(Other)}}"
            }
            """);
        Assert.Equal(new[] { Root, Other }, folders.ToArray());
    }

    [Theory]
    [InlineData(4L, 0L, 0L, "ready", null)]
    [InlineData(6L, 100L, 0L, "update", 0.0)]
    [InlineData(1026L, 200L, 50L, "updating", 0.25)]
    [InlineData(4L | 131072L, 1000L, 999L, "updating", 0.999)] // validating
    [InlineData(4L | 1048576L, 400L, 100L, "updating", 0.25)]   // downloading
    [InlineData(4L | 2097152L, 400L, 400L, "updating", 1.0)]    // staging
    [InlineData(4L | 4194304L, 0L, 0L, "updating", null)]        // committing
    [InlineData(4L | 65536L, 0L, 0L, "updating", null)]          // reconfiguring
    [InlineData(4L | 512L, 10L, 5L, "paused", 0.5)]
    [InlineData(4L | 8388608L | 1048576L, 10L, 5L, "paused", 0.5)] // a pause being applied
    [InlineData(4L | 16384L, 0L, 0L, "ready", null)]              // not a state Steam uses
    [InlineData(1L, 0L, 0L, "update", null)]
    public void StateFlagsBecomeWhatTheWidgetShows(long flags, long toDownload, long downloaded, string state, double? progress)
    {
        var (s, p) = SteamLibrary.StateOf(flags, toDownload, downloaded);
        Assert.Equal(state, s);
        Assert.Equal(progress, p);
    }

    [Fact]
    public void LocalConfigLastPlayedWinsWhenItIsNewer()
    {
        var played = SteamLibrary.LastPlayed(VdfNode.Parse("""
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" {
                "570" { "LastPlayed" "1900000000" "Playtime" "100" }
                "10" { "Playtime" "5" }
            } } } } }
            """));
        Assert.Equal(1_900_000_000, played["570"]);
        Assert.False(played.ContainsKey("10"));

        Manifest(Root, "570", "Dota 2", lastPlayed: 1_700_000_000);
        Manifest(Root, "620", "Portal 2", lastPlayed: 1_800_000_000);
        var config = Path.Combine(Root, "userdata", "123", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "localconfig.vdf"), """
            "UserLocalConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "570" { "LastPlayed" "1900000000" } } } } } }
            """);
        var games = Library(Config()).View().Games;
        Assert.Equal(new[] { "570", "620" }, games.Select(g => g.Id).ToArray());
        Assert.Equal(1_900_000_000, games[0].LastPlayed);
    }

    [Fact]
    public void RegistryVdfGivesTheRunningApp()
    {
        Assert.Equal(1245620, SteamLibrary.RunningAppIdFromRegistryVdf("""
            "Registry" { "HKCU" { "Software" { "Valve" { "Steam" { "RunningAppID" "1245620" "language" "english" } } } } }
            """));
        Assert.Equal(0, SteamLibrary.RunningAppIdFromRegistryVdf("\"Registry\" { }"));
    }

    [Fact]
    public void TheGameBeingPlayedIsNamedByItsAppId()
    {
        Manifest(Root, "570", "Dota 2");
        Assert.Equal("570", Library(Config(), running: 570).View().Running);
        Assert.Null(Library(Config()).View().Running);
    }

    // ---------------------------------------------------------------- covers

    [Fact]
    public void CoversAreFoundInTheNewAndTheOldCacheLayout()
    {
        Cover(Path.Combine("570", "a1b2c3", "library_600x900.jpg"));
        Cover("730_header.jpg");
        Cover(Path.Combine("440", "header.jpg"));
        Cover(Path.Combine("440", "library_600x900.jpg"));

        Assert.EndsWith(Path.Combine("570", "a1b2c3", "library_600x900.jpg"), SteamLibrary.FindCover(Root, "570"));
        Assert.EndsWith("730_header.jpg", SteamLibrary.FindCover(Root, "730"));
        Assert.EndsWith(Path.Combine("440", "library_600x900.jpg"), SteamLibrary.FindCover(Root, "440")); // the portrait first
        Assert.Null(SteamLibrary.FindCover(Root, "999"));
    }

    [Theory]
    [InlineData("..\\570")]
    [InlineData("../570")]
    [InlineData("570/..")]
    [InlineData("570/../../x")]
    [InlineData("")]
    [InlineData("-570")]
    [InlineData("12345678901")]
    public void ACoverIdIsOnlyANumber(string id)
    {
        Cover("570_header.jpg");
        Assert.Null(SteamLibrary.FindCover(Root, id));
    }

    [Fact]
    public void ACoverTooBigIsSkipped()
    {
        Cover("570_library_600x900.jpg", (int)SteamLibrary.MaxCoverBytes + 1);
        Cover("570_header.jpg");
        Assert.EndsWith("570_header.jpg", SteamLibrary.FindCover(Root, "570"));
    }

    [Fact]
    public void LinksOutOfTheCacheAreNotFollowed()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "library_600x900.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(outside, "secret.jpg"), [1]);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Path.Combine(Cache, "730"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(Cache, "570"), outside);                       // the id's folder is a link
            Directory.CreateSymbolicLink(Path.Combine(Cache, "730", "a1b2c3"), outside);             // a link inside the id's folder
            File.CreateSymbolicLink(Path.Combine(Cache, "440_header.jpg"), Path.Combine(outside, "secret.jpg")); // a file link
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Windows without Developer Mode cannot create symbolic links; the checks below need them
        }

        Assert.Null(SteamLibrary.FindCover(Root, "570"));
        Assert.Null(SteamLibrary.FindCover(Root, "730"));
        Assert.Null(SteamLibrary.FindCover(Root, "440"));
        Assert.False(SteamLibrary.SafeCover(Path.GetFullPath(Cache), Path.Combine(Cache, "570", "library_600x900.jpg")));
        Assert.False(SteamLibrary.SafeCover(Path.GetFullPath(Cache), Path.Combine(Cache, "..", "..", "..", "outside", "secret.jpg")));
    }

    [Fact]
    public void CoverPathIsOnlyForAnInstalledGame()
    {
        LibraryFolders();
        Manifest(Root, "570", "Dota 2");
        Cover("570_library_600x900.jpg");
        Cover("730_library_600x900.jpg");
        var library = Library(Config());
        Assert.NotNull(library.CoverPath("570"));
        Assert.True(library.View().Games.Single().Cover);
        Assert.Null(library.CoverPath("730")); // in the cache, but not installed
        Assert.Null(library.CoverPath(null));
        Assert.Null(Library(Config("clock")).CoverPath("570")); // no Steam library widget on an enabled page
    }

    // ---------------------------------------------------------------- reading only what changed, and only so much

    [Fact]
    public void AnUnchangedManifestIsNotReadAgainAndScansAreSpacedOut()
    {
        Manifest(Root, "570", "Dota 2");
        var path = Manifest(Root, "620", "Portal 2");
        long now = 1_000_000;
        var library = Library(Config(), () => now);

        library.View();
        Assert.Equal(2, library.ManifestReads);

        library.View(); // within the minimum interval: the last scan is reused
        now += SteamLibrary.MinScanMs + 1;
        library.View(); // a new scan, but the files did not change
        Assert.Equal(2, library.ManifestReads);

        Manifest(Root, "620", "Portal 2 (updated)");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        library.View();
        Assert.Equal(2, library.ManifestReads); // still within the interval
        now += SteamLibrary.MinScanMs + 1;
        Assert.Contains(library.View().Games, g => g.Name == "Portal 2 (updated)");
        Assert.Equal(3, library.ManifestReads);

        File.Delete(path);
        now += SteamLibrary.MinScanMs + 1;
        Assert.Equal(new[] { "570" }, library.View().Games.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task ASlowLibraryFolderDoesNotHoldUpOtherRequests()
    {
        var slow = Path.Combine(_dir, "Slow");
        File.WriteAllText(Path.Combine(Root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders" { "0" { "path" "{{Quote(Root)}}" } "1" { "path" "{{Quote(slow)}}" } }
            """);
        Manifest(Root, "570", "Dota 2");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var library = new SteamLibrary(Config(), NullLogger<SteamLibrary>.Instance)
        {
            FindRoot = () => Root, RunningAppId = _ => 0, StartProcess = _started.Add,
            // an unplugged or network drive: the first look at it hangs until the test lets it go
            FolderExists = path =>
            {
                if (path.StartsWith(slow, StringComparison.OrdinalIgnoreCase)) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
                return Directory.Exists(path);
            },
        };

        var first = Task.Run(() => library.View());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        // while that scan waits on the drive, other requests answer at once with the last list (none yet)
        var clock = Stopwatch.StartNew();
        var view = library.View();
        Assert.True(view.Found);
        Assert.Empty(view.Games);
        Assert.Null(library.CoverPath("570"));
        Assert.Empty(library.Installed());
        Assert.True(clock.ElapsedMilliseconds < 2000, $"waited {clock.ElapsedMilliseconds} ms");

        release.Set();
        var done = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("570", Assert.Single(done.Games).Id);
    }

    [Fact]
    public void AManifestOverTheSizeCapIsSkipped()
    {
        Manifest(Root, "570", "Dota 2");
        Manifest(Root, "620", "Portal 2", extra: "// " + new string('x', (int)SteamLibrary.MaxManifestBytes));
        Assert.Equal(new[] { "570" }, Library(Config()).View().Games.Select(g => g.Id).ToArray());
    }

    [Fact]
    public void NothingIsReadWithoutTheWidgetOnAnEnabledPage()
    {
        Manifest(Root, "570", "Dota 2");
        foreach (var config in new[] { Config("clock"), Config(pageEnabled: false), Config(widget: null) })
        {
            var library = Library(config);
            var view = library.View();
            Assert.False(view.Enabled);
            Assert.Empty(view.Games);
            Assert.Equal(0, library.ManifestReads);
        }
    }

    [Fact]
    public void TheSettingsPickerListsInstalledGamesByName()
    {
        Manifest(Root, "620", "Portal 2", lastPlayed: 10);
        Manifest(Root, "570", "Dota 2", lastPlayed: 5);
        Manifest(Root, "228980", "Steamworks Common Redistributables");
        // the widget's editor opens before the widget is saved on a page
        var picks = Library(Config("clock")).Installed();
        Assert.Equal(new[] { new SteamPick("570", "Dota 2"), new SteamPick("620", "Portal 2") }, picks.ToArray());
        Assert.Empty(new SteamLibrary(Config(), NullLogger<SteamLibrary>.Instance) { FindRoot = () => null, StartProcess = _started.Add }.Installed());
    }

    // ---------------------------------------------------------------- starting a game

    [Fact]
    public void StartingAGameIsOffByDefaultAndOnlyForAnInstalledGame()
    {
        LibraryFolders();
        Manifest(Root, "570", "Dota 2");

        // the widget alone only shows the library: starting games is a separate option, off by default
        var shownOnly = Library(Config(allowLaunch: false));
        Assert.NotEmpty(shownOnly.View().Games);
        Assert.Equal("off", shownOnly.Launch(null, "p/w", "570")?.Code);

        // the option on a widget of a disabled page does not count, nor a widget of another type with that key
        Assert.Equal("off", Library(Config(pageEnabled: false)).Launch(null, "p/w", "570")?.Code);
        Assert.Equal("off", Library(Config("clock")).Launch(null, "p/w", "570")?.Code);

        var allowed = Library(Config());
        // only a number that is in the installed list reaches Steam; nothing else is started
        Assert.Equal("notinstalled", allowed.Launch(null, "p/w", "570; calc")?.Code);
        Assert.Equal("notinstalled", allowed.Launch(null, "p/w", "570 & calc")?.Code);
        Assert.Equal("notinstalled", allowed.Launch(null, "p/w", "steam://rungameid/570")?.Code);
        Assert.Equal("notinstalled", allowed.Launch(null, "p/w", null)?.Code);
        Assert.Equal("notinstalled", allowed.Launch(null, "p/w", "12345")?.Code);

        var none = new SteamLibrary(Config(), NullLogger<SteamLibrary>.Instance) { FindRoot = () => null, RunningAppId = _ => 0, StartProcess = _started.Add };
        Assert.False(none.View().Found);
        Assert.Empty(none.View().Games);
        Assert.Equal("nosteam", none.Launch(null, "p/w", "570")?.Code);
        Assert.Empty(_started); // nothing above reached the system
    }

    [Fact]
    public void EachWidgetDecidesForItself()
    {
        Manifest(Root, "570", "Dota 2");
        var library = Library(ConfigJson("""
            {"pages":[
              {"id":"p","title":"P","enabled":true,"widgets":[
                {"id":"on","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunch":true}},
                {"id":"off","type":"steam-library","x":6,"y":0,"w":6,"h":4,"options":{"allowLaunch":false}},
                {"id":"plain","type":"steam-library","x":0,"y":4,"w":6,"h":4}]},
              {"id":"q","title":"Q","enabled":false,"widgets":[
                {"id":"on","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunch":true}}]}]}
            """));

        // another widget with the option on does not let this one start games
        Assert.False(library.LaunchAllowed(null, "p/off"));
        Assert.Equal("off", library.Launch(null, "p/off", "570")?.Code);
        Assert.Equal("off", library.Launch(null, "p/plain", "570")?.Code); // off by default
        Assert.Equal("off", library.Launch(null, "q/on", "570")?.Code);    // a disabled page
        Assert.Equal("off", library.Launch(null, null, "570")?.Code);      // no widget named
        Assert.Equal("off", library.Launch(null, "", "570")?.Code);
        Assert.Equal("off", library.Launch(null, "p", "570")?.Code);
        Assert.Equal("off", library.Launch(null, "p/missing", "570")?.Code);
        Assert.Empty(_started);

        Assert.True(library.LaunchAllowed(null, "p/on"));
        Assert.Null(library.Launch(null, "p/on", "570"));
        Assert.Single(_started);
    }

    [Fact]
    public async Task TurningStartingOffWhileTheFilesAreReadStopsTheStart()
    {
        var slow = Path.Combine(_dir, "Slow");
        File.WriteAllText(Path.Combine(Root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders" { "0" { "path" "{{Quote(Root)}}" } "1" { "path" "{{Quote(slow)}}" } }
            """);
        Manifest(Root, "570", "Dota 2");
        var store = Config();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var library = new SteamLibrary(store, NullLogger<SteamLibrary>.Instance)
        {
            FindRoot = () => Root, RunningAppId = _ => 0, StartProcess = _started.Add,
            FolderExists = path =>
            {
                if (path.StartsWith(slow, StringComparison.OrdinalIgnoreCase)) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
                return Directory.Exists(path);
            },
        };

        // the start reads the installed list again; while a slow drive holds that up, the option is turned off
        var start = Task.Run(() => library.Launch(null, "p/w", "570"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(store.Save("""{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"allowLaunch":false}}]}]}"""));
        release.Set();

        Assert.Equal("off", (await start.WaitAsync(TimeSpan.FromSeconds(10)))?.Code);
        Assert.Empty(_started);
    }

    [Fact]
    public void AnInstalledGameIsHandedToSteamsUrlHandlerByItsOwnNumber()
    {
        Manifest(Root, "570", "Dota 2");
        Assert.Null(Library(Config()).Launch(null, "p/w", "570"));
        var psi = Assert.Single(_started);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("steam://rungameid/570", psi.FileName);
            Assert.True(psi.UseShellExecute);
            Assert.Empty(psi.ArgumentList);
        }
        else
        {
            Assert.Equal(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", psi.FileName);
            Assert.False(psi.UseShellExecute);
            Assert.Equal(new[] { "steam://rungameid/570" }, psi.ArgumentList.ToArray());
        }
        Assert.Equal("", psi.Arguments);
    }
}
