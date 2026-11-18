using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>Spotify PKCE and refresh credentials remain on this computer. There is no browser login during polling.</summary>
public sealed class SpotifyControl : IDisposable
{
    private sealed record Token(string ClientId, string Access, string Refresh, long Expires);
    private sealed record Pending(string State, string Verifier, string ClientId, long Expires);
    private readonly ConfigStore _config;
    private readonly AgentEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1), _readGate = new(1, 1);
    private readonly Lock _gate = new();
    /// <summary>A published snapshot with the settings and credential generation it was read with.</summary>
    private sealed record SnapshotEntry(JsonObject Value, (string ClientId, string ManualToken) Auth, long Generation, long ReadAt);
    private Pending? _pending;
    private Token? _token;
    private bool _tokenIsManual; // _token came from the manual token in settings, not from the credential file
    private volatile SnapshotEntry? _snapshot;
    // Advanced (under _gate) when the account changes on this computer: connect or disconnect. Answers to requests
    // sent with an older generation must not be published or restore a rate limit. Settings changes (client id,
    // manual token) are caught by comparing CurrentAuth() instead.
    private long _generation;
    private long _retryAt;
    private (string ClientId, string ManualToken) CurrentAuth() { var c = _config.Get().Controls; return (c.SpotifyClientId ?? "", c.SpotifyToken ?? ""); }
    private string CredentialPath => _config.Path + ".spotify.json";
    public string RedirectUri => $"http://127.0.0.1:{_endpoint.Port}/api/controls/spotify/callback";

    public SpotifyControl(ConfigStore config, AgentEndpoint endpoint) : this(config, endpoint,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) }) { }
    internal SpotifyControl(ConfigStore config, AgentEndpoint endpoint, HttpClient http) { _config = config; _endpoint = endpoint; _http = http; }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public string Begin()
    {
        var client = _config.Get().Controls.SpotifyClientId;
        if (string.IsNullOrWhiteSpace(client) || client.Length > 128) throw new InvalidOperationException("Save a Spotify Client ID in desktop settings first");
        var pending = new Pending(Base64(RandomNumberGenerator.GetBytes(32)), Base64(RandomNumberGenerator.GetBytes(64)), client, Now + 600_000);
        lock (_gate) _pending = pending;
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client, ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["state"] = pending.State,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(pending.Verifier))),
            ["scope"] = "user-read-playback-state user-read-currently-playing user-modify-playback-state user-library-modify",
        };
        return "https://accounts.spotify.com/authorize?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }
    public bool HasPendingState(string? state)
    {
        lock (_gate) return !string.IsNullOrEmpty(state) && _pending is { } pending && pending.Expires > Now
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(pending.State));
    }
    public async Task<bool> Complete(string state, string code, CancellationToken ct)
    {
        Pending pending;
        lock (_gate) { if (!HasPendingState(state) || code.Length is 0 or > 4096) return false; pending = _pending!; _pending = null; }
        if (pending.ClientId != _config.Get().Controls.SpotifyClientId) return false;
        await _tokenGate.WaitAsync(ct);
        try
        {
            var token = await TokenRequest(new() { ["client_id"] = pending.ClientId, ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = pending.Verifier }, pending.ClientId, "", ct);
            Save(token); NewGeneration(); return true;
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>The account changed: drop the snapshot and the rate limit that belong to the previous one.</summary>
    private void NewGeneration()
    {
        lock (_gate) { Interlocked.Increment(ref _generation); _snapshot = null; Interlocked.Exchange(ref _retryAt, 0); }
    }
    private async Task<Token> TokenRequest(Dictionary<string, string> fields, string client, string oldRefresh, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token") { Content = new FormUrlEncodedContent(fields) };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new IOException("Spotify authorization failed; reconnect from desktop settings");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access)) throw new IOException("Spotify returned no access token");
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } nextRefresh ? nextRefresh : oldRefresh;
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var value) ? Math.Clamp(value, 30, 86400) : 3600;
        return new(client, access, refresh, Now + seconds * 1000L);
    }
    private void Save(Token token)
    {
        var temporary = CredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialPath)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var file = new FileStream(temporary, options)) { JsonSerializer.Serialize(file, token); file.Flush(flushToDisk: true); }
            File.Move(temporary, CredentialPath, overwrite: true); _token = token; _tokenIsManual = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task Disconnect(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try { lock (_gate) _pending = null; Save(new(_config.Get().Controls.SpotifyClientId ?? "", "", "", 0)); NewGeneration(); }
        finally { _tokenGate.Release(); }
    }
    private async Task<string?> Access(bool force, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            var controls = _config.Get().Controls;
            var client = controls.SpotifyClientId ?? "";
            var manual = controls.SpotifyToken ?? "";
            if (_token is null && File.Exists(CredentialPath))
                try { _token = JsonSerializer.Deserialize<Token>(File.ReadAllText(CredentialPath)); _tokenIsManual = false; } catch (JsonException) { }
            if (_token is { } wrong && wrong.ClientId != client) DropToken();
            // A token taken from the manual setting follows it, whether or not a credential file exists (a file
            // for another client is set aside above but not deleted). A persisted disconnect is not a manual token.
            if (_tokenIsManual && _token is { } cur && cur.Access != manual) DropToken();
            if (_token is null && manual.Length > 0)
            {
                _token = new(client, manual, controls.SpotifyRefreshToken ?? "", 0);
                _tokenIsManual = true;
            }
            if (_token is null) return null;
            if (string.IsNullOrEmpty(_token.Access) && string.IsNullOrEmpty(_token.Refresh)) return null;
            if (!force && (_token.Expires == 0 || _token.Expires - Now > 60_000)) return _token.Access;
            if (_token.Refresh.Length == 0 || client.Length == 0) return null;
            var refreshed = await TokenRequest(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = _token.Refresh,
                ["client_id"] = client }, client, _token.Refresh, ct);
            Save(refreshed); return refreshed.Access;
        }
        finally { _tokenGate.Release(); }
    }
    /// <summary>
    /// Forgets the token in memory (called with the token gate held). Only a settings change gets here, and a
    /// snapshot compares the settings it was read with, so the generation stays.
    /// </summary>
    private void DropToken() { _token = null; _tokenIsManual = false; }
    private async Task<JsonNode?> Request(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (Interlocked.Read(ref _retryAt) > Now) throw new IOException("Spotify is rate limited; try again later");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await Access(attempt > 0, ct) ?? throw new IOException("Connect Spotify in desktop settings");
            var generation = Interlocked.Read(ref _generation);
            using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if ((int)response.StatusCode == 429)
            {
                // a limit on the previous account must not block the one connected since this request was sent
                var until = Now + (long)Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? 30000, 1000, 300000);
                lock (_gate) if (Interlocked.Read(ref _generation) == generation) Interlocked.Exchange(ref _retryAt, until);
                throw new IOException("Spotify is rate limited; try again later");
            }
            if (!response.IsSuccessStatusCode) throw new IOException($"Spotify returned HTTP {(int)response.StatusCode}; check account permissions and the active player");
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        throw new IOException("Reconnect Spotify in desktop settings");
    }
    /// <summary>The published snapshot if it is fresh and was read with these settings and the current account.</summary>
    private JsonObject? Cached((string ClientId, string ManualToken) auth)
    {
        // one reference for the check and the value, so a concurrent publish cannot pair one account's data with another's key
        var entry = _snapshot;
        return entry is not null && entry.Auth == auth && entry.Generation == Interlocked.Read(ref _generation) && Now - entry.ReadAt < 5000
            ? entry.Value : null;
    }
    public async Task<JsonObject> Snapshot(CancellationToken ct)
    {
        if (Cached(CurrentAuth()) is { } fast) return fast;
        await _readGate.WaitAsync(ct);
        try
        {
            // Both requests must describe one account. A connect, disconnect or settings change while they run
            // discards the result; read once more, then report the change instead of mixing the two.
            for (var attempt = 0; ; attempt++)
            {
                var auth = CurrentAuth();
                if (Cached(auth) is { } cached) return cached;
                var generation = Interlocked.Read(ref _generation);
                JsonObject value;
                try
                {
                    // episodes too: without additional_types an episode that is playing comes back empty
                    var playing = await Request(HttpMethod.Get, "me/player/currently-playing?additional_types=track,episode", null, ct);
                    var queue = await Request(HttpMethod.Get, "me/player/queue?additional_types=track,episode", null, ct);
                    value = new() { ["connected"] = true, ["current"] = TrackOf(playing?["item"]), ["playing"] = playing?["is_playing"]?.DeepClone(),
                        ["queue"] = new JsonArray((queue?["queue"] as JsonArray ?? []).Take(30).Select(TrackOf).ToArray()) };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
                { value = new() { ["connected"] = false, ["error"] = ex is IOException ? ex.Message : "Spotify is unavailable" }; }
                var unchanged = CurrentAuth() == auth;
                lock (_gate)
                {
                    if (unchanged && Interlocked.Read(ref _generation) == generation)
                    {
                        _snapshot = new(value, auth, generation, Now);
                        return value;
                    }
                }
                if (attempt > 0) return new() { ["connected"] = false, ["error"] = "The Spotify account changed; try again" };
            }
        }
        finally { _readGate.Release(); }
    }
    /// <summary>
    /// What the phone gets for a track or an episode: its URI (to play it by id), name, artist or show, and a small cover
    /// from Spotify's own image server, the smallest one at least 64 px wide. Nothing else from Spotify's answer is passed on.
    /// </summary>
    internal static JsonObject? TrackOf(JsonNode? item)
    {
        if (item is null) return null;
        var artist = item["artists"] is JsonArray artists
            ? string.Join(", ", artists.Select(a => a?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0))
            : item["show"]?["name"]?.GetValue<string>() ?? "";
        var images = (item["album"]?["images"] ?? item["images"]) as JsonArray ?? [];
        var cover = images
            .Select(i => (Url: i?["url"]?.GetValue<string>(), Width: i?["width"]?.GetValue<int?>() ?? 0))
            .Where(i => CoverUrl(i.Url))
            .OrderBy(i => i.Width >= 64 ? i.Width : int.MaxValue).Select(i => i.Url).FirstOrDefault();
        return new JsonObject { ["id"] = item["uri"]?.DeepClone(), ["name"] = item["name"]?.DeepClone(), ["artist"] = artist, ["image"] = cover };
    }

    /// <summary>Only covers on Spotify's image server reach the phone, which loads them itself.</summary>
    internal static bool CoverUrl(string? url) => url is not null && url.Length <= 200
        && System.Text.RegularExpressions.Regex.IsMatch(url, @"\Ahttps://i\.scdn\.co/image/[A-Za-z0-9]+\z");

    internal static bool PlaybackUri(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,
        @"\Aspotify:(?:track|episode|playlist|album):[A-Za-z0-9]{22}\z");
    public async Task<DeckResult> Act(string action, string? id, CancellationToken ct)
    {
        try
        {
            if (action == "favorite")
            {
                var state = await Snapshot(ct); var uri = state["current"]?["id"]?.GetValue<string>();
                if (uri is null || !PlaybackUri(uri)) return new(false, "No Spotify item is playing");
                await Request(HttpMethod.Put, "me/library?uris=" + Uri.EscapeDataString(uri), null, ct);
            }
            else if (action == "play-queued")
            {
                var state = await Snapshot(ct);
                if (id is null || !PlaybackUri(id) || (state["queue"] as JsonArray)?.Any(t => t?["id"]?.GetValue<string>() == id) != true) return new(false, "That queue item is no longer available");
                await Request(HttpMethod.Put, "me/player/play" + DeviceQuery(), new JsonObject { ["uris"] = new JsonArray(id) }, ct);
            }
            else if (action == "playlist")
            {
                var configured = _config.Get().Controls.Actions.FirstOrDefault(a => a.Id == id && a.Kind == "playlist");
                if (configured is null) return new(false, "Configure a Spotify playlist URI on the computer");
                if (!configured.Enabled) return new(false, "Control is disabled");
                if (!PlaybackUri(configured.Target)) return new(false, "Configure a Spotify playlist URI on the computer");
                var body = configured.Target.StartsWith("spotify:track:", StringComparison.Ordinal) || configured.Target.StartsWith("spotify:episode:", StringComparison.Ordinal)
                    ? new JsonObject { ["uris"] = new JsonArray(configured.Target) }
                    : new JsonObject { ["context_uri"] = configured.Target };
                await Request(HttpMethod.Put, "me/player/play" + DeviceQuery(), body, ct);
            }
            else return new(false, "Unknown Spotify control");
            _snapshot = null; return new(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        { return new(false, ex is IOException ? ex.Message : "Spotify is unavailable"); }
    }
    private string DeviceQuery() => _config.Get().Controls.SpotifyDevice is { Length: > 0 } id ? "?device_id=" + Uri.EscapeDataString(id) : "";
    public void Dispose() { _http.Dispose(); _tokenGate.Dispose(); _readGate.Dispose(); }
}
