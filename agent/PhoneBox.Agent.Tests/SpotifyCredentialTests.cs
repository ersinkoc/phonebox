using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using PhoneBox.Agent;
using Xunit;

/// <summary>A Spotify snapshot belongs to one account: settings changes, connects and disconnects never mix two.</summary>
public sealed class SpotifyCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-spotify-cred-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;

    public SpotifyCredentialTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        _store = new ConfigStore(path);
    }

    private void Configure(string client, string? manual)
    {
        var error = _store.Save(JsonSerializer.Serialize(new { controls = new { spotifyClientId = client, spotifyToken = manual } }));
        if (error is not null) throw new Exception(error);
    }

    private SpotifyControl Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(_store, new AgentEndpoint(19886), new HttpClient(new Handler(respond)));

    private static string Bearer(HttpRequestMessage r) => r.Headers.Authorization?.Parameter ?? "";
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Playing(string name) =>
        Json($$$"""{"is_playing":true,"item":{"uri":"spotify:track:1234567890123456789012","name":"{{{name}}}","artists":[]}}""");

    [Fact]
    public async Task ARotatedManualTokenReplacesTheOldOneWhenAnotherClientsCredentialFileExists()
    {
        Configure("client-b", "manual-1");
        File.WriteAllText(_store.Path + ".spotify.json",
            JsonSerializer.Serialize(new { ClientId = "client-a", Access = "file-a", Refresh = "refresh-a", Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }));
        var bearers = new List<string>();
        using var spotify = Create(r =>
        {
            lock (bearers) bearers.Add(Bearer(r));
            return Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/queue") ? Json("""{"queue":[]}""") : Playing("Song"));
        });
        Assert.True((await spotify.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
        Assert.All(bearers, b => Assert.Equal("manual-1", b));
        lock (bearers) bearers.Clear();

        Configure("client-b", "manual-2");
        Assert.True((await spotify.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
        Assert.NotEmpty(bearers);
        Assert.All(bearers, b => Assert.Equal("manual-2", b));
    }

    [Fact]
    public async Task ASettingsChangeDuringASnapshotDoesNotMixTwoAccounts()
    {
        Configure("client", "manual-1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var spotify = Create(async r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/queue")) return Json("""{"queue":[]}""");
            if (Bearer(r) != "manual-1") return Playing("New account");
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Playing("Old account");
        });
        var snapshot = spotify.Snapshot(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Configure("client", "manual-2");
        }
        finally { release.TrySetResult(); }
        var snap = await snapshot.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("New account", snap["current"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADisconnectDuringASnapshotIsNotUndoneByItsAnswer()
    {
        Configure("client", "manual-1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var spotify = Create(async r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/queue")) return Playing("Song");
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Json("""{"queue":[]}""");
        });
        var snapshot = spotify.Snapshot(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await spotify.Disconnect(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        Assert.False((await snapshot.WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
        Assert.False((await spotify.Snapshot(default).WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ARateLimitAnsweredToThePreviousAccountDoesNotBlockTheNewConnection()
    {
        Configure("client", "manual-1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var spotify = Create(async r =>
        {
            if (r.RequestUri!.Host == "accounts.spotify.com")
                return Json("""{"access_token":"fresh","refresh_token":"fresh-refresh","expires_in":3600}""");
            if (Bearer(r) == "manual-1")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var limited = Json("{}", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
                return limited;
            }
            if (r.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return r.RequestUri.AbsolutePath.EndsWith("/queue") ? Json("""{"queue":[]}""") : Playing("Song");
        });
        var snapshot = spotify.Snapshot(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var state = QueryHelpers.ParseQuery(new Uri(spotify.Begin()).Query)["state"].ToString();
            Assert.True(await spotify.Complete(state, "code", default).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.TrySetResult(); }
        await snapshot.WaitAsync(TimeSpan.FromSeconds(10));
        var favorite = await spotify.Act("favorite", null, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(favorite.Ok, favorite.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
