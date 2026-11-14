using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Resolved station logo payload and whether it came from the monogram SVG fallback.</summary>
internal readonly record struct RadioLogo(byte[] Bytes, string Mime, bool IsFallback);

internal sealed class RadioLogoCache : IDisposable
{
    private sealed record Entry(byte[] Bytes, string Mime, long ExpiresAt, bool IsFallback, long Seq);

    private const int MaxImageBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int MaxMemoryEntries = 128;
    private const int MaxDiskEntries = 256;
    private const long DiskTtlMs = 14L * 24 * 3600 * 1000;
    private const long FallbackTtlMs = 5L * 60 * 1000;

    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, Entry> _memory = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<RadioLogo>>> _inflight = new();
    private long _memSeq;

    /// <summary>Creates a logo cache backed by the local directory and optional HTTP client.</summary>
    public RadioLogoCache(string? cacheDir = null, HttpClient? http = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(AppPaths.LocalDir, "radio-logos");
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = ConnectPublicEndpointAsync,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
            _ownsHttp = true;
        }
    }

    /// <summary>
    /// Resolves and caches a station logo or monogram SVG fallback for a radio control action.
    /// </summary>
    public async Task<RadioLogo> GetAsync(DeckAction action, bool allowOnlineLookup = false, CancellationToken ct = default)
    {
        var key = CacheKey(action, allowOnlineLookup);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_memory.TryGetValue(key, out var hit) && hit.ExpiresAt > now)
            return new(hit.Bytes, hit.Mime, hit.IsFallback);

        Lazy<Task<RadioLogo>>? created = null;
        var lazy = _inflight.GetOrAdd(key, _ =>
        {
            created = new Lazy<Task<RadioLogo>>(
                () => ResolveSharedAsync(key, action, allowOnlineLookup, created!),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return created;
        });
        return await lazy.Value.WaitAsync(ct);
    }

    /// <summary>Runs the deduplicated logo resolution with a hard 8-second timeout fallback.</summary>
    private async Task<RadioLogo> ResolveSharedAsync(
        string key, DeckAction action, bool allowOnlineLookup, Lazy<Task<RadioLogo>> self)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            return await ResolveAndCacheAsync(key, action, allowOnlineLookup, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fallback = BuildMonogramSvg(action.Label);
            StoreMemory(key, fallback, "image/svg+xml", now + FallbackTtlMs, isFallback: true, now);
            return new(fallback, "image/svg+xml", IsFallback: true);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<RadioLogo>>>(key, self));
        }
    }

    /// <summary>Checks the disk cache, fetches a logo, or falls back to a monogram SVG.</summary>
    private async Task<RadioLogo> ResolveAndCacheAsync(string key, DeckAction action, bool allowOnlineLookup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (TryReadDisk(key, now) is { } disk)
        {
            StoreMemory(key, disk.Bytes, disk.Mime, now + DiskTtlMs, isFallback: false, now);
            return new(disk.Bytes, disk.Mime, IsFallback: false);
        }

        if (await FetchLogoAsync(action, allowOnlineLookup, ct) is { } fetched)
        {
            WriteDisk(key, fetched.Bytes, fetched.Mime);
            StoreMemory(key, fetched.Bytes, fetched.Mime, now + DiskTtlMs, isFallback: false, now);
            return new(fetched.Bytes, fetched.Mime, IsFallback: false);
        }

        var fallback = BuildMonogramSvg(action.Label);
        StoreMemory(key, fallback, "image/svg+xml", now + FallbackTtlMs, isFallback: true, now);
        return new(fallback, "image/svg+xml", IsFallback: true);
    }

    /// <summary>Stores a resolved or fallback logo in the bounded in-memory cache.</summary>
    private void StoreMemory(string key, byte[] bytes, string mime, long expiresAt, bool isFallback, long now)
    {
        if (_memory.Count >= MaxMemoryEntries)
        {
            foreach (var (k, v) in _memory)
                if (v.ExpiresAt <= now) _memory.TryRemove(k, out _);
            if (!_memory.ContainsKey(key) && _memory.Count >= MaxMemoryEntries)
            {
                var toEvict = _memory.Count - MaxMemoryEntries + 1;
                foreach (var old in _memory.OrderBy(kv => kv.Value.ExpiresAt).ThenBy(kv => kv.Value.Seq).Take(toEvict))
                    _memory.TryRemove(old.Key, out _);
            }
        }
        _memory[key] = new Entry(bytes, mime, expiresAt, isFallback, Interlocked.Increment(ref _memSeq));
    }

    /// <summary>Fetches a custom icon URL first, then optional online radio directory logos.</summary>
    private async Task<(byte[] Bytes, string Mime)?> FetchLogoAsync(DeckAction action, bool allowOnlineLookup, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(action.Icon)
            && Uri.TryCreate(action.Icon.Trim(), UriKind.Absolute, out var customUri)
            && customUri.Scheme is "http" or "https"
            && await DownloadRasterAsync(customUri, ct) is { } customImg)
        {
            return customImg;
        }

        if (!allowOnlineLookup) return null;

        var searchName = CleanSearchName(action.Label);
        if (searchName.Length >= 2 && NormalizeKey(searchName).Length > 0)
        {
            if (await SearchRadioBrowserAsync(searchName, ct) is { } rbImg) return rbImg;
            if (await SearchTuneInAsync(searchName, ct) is { } tuneInImg) return tuneInImg;
        }

        return null;
    }

    /// <summary>Searches TuneIn OPML for a matching station image URL.</summary>
    private async Task<(byte[] Bytes, string Mime)?> SearchTuneInAsync(string searchName, CancellationToken ct)
    {
        try
        {
            var normTarget = NormalizeKey(searchName);
            if (normTarget.Length == 0) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var url = new Uri("https://opml.radiotime.com/Search.ashx?query=" + Uri.EscapeDataString(searchName) + "&types=station&render=json");
            using var resp = await SendPublicGetAsync(url, cts.Token);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<(int Score, string ImageUrl)>();
            foreach (var item in body.EnumerateArray())
            {
                if (!item.TryGetProperty("image", out var imgProp) || imgProp.ValueKind != JsonValueKind.String) continue;
                var img = imgProp.GetString();
                if (string.IsNullOrWhiteSpace(img) || img.Contains("s0q.png", StringComparison.OrdinalIgnoreCase) || img.Contains("logod.png", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String ? textProp.GetString() ?? "" : "";
                var normText = NormalizeKey(text);
                if (normText.Length == 0) continue;
                var score = normText == normTarget ? 3 : normText.StartsWith(normTarget, StringComparison.Ordinal) ? 2 : normText.Contains(normTarget, StringComparison.Ordinal) ? 1 : 0;
                if (score > 0) candidates.Add((score, img.Trim()));
            }

            foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(2))
            {
                if (Uri.TryCreate(candidate.ImageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    && await DownloadRasterAsync(uri, ct) is { } found)
                    return found;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    /// <summary>Searches RadioBrowser over HTTPS for a matching station favicon.</summary>
    private async Task<(byte[] Bytes, string Mime)?> SearchRadioBrowserAsync(string searchName, CancellationToken ct)
    {
        try
        {
            var normTarget = NormalizeKey(searchName);
            if (normTarget.Length == 0) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var url = new Uri("https://de1.api.radio-browser.info/json/stations/search?name=" + Uri.EscapeDataString(searchName)
                + "&order=clickcount&reverse=true&limit=6&hidebroken=true");
            using var resp = await SendPublicGetAsync(url, cts.Token);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() ?? "" : "";
                var normName = NormalizeKey(name);
                if (normName.Length == 0) continue;
                if (normName != normTarget
                    && !normName.Contains(normTarget, StringComparison.Ordinal)
                    && !normTarget.Contains(normName, StringComparison.Ordinal))
                    continue;
                if (!item.TryGetProperty("favicon", out var favProp) || favProp.ValueKind != JsonValueKind.String) continue;
                var fav = favProp.GetString();
                if (string.IsNullOrWhiteSpace(fav) || fav.Contains("tunein.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (Uri.TryCreate(fav.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    && await DownloadRasterAsync(uri, ct) is { } found)
                    return found;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    /// <summary>Downloads and sniffs a raster image from a validated public URI.</summary>
    private async Task<(byte[] Bytes, string Mime)?> DownloadRasterAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));
            using var resp = await SendPublicGetAsync(uri, cts.Token);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is > MaxImageBytes) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                if (ms.Length + read > MaxImageBytes) return null;
                ms.Write(buf, 0, read);
            }
            var bytes = ms.ToArray();
            if (bytes.Length < 32) return null;
            return DetectRasterMime(bytes) is { } mime ? (bytes, mime) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>Issues a GET with manual redirect handling, blocking local IPs and https-to-http downgrades.</summary>
    private async Task<HttpResponseMessage?> SendPublicGetAsync(Uri uri, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsSafePublicUri(current)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and <= 399 && resp.Headers.Location is { } location)
            {
                resp.Dispose();
                if (!Uri.TryCreate(current, location, out var next) || next.Scheme is not ("http" or "https"))
                    return null;
                if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    return null;
                if (!IsSafePublicUri(next))
                    return null;
                current = next;
                continue;
            }
            return resp;
        }
        return null;
    }

    /// <summary>
    /// Rejects loopback, private LAN, link-local, CGNAT, and non-HTTP(S) URIs to prevent SSRF.
    /// </summary>
    internal static bool IsSafePublicUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(host, out var ip))
            return IsPublicIpAddress(ip);
        return true;
    }

    /// <summary>
    /// Checks whether an IPv4 or IPv6 address is publicly routable (not loopback, private, link-local, or unspecified).
    /// </summary>
    internal static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127) return false;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false; // CGNAT / Tailscale
            if (b[0] == 169 && b[1] == 254) return false; // link-local
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false; // RFC 1918
            if (b[0] == 192 && b[1] == 168) return false; // RFC 1918
            if (b[0] >= 224) return false; // multicast / reserved
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
            if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false; // 64:ff9b::/96 NAT64
            return true;
        }
        return false;
    }

    /// <summary>Resolves DNS and connects only to publicly routable IP addresses.</summary>
    private static async ValueTask<Stream> ConnectPublicEndpointAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var parsed)
            ? [parsed]
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublicIpAddress).ToArray();
        if (allowed.Length == 0)
            throw new InvalidOperationException("Blocked connection to non-public address");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Identifies supported raster image formats from magic bytes.</summary>
    internal static string? DetectRasterMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 }))
            return "image/x-icon";
        return null;
    }

    /// <summary>Strips parenthetical suffixes from a station label before directory search.</summary>
    internal static string CleanSearchName(string label) =>
        Regex.Replace(label, @"\s*\([^)]*\)\s*", " ").Trim();

    /// <summary>Normalizes a station name into lowercase ASCII alphanumeric characters for matching.</summary>
    internal static string NormalizeKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var mapped = MapChar(ch);
            if (mapped is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(mapped);
        }
        return sb.ToString();
    }

    private static char MapChar(char ch) => ch switch
    {
        'İ' or 'I' or 'ı' or 'î' or 'Î' => 'i',
        'Ş' or 'ş' => 's',
        'Ğ' or 'ğ' => 'g',
        'Ü' or 'ü' or 'û' or 'Û' => 'u',
        'Ö' or 'ö' => 'o',
        'Ç' or 'ç' => 'c',
        'Â' or 'â' => 'a',
        _ => char.ToLowerInvariant(ch),
    };

    /// <summary>Generates a colored monogram SVG fallback for a radio station.</summary>
    internal static byte[] BuildMonogramSvg(string? label)
    {
        static string FirstRunes(string s, int count) => string.Concat(s.EnumerateRunes().Take(count).Select(r => r.ToString()));
        var clean = CleanSearchName(label ?? "FM");
        var words = clean.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        var initials = words.Length switch
        {
            0 => "FM",
            1 => FirstRunes(words[0], 2).ToUpperInvariant(),
            _ => (FirstRunes(words[0], 1) + FirstRunes(words[1], 1)).ToUpperInvariant(),
        };
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean));
        var hue = (hash[0] * 360) / 256;
        var hue2 = (hue + 38) % 360;
        var escaped = WebUtility.HtmlEncode(initials);
        var svg = string.Create(CultureInfo.InvariantCulture, $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 160 160">
              <defs>
                <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="hsl({hue},72%,36%)"/>
                  <stop offset="100%" stop-color="hsl({hue2},78%,18%)"/>
                </linearGradient>
              </defs>
              <rect width="160" height="160" rx="28" fill="url(#g)"/>
              <circle cx="80" cy="80" r="60" fill="none" stroke="rgba(255,255,255,0.14)" stroke-width="2"/>
              <text x="80" y="96" text-anchor="middle" font-family="system-ui,-apple-system,sans-serif" font-size="52" font-weight="800" letter-spacing="1" fill="#ffffff">{escaped}</text>
            </svg>
            """);
        return Encoding.UTF8.GetBytes(svg);
    }

    /// <summary>Computes the logo cache and revision key for a radio action.</summary>
    internal static string CacheKey(DeckAction action, bool allowOnlineLookup)
    {
        var raw = $"{action.Icon?.Trim()}\n{action.Label.Trim()}\n{action.Target.Trim()}\n{(allowOnlineLookup ? "1" : "0")}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }

    /// <summary>Reads a cached logo from disk if present, fresh, and valid raster data.</summary>
    private (byte[] Bytes, string Mime)? TryReadDisk(string key, long now)
    {
        try
        {
            var binPath = Path.Combine(_cacheDir, key + ".bin");
            var mimePath = Path.Combine(_cacheDir, key + ".mime");
            if (!File.Exists(binPath) || !File.Exists(mimePath)) return null;
            var ageMs = now - new DateTimeOffset(File.GetLastWriteTimeUtc(binPath)).ToUnixTimeMilliseconds();
            if (ageMs > DiskTtlMs)
            {
                try { File.Delete(binPath); File.Delete(mimePath); } catch { }
                return null;
            }
            var bytes = File.ReadAllBytes(binPath);
            var mime = File.ReadAllText(mimePath).Trim();
            if (bytes.Length < 32 || DetectRasterMime(bytes) != mime) return null;
            return (bytes, mime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Atomically writes a verified logo and its MIME type to the disk cache.</summary>
    private void WriteDisk(string key, byte[] bytes, string mime)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var tmp = Path.Combine(_cacheDir, key + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, Path.Combine(_cacheDir, key + ".bin"), overwrite: true);
            File.WriteAllText(Path.Combine(_cacheDir, key + ".mime"), mime);
            PruneDiskCache();
        }
        catch { }
    }

    /// <summary>Removes stale temp files, expired entries, and excess logos beyond the disk cap.</summary>
    private void PruneDiskCache()
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (!dir.Exists) return;
            var now = DateTime.UtcNow;

            foreach (var tmp in dir.EnumerateFiles("*.tmp"))
            {
                if ((now - tmp.LastWriteTimeUtc).TotalMinutes > 5)
                    try { tmp.Delete(); } catch { }
            }

            var bins = new List<FileInfo>();
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bin in dir.EnumerateFiles("*.bin"))
            {
                var ageMs = (now - bin.LastWriteTimeUtc).TotalMilliseconds;
                if (ageMs > DiskTtlMs)
                {
                    var stem = Path.GetFileNameWithoutExtension(bin.Name);
                    try { bin.Delete(); } catch { }
                    try { File.Delete(Path.Combine(_cacheDir, stem + ".mime")); } catch { }
                }
                else
                {
                    bins.Add(bin);
                    activeKeys.Add(Path.GetFileNameWithoutExtension(bin.Name));
                }
            }

            foreach (var mime in dir.EnumerateFiles("*.mime"))
            {
                var stem = Path.GetFileNameWithoutExtension(mime.Name);
                if (!activeKeys.Contains(stem))
                    try { mime.Delete(); } catch { }
            }

            if (bins.Count > MaxDiskEntries)
            {
                foreach (var old in bins.OrderBy(f => f.LastWriteTimeUtc).Take(bins.Count - MaxDiskEntries))
                {
                    var stem = Path.GetFileNameWithoutExtension(old.Name);
                    try { old.Delete(); } catch { }
                    try { File.Delete(Path.Combine(_cacheDir, stem + ".mime")); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>Disposes the internal HTTP client if owned by this instance.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
