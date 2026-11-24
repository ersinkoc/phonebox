using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using PhoneBox.Agent;
using Xunit;

public sealed class PlexControlTests
{
    private const string Token = "secret-plex-token";
    private const string Machine = "0123456789abcdef0123456789abcdef01234567";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private sealed record Call(HttpMethod Method, Uri Uri, bool HasToken, bool Redirects);

    /// <summary>A config file, a fake server on plex.example.test and a fake player on 127.0.0.1, with every request recorded.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "phonebox-plex-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Dir, "config.json");
        public ConfigStore Config { get; private set; } = null!;
        public readonly List<Call> Calls = [];
        public long Clock = 1_000_000;
        public string Timeline = TimelineXml();
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override;
        public string QueueJson = QueueOf("9001", "9002", "9003");
        public HttpStatusCode PlayerCommandStatus = HttpStatusCode.OK;
        /// <summary>One enabled page that shows both Plex widgets (the page gate lets everything through).</summary>
        public const string BothWidgets = """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"w1","type":"plex-queue","x":0,"y":0,"w":4,"h":4},{"id":"w2","type":"plex-playlists","x":4,"y":0,"w":4,"h":4}]}]""";
        public Fixture(string? url = "https://plex.example.test:8443", string? token = Token, string? extra = null, string? pages = null)
        {
            Directory.CreateDirectory(Dir);
            Write(url, token, extra, pages);
            Config = new ConfigStore(ConfigPath);
        }
        public void Write(string? url, string? token, string? extra = null, string? pages = null)
        {
            File.WriteAllText(ConfigPath, "{\"controls\":" + JsonSerializer.Serialize(new { plexUrl = url, plexToken = token }).TrimEnd('}') + (extra ?? "") + "},\"pages\":" + (pages ?? BothWidgets) + "}");
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _writes)));
        }
        private static int _writes;
        public PlexControl Create() => new(Config, new HttpClient(new Handler(Route)), () => Clock);
        public IEnumerable<Call> ToPlayer => Calls.Where(c => c.Uri.Host == "127.0.0.1");
        public IEnumerable<Call> ToServer => Calls.Where(c => c.Uri.Host != "127.0.0.1");

        private Task<HttpResponseMessage> Route(HttpRequestMessage r)
        {
            lock (Calls) Calls.Add(new(r.Method, r.RequestUri!, r.Headers.Contains("X-Plex-Token"), false));
            if (Override?.Invoke(r) is { } custom) return Task.FromResult(custom);
            var path = r.RequestUri!.AbsolutePath;
            if (r.RequestUri.Host == "127.0.0.1")
            {
                if (path == "/player/timeline/poll") return Task.FromResult(Xml(Timeline));
                return Task.FromResult(new HttpResponseMessage(PlayerCommandStatus));
            }
            if (path == "/identity") return Task.FromResult(Json("{\"MediaContainer\":{\"size\":0,\"machineIdentifier\":\"" + Machine + "\"}}"));
            if (path.StartsWith("/playQueues/")) return Task.FromResult(Json(QueueJson));
            if (path == "/playQueues" && r.Method == HttpMethod.Post)
                return Task.FromResult(Json("""{"MediaContainer":{"playQueueID":5555,"playQueueSelectedMetadataItemID":777,"Metadata":[{"key":"/library/metadata/777"}]}}"""));
            if (path == "/playlists")
                return Task.FromResult(Json("""{"MediaContainer":{"size":3,"Metadata":[{"ratingKey":"12","title":"Chill"},{"ratingKey":"../x","title":"Bad"},{"ratingKey":13,"title":"Run"}]}}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public void Dispose() => Directory.Delete(Dir, true);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
    private static string TimelineXml(string state = "playing", string machine = Machine, string queueId = "4431", string item = "9001") =>
        $"""<MediaContainer commandID="10"><Timeline type="music" itemType="music" state="{state}" controllable="playPause,stop,skipPrevious,skipNext,seekTo,volume,shuffle,repeat" volume="73" shuffle="0" repeat="0" time="83310" duration="301920" key="x" ratingKey="x" playQueueItemID="{item}" playQueueID="{queueId}" containerKey="x" playQueueVersion="1" machineIdentifier="{machine}" protocol="https" address="evil.example" port="1"/><Timeline type="video" state="stopped"/><Timeline type="photo" state="stopped"/></MediaContainer>""";
    private static string QueueOf(params string[] ids) => new JsonObject { ["MediaContainer"] = new JsonObject { ["Metadata"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
    {
        ["playQueueItemID"] = long.Parse(id), ["key"] = "/library/metadata/" + (long.Parse(id) - 8000), ["title"] = "Song " + id,
        ["grandparentTitle"] = id == "9003" ? null : "Artist " + id, ["originalTitle"] = "Original " + id, ["thumb"] = "/secret/thumb",
    }).ToArray()) } }.ToJsonString();
    private static HttpRequestException Refused() => new("connection refused", new System.Net.Sockets.SocketException(10061));

    [Fact]
    public async Task NothingIsSentUntilTheServerAndTheTokenAreSet()
    {
        foreach (var (url, token) in new (string?, string?)[] { (null, Token), ("https://plex.example.test", null), (null, null) })
        {
            using var fixture = new Fixture(url, token);
            using var plex = fixture.Create();
            var snap = await plex.Snapshot(default);
            Assert.False(snap["connected"]!.GetValue<bool>());
            Assert.Equal("Connect Plex in desktop settings", snap["error"]!.GetValue<string>());
            Assert.Empty(fixture.Calls);
        }
    }

    [Fact]
    public async Task ThePlayingTrackAndWhatFollowsAreRead_AndOnlyIdNameAndArtistPassOn()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.True(snap["playing"]!.GetValue<bool>());
        Assert.Equal("9001", snap["current"]!["id"]!.GetValue<string>());
        Assert.Equal("Song 9001", snap["current"]!["name"]!.GetValue<string>());
        Assert.Equal("Artist 9001", snap["current"]!["artist"]!.GetValue<string>());
        var queue = snap["queue"]!.AsArray();
        Assert.Equal(new[] { "9002", "9003" }, queue.Select(t => t!["id"]!.GetValue<string>()));
        Assert.Equal("Original 9003", queue[1]!["artist"]!.GetValue<string>()); // no grandparentTitle: originalTitle
        Assert.All(queue.Append(snap["current"]).Select(t => t!.AsObject().Select(p => p.Key).Order().ToArray()), keys => Assert.Equal(new[] { "artist", "id", "name" }, keys));
        var text = snap.ToJsonString();
        Assert.DoesNotContain(Token, text); Assert.DoesNotContain("plex.example.test", text); Assert.DoesNotContain(Machine, text); Assert.DoesNotContain("/library", text);
    }

    [Fact]
    public async Task TheQueueIsCappedAtThirtyItems()
    {
        using var fixture = new Fixture { QueueJson = QueueOf(Enumerable.Range(9001, 60).Select(i => i.ToString()).ToArray()) };
        using var plex = fixture.Create();
        Assert.Equal(30, (await plex.Snapshot(default))["queue"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheTokenGoesToTheServerOnly_AndHeadersAndCommandIdsAreSet()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        Assert.NotEmpty(fixture.ToPlayer); Assert.NotEmpty(fixture.ToServer);
        Assert.All(fixture.ToPlayer, c => Assert.False(c.HasToken));
        Assert.All(fixture.ToServer, c => Assert.True(c.HasToken));
        Assert.All(fixture.Calls, c => Assert.DoesNotContain(Token, c.Uri.ToString()));
        Assert.All(fixture.ToPlayer.Where(c => c.Uri.AbsolutePath == "/player/timeline/poll"), c =>
        {
            var query = QueryHelpers.ParseQuery(c.Uri.Query);
            Assert.Equal("0", query["wait"]); Assert.True(long.Parse(query["commandID"]!) > 0);
        });
        Assert.Equal(32500, fixture.ToPlayer.First().Uri.Port);
    }

    [Fact]
    public async Task EveryRequestCarriesTheClientHeaders()
    {
        using var fixture = new Fixture();
        var seen = new List<HttpRequestHeaders>();
        fixture.Override = r => { seen.Add(r.Headers); return null; };
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        Assert.All(seen, h =>
        {
            Assert.Equal("PhoneBox", h.GetValues("X-Plex-Product").Single());
            Assert.Matches("^[0-9a-f]{32}$", h.GetValues("X-Plex-Client-Identifier").Single());
        });
        Assert.Single(seen.Select(h => h.GetValues("X-Plex-Client-Identifier").Single()).Distinct());
    }

    [Fact]
    public async Task APlayerThatIsNotRunningIsReported()
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host == "127.0.0.1" ? throw Refused() : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("Start Caldera Amp (or another Plex player) on this computer", snap["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task APlayerThatTimesOutIsReportedAsNotRunning()
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host == "127.0.0.1" ? throw new TaskCanceledException("timeout") : null;
        using var plex = fixture.Create();
        Assert.StartsWith("Start Caldera Amp", (await plex.Snapshot(default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsReported()
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw Refused() : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedTokenIsReportedAndNotRetried(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? new(status) : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.Equal("Check the Plex token in desktop settings", snap["error"]!.GetValue<string>());
        Assert.Single(fixture.ToServer);
    }

    [Fact]
    public async Task ACertificateThatIsNotTrustedIsReported()
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw new HttpRequestException(HttpRequestError.SecureConnectionError, "tls") : null;
        using var plex = fixture.Create();
        Assert.Equal("The Plex server certificate is not trusted", (await plex.Snapshot(default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARedirectIsAnErrorAndIsNotFollowed()
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1"
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.example/identity") } } : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.Host == "other.example");
    }

    [Fact]
    public void ThePublicConstructorLeavesTheTimeToTheStallToken()
    {
        using var fixture = new Fixture();
        using var real = new PlexControl(fixture.Config);
        var field = typeof(PlexControl).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.Equal(Timeout.InfiniteTimeSpan, ((HttpClient)field!.GetValue(real)!).Timeout);
    }

    [Fact]
    public async Task AStoppedOrMissingMusicTimelineIsAnIdlePlayer()
    {
        foreach (var xml in new[] { TimelineXml("stopped"), """<MediaContainer><Timeline type="video" state="playing"/></MediaContainer>""" })
        {
            using var fixture = new Fixture { Timeline = xml };
            using var plex = fixture.Create();
            var snap = await plex.Snapshot(default);
            Assert.True(snap["connected"]!.GetValue<bool>());
            Assert.False(snap["playing"]!.GetValue<bool>());
            Assert.Null(snap["current"]);
            Assert.Empty(snap["queue"]!.AsArray());
            Assert.DoesNotContain(fixture.Calls, c => c.Uri.AbsolutePath.StartsWith("/playQueues"));
        }
    }

    [Fact]
    public async Task APausedTrackIsShownAsNotPlaying()
    {
        using var fixture = new Fixture { Timeline = TimelineXml("paused") };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["playing"]!.GetValue<bool>());
        Assert.Equal("9001", snap["current"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task APlayerPlayingFromAnotherServerShowsNothing_AndItsAddressIsNeverUsed()
    {
        using var fixture = new Fixture { Timeline = TimelineXml(machine: "ffffffffffffffffffffffffffffffffffffffff") };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.Null(snap["current"]);
        Assert.Empty(snap["queue"]!.AsArray());
        Assert.Equal("The player is playing from another Plex server", snap["error"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.Host == "evil.example" || c.Uri.AbsolutePath.StartsWith("/playQueues"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12/../x")]
    public async Task AMissingOrBadPlayQueueIdGivesNoQueue(string queueId)
    {
        using var fixture = new Fixture { Timeline = TimelineXml(queueId: queueId) };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.Null(snap["current"]);
        Assert.Empty(snap["queue"]!.AsArray());
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.AbsolutePath.StartsWith("/playQueues"));
    }

    [Fact]
    public async Task MalformedAnswersAndOversizedBodiesAreErrorsNotCrashes()
    {
        var bodies = new Dictionary<string, Func<Fixture, Action>>
        {
            ["xml"] = f => () => f.Timeline = "<MediaContainer><Timeline",
            ["dtd"] = f => () => f.Timeline = """<?xml version="1.0"?><!DOCTYPE m [<!ENTITY x SYSTEM "file:///etc/passwd">]><MediaContainer><Timeline type="music" state="playing" playQueueID="1" machineIdentifier="&x;"/></MediaContainer>""",
            ["queue"] = f => () => f.QueueJson = "{ not json",
            ["queue-array"] = f => () => f.QueueJson = "[1,2]",
            ["big"] = f => () => f.QueueJson = new string(' ', 1_100_000) + "{}",
        };
        foreach (var (name, make) in bodies)
        {
            using var fixture = new Fixture();
            make(fixture)();
            using var plex = fixture.Create();
            var snap = await plex.Snapshot(default);
            Assert.False(snap["connected"]!.GetValue<bool>(), name);
            Assert.False(string.IsNullOrEmpty(snap["error"]!.GetValue<string>()), name);
        }
    }

    [Fact]
    public async Task ManyPollsMakeOneSnapshotPerFiveSeconds()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => plex.Snapshot(default)));
        Assert.Single(fixture.ToPlayer.Where(c => c.Uri.AbsolutePath == "/player/timeline/poll"));
        fixture.Clock += 4_000; await plex.Snapshot(default);
        Assert.Single(fixture.ToPlayer.Where(c => c.Uri.AbsolutePath == "/player/timeline/poll"));
        fixture.Clock += 2_000; await plex.Snapshot(default);
        Assert.Equal(2, fixture.ToPlayer.Count(c => c.Uri.AbsolutePath == "/player/timeline/poll"));
        Assert.Single(fixture.ToServer.Where(c => c.Uri.AbsolutePath == "/identity")); // cached per server address
    }

    [Fact]
    public async Task ASettingsChangeWhileReadingIsNotMixedIn()
    {
        using var fixture = new Fixture();
        var n = 0;
        // the server address changes during every read
        fixture.Override = r =>
        {
            if (r.RequestUri!.AbsolutePath == "/player/timeline/poll") fixture.Write("https://plex" + (++n) + ".example.test", Token);
            return null;
        };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex settings changed; try again", snap["error"]!.GetValue<string>());
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task ATapIsRefusedWhenTheSettingsChangedWhileItsQueueWasRead()
    {
        using var fixture = new Fixture();
        var changed = false;
        // the server address changes once, during the read that the tap starts: the queue then comes from the new server
        fixture.Override = r =>
        {
            if (!changed && r.RequestUri!.AbsolutePath == "/player/timeline/poll") { changed = true; fixture.Write("https://other.example.test", Token); }
            return null;
        };
        using var plex = fixture.Create();
        var result = await plex.Act("play-queued", "9002", default);
        Assert.True(changed);
        Assert.False(result.Ok); Assert.Equal("The Plex settings changed; try again", result.Error);
        Assert.DoesNotContain(fixture.ToPlayer, c => c.Uri.AbsolutePath.StartsWith("/player/playback"));
    }

    [Theory]
    [InlineData("/identity")]   // before the play queue is made: nothing more is sent
    [InlineData("/playQueues")] // after: the queue is left on the old server, and the player gets no command
    public async Task APlaylistStartStopsWhenTheSettingsChangeDuringIt(string during)
    {
        using var fixture = new Fixture(extra: Actions(PlaylistAction));
        var changed = false;
        fixture.Override = r =>
        {
            if (!changed && r.RequestUri!.AbsolutePath == during) { changed = true; fixture.Write("https://other.example.test", Token, Actions(PlaylistAction)); }
            return null;
        };
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", "pl1", default);
        Assert.True(changed);
        Assert.False(result.Ok); Assert.Equal("The Plex settings changed; try again", result.Error);
        Assert.Equal(during == "/playQueues" ? 1 : 0, fixture.ToServer.Count(c => c.Uri.AbsolutePath == "/playQueues"));
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.Host == "other.example.test");
        Assert.DoesNotContain(fixture.ToPlayer, c => c.Uri.AbsolutePath.StartsWith("/player/playback"));
    }

    [Fact]
    public async Task ASettingsChangeThatSettlesGivesTheNewResult()
    {
        using var fixture = new Fixture();
        var changed = false;
        fixture.Override = r =>
        {
            if (!changed && r.RequestUri!.AbsolutePath == "/player/timeline/poll") { changed = true; fixture.Write("https://other.example.test", Token); }
            return null;
        };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.Contains(fixture.ToServer, c => c.Uri.Host == "other.example.test");
    }

    [Fact]
    public async Task TappingAQueueItemSkipsToItOnThePlayer_WithoutTheToken()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var result = await plex.Act("play-queued", "9002", default);
        Assert.True(result.Ok);
        var skip = fixture.ToPlayer.Single(c => c.Uri.AbsolutePath == "/player/playback/skipTo");
        Assert.False(skip.HasToken);
        var query = QueryHelpers.ParseQuery(skip.Uri.Query);
        Assert.Equal("music", query["type"]); Assert.Equal("9002", query["playQueueItemID"]);
        Assert.Equal("/library/metadata/1002", query["key"]);
        Assert.True(long.Parse(query["commandID"]!) > 0);
        Assert.DoesNotContain(Token, skip.Uri.ToString());
        // the cache is cleared: the next snapshot reads again
        var before = fixture.ToPlayer.Count(c => c.Uri.AbsolutePath == "/player/timeline/poll");
        await plex.Snapshot(default);
        Assert.Equal(before + 1, fixture.ToPlayer.Count(c => c.Uri.AbsolutePath == "/player/timeline/poll"));
    }

    [Theory]
    [InlineData("9001")]       // the current track is not in the queue
    [InlineData("9999")]
    [InlineData("12345678901234")]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AQueueItemThatIsNotInTheQueueIsRefused(string? id)
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var result = await plex.Act("play-queued", id, default);
        Assert.False(result.Ok);
        Assert.Equal("That queue item is no longer available", result.Error);
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.AbsolutePath.StartsWith("/player/playback"));
    }

    [Fact]
    public async Task AnUnknownActionIsRefused()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var result = await plex.Act("explode", "1", default);
        Assert.False(result.Ok); Assert.Equal("Unknown Plex control", result.Error);
        Assert.Empty(fixture.Calls);
    }

    private static string Actions(params string[] actions) => ",\"actions\":[" + string.Join(',', actions) + "]";

    [Fact]
    public async Task AConfiguredPlaylistStartsOnThePlayer_WithAddressFieldsFromTheConfiguredServer()
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", "pl1", default);
        Assert.True(result.Ok, result.Error);
        var create = fixture.ToServer.Single(c => c.Uri.AbsolutePath == "/playQueues");
        Assert.Equal(HttpMethod.Post, create.Method); Assert.True(create.HasToken);
        var createQuery = QueryHelpers.ParseQuery(create.Uri.Query);
        Assert.Equal("12", createQuery["playlistID"]); Assert.Equal("audio", createQuery["type"]); Assert.Equal("0", createQuery["shuffle"]);
        var play = fixture.ToPlayer.Single(c => c.Uri.AbsolutePath == "/player/playback/playMedia");
        Assert.False(play.HasToken);
        var q = QueryHelpers.ParseQuery(play.Uri.Query);
        Assert.Equal("music", q["type"]); Assert.Equal(Machine, q["machineIdentifier"]);
        Assert.Equal("https", q["protocol"]); Assert.Equal("plex.example.test", q["address"]); Assert.Equal("8443", q["port"]);
        Assert.Equal("/library/metadata/777", q["key"]); Assert.Equal("/playQueues/5555?own=1", q["containerKey"]);
        Assert.Equal("0", q["offset"]);
        Assert.DoesNotContain("evil.example", play.Uri.ToString());
        Assert.DoesNotContain(Token, play.Uri.ToString());
    }

    [Theory]
    [InlineData("http://plex.lan:32400", "http", "plex.lan", "32400")]
    [InlineData("https://plex.example.test", "https", "plex.example.test", "443")]
    [InlineData("http://plex.lan", "http", "plex.lan", "80")]
    public async Task ThePortOfAnAddressWithoutOneIsTheSchemeDefault(string url, string scheme, string host, string port)
    {
        using var fixture = new Fixture(url, extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        using var plex = fixture.Create();
        await plex.Act("playlist", "pl1", default);
        var q = QueryHelpers.ParseQuery(fixture.ToPlayer.Single(c => c.Uri.AbsolutePath == "/player/playback/playMedia").Uri.Query);
        Assert.Equal(scheme, q["protocol"]); Assert.Equal(host, q["address"]); Assert.Equal(port, q["port"]);
    }

    [Theory]
    [InlineData("unknown", "plex-playlist", "12", true, "Choose a Plex playlist in desktop settings")]
    [InlineData("pl1", "playlist", "12", true, "Choose a Plex playlist in desktop settings")]
    [InlineData("pl1", "plex-playlist", "12", false, "Control is disabled")]
    [InlineData("pl1", "plex-playlist", "spotify:playlist:x", true, "Choose a Plex playlist in desktop settings")]
    [InlineData("pl1", "plex-playlist", "12/../x", true, "Choose a Plex playlist in desktop settings")]
    [InlineData("pl1", "plex-playlist", "", true, "Choose a Plex playlist in desktop settings")]
    public async Task APlaylistThatIsNotAConfiguredEnabledPlexPlaylistIsRefused(string id, string kind, string target, bool enabled, string error)
    {
        using var fixture = new Fixture(extra: Actions($$"""{"id":"pl1","label":"Chill","kind":"{{kind}}","target":"{{target}}","enabled":{{(enabled ? "true" : "false")}}}"""));
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", id, default);
        Assert.False(result.Ok); Assert.Equal(error, result.Error);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task APlayerThatRefusesACommandIsReported_AndTheCacheIsCleared(HttpStatusCode status)
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.PlayerCommandStatus = status;
        foreach (var result in new[] { await plex.Act("play-queued", "9002", default), await plex.Act("playlist", "pl1", default) })
        {
            Assert.False(result.Ok); Assert.Equal("The Plex player refused the command", result.Error);
        }
        var before = fixture.ToPlayer.Count(c => c.Uri.AbsolutePath == "/player/timeline/poll");
        await plex.Snapshot(default);
        Assert.Equal(before + 1, fixture.ToPlayer.Count(c => c.Uri.AbsolutePath == "/player/timeline/poll"));
    }

    [Fact]
    public async Task APlayerThatIsNotRunningAnswersACommandWithTheStartMessage()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/player/playback") ? throw Refused() : null;
        var result = await plex.Act("play-queued", "9002", default);
        Assert.False(result.Ok); Assert.StartsWith("Start Caldera Amp", result.Error);
    }

    [Fact]
    public async Task ThePlaylistPickerListsAudioPlaylistsWithNumericIds()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var list = (await plex.Playlists(default)).AsArray();
        Assert.Equal(new[] { "12", "13" }, list.Select(p => p!["id"]!.GetValue<string>()));
        Assert.Equal("Chill", list[0]!["name"]!.GetValue<string>());
        Assert.Contains(fixture.ToServer, c => c.Uri.AbsolutePath == "/playlists" && c.Uri.Query == "?playlistType=audio" && c.HasToken);
        Assert.Empty(fixture.ToPlayer);
    }

    [Fact]
    public async Task ThePlaylistPickerIsEmptyWithoutSettings()
    {
        using var fixture = new Fixture(null, null);
        using var plex = fixture.Create();
        Assert.Empty((await plex.Playlists(default)).AsArray());
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Check the Plex token in desktop settings")]
    [InlineData(HttpStatusCode.InternalServerError, "The Plex server is unavailable")]
    public async Task APlaylistReadThatFailsGivesTheReasonNotAnEmptyList(HttpStatusCode status, string error)
    {
        using var fixture = new Fixture();
        fixture.Override = r => r.RequestUri!.AbsolutePath == "/playlists" ? new HttpResponseMessage(status) : null;
        using var plex = fixture.Create();
        var answer = await plex.Playlists(default);
        Assert.IsType<JsonObject>(answer);
        Assert.Equal(error, answer["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://plex.example.test/", "https://plex.example.test")]
    [InlineData("  http://192.168.1.5:32400  ", "http://192.168.1.5:32400")]
    [InlineData("https://plex.example.test/base//", "https://plex.example.test/base")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("plex.example.test", null)]
    [InlineData("/relative", null)]
    [InlineData("ftp://plex.example.test", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("https://user:pass@plex.example.test", null)]
    [InlineData("https://user@plex.example.test", null)]
    [InlineData("https://plex.example.test/?a=1", null)]
    [InlineData("https://plex.example.test/#x", null)]
    [InlineData("https://plex.example.test/a b", null)]
    public void UrlsAreNormalizedOrRefused(string? value, string? expected) => Assert.Equal(expected, PlexControl.NormalizeUrl(value));

    [Fact]
    public void AnAddressOverTheLimitIsRefused() => Assert.Null(PlexControl.NormalizeUrl("https://plex.example.test/" + new string('a', 2048)));

    [Fact]
    public void SettingsAreNormalizedWhenLoaded()
    {
        using var fixture = new Fixture("https://user:pw@plex.example.test", "  tok\ten\n" + new string('x', 400) + "  ", ""","plexPlayerUrl":"not a url" """);
        var controls = fixture.Config.Get().Controls;
        Assert.Null(controls.PlexUrl);
        Assert.Equal(PlexControl.DefaultPlayerUrl, controls.PlexPlayerUrl);
        Assert.Equal(256, controls.PlexToken!.Length);
        Assert.StartsWith("token", controls.PlexToken);
        Assert.All(controls.PlexToken, c => Assert.False(char.IsControl(c)));
        fixture.Write("https://plex.example.test/", "   ", ""","plexPlayerUrl":"http://127.0.0.1:2000/" """);
        controls = fixture.Config.Get().Controls;
        Assert.Equal("https://plex.example.test", controls.PlexUrl);
        Assert.Null(controls.PlexToken);
        Assert.Equal("http://127.0.0.1:2000", controls.PlexPlayerUrl);
    }

    [Fact]
    public void ADefaultConfigHasNoPlexServerAndTheDefaultPlayer()
    {
        using var fixture = new Fixture(null, null);
        File.WriteAllText(fixture.ConfigPath, "{}");
        File.SetLastWriteTimeUtc(fixture.ConfigPath, DateTime.UtcNow.AddMinutes(5));
        var controls = fixture.Config.Get().Controls;
        Assert.Null(controls.PlexUrl); Assert.Null(controls.PlexToken);
        Assert.Equal("http://127.0.0.1:32500", controls.PlexPlayerUrl);
    }

    [Fact]
    public void NoPlexSettingReachesThePhone()
    {
        using var fixture = new Fixture(extra: ""","plexPlayerUrl":"http://127.0.0.1:2000" """, pages: """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"wc","type":"clock","x":0,"y":0,"w":2,"h":2}]}]""");
        var json = JsonSerializer.Serialize(fixture.Config.Get().ToPublic());
        Assert.DoesNotContain("plex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain("example.test", json);
        Assert.DoesNotContain("127.0.0.1:2000", json);
    }

    [Fact]
    public void ThePhoneMayReadTheQueueAndSendAnIdButNotListPlaylists()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/controls/plex/state")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/controls/plex/action/play-queued")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/controls/plex/action/playlist")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/controls/plex/playlists")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/controls/plex/check")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/controls/plex/state/x")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/controls/plex")));
    }

    // ---- tester additions (spec edge cases and the user's hand-check fact about keys that differ only in case) ----

    [Fact]
    public async Task KeysThatDifferOnlyInCaseInTheServerJsonAreReadAsWritten()
    {
        // real Plex answers repeat a name in two cases on one object: "guid" (text) and "Guid" (list)
        using var fixture = new Fixture
        {
            QueueJson = """
            {"MediaContainer":{"size":2,"playQueueID":4431,"Metadata":[
              {"playQueueItemID":9001,"key":"/library/metadata/1001","title":"Song 9001","grandparentTitle":"Artist 9001","guid":"plex://track/a","Guid":[{"id":"mbid://a"}]},
              {"playQueueItemID":9002,"key":"/library/metadata/1002","title":"Song 9002","grandparentTitle":"Artist 9002","guid":"plex://track/b","Guid":[{"id":"mbid://b"}]}
            ]}}
            """,
        };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>(), snap["error"]?.ToString());
        Assert.Equal("9001", snap["current"]!["id"]!.GetValue<string>());
        Assert.Equal("Song 9002", snap["queue"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Artist 9002", snap["queue"]![0]!["artist"]!.GetValue<string>());
    }

    [Fact]
    public async Task KeysThatDifferOnlyInCaseInTheCreatedQueueAreReadAsWritten()
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        fixture.Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/playQueues"
            ? Json("""{"MediaContainer":{"playQueueID":5555,"guid":"x","Guid":[],"Metadata":[{"guid":"g","Guid":[{"id":"mbid://z"}],"key":"/library/metadata/777"}]}}""")
            : null;
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", "pl1", default);
        Assert.True(result.Ok, result.Error);
        var q = QueryHelpers.ParseQuery(fixture.ToPlayer.Single(c => c.Uri.AbsolutePath == "/player/playback/playMedia").Uri.Query);
        Assert.Equal("/library/metadata/777", q["key"]); Assert.Equal("/playQueues/5555?own=1", q["containerKey"]);
    }

    [Fact]
    public async Task APlaylistCreatedWithoutASelectedItemUsesTheFirstKey()
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        fixture.Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/playQueues"
            ? Json("""{"MediaContainer":{"playQueueID":5556,"Metadata":[{"key":"/library/metadata/778"},{"key":"/library/metadata/779"}]}}""")
            : null;
        using var plex = fixture.Create();
        Assert.True((await plex.Act("playlist", "pl1", default)).Ok);
        var q = QueryHelpers.ParseQuery(fixture.ToPlayer.Single(c => c.Uri.AbsolutePath == "/player/playback/playMedia").Uri.Query);
        Assert.Equal("/library/metadata/778", q["key"]);
    }

    [Fact]
    public async Task ADuplicateKeyInAnAnswerDoesNotCrash()
    {
        using var fixture = new Fixture
        {
            QueueJson = """{"MediaContainer":{"Metadata":[{"playQueueItemID":9001,"playQueueItemID":9002,"key":"/library/metadata/1001"}]}}""",
        };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("Plex sent an answer that could not be read", snap["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADuplicateKeyInAServerAnswerIsUnreadableForActAndPlaylistsToo()
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        fixture.Override = r => r.RequestUri!.AbsolutePath is "/playQueues" or "/playlists"
            ? Json("""{"MediaContainer":{"playQueueID":1,"playQueueID":2,"Metadata":[{"ratingKey":"1","ratingKey":"2"}]}}""") : null;
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", "pl1", default);
        Assert.False(result.Ok); Assert.Equal("Plex sent an answer that could not be read", result.Error);
        Assert.Equal("Plex sent an answer that could not be read", (await plex.Playlists(default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMissingPlayQueueIdAttributeGivesNoQueue()
    {
        using var fixture = new Fixture
        {
            Timeline = $"""<MediaContainer><Timeline type="music" state="playing" playQueueItemID="9001" machineIdentifier="{Machine}"/></MediaContainer>""",
        };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.Null(snap["current"]);
        Assert.Empty(snap["queue"]!.AsArray());
        Assert.DoesNotContain(fixture.Calls, c => c.Uri.AbsolutePath.StartsWith("/playQueues"));
    }

    [Fact]
    public async Task AnOversizedPlayerAnswerIsAnErrorNotACrash()
    {
        using var fixture = new Fixture { Timeline = new string(' ', 1_100_000) + TimelineXml() };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(snap["error"]?.GetValue<string>()));
    }

    [Fact]
    public async Task AStoppedTimelineWithTheServerDownIsAServerError_SpecEdgeSix()
    {
        // round 3, decision 1: edge 4 wins when the player is idle and the server cannot be reached
        using var fixture = new Fixture { Timeline = TimelineXml("stopped") };
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw Refused() : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnIdlePlayerWithTheServerDownLaterIsAServerError_SpecEdgeSixAfterACachedIdentity()
    {
        // tester: edge 6 says the agent still reads /identity; a cached identity must not hide a server that is now down
        using var fixture = new Fixture { Timeline = TimelineXml("stopped") };
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        fixture.Clock += 61_000; // past the 5 s cache and the 60 s last good window
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw Refused() : null;
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
    }

    private static int Identities(Fixture f) => f.ToServer.Count(c => c.Uri.AbsolutePath == "/identity");
    private static int Queues(Fixture f) => f.ToServer.Count(c => c.Uri.AbsolutePath.StartsWith("/playQueues/"));

    [Fact]
    public async Task AnIdlePlayerWithTheServerDownWithinSixtySecondsKeepsTheLastGoodSnapshot_R42()
    {
        using var fixture = new Fixture { Timeline = TimelineXml("stopped") };
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        fixture.Clock += 6_000;
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw Refused() : null;
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        Assert.Equal(2, Identities(fixture));
    }

    [Fact]
    public async Task AnIdlePlayerReadsTheIdentityOnEveryRead_R43()
    {
        using var fixture = new Fixture { Timeline = TimelineXml("stopped") };
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Clock += 6_000;
        await plex.Snapshot(default);
        Assert.Equal(2, Identities(fixture));
    }

    [Fact]
    public async Task AnActivePlayerUsesTheCachedIdentity_R44()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Clock += 6_000;
        await plex.Snapshot(default);
        Assert.Equal(1, Identities(fixture));
        Assert.Equal(2, Queues(fixture));
    }

    [Fact]
    public async Task AnActivePlayerWithTheServerDownIsAServerErrorThroughThePlayQueue_R45()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        fixture.Clock += 61_000;
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? throw Refused() : null;
        var before = Identities(fixture);
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
        Assert.Equal(before, Identities(fixture)); // Route records every call before Override runs, so an unchanged count means no /identity request
    }

    [Fact]
    public async Task AFailedIdentityReadKeepsTheCachedIdentityForTheSameUrl_SpecEdgeThirty()
    {
        // tester: edge 30. A failed fresh /identity must not clear the value an active read trusts for the same plexUrl
        using var fixture = new Fixture { Timeline = TimelineXml("stopped") };
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        fixture.Clock += 6_000;
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" && r.RequestUri.AbsolutePath == "/identity" ? throw Refused() : null;
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>()); // last good snapshot inside 60 s
        Assert.Equal(2, Identities(fixture));
        fixture.Clock += 6_000;
        fixture.Timeline = TimelineXml("playing");
        fixture.Override = null;
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.False(snap.ContainsKey("error"));
        Assert.NotNull(snap["current"]);
        Assert.Equal(2, Identities(fixture)); // the active read used the cached identity: no third /identity request
    }

    [Fact]
    public async Task AnActiveReadAfterTheUrlChangesReadsTheIdentityOfTheNewUrl_SpecEdgeThirty()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        Assert.Equal(1, Identities(fixture));
        fixture.Write("https://other.example.test:8443", Token);
        fixture.Clock += 6_000;
        await plex.Snapshot(default);
        Assert.Equal(1, fixture.ToServer.Count(c => c.Uri.Host == "other.example.test" && c.Uri.AbsolutePath == "/identity"));
    }

    [Fact]
    public async Task AnUnknownOrOtherKindPlaylistIdOnAShownWidgetIsAChoosePlaylistError_R46()
    {
        using var fixture = new Fixture(pages: PlaylistsPage("{}"), extra: Actions(PlaylistAction, """{"id":"x1","label":"Other","kind":"playlist","target":"spotify:playlist:1"}"""));
        using var plex = fixture.Create();
        foreach (var id in new[] { "nope", "x1" })
        {
            var result = await plex.Act("playlist", id, default);
            Assert.False(result.Ok); Assert.Equal("Choose a Plex playlist in desktop settings", result.Error);
        }
        Assert.Empty(fixture.Calls);
        using var hidden = new Fixture(pages: PlaylistsPage("""{"ids":["pl1"]}"""), extra: Actions(PlaylistAction));
        using var plex2 = hidden.Create();
        var gated = await plex2.Act("playlist", "nope", default);
        Assert.Equal("Add a Plex playlists widget to an enabled page", gated.Error);
        Assert.Empty(hidden.Calls);
    }

    [Fact]
    public async Task ThePlayerIsAskedWithNoAcceptHeader_AndTheServerForJson()
    {
        // round 3, decision 2: the hand checks sent no Accept header to the player and Caldera answered XML
        var playerAccept = new List<bool>(); var serverAccept = new List<string>();
        using var fixture = new Fixture();
        fixture.Override = r =>
        {
            if (r.RequestUri!.Host == "127.0.0.1") playerAccept.Add(r.Headers.Accept.Count > 0);
            else serverAccept.Add(string.Join(",", r.Headers.Accept));
            return null;
        };
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        Assert.NotEmpty(playerAccept); Assert.NotEmpty(serverAccept);
        Assert.All(playerAccept, has => Assert.False(has));
        Assert.All(serverAccept, a => Assert.Equal("application/json", a));
    }

    [Fact]
    public async Task ThePlexPlaylistActionRunsThroughTheDeck()
    {
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(new
        {
            controls = new { plexUrl = "https://plex.example.test:8443", plexToken = Token, actions = new[] { new { id = "pl1", label = "Chill", kind = "plex-playlist", target = "12", enabled = true } } },
            pages = new[] { new { id = "p1", enabled = true, widgets = new[] { new { id = "w1", type = "plex-playlists", x = 0, y = 0, w = 2, h = 2 } } } },
        }));
        File.SetLastWriteTimeUtc(fixture.ConfigPath, DateTime.UtcNow.AddMinutes(1));
        using var plex = fixture.Create();
        using var deck = new PhoneBox.Agent.ControlDeck(fixture.Config, new NoNative(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhoneBox.Agent.ControlDeck>.Instance,
            () => 1_000_000, null, null, null, null, plex);
        var result = await deck.Act("action", "pl1", null, null, null, null, default);
        Assert.True(result.Ok, result.Error);
        Assert.Single(fixture.ToPlayer.Where(c => c.Uri.AbsolutePath == "/player/playback/playMedia"));
    }

    [Fact]
    public async Task ThePlexPlaylistActionIsRefusedWhenNoEnabledPageShowsAPlexWidget_SpecEdgeSeventeen()
    {
        // round 3, decision 3: the gate is inside PlexControl.Act, so the deck path is gated too
        using var fixture = new Fixture(extra: Actions("""{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}"""));
        File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(new
        {
            controls = new { plexUrl = "https://plex.example.test:8443", plexToken = Token, actions = new[] { new { id = "pl1", label = "Chill", kind = "plex-playlist", target = "12", enabled = true } } },
            pages = Array.Empty<object>(),
        }));
        File.SetLastWriteTimeUtc(fixture.ConfigPath, DateTime.UtcNow.AddMinutes(2));
        using var plex = fixture.Create();
        using var deck = new PhoneBox.Agent.ControlDeck(fixture.Config, new NoNative(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhoneBox.Agent.ControlDeck>.Instance,
            () => 1_000_000, null, null, null, null, plex);
        var result = await deck.Act("action", "pl1", null, null, null, null, default);
        Assert.False(result.Ok);
        Assert.Empty(fixture.ToPlayer);
    }

    // ---- round 3: page gate (edge 17) ----

    private const string PlaylistAction = """{"id":"pl1","label":"Chill","kind":"plex-playlist","target":"12"}""";
    private const string ClockWidget = """{"id":"wc","type":"clock","x":0,"y":0,"w":2,"h":2}""";
    private const string QueueOnly = """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"w1","type":"plex-queue","x":0,"y":0,"w":4,"h":4}]}]""";
    private static string PlaylistsPage(string options) =>
        """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"w2","type":"plex-playlists","x":0,"y":0,"w":4,"h":4,"options":""" + options + "}]}]";

    public static TheoryData<string> NoWidgetPages => new()
    {
        // no Plex widget at all
        $$"""[{"id":"p1","title":"P","enabled":true,"widgets":[{{ClockWidget}}]}]""",
        // both Plex widgets, on a disabled page
        $$"""[{"id":"p1","title":"P","enabled":false,"widgets":[{"id":"w1","type":"plex-queue","x":0,"y":0,"w":4,"h":4},{"id":"w2","type":"plex-playlists","x":4,"y":0,"w":4,"h":4}]},{"id":"p2","title":"Q","enabled":true,"widgets":[{{ClockWidget}}]}]""",
        // a Smart Stack child that is switched off
        """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"s","type":"stack","x":0,"y":0,"w":4,"h":4,"options":{"widgets":[{"id":"c1","type":"plex-queue","enabled":false},{"id":"c2","type":"plex-playlists","active":false}]}}]}]""",
    };

    [Theory]
    [MemberData(nameof(NoWidgetPages))]
    public async Task WithNoPlexWidgetOnAnEnabledPageNothingIsSentAndNothingIsCached(string pages)
    {
        using var fixture = new Fixture(pages: pages, extra: Actions(PlaylistAction));
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("Add a Plex widget to an enabled page", snap["error"]!.GetValue<string>());
        var queued = await plex.Act("play-queued", "9002", default);
        Assert.False(queued.Ok); Assert.Equal("Add a Plex widget to an enabled page", queued.Error);
        var playlist = await plex.Act("playlist", "pl1", default);
        Assert.False(playlist.Ok); Assert.Equal("Add a Plex playlists widget to an enabled page", playlist.Error);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task AnEnabledSmartStackChildCountsAsAPlexWidget()
    {
        var pages = """[{"id":"p1","title":"P","enabled":true,"widgets":[{"id":"s","type":"stack","x":0,"y":0,"w":4,"h":4,"options":{"widgets":[{"id":"c1","type":"plex-queue"}]}}]}]""";
        using var fixture = new Fixture(pages: pages);
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HidingThePageStopsTheRequestsOnTheNextPoll()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        var before = fixture.Calls.Count;
        fixture.Write("https://plex.example.test:8443", Token, pages: $$"""[{"id":"p1","title":"P","enabled":true,"widgets":[{{ClockWidget}}]}]""");
        fixture.Clock += 10_000;
        var snap = await plex.Snapshot(default);
        Assert.Equal("Add a Plex widget to an enabled page", snap["error"]!.GetValue<string>());
        Assert.Equal(before, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(QueueOnly, false)]                                  // a queue widget alone does not allow a playlist
    [InlineData("""{"ids":["other"]}""", false)]                    // the widget does not show this action
    [InlineData("""{"ids":["pl1"]}""", true)]
    [InlineData("""{"ids":[]}""", true)]                            // an empty list shows every action
    [InlineData("""{"ids":"other"}""", true)]                       // any other kind counts as absent
    [InlineData("""{}""", true)]
    public async Task APlaylistNeedsAPlaylistsWidgetThatShowsIt(string pagesOrOptions, bool allowed)
    {
        var pages = pagesOrOptions == QueueOnly ? QueueOnly : PlaylistsPage(pagesOrOptions);
        using var fixture = new Fixture(pages: pages, extra: Actions(PlaylistAction));
        using var plex = fixture.Create();
        var result = await plex.Act("playlist", "pl1", default);
        if (allowed) { Assert.True(result.Ok, result.Error); return; }
        Assert.False(result.Ok); Assert.Equal("Add a Plex playlists widget to an enabled page", result.Error);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CheckAndThePlaylistListAreNotGated()
    {
        using var fixture = new Fixture(pages: $$"""[{"id":"p1","title":"P","enabled":true,"widgets":[{{ClockWidget}}]}]""");
        using var plex = fixture.Create();
        Assert.True((await plex.Check(default))["connected"]!.GetValue<bool>());
        Assert.NotEmpty((await plex.Playlists(default)).AsArray());
        Assert.NotEmpty(fixture.ToPlayer); Assert.NotEmpty(fixture.ToServer);
    }

    // ---- round 3: upstream review checklist (a to i, the C# half) ----

    private static void ServeLocal(HttpListener listener, CancellationToken stop, Func<HttpListenerContext, Task> handle) =>
        _ = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var c = await listener.GetContextAsync(); _ = Task.Run(() => handle(c).ContinueWith(_ => { })); } }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });

    /// <summary>The status line and headers go out, a few body bytes follow, and then the server goes quiet.</summary>
    private static async Task StallLocal(HttpListenerContext c, CancellationToken stop, string type)
    {
        c.Response.StatusCode = 200;
        c.Response.ContentType = type;
        c.Response.SendChunked = true;
        await c.Response.OutputStream.WriteAsync("<M"u8.ToArray(), stop);
        await c.Response.OutputStream.FlushAsync(stop);
        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
    }

    private static async Task Reply(HttpListenerContext c, string body, string type = "application/json", int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = status; c.Response.ContentType = type; c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    private static string Url(HttpListener l) => l.Prefixes.First().TrimEnd('/');
    private static PlexControl Real(Fixture f, TimeSpan stall) => new(f.Config, new HttpClient(PlexControl.NewPlexHandler()) { Timeout = Timeout.InfiniteTimeSpan }, null, stall);

    [Fact]
    public async Task AStallingPlayerIsReportedAsNotRunning_WithinSeconds()
    {
        using var stop = new CancellationTokenSource();
        var player = TestPorts.Listen();
        try
        {
            ServeLocal(player, stop.Token, c => StallLocal(c, stop.Token, "text/xml"));
            using var fixture = new Fixture("http://127.0.0.1:9", extra: $",\"plexPlayerUrl\":\"{Url(player)}\"");
            using var plex = Real(fixture, TimeSpan.FromMilliseconds(300));
            var snap = await plex.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("Start Caldera Amp", snap["error"]!.GetValue<string>());
        }
        finally { stop.Cancel(); player.Close(); }
    }

    [Fact]
    public async Task AStallingServerIsReportedAsUnavailable_WithinSeconds()
    {
        using var stop = new CancellationTokenSource();
        var player = TestPorts.Listen(); var server = TestPorts.Listen();
        try
        {
            ServeLocal(player, stop.Token, c => Reply(c, TimelineXml(), "text/xml"));
            ServeLocal(server, stop.Token, c => StallLocal(c, stop.Token, "application/json"));
            using var fixture = new Fixture(Url(server), extra: $",\"plexPlayerUrl\":\"{Url(player)}\"");
            // the player answers at once, but a busy CI runner can take longer than 300 ms to get that answer to the
            // agent, which then reports the player missing instead of the server stalling: a budget the stall still
            // exceeds by far, while the player's answer fits in it
            using var plex = Real(fixture, TimeSpan.FromMilliseconds(1500));
            var snap = await plex.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
        }
        finally { stop.Cancel(); player.Close(); server.Close(); }
    }

    [Fact]
    public async Task ACancelledCallerIsRethrownNotTurnedIntoAMessage()
    {
        using var stop = new CancellationTokenSource();
        var player = TestPorts.Listen();
        try
        {
            ServeLocal(player, stop.Token, c => StallLocal(c, stop.Token, "text/xml"));
            using var fixture = new Fixture("http://127.0.0.1:9", extra: $",\"plexPlayerUrl\":\"{Url(player)}\"");
            using var plex = Real(fixture, TimeSpan.FromSeconds(30));
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plex.Snapshot(caller.Token));
        }
        finally { stop.Cancel(); player.Close(); }
    }

    /// <summary>A body that declares its length and fails the test if anything reads it.</summary>
    private sealed class Declared(long length) : HttpContent
    {
        public int Reads;
        protected override bool TryComputeLength(out long l) { l = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Reads++; throw new IOException("must not be read"); }
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCapIsRefusedBeforeAnyRead()
    {
        using var fixture = new Fixture();
        var body = new Declared(2_000_000);
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/playQueues/") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = body } : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.Equal("Plex sent an answer that could not be read", snap["error"]!.GetValue<string>());
        Assert.Equal(0, body.Reads);
    }

    [Fact]
    public async Task AnErrorBodyIsNeverRead()
    {
        using var fixture = new Fixture();
        var body = new Declared(10);
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/playQueues/") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = body } : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
        Assert.Equal(0, body.Reads);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    [InlineData("""{"MediaContainer":[]}""")]
    [InlineData("""{"MediaContainer":{"Metadata":["x"]}}""")]
    public async Task OddJsonKindsAreUnreadableNotACrash(string json)
    {
        using var fixture = new Fixture(extra: Actions(PlaylistAction));
        fixture.Override = r => r.RequestUri!.AbsolutePath is "/playlists" or "/playQueues" || r.RequestUri.AbsolutePath.StartsWith("/playQueues/") ? Json(json) : null;
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("Plex sent an answer that could not be read", snap["error"]!.GetValue<string>());
        var result = await plex.Act("playlist", "pl1", default);
        Assert.False(result.Ok); Assert.Equal("Plex sent an answer that could not be read", result.Error);
        Assert.Equal("Plex sent an answer that could not be read", (await plex.Playlists(default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMetadataThatIsNotAListIsAnEmptyQueue()
    {
        using var fixture = new Fixture { QueueJson = """{"MediaContainer":{"Metadata":{}}}""" };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>());
        Assert.Empty(snap["queue"]!.AsArray());
    }

    [Fact]
    public async Task ATokenWithControlCharactersIsCleanedAndSendsWithoutAnError()
    {
        using var fixture = new Fixture(token: "to\u0001ke\u007fn\r\n");
        var sent = new List<string>();
        fixture.Override = r => { if (r.Headers.TryGetValues("X-Plex-Token", out var v)) sent.AddRange(v); return null; };
        using var plex = fixture.Create();
        var snap = await plex.Snapshot(default);
        Assert.True(snap["connected"]!.GetValue<bool>(), snap["error"]?.ToString());
        Assert.NotEmpty(sent);
        Assert.All(sent, v => Assert.Equal("token", v));
    }

    // ---- last good state (edge 24) ----

    [Fact]
    public async Task OneBadServerPollKeepsTheLastGoodSnapshotForAMinute()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        var good = await plex.Snapshot(default);
        Assert.True(good["connected"]!.GetValue<bool>());
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/playQueues/") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        fixture.Clock += 6_000;
        var kept = await plex.Snapshot(default);
        Assert.True(kept["connected"]!.GetValue<bool>());
        Assert.Equal("9001", kept["current"]!["id"]!.GetValue<string>());
        // the kept queue keys still work for a tap while it is the current snapshot
        Assert.True((await plex.Act("play-queued", "9002", default)).Ok);
        fixture.Clock += 70_000;
        var late = await plex.Snapshot(default);
        Assert.False(late["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", late["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task APlayerThatDisappearsKeepsTheLastGoodSnapshotForAMinute()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Override = r => r.RequestUri!.Host == "127.0.0.1" ? throw Refused() : null;
        fixture.Clock += 6_000;
        Assert.True((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
        fixture.Clock += 70_000;
        Assert.StartsWith("Start Caldera Amp", (await plex.Snapshot(default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangedSettingsDoNotGetTheOldSnapshot()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Write("https://other.example.test", Token);
        fixture.Override = r => r.RequestUri!.Host != "127.0.0.1" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        fixture.Clock += 6_000;
        var snap = await plex.Snapshot(default);
        Assert.False(snap["connected"]!.GetValue<bool>());
        Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARefusedTokenIsReportedAtOnceAndForgetsTheLastGoodSnapshot()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/playQueues/") ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;
        fixture.Clock += 6_000;
        Assert.Equal("Check the Plex token in desktop settings", (await plex.Snapshot(default))["error"]!.GetValue<string>());
        fixture.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/playQueues/") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        fixture.Clock += 6_000;
        Assert.False((await plex.Snapshot(default))["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnUnreadableAnswerIsReportedAtOnceNotHiddenBehindTheLastGoodSnapshot()
    {
        using var fixture = new Fixture();
        using var plex = fixture.Create();
        await plex.Snapshot(default);
        fixture.QueueJson = "{ not json";
        fixture.Clock += 6_000;
        Assert.Equal("Plex sent an answer that could not be read", (await plex.Snapshot(default))["error"]!.GetValue<string>());
    }

    // ---- redirects with real listeners (edge 25) and the handler (checklist h) ----

    [Fact]
    public async Task AServerRedirectIsNotFollowedAndTheOtherListenerSeesNothing()
    {
        using var stop = new CancellationTokenSource();
        var player = TestPorts.Listen(); var server = TestPorts.Listen(); var other = TestPorts.Listen();
        var otherHits = 0;
        try
        {
            ServeLocal(player, stop.Token, c => Reply(c, TimelineXml(), "text/xml"));
            ServeLocal(server, stop.Token, c => { c.Response.StatusCode = 302; c.Response.RedirectLocation = Url(other) + "/identity"; c.Response.Close(); return Task.CompletedTask; });
            ServeLocal(other, stop.Token, c => { Interlocked.Increment(ref otherHits); return Reply(c, "{}"); });
            using var fixture = new Fixture(Url(server), extra: $",\"plexPlayerUrl\":\"{Url(player)}\"");
            using var plex = Real(fixture, TimeSpan.FromSeconds(5));
            var snap = await plex.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("The Plex server is unavailable", snap["error"]!.GetValue<string>());
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref otherHits));
        }
        finally { stop.Cancel(); player.Close(); server.Close(); other.Close(); }
    }

    [Fact]
    public async Task APlayerRedirectIsNotFollowedAndTheOtherListenerSeesNothing()
    {
        using var stop = new CancellationTokenSource();
        var player = TestPorts.Listen(); var server = TestPorts.Listen(); var other = TestPorts.Listen();
        var otherHits = 0;
        try
        {
            ServeLocal(player, stop.Token, c =>
            {
                if (c.Request.Url!.AbsolutePath == "/player/timeline/poll") return Reply(c, TimelineXml(), "text/xml");
                c.Response.StatusCode = 302; c.Response.RedirectLocation = Url(other) + "/x"; c.Response.Close(); return Task.CompletedTask;
            });
            ServeLocal(server, stop.Token, c => c.Request.Url!.AbsolutePath == "/identity"
                ? Reply(c, "{\"MediaContainer\":{\"machineIdentifier\":\"" + Machine + "\"}}") : Reply(c, QueueOf("9001", "9002")));
            ServeLocal(other, stop.Token, c => { Interlocked.Increment(ref otherHits); return Reply(c, "{}"); });
            using var fixture = new Fixture(Url(server), extra: $",\"plexPlayerUrl\":\"{Url(player)}\"");
            using var plex = Real(fixture, TimeSpan.FromSeconds(5));
            var result = await plex.Act("play-queued", "9002", default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Ok); Assert.Equal("The Plex player refused the command", result.Error);
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref otherHits));
        }
        finally { stop.Cancel(); player.Close(); server.Close(); other.Close(); }
    }

    [Fact]
    public void TheHandlerNeverFollowsRedirectsAndRenewsConnections()
    {
        using var handler = PlexControl.NewPlexHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
    }

    // ---- no CancellationToken.None in the Plex code (checklist d) ----

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "agent", "PhoneBox.Agent", file))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "agent", "PhoneBox.Agent", file));
    }

    [Fact]
    public void ThePlexCodeAndRoutesUseNoNoneToken()
    {
        Assert.DoesNotContain("CancellationToken.None", Source("PlexControl.cs"));
        var program = Source("Program.cs");
        var from = program.IndexOf("\"/api/controls/plex/state\"", StringComparison.Ordinal);
        var to = program.IndexOf("\"/api/controls/spotify/connect\"", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from);
        Assert.DoesNotContain("CancellationToken.None", program[from..to]);
    }

    /// <summary>A computer with no media, audio or MIDI; only the calls the Plex path can reach are needed.</summary>
    private sealed class NoNative : PhoneBox.Agent.IDeckNative
    {
        public PhoneBox.Agent.DeckAudio Audio() => null!;
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => false;
        public bool Session(string id, int? volume, bool? muted) => false;
        public bool DefaultDevice(string id, int? role = null) => false;
        public IReadOnlyList<PhoneBox.Agent.DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<PhoneBox.Agent.DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<PhoneBox.Agent.DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<PhoneBox.Agent.DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(false);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<PhoneBox.Agent.DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<PhoneBox.Agent.DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { yield break; }
        public void Dispose() { }
    }
}
