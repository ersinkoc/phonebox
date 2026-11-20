using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record ImmichAlbum(string Id, string Name, int Count);

/// <summary>
/// The few Immich REST calls the photo frame needs, with an API key (Settings › General › Photo frame). The server
/// address and the key stay on this computer: errors name neither, so they are safe to show on the phone.
/// </summary>
internal sealed class ImmichClient(HttpClient http, Uri server, string key)
{
    internal const int MaxAlbums = 500;
    private const long MaxJsonBytes = 16L * 1024 * 1024, MaxImageBytes = 12L * 1024 * 1024;
    private const int PageSize = 1000, MaxPages = 10, MaxForbiddenBytes = 4096;

    internal sealed record Asset(string Id, long Ticks);

    internal const string BadKey = "The Immich API key has characters that are not allowed";
    internal const string Unreachable = "The Immich server can't be reached", TimedOut = "The Immich server didn't answer in time";

    /// <summary>A failure that says nothing about the settings: the server is down, slow or erroring, so a list already shown stays.</summary>
    internal static bool IsTransient(string? error) =>
        error is Unreachable or TimedOut || error?.StartsWith("The Immich server answered 5", StringComparison.Ordinal) == true;

    /// <summary>How long one request may take, from sending it to the last byte of its body (HttpClient.Timeout ends at the headers).</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The server address as a base for relative calls; null unless it is a plain http(s) address (no login, query or fragment).</summary>
    internal static Uri? ParseServer(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u)) return null;
        return u.Scheme is "http" or "https" && u.Host.Length > 0 && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 ? u : null;
    }

    internal static string KeyTag(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    /// <summary>The user's albums by name, or the reason they can't be listed.</summary>
    internal async Task<(IReadOnlyList<ImmichAlbum>? Albums, string? Error)> ListAlbums(CancellationToken ct)
    {
        var (body, error) = await Send(HttpMethod.Get, "api/albums", null, MaxJsonBytes, ct);
        if (body is null) return (null, error);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, "That doesn't look like an Immich server");
            var albums = new List<ImmichAlbum>();
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Str(a, "id") is not { } id || !Guid.TryParse(id, out _)) continue;
                albums.Add(new(id, Str(a, "albumName") ?? "", a.TryGetProperty("assetCount", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) ? count : 0));
                if (albums.Count >= MaxAlbums) break;
            }
            albums.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            return (albums, null);
        }
        catch (JsonException) { return (null, "That doesn't look like an Immich server"); }
    }

    /// <summary>
    /// The still images of an album (videos and trashed items are left out), or the reason it can't be read. Asked as an
    /// asset search limited to the album, a page at a time: newer Immich servers no longer list assets with the album itself.
    /// </summary>
    internal async Task<(IReadOnlyList<Asset>? Assets, string? Error)> AlbumAssets(string album, CancellationToken ct)
    {
        if (!Guid.TryParse(album, out _)) return (null, "Choose an album in Settings › General");
        var assets = new List<Asset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxPages && assets.Count < PhotoFrame.MaxFiles; page++)
        {
            ct.ThrowIfCancellationRequested(); // no more pages once the scan is not wanted
            var request = JsonSerializer.Serialize(new { albumIds = new[] { album }, type = "IMAGE", size = PageSize, page });
            var (body, error) = await Send(HttpMethod.Post, "api/search/metadata", request, MaxJsonBytes, ct);
            // a 400 or 404 may be a deleted album, or a server that doesn't take the album filter: don't say which
            if (body is null) return (null, error == "Immich couldn't find that" ? "Immich couldn't read that album" : error);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("assets", out var found)
                    || found.ValueKind != JsonValueKind.Object || !found.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
                    return (null, "That doesn't look like an Immich server");
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || Str(a, "type") != "IMAGE" || Str(a, "id") is not { } id || !Guid.TryParse(id, out _) || !seen.Add(id)) continue;
                    if (a.TryGetProperty("isTrashed", out var trashed) && trashed.ValueKind == JsonValueKind.True) continue;
                    var ticks = Str(a, "updatedAt") is { } t && DateTimeOffset.TryParse(t, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.UtcTicks : 0;
                    assets.Add(new(id, ticks));
                    if (assets.Count >= PhotoFrame.MaxFiles) break;
                }
                if (Str(found, "nextPage") is not { Length: > 0 }) break;
            }
            catch (JsonException) { return (null, "That doesn't look like an Immich server"); }
        }
        return (assets, null);
    }

    /// <summary>The preview-size picture of an asset (what Immich's own web app shows), or the reason it can't be fetched.</summary>
    internal async Task<(byte[]? Bytes, string? Error)> Preview(string asset, CancellationToken ct) =>
        Guid.TryParse(asset, out _) ? await Send(HttpMethod.Get, "api/assets/" + asset + "/thumbnail?size=preview", null, MaxImageBytes, ct) : (null, "Unknown photo");

    // TryGetProperty throws on anything but an object, so a body that is an array or a bare value has no such property
    private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A key with too few permissions: Immich says which one ("Missing required permission: asset.view"), so the user can fix the key.</summary>
    private static async Task<string> Forbidden(HttpResponseMessage res, CancellationToken ct)
    {
        const string generic = "The Immich server rejected the API key";
        // a message is a few dozen bytes: a bigger body is never read, whatever it says about itself
        if (res.Content.Headers.ContentLength > MaxForbiddenBytes) return generic;
        try
        {
            if (await ReadCapped(res.Content, MaxForbiddenBytes, ct) is not { } text) return generic;
            using var doc = JsonDocument.Parse(text);
            const string prefix = "Missing required permission: ";
            if (Str(doc.RootElement, "message") is { } m && m.StartsWith(prefix, StringComparison.Ordinal) && m[prefix.Length..] is { Length: > 0 and <= 40 } perm
                && perm.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_'))
                return $"The Immich API key is missing the {perm} permission";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException) { }
        return generic;
    }

    private async Task<(byte[]? Body, string? Error)> Send(HttpMethod method, string path, string? json, long max, CancellationToken ct)
    {
        // a header value can't hold a control character or anything outside ASCII: refuse here, instead of throwing from Headers.Add
        if (key.Any(c => char.IsControl(c) || c > '~')) return (null, BadKey);
        // one limit for the whole exchange: a server that sends its headers and then stalls must not hold a scan for good
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        try
        {
            using var req = new HttpRequestMessage(method, new Uri(server, path));
            req.Headers.Add("x-api-key", key);
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (res.StatusCode == HttpStatusCode.Unauthorized) return (null, "The Immich server rejected the API key");
            if (res.StatusCode == HttpStatusCode.Forbidden) return (null, await Forbidden(res, limit.Token));
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return (null, "Immich couldn't find that");
            // a redirect is never followed: it could carry the key to another address
            if (!res.IsSuccessStatusCode) return (null, $"The Immich server answered {(int)res.StatusCode}");
            if (res.Content.Headers.ContentLength > max) return (null, "The Immich answer is too large");
            return await ReadCapped(res.Content, max, limit.Token) is { } body ? (body, null) : (null, "The Immich answer is too large");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, TimedOut); }
        catch (FormatException) { return (null, BadKey); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return (null, Unreachable);
        }
    }

    /// <summary>The body, or null when it is longer than <paramref name="max"/> bytes (reading stops there).</summary>
    private static async Task<byte[]?> ReadCapped(HttpContent content, long max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[(int)Math.Min(81920, max + 1)]; // never asks for more than one byte past the cap
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}

public sealed partial class PhotoFrame
{
    /// <summary>Test seam: the handler Immich requests go through. Redirects are never followed in production.</summary>
    internal HttpMessageHandler? ImmichHandler { get; init; }
    /// <summary>Test seam: how long one Immich request, its body included, may take.</summary>
    internal TimeSpan ImmichTimeout { get; init; } = TimeSpan.FromSeconds(30);
    private HttpClient? _immichHttp;

    private HttpClient ImmichHttp
    {
        get
        {
            lock (_gate)
                return _immichHttp ??= new HttpClient(ImmichHandler ?? NewImmichHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        }
    }

    /// <summary>The handler for a real server: a redirect is never followed (it could carry the key elsewhere), and a pooled connection is dropped after five minutes so a changed DNS answer is picked up.</summary>
    internal static SocketsHttpHandler NewImmichHandler() => new() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>For the settings page's album picker: lists the albums of a server. The key is the saved one when the address is the saved one and none is given.</summary>
    internal async Task<(IReadOnlyList<ImmichAlbum>? Albums, string? Error)> ImmichAlbums(string? url, string? key, CancellationToken ct)
    {
        var saved = config.Get().Photos;
        var server = ImmichClient.ParseServer(url);
        if (server is null) return (null, "Enter the address of your Immich server, like http://192.168.1.10:2283");
        var useKey = string.IsNullOrWhiteSpace(key) && server == ImmichClient.ParseServer(saved.ImmichUrl) ? saved.ImmichKey : (key ?? "").Trim();
        if (useKey.Length == 0) return (null, "Enter an API key");
        return await new ImmichClient(ImmichHttp, server, useKey) { RequestTimeout = ImmichTimeout }.ListAlbums(ct);
    }

    /// <summary>Photos from one Immich album, as the preview-size pictures Immich makes itself.</summary>
    private sealed class ImmichSource(PhotoFrame owner, Uri server, string key, string album) : IPhotoSource
    {
        public string Key { get; } = "immich\n" + server + "\n" + album + "\n" + ImmichClient.KeyTag(key);
        public string ScanError => "The Immich album can't be read";
        private ImmichClient Client => new(owner.ImmichHttp, server, key) { RequestTimeout = owner.ImmichTimeout };

        public Library Scan(CancellationToken ct)
        {
            var (assets, error) = Client.AlbumAssets(album, ct).GetAwaiter().GetResult();
            if (assets is null) return new(Key, [], 0, error, ImmichClient.IsTransient(error));
            // Path holds the asset id: the phone only ever sees the keyed id
            var photos = assets.Select(a => new Photo(IdOf("immich:" + album, a.Id), a.Id, 0, a.Ticks)).ToList();
            return new(Key, photos, 0, photos.Count == 0 ? "No photos in the album" : null);
        }

        public async Task<(byte[]? Bytes, string? Type, string? Error)> Fetch(Photo photo, CancellationToken ct)
        {
            var (bytes, error) = await Client.Preview(photo.Path, ct);
            // the messages that name no address or key (a missing permission, a rejected key) help the user fix the settings
            if (bytes is null) return (null, null, error is "Unknown photo" || error?.StartsWith("The Immich ", StringComparison.Ordinal) == true && error.Contains("API key") ? error : "That photo can't be shown");
            if (MimeOf(bytes) is not { } mime) return (null, null, "That photo can't be shown");
            // the size limits a folder photo is held to (the server may be set to send larger previews); nothing is scaled here.
            // The whole body is in memory, so the header is found wherever the metadata ends; a picture whose size can't be
            // read is refused, as a folder photo of unknown size is never sent as it is.
            if (SizeOf(bytes, mime) is not { } d) return (null, null, "That photo can't be shown");
            if (d.W <= 0 || d.H <= 0 || d.W > 8192 || d.H > 8192 || (long)d.W * d.H > MaxPhonePixels)
                return (null, null, "That photo is too large to show");
            return (bytes, mime, null);
        }
    }
}
