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
        covers.Want([("x:1", ["Grand Theft Auto IV", "GTA 4 LCPDFR"]), ("x:2", ["FIFA 23"])]);
        await covers.Idle();
        // the Episodes are another game: its name has words the game's has not
        Assert.EndsWith("12210.jpg", covers.CoverFile("x:1"));
        Assert.Null(covers.CoverFile("x:2"));
        var asked = store.Asked.Count;

        // kept on disk: a new start asks nothing for either game
        var again = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0);
        again.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])]);
        await again.Idle();
        Assert.Equal(asked, store.Asked.Count);
        Assert.NotNull(again.CoverFile("x:1"));

        // a week later the missing one is asked again
        var later = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => DateTimeOffset.UtcNow.AddDays(8), pauseMs: 0);
        later.Want([("x:2", ["FIFA 23"])]);
        await later.Idle();
        Assert.Equal(asked + 1, store.Asked.Count);
    }

    [Fact]
    public async Task WithoutTheNetworkNothingIsMarkedMissing()
    {
        var down = new FakeStore(_ => throw new HttpRequestException("offline"));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, down, _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"])]);
        await covers.Idle();
        Assert.Null(covers.CoverFile("x:1"));

        var up = new FakeStore(Store);
        var back = new GameCovers(NullLogger<GameCovers>.Instance, up, _dir, pauseMs: 0);
        back.Want([("x:1", ["Grand Theft Auto IV"])]);
        await back.Idle();
        Assert.NotNull(back.CoverFile("x:1"));
    }

    [Fact]
    public async Task AFailureLeavesTheStoreAloneForAWhileOrAsLongAsItsRetryAfterAsks()
    {
        var at = DateTimeOffset.UtcNow;
        var busy = true;
        var store = new FakeStore(r =>
        {
            if (!busy) return Store(r);
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return res;
        });
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => at, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["Red Dead Redemption 2"])]);
        await covers.Idle();
        Assert.Single(store.Asked);   // the first refusal stops the rest
        Assert.InRange(covers.PausedFor(), 4 * 60_000, 5 * 60_000); // Retry-After: longer than the first minute

        // the panel asks every 30 s: nothing is asked while the wait lasts
        at = at.AddMinutes(1);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["Red Dead Redemption 2"])]);
        await covers.Idle();
        Assert.Single(store.Asked);

        // after it, the other game is asked; the one that failed waits its own turn (an hour)
        busy = false;
        at = at.AddMinutes(5);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["Red Dead Redemption 2"])]);
        await covers.Idle();
        Assert.Equal(2, store.Asked.Count);
        Assert.Contains("Red Dead", store.Asked[1]);
        Assert.Null(covers.CoverFile("x:1"));

        at = at.AddHours(1);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["Red Dead Redemption 2"])]);
        await covers.Idle();
        Assert.NotNull(covers.CoverFile("x:1"));
    }

    [Fact]
    public async Task FailuresInARowDoubleTheWaitUpToHalfAnHour()
    {
        var at = DateTimeOffset.UtcNow;
        var store = new FakeStore(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, () => at, pauseMs: 0);
        var waits = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            covers.Want([("x:" + i, ["Game " + i])]); // a new game each time: each one's own wait does not get in the way
            await covers.Idle();
            waits.Add(covers.PausedFor());
            at = at.AddMilliseconds(covers.PausedFor() + 1);
        }
        Assert.Equal([60_000L, 120_000, 240_000, 480_000, 960_000, 1_800_000, 1_800_000], waits);
    }

    [Fact]
    public async Task NothingIsAskedOnceTheOptionIsTurnedOff()
    {
        var on = false;
        var store = new FakeStore(Store);
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, store, _dir, pauseMs: 0, wanted: () => on);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])]);
        await covers.Idle();
        Assert.Empty(store.Asked);
        Assert.Equal(0, covers.PausedFor()); // not a failure

        on = true;
        covers.Want([("x:1", ["Grand Theft Auto IV"])]);
        await covers.Idle();
        Assert.NotNull(covers.CoverFile("x:1"));
    }

    [Fact]
    public async Task TheCoversOfGamesNoLongerFoundAreDropped()
    {
        var covers = new GameCovers(NullLogger<GameCovers>.Instance, new FakeStore(Store), _dir, pauseMs: 0);
        covers.Want([("x:1", ["Grand Theft Auto IV"]), ("x:2", ["FIFA 23"])]);
        await covers.Idle();
        var art = covers.CoverFile("x:1");
        Assert.NotNull(art);
        File.WriteAllText(Path.Combine(_dir, "notes.jpg"), "not ours"); // a file not named like ours stays

        covers.Want([("x:2", ["FIFA 23"])]);
        Assert.Null(covers.CoverFile("x:1"));
        Assert.False(File.Exists(art));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.jpg")));
        // and the index on disk no longer has it
        var again = new GameCovers(NullLogger<GameCovers>.Instance, new FakeStore(Store), _dir, pauseMs: 0);
        Assert.DoesNotContain("x:1", File.ReadAllText(Path.Combine(_dir, "index.json")));
        Assert.Null(again.CoverFile("x:1"));
    }
}