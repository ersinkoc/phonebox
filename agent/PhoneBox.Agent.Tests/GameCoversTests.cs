using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Covers from the Steam store for the games found outside Steam: names, matching, lookups kept on disk.</summary>
public class GameCoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-covers-{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("Grand Theft Auto IV", "Grand Theft Auto IV: The Complete Edition", true)]
    [InlineData("Red Dead Redemption 2", "Red Dead Redemption 2", true)]
    [InlineData("Witcher 3", "The Witcher® 3: Wild Hunt", true)]
    [InlineData("Pokemon", "Pokémon™", true)]
    [InlineData("Skyrim Special Edition", "The Elder Scrolls V: Skyrim Special Edition", false)]
    [InlineData("FIFA 23", "FIFA 22", false)]
    [InlineData("Portal", "Portal 2", false)]
    [InlineData("Portal", "Portal Knights", false)]
    [InlineData("GTA 4 LCPDFR", "Grand Theft Auto IV: The Complete Edition", false)]
    [InlineData("Grand Theft Auto IV", "Grand Theft Auto IV: Episodes from Liberty City", false)]
    [InlineData("Witcher 3", "The Witcher 3: Wild Hunt - Expansion Pass", false)]
    [InlineData("", "Anything", false)]
    public void AResultIsTakenOnlyWhenItIsTheGame(string name, string result, bool same) =>
        Assert.Equal(same, GameCovers.Matches(name, result));

    [Fact]
    public void TheProgramsProductNameComesFirstWhenItIsAGamesName()
    {
        Assert.Equal(["Grand Theft Auto IV", "GTA 4 LCPDFR"], GameCovers.NamesOf("GTA 4 LCPDFR", "Grand Theft Auto IV"));
        Assert.Equal(["FIFA 23"], GameCovers.NamesOf("FIFA 23", "FIFA 23"));
        Assert.Equal(["Old Fort"], GameCovers.NamesOf("Old Fort", "Unity Player"));
        Assert.Equal(["Old Fort"], GameCovers.NamesOf("Old Fort", "Microsoft® Windows® Operating System"));
        Assert.Equal(["Old Fort"], GameCovers.NamesOf("Old Fort", null));
    }

    private sealed class FakeStore(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Asked) Asked.Add(r.RequestUri!.ToString());
            return Task.FromResult(answer(r));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Jpeg()
    {
        var bytes = new byte[2048];
        bytes[0] = 0xFF; bytes[1] = 0xD8;
        var c = new ByteArrayContent(bytes);
        c.Headers.ContentType = new("image/jpeg");
        return new(HttpStatusCode.OK) { Content = c };
    }

    private static HttpResponseMessage Store(HttpRequestMessage r)
    {
        var u = r.RequestUri!.AbsoluteUri;
        if (u.Contains("library_600x900")) return Jpeg();
        if (u.Contains("Grand%20Theft%20Auto%20IV"))
            return Json("""{"total":2,"items":[{"type":"app","name":"Grand Theft Auto IV: Episodes from Liberty City","id":12220},{"type":"app","name":"Grand Theft Auto IV: The Complete Edition","id":12210}]}""");
        return Json("""{"total":0,"items":[]}""");
    }

    [Fact]
    public async Task AFoundGameGetsItsStoreArtOnceAndAMissingOneIsNotAskedAgainSoon()
    {
        var store = new FakeStore(Store);
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV", "GTA 4 LCPDFR"]), ("x:2", ["FIFA 23"])], () => true);
        await covers.Idle();
        // the Episodes are another game: its name has words the game's has not
        Assert.EndsWith("12210.jpg", covers.CoverFile("x:1"));
        Assert.Null(covers.CoverFile("x:2"));
        var asked = store.Asked.Count;

        // kept on disk: a new start asks nothing for either game
        var again = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        again.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])], () => true);
        await again.Idle();
        Assert.Equal(asked, store.Asked.Count);
        Assert.NotNull(again.CoverFile("x:1"));

        // a week later the missing one is asked again
        var later = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => DateTimeOffset.UtcNow.AddDays(8), pauseMs: 0);
        later.Want([("x:2", ["FIFA 23"])], () => true);
        await later.Idle();
        Assert.Equal(asked + 1, store.Asked.Count);
    }

    [Fact]
    public async Task WithoutTheNetworkNothingIsMarkedMissing()
    {
        var down = new FakeStore(_ => throw new HttpRequestException("offline"));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, down, _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await covers.Idle();
        Assert.Null(covers.CoverFile("x:1"));

        var up = new FakeStore(Store);
        var back = new GameCovers(NullLogger<GameCovers>.Instance, up, _dir, pauseMs: 0);
        back.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await back.Idle();
        Assert.NotNull(back.CoverFile("x:1"));
    }

    private static HttpResponseMessage Busy(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("busy") };
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new(TimeSpan.FromSeconds(s));
        return r;
    }

    [Fact]
    public async Task AfterTheStoreSaysSlowDownNothingIsAskedUntilItsWaitIsOver()
    {
        var now = DateTimeOffset.UtcNow;
        var busy = true;
        var store = new FakeStore(r => busy ? Busy(HttpStatusCode.TooManyRequests, 300) : Store(r));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => now, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])], () => true);
        await covers.Idle();
        Assert.Single(store.Asked); // stopped at the first refusal; FIFA 23 was not asked
        Assert.Null(covers.CoverFile("x:1"));

        busy = false;
        now = now.AddMinutes(2); // past the minute of the first backoff, not the five minutes asked for
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])], () => true);
        await covers.Idle();
        Assert.Single(store.Asked);

        now = now.AddMinutes(4);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])], () => true);
        await covers.Idle();
        Assert.NotNull(covers.CoverFile("x:1")); // not marked missing by the refusal
    }

    [Fact]
    public async Task ArtTheStoreCannotSendNowIsNotTakenAsMissing()
    {
        var now = DateTimeOffset.UtcNow;
        var artDown = true;
        var store = new FakeStore(r => r.RequestUri!.AbsoluteUri.Contains("library_600x900") && artDown ? Busy(HttpStatusCode.ServiceUnavailable) : Store(r));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => now, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await covers.Idle();
        Assert.Null(covers.CoverFile("x:1"));

        artDown = false;
        now = now.AddMinutes(2);
        covers.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await covers.Idle();
        Assert.EndsWith("12210.jpg", covers.CoverFile("x:1"));
    }

    [Fact]
    public async Task AGameWhoseLookupKeepsFailingDoesNotHoldUpTheOthers()
    {
        var now = DateTimeOffset.UtcNow;
        // the store fails only for this game's name (an answer that is not JSON): each time, a pause, and the game goes last
        var store = new FakeStore(r => r.RequestUri!.AbsoluteUri.Contains("Broken") ? Json("{not json") : Store(r));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => now, pauseMs: 0);
        (string, string[])[] games = [("x:0", ["Broken"]), ("x:1", ["Grand Theft Auto IV"])];
        covers.Want(games, () => true);
        await covers.Idle();
        Assert.Single(store.Asked); // Broken first (by name), then the pause
        now = now.AddMinutes(31);
        covers.Want(games, () => true);
        await covers.Idle();
        Assert.NotNull(covers.CoverFile("x:1")); // the other game went first this time
        for (var i = 2; i <= GameCovers.MaxGameFailures; i++)
        {
            now = now.AddMinutes(31);
            covers.Want(games, () => true);
            await covers.Idle();
        }
        var asked = store.Asked.Count;
        now = now.AddMinutes(31);
        covers.Want(games, () => true);
        await covers.Idle();
        Assert.Equal(asked, store.Asked.Count); // set aside like a missing game, for a week
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ARefusalPausesEveryLookupAndMarksNothingMissing(HttpStatusCode code)
    {
        var now = DateTimeOffset.UtcNow;
        var refusing = true;
        var store = new FakeStore(r => refusing ? Busy(code) : Store(r));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => now, pauseMs: 0);
        (string, string[])[] games = [("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"]), ("x:3", ["Portal"])];
        covers.Want(games, () => true);
        await covers.Idle();
        Assert.Single(store.Asked);
        covers.Want(games, () => true); // within the pause
        await covers.Idle();
        Assert.Single(store.Asked);
        refusing = false;
        now = now.AddMinutes(2);
        covers.Want(games, () => true);
        await covers.Idle();
        Assert.NotNull(covers.CoverFile("x:1"));
    }

    [Fact]
    public async Task ThePauseDoublesWithFailuresInARowAndEndsWithASuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var down = true;
        var store = new FakeStore(r => down ? Busy(HttpStatusCode.ServiceUnavailable) : Store(r));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => now, pauseMs: 0);
        (string, string[])[] games = [("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"]), ("x:3", ["Portal"])];
        async Task<int> At(double minutes) { now = now.AddMinutes(minutes); covers.Want(games, () => true); await covers.Idle(); return store.Asked.Count; }
        Assert.Equal(1, await At(0));     // paused 1 minute
        Assert.Equal(2, await At(1.5));   // paused 2 minutes
        Assert.Equal(2, await At(1.5));
        Assert.Equal(3, await At(1));     // paused 4 minutes
        Assert.Equal(3, await At(3.5));
        down = false;
        var asked = await At(1);
        Assert.True(asked > 3);
        Assert.NotNull(covers.CoverFile("x:1"));
        // a success ends the run of failures: the next one pauses 1 minute again
        down = true;
        games = [("x:7", ["Portal 2"])];
        Assert.Equal(asked + 1, await At(0));
        Assert.Equal(asked + 2, await At(1.5));
    }

    [Fact]
    public async Task TurningTheOptionOffStopsTheLookups()
    {
        var on = true;
        var store = new FakeStore(r => { on = false; return Store(r); }); // turned off while the first search runs
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"]), ("x:3", ["Portal"])], () => on);
        await covers.Idle();
        Assert.Single(store.Asked); // no art fetched, no other game searched
        Assert.Null(covers.CoverFile("x:1"));

        var off = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        off.Want([("x:2", ["FIFA 23"])], () => false);
        await off.Idle();
        Assert.Single(store.Asked);
    }

    [Fact]
    public async Task TheCoversOfGamesThatAreGoneAreForgotten()
    {
        var store = new FakeStore(Store);
        var later = DateTimeOffset.UtcNow;
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => later, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await covers.Idle();
        var file = covers.CoverFile("x:1")!;
        Assert.True(File.Exists(file));

        covers.Want([("x:9", ["Old Fort"])], () => true); // GTA IV is no longer found: kept a day (a drive not ready, say)
        await covers.Idle();
        Assert.True(File.Exists(file));
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:9", ["Old Fort"])], () => true); // back: the day starts again
        await covers.Idle();
        covers.Want([("x:9", ["Old Fort"])], () => true);
        await covers.Idle();
        later = later.AddHours(23);
        covers.Want([("x:9", ["Old Fort"])], () => true);
        await covers.Idle();
        Assert.True(File.Exists(file));
        later = later.AddHours(2);
        covers.Want([("x:9", ["Old Fort"])], () => true);
        await covers.Idle();
        Assert.False(File.Exists(file));
        Assert.Null(covers.CoverFile("x:1"));
        Assert.DoesNotContain("x:1", File.ReadAllText(Path.Combine(_dir, "index.json")));
    }

    private OtherGames Library(GameCovers covers, bool storeCovers, out ConfigStore store)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Config(storeCovers));
        store = new ConfigStore(path);
        var dir = Path.Combine(_dir, "games", "Grand Theft Auto IV");
        Directory.CreateDirectory(dir);
        var game = new OtherGames.Found(OtherGames.IdOf("drive", dir), "Grand Theft Auto IV", "drive", dir, null, "x-test://none");
        return new OtherGames(store, NullLogger<OtherGames>.Instance, covers) { Launchers = () => [game], Disks = () => [] };
    }

    private static string Config(bool storeCovers) =>
        """{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"steam-library","x":0,"y":0,"w":6,"h":4,"options":{"storeCovers":"""
        + (storeCovers ? "true" : "false") + "}}]}]}";

    [Fact]
    public async Task WithTheOptionOffTheStoreIsNeverAsked()
    {
        var store = new FakeStore(Store);
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        var library = Library(covers, storeCovers: false, out var config);
        Assert.Single(library.List());
        await covers.Idle();
        Assert.Empty(store.Asked);
        Assert.False(Assert.Single(library.List()).Art);

        Assert.Null(config.Save(Config(true)));
        library.List();
        await covers.Idle();
        Assert.NotEmpty(store.Asked);
        Assert.True(Assert.Single(library.List()).Art);

        // turned off: the store art is no longer served, and nothing more is asked
        Assert.Null(config.Save(Config(false)));
        var asked = store.Asked.Count;
        Assert.False(Assert.Single(library.List()).Art);
        await covers.Idle();
        Assert.Equal(asked, store.Asked.Count);
    }

    [Fact]
    public async Task ACoverThatIsALinkIsNotServed()
    {
        var store = new FakeStore(Store);
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"])], () => true);
        await covers.Idle();
        var file = covers.CoverFile("x:1")!;
        var target = Path.Combine(Path.GetTempPath(), $"phonebox-secret-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(target, [0xFF, 0xD8, 1, 2]);
        try
        {
            File.Delete(file);
            try { File.CreateSymbolicLink(file, target); }
            catch (Exception) { return; } // no right to make links here (Windows without developer mode)
            Assert.Null(covers.CoverFile("x:1"));
        }
        finally { File.Delete(target); }
    }
}
