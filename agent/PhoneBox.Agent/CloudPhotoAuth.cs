using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Manages OAuth 2.0 PKCE credentials for OneDrive, Google Drive, and Dropbox.
/// Tokens stay on this computer in config.json.photos.json (0600 on Unix) and are never sent to the phone.
/// </summary>
public sealed class CloudPhotoAuth : IDisposable
{
    public static readonly IReadOnlyList<string> OAuthProviders = ["onedrive", "google", "dropbox"];

    private static readonly JsonSerializerOptions StoreReadOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class TokenStoreFile
    {
        public Dictionary<string, ProviderToken> Providers { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed record ProviderToken(
        string ClientId,
        string AccessToken,
        string RefreshToken,
        long ExpiresAtMs,
        string? Account
    );

    private sealed record PendingAuth(
        string Provider,
        string State,
        string Verifier,
        string ClientId,
        string? ClientSecret,
        long ExpiresAtMs
    );

    private readonly ConfigStore _config;
    private readonly AgentEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingAuth> _pending = new(StringComparer.Ordinal);
    private TokenStoreFile? _store;

    /// <summary>Raised when a provider connects, reconnects, or disconnects so downstream caches can be invalidated.</summary>
    public event Action<string>? ProviderAuthChanged;

    public CloudPhotoAuth(ConfigStore config, AgentEndpoint endpoint)
        : this(config, endpoint, SafePhotoHttp.CreateClient(TimeSpan.FromSeconds(15)), ownsHttp: true) { }

    internal CloudPhotoAuth(ConfigStore config, AgentEndpoint endpoint, HttpClient http, bool ownsHttp = false)
    {
        _config = config;
        _endpoint = endpoint;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    private string CredentialPath => _config.Path + ".photos.json";
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsOAuthProvider(string? provider) =>
        provider is "onedrive" or "google" or "dropbox";

    public string RedirectUri(string provider) =>
        $"http://127.0.0.1:{_endpoint.Port}/api/photos/oauth/callback/{provider}";

    public string? GetClientId(string provider)
    {
        var cp = _config.Get().CloudPhotos;
        return provider switch
        {
            "onedrive" => cp.OneDriveClientId,
            "google" => cp.GoogleClientId,
            "dropbox" => cp.DropboxClientId,
            _ => null,
        };
    }

    private string? GetClientSecret(string provider) =>
        provider == "google" ? _config.Get().CloudPhotos.GoogleClientSecret : null;

    public (string Url, string RedirectUri) Begin(string provider)
    {
        if (!IsOAuthProvider(provider))
            throw new ArgumentException($"Unknown OAuth provider '{provider}'");

        var clientId = GetClientId(provider);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException($"Save a {ProviderDisplayName(provider)} Client ID in Settings › General first");

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = RedirectUri(provider);
        var secret = GetClientSecret(provider);

        lock (_gate)
        {
            _pending[provider] = new PendingAuth(provider, state, verifier, clientId, secret, NowMs + 600_000);
        }

        var url = provider switch
        {
            "onedrive" => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?" + BuildQuery(new()
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["response_mode"] = "query",
                ["scope"] = "Files.Read offline_access User.Read",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            }),
            "google" => "https://accounts.google.com/o/oauth2/v2/auth?" + BuildQuery(new()
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["scope"] = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/userinfo.email",
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            }),
            "dropbox" => "https://www.dropbox.com/oauth2/authorize?" + BuildQuery(new()
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["token_access_type"] = "offline",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            }),
            _ => throw new InvalidOperationException("Unsupported provider"),
        };

        return (url, redirectUri);
    }

    public bool HasPendingState(string? provider, string? state)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(state) || !IsOAuthProvider(provider))
            return false;

        lock (_gate)
        {
            if (!_pending.TryGetValue(provider, out var p) || p.ExpiresAtMs <= NowMs)
                return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(p.State));
        }
    }

    public void CancelPending(string provider, string? state)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(state) || !IsOAuthProvider(provider))
            return;

        lock (_gate)
        {
            if (_pending.TryGetValue(provider, out var p)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(p.State)))
            {
                _pending.Remove(provider);
            }
        }
    }

    public async Task<(bool Ok, string? Error)> CompleteAsync(string provider, string state, string code, CancellationToken ct)
    {
        if (!IsOAuthProvider(provider))
            return (false, "Unknown photo provider");

        PendingAuth? pending;
        lock (_gate)
        {
            if (!HasPendingState(provider, state) || code.Length is 0 or > 4096)
                return (false, "Sign-in expired or cancelled. Start again from desktop settings.");
            pending = _pending[provider];
            _pending.Remove(provider);
        }

        var currentClientId = GetClientId(provider);
        if (!string.Equals(pending.ClientId, currentClientId, StringComparison.Ordinal))
            return (false, "Client ID changed before sign-in finished. Try connecting again.");

        await _tokenGate.WaitAsync(ct);
        try
        {
            var fields = new Dictionary<string, string>
            {
                ["client_id"] = pending.ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri(provider),
                ["code_verifier"] = pending.Verifier,
            };
            if (provider == "google" && !string.IsNullOrWhiteSpace(pending.ClientSecret))
                fields["client_secret"] = pending.ClientSecret;

            var token = await ExchangeTokenAsync(provider, fields, pending.ClientId, oldRefresh: "", oldAccount: null, ct);
            var account = await TryFetchAccountAsync(provider, token.AccessToken, ct);
            token = token with { Account = account };

            var current = EnsureStoreLoaded();
            var nextProviders = new Dictionary<string, ProviderToken>(current.Providers, StringComparer.Ordinal)
            {
                [provider] = token,
            };
            SaveStore(new TokenStoreFile { Providers = nextProviders });
            ProviderAuthChanged?.Invoke(provider);
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TaskCanceledException)
        {
            return (false, ex is IOException ? ex.Message : $"{ProviderDisplayName(provider)} token exchange failed");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public async Task DisconnectAsync(string provider, CancellationToken ct)
    {
        if (!IsOAuthProvider(provider)) return;
        lock (_gate) _pending.Remove(provider);

        await _tokenGate.WaitAsync(ct);
        try
        {
            var current = EnsureStoreLoaded();
            if (current.Providers.ContainsKey(provider))
            {
                var nextProviders = new Dictionary<string, ProviderToken>(current.Providers, StringComparer.Ordinal);
                nextProviders.Remove(provider);
                SaveStore(new TokenStoreFile { Providers = nextProviders });
            }
            ProviderAuthChanged?.Invoke(provider);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public bool IsConnected(string provider) => GetTokenSummary(provider) is not null;

    public string? GetAccount(string provider) => GetTokenSummary(provider)?.Account;

    public string GetSessionStamp(string provider)
    {
        var tok = GetTokenSummary(provider);
        if (tok is null) return "none";
        var secretTail = string.IsNullOrEmpty(tok.RefreshToken) ? tok.AccessToken : tok.RefreshToken;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tok.ClientId}\0{secretTail}\0{tok.Account ?? ""}"));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private ProviderToken? GetTokenSummary(string provider)
    {
        if (!IsOAuthProvider(provider)) return null;
        var clientId = GetClientId(provider);
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var store = EnsureStoreLoaded();
        if (!store.Providers.TryGetValue(provider, out var tok)) return null;
        if (!string.Equals(tok.ClientId, clientId, StringComparison.Ordinal)) return null;
        if (string.IsNullOrEmpty(tok.AccessToken) && string.IsNullOrEmpty(tok.RefreshToken)) return null;
        return tok;
    }

    public async Task<string?> GetAccessTokenAsync(string provider, bool forceRefresh, CancellationToken ct)
    {
        if (!IsOAuthProvider(provider)) return null;

        await _tokenGate.WaitAsync(ct);
        try
        {
            var clientId = GetClientId(provider);
            if (string.IsNullOrWhiteSpace(clientId)) return null;

            var store = EnsureStoreLoaded();
            if (!store.Providers.TryGetValue(provider, out var tok)) return null;
            if (!string.Equals(tok.ClientId, clientId, StringComparison.Ordinal))
                return null;
            if (string.IsNullOrEmpty(tok.AccessToken) && string.IsNullOrEmpty(tok.RefreshToken))
                return null;

            if (!forceRefresh && tok.ExpiresAtMs > NowMs + 60_000 && !string.IsNullOrEmpty(tok.AccessToken))
                return tok.AccessToken;

            if (string.IsNullOrEmpty(tok.RefreshToken))
                return forceRefresh ? null : tok.AccessToken;

            var fields = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tok.RefreshToken,
            };
            var secret = GetClientSecret(provider);
            if (!string.IsNullOrWhiteSpace(secret))
                fields["client_secret"] = secret;

            var refreshed = await ExchangeTokenAsync(provider, fields, clientId, tok.RefreshToken, tok.Account, ct);
            var latest = EnsureStoreLoaded();
            var nextProviders = new Dictionary<string, ProviderToken>(latest.Providers, StringComparer.Ordinal)
            {
                [provider] = refreshed,
            };
            SaveStore(new TokenStoreFile { Providers = nextProviders });
            return refreshed.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<ProviderToken> ExchangeTokenAsync(
        string provider,
        Dictionary<string, string> fields,
        string clientId,
        string oldRefresh,
        string? oldAccount,
        CancellationToken ct)
    {
        var tokenUrl = provider switch
        {
            "onedrive" => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            "google" => "https://oauth2.googleapis.com/token",
            "dropbox" => "https://api.dropboxapi.com/oauth2/token",
            _ => throw new InvalidOperationException("Unsupported provider"),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await SafePhotoHttp.ReadBoundedStringAsync(resp.Content, SafePhotoHttp.MaxJsonBytes, ct);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"{ProviderDisplayName(provider)} authorization failed (HTTP {(int)resp.StatusCode}); reconnect from desktop settings");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(access))
            throw new IOException($"{ProviderDisplayName(provider)} returned no access token");

        var refresh = root.TryGetProperty("refresh_token", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
            ? rtEl.GetString() ?? oldRefresh
            : oldRefresh;
        var expiresSec = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var s)
            ? Math.Clamp(s, 30, 86400 * 30)
            : 3600;

        return new ProviderToken(clientId, access, refresh, NowMs + expiresSec * 1000L, oldAccount);
    }

    private async Task<string?> TryFetchAccountAsync(string provider, string accessToken, CancellationToken ct)
    {
        try
        {
            HttpRequestMessage req = provider switch
            {
                "onedrive" => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=displayName,userPrincipalName,mail"),
                "google" => new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo"),
                "dropbox" => new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account"),
                _ => throw new InvalidOperationException(),
            };
            using (req)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await SafePhotoHttp.ReadBoundedStringAsync(resp.Content, 262_144, ct);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                return provider switch
                {
                    "onedrive" => FirstString(r, "mail", "userPrincipalName", "displayName"),
                    "google" => FirstString(r, "email", "name"),
                    "dropbox" => r.TryGetProperty("email", out var em) ? em.GetString() : null,
                    _ => null,
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private TokenStoreFile EnsureStoreLoaded()
    {
        TokenStoreFile? existing;
        lock (_gate)
        {
            existing = _store;
        }
        if (existing is not null) return existing;

        var loaded = LoadStoreFromDisk();
        lock (_gate)
        {
            _store ??= loaded;
            return _store;
        }
    }

    private TokenStoreFile LoadStoreFromDisk()
    {
        if (!File.Exists(CredentialPath)) return new TokenStoreFile();
        try
        {
            var deserialized = JsonSerializer.Deserialize<TokenStoreFile>(File.ReadAllText(CredentialPath), StoreReadOpts);
            return deserialized is null ? new TokenStoreFile() : new TokenStoreFile
            {
                Providers = new Dictionary<string, ProviderToken>(deserialized.Providers, StringComparer.Ordinal),
            };
        }
        catch
        {
            return new TokenStoreFile();
        }
    }

    private void SaveStore(TokenStoreFile store)
    {
        var temp = CredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var dir = Path.GetDirectoryName(CredentialPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var opts = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var fs = new FileStream(temp, opts))
            {
                JsonSerializer.Serialize(fs, store, ConfigStore.WriteOpts);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, CredentialPath, overwrite: true);
            lock (_gate) _store = store;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string BuildQuery(Dictionary<string, string> items) =>
        string.Join('&', items.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    public static string ProviderDisplayName(string provider) => provider switch
    {
        "onedrive" => "OneDrive",
        "google" => "Google Drive",
        "dropbox" => "Dropbox",
        "icloud" => "iCloud",
        "nextcloud" => "Nextcloud",
        "local" => "Computer / Synced folder",
        _ => provider,
    };

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _tokenGate.Dispose();
    }
}
