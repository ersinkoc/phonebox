using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using PhoneBox.Agent;
using Xunit;

public sealed class SpotifyControlTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "phonebox-spotify-" + Guid.NewGuid().ToString("N"));
        public ConfigStore Config { get; }
        public Fixture(bool manual = false)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath,"config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { controls = new { spotifyClientId = "test-client", spotifyToken = manual ? "manual-token" : null } }));
            Config = new ConfigStore(path);
        }
        public SpotifyControl Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => new(Config,new AgentEndpoint(19885),new HttpClient(new Handler(handler)));
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) {Content=new StringContent(json,Encoding.UTF8,"application/json")};

    [Fact]
    public void TracksCarryTheSmallestUsableCoverFromSpotifysImageServer()
    {
        var track = SpotifyControl.TrackOf(JsonNode.Parse("""
            {"uri":"spotify:track:0000000000000000000001","name":"Song","artists":[{"name":"A"},{"name":"B"}],
             "album":{"images":[{"url":"https://i.scdn.co/image/big640","width":640},{"url":"https://i.scdn.co/image/mid300","width":300},
                                {"url":"https://i.scdn.co/image/tiny32","width":32}]},"external_urls":{"spotify":"https://open.spotify.com/x"}}
            """))!;
        Assert.Equal("A, B", track["artist"]!.GetValue<string>());
        Assert.Equal("https://i.scdn.co/image/mid300", track["image"]!.GetValue<string>());
        Assert.Null(track["external_urls"]); // only id, name, artist and cover reach the phone
    }

    [Fact]
    public async Task ThePlayingEpisodeAndQueuedEpisodesAreAskedFor()
    {
        using var fixture = new Fixture(manual: true);
        var asked = new List<string>();
        using var spotify = fixture.Create(r =>
        {
            asked.Add(r.RequestUri!.PathAndQuery);
            return Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/queue")
                ? Json("""{"queue":[]}""")
                : Json("""{"is_playing":true,"item":{"uri":"spotify:episode:0000000000000000000002","name":"Ep","show":{"name":"Show"},"images":[]}}"""));
        });
        var snap = await spotify.Snapshot(default);
        Assert.Equal("Ep", snap["current"]!["name"]!.GetValue<string>());
        Assert.All(asked, path => Assert.Contains("additional_types=track,episode", path));
    }

    [Fact]
    public void CoversFromAnyOtherHostAreDropped()
    {
        var track = SpotifyControl.TrackOf(JsonNode.Parse("""
            {"uri":"spotify:track:0000000000000000000001","name":"Song","artists":[],
             "album":{"images":[{"url":"https://evil.example/i.scdn.co/image/x","width":300},{"url":"http://i.scdn.co/image/abc","width":300},
                                {"url":"https://i.scdn.co/image/abc?x=1","width":300}]}}
            """))!;
        Assert.Null(track["image"]);
    }

    [Fact]
    public void EpisodesUseTheShowNameAndTheirOwnImages()
    {
        var episode = SpotifyControl.TrackOf(JsonNode.Parse("""
            {"uri":"spotify:episode:0000000000000000000002","name":"Ep 1","show":{"name":"The Show"},
             "images":[{"url":"https://i.scdn.co/image/ep64","width":64}]}
            """))!;
        Assert.Equal("The Show", episode["artist"]!.GetValue<string>());
        Assert.Equal("https://i.scdn.co/image/ep64", episode["image"]!.GetValue<string>());
    }

    [Fact]
    public async Task PkceUsesSingleUseStateAndDoesNotWriteTokensIntoSettings()
    {
        using var fixture = new Fixture(); var calls = 0; string? tokenBody = null;
        using var spotify = fixture.Create(async r=> { calls++; Assert.Equal("accounts.spotify.com",r.RequestUri!.Host); tokenBody=await r.Content!.ReadAsStringAsync();return Json("""{"access_token":"secret-access","refresh_token":"secret-refresh","expires_in":3600}"""); });
        var query = QueryHelpers.ParseQuery(new Uri(spotify.Begin()).Query);
        Assert.Equal("S256",query["code_challenge_method"]); Assert.Equal(43,query["code_challenge"].ToString().Length);
        Assert.Equal("http://127.0.0.1:19885/api/controls/spotify/callback",query["redirect_uri"]);
        var state=query["state"].ToString();
        Assert.False(await spotify.Complete("wrong-state","code",default)); Assert.Equal(0,calls);
        Assert.True(await spotify.Complete(state,"code",default));
        Assert.Contains("code_verifier=",tokenBody); Assert.False(spotify.HasPendingState(state));
        Assert.False(await spotify.Complete(state,"code",default)); Assert.Equal(1,calls);
        Assert.DoesNotContain("secret-access",File.ReadAllText(fixture.Config.Path));
        Assert.DoesNotContain("secret-refresh",JsonSerializer.Serialize(fixture.Config.Get().ToPublic()));
    }

    [Fact]
    public async Task StartingAnotherSignInInvalidatesThePreviousState()
    {
        using var fixture=new Fixture();using var spotify=fixture.Create(_=>throw new Exception("No token exchange expected"));
        var first=QueryHelpers.ParseQuery(new Uri(spotify.Begin()).Query)["state"].ToString();
        var second=QueryHelpers.ParseQuery(new Uri(spotify.Begin()).Query)["state"].ToString();
        Assert.False(spotify.HasPendingState(first));Assert.True(spotify.HasPendingState(second));
        await spotify.Disconnect(default);Assert.False(spotify.HasPendingState(second));
    }

    [Fact]
    public async Task DisconnectPersistsAcrossRestartAndDisablesManualTokenFallback()
    {
        using var fixture=new Fixture(manual:true);var calls=0;
        Task<HttpResponseMessage> Handler(HttpRequestMessage r){calls++;return Task.FromResult(Json("{}"));}
        using(var spotify=fixture.Create(Handler))await spotify.Disconnect(default);
        using(var restarted=fixture.Create(Handler))Assert.False((await restarted.Snapshot(default))["connected"]!.GetValue<bool>());
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task QueueCommandsRejectUnobservedItemsAndSaveUsesCurrentItem()
    {
        using var fixture=new Fixture(manual:true);var paths=new List<string>();
        using var spotify=fixture.Create(r=>{
            paths.Add(r.Method+" "+r.RequestUri!.PathAndQuery);
            return Task.FromResult(r.Method==HttpMethod.Put?new HttpResponseMessage(HttpStatusCode.NoContent):Json(r.RequestUri.AbsolutePath.EndsWith("queue")
                ? """{"queue":[{"uri":"spotify:track:abcdefghijklmnopqrstuv","name":"Queued","artists":[]}]}"""
                : """{"item":{"uri":"spotify:track:1234567890123456789012","name":"Current","artists":[]},"is_playing":true}"""));
        });
        Assert.False((await spotify.Act("play-queued","spotify:track:0000000000000000000000",default)).Ok);
        Assert.DoesNotContain(paths,p=>p.StartsWith("PUT"));
        Assert.True((await spotify.Act("favorite",null,default)).Ok);
        Assert.Contains(paths,p=>p.StartsWith("PUT /v1/me/library?uris=spotify%3Atrack%3A1234567890123456789012"));
    }

    [Fact]
    public async Task RateLimitSuppressesRepeatedHttpRequests()
    {
        using var fixture=new Fixture(manual:true);var calls=0;
        using var spotify=fixture.Create(_=>{calls++;var r=Json("{}",HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromSeconds(30));return Task.FromResult(r);});
        Assert.False((await spotify.Snapshot(default))["connected"]!.GetValue<bool>());
        Assert.False((await spotify.Act("favorite",null,default)).Ok);
        Assert.Equal(1,calls);
    }

    [Theory]
    [InlineData("spotify:track:1234567890123456789012",true)]
    [InlineData("https://example.com/",false)]
    [InlineData("spotify:playlist:../../token",false)]
    public void PlaybackTargetsAreSpotifyUrisOnly(string uri,bool valid)=>Assert.Equal(valid,SpotifyControl.PlaybackUri(uri));

    [Fact]
    public async Task ClientChangesInvalidateSnapshotReconnectClearsRateLimitAndEmptyRefreshPreservesToken()
    {
        using var fixture = new Fixture(manual: true);
        var rateLimit = false;
        var tokenCalls = 0;
        using var spotify = fixture.Create(req =>
        {
            if (req.RequestUri!.Host == "accounts.spotify.com")
            {
                tokenCalls++;
                return Task.FromResult(tokenCalls == 1
                    ? Json("""{"access_token":"a1","refresh_token":"keep-refresh","expires_in":30}""")
                    : Json("""{"access_token":"a2","refresh_token":"","expires_in":30}"""));
            }
            if (rateLimit)
            {
                var r429 = Json("{}", HttpStatusCode.TooManyRequests);
                r429.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
                return Task.FromResult(r429);
            }
            return Task.FromResult(req.RequestUri.AbsolutePath.EndsWith("/queue")
                ? Json("""{"queue":[]}""")
                : Json("""{"is_playing":true,"item":{"uri":"spotify:track:1111111111111111111111","name":"Song","artists":[]}}"""));
        });

        Assert.True((await spotify.Snapshot(default))["connected"]!.GetValue<bool>());

        // Changing clientId invalidates cached snapshot immediately
        File.WriteAllText(fixture.Config.Path, JsonSerializer.Serialize(new { controls = new { spotifyClientId = "other-client" } }));
        File.SetLastWriteTimeUtc(fixture.Config.Path, DateTime.UtcNow.AddMinutes(1));
        Assert.False((await spotify.Snapshot(default))["connected"]!.GetValue<bool>());

        // Rate limit lockout is cleared on PKCE Complete, and empty refresh_token="" preserves keep-refresh across refreshes
        File.WriteAllText(fixture.Config.Path, JsonSerializer.Serialize(new { controls = new { spotifyClientId = "pkce-client", spotifyToken = "temp-tok" } }));
        File.SetLastWriteTimeUtc(fixture.Config.Path, DateTime.UtcNow.AddMinutes(2));
        rateLimit = true;
        Assert.False((await spotify.Snapshot(default))["connected"]!.GetValue<bool>());
        rateLimit = false;
        await spotify.Disconnect(default);
        File.WriteAllText(fixture.Config.Path, JsonSerializer.Serialize(new { controls = new { spotifyClientId = "pkce-client" } }));
        File.SetLastWriteTimeUtc(fixture.Config.Path, DateTime.UtcNow.AddMinutes(3));
        var state = QueryHelpers.ParseQuery(new Uri(spotify.Begin()).Query)["state"].ToString();
        Assert.True(await spotify.Complete(state, "code", default));
        Assert.True((await spotify.Snapshot(default))["connected"]!.GetValue<bool>());
        Assert.Equal(3, tokenCalls); // 1 Complete + 2 refreshes (proving second refresh kept "keep-refresh")
    }
}
