using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Summary metadata for a country in the radio-browser.info directory.</summary>
public sealed record RadioCountryInfo(string Code, string Name, int StationCount);

/// <summary>Normalized radio station candidate returned from radio-browser.info search results.</summary>
public sealed record RadioStationCandidate(string Id, string Label, string StreamUrl, string? LogoUrl, int ClickCount, int Votes);

/// <summary>Request payload containing configured radio stations to probe for stream health.</summary>
public sealed record RadioCheckRequest(IReadOnlyList<RadioCheckItem> Stations);

/// <summary>Single radio station item submitted to the health-check endpoint.</summary>
public sealed record RadioCheckItem(string Id, string StreamUrl);

/// <summary>Result of probing a configured radio station stream URL.</summary>
public sealed record RadioCheckResult(string Id, bool Ok, string Status);

/// <summary>Raised when all radio-browser.info API mirrors are unreachable or fail.</summary>
internal sealed class RadioDirectoryUnavailableException(string message) : Exception(message);

internal static partial class RadioBrowserDirectory
{
    private static readonly string[] FallbackMirrors =
    [
        "de1.api.radio-browser.info",
        "fi1.api.radio-browser.info",
        "nl1.api.radio-browser.info",
        "at1.api.radio-browser.info",
        "de2.api.radio-browser.info",
    ];
    private static volatile string? _preferredMirror;
    private const int MaxRedirects = 5;
    private const int MaxDirectoryBytes = 4 * 1024 * 1024;

    private sealed class NonPublicAddressException() : Exception("local address, not checked");

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectPublicEndpointAsync,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static async Task<IReadOnlyList<string>> CandidateMirrorsAsync(bool discover, CancellationToken ct)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddHost(string? host)
        {
            if (!string.IsNullOrWhiteSpace(host) && seen.Add(host.Trim()))
                list.Add(host.Trim());
        }

        AddHost(_preferredMirror);
        if (discover)
        {
            try
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dnsCts.CancelAfter(TimeSpan.FromSeconds(2));
                var ips = await Dns.GetHostAddressesAsync("all.api.radio-browser.info", dnsCts.Token);
                foreach (var ip in ips.Where(IsPublicIpAddress).Take(4))
                {
                    try
                    {
                        var entry = await Dns.GetHostEntryAsync(ip.ToString(), dnsCts.Token);
                        if (entry.HostName.EndsWith(".api.radio-browser.info", StringComparison.OrdinalIgnoreCase))
                            AddHost(entry.HostName);
                    }
                    catch { /* ignore individual PTR lookup failure */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* fall back to static mirror list */ }
        }
        foreach (var host in FallbackMirrors) AddHost(host);
        return list;
    }

    internal static async Task<string> GetDirectoryJsonAsync(string relativePathAndQuery, CancellationToken ct, HttpClient? client = null)
    {
        using var owned = client is null ? CreateClient() : null;
        var http = client ?? owned!;
        var hosts = await CandidateMirrorsAsync(discover: client is null, ct);
        foreach (var host in hosts)
        {
            var uri = new Uri($"https://{host}/json/{relativePathAndQuery}");
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(5));
                using var res = await SendPublicGetAsync(http, uri, attemptCts.Token);
                if (res is null || !res.IsSuccessStatusCode) continue;
                var body = await ReadBoundedStringAsync(res, MaxDirectoryBytes, attemptCts.Token);
                if (body is null || !IsJsonArray(body)) continue;
                if (client is null) _preferredMirror = host;
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next mirror when the current host fails or times out.
            }
        }
        throw new RadioDirectoryUnavailableException("Could not reach radio-browser.info mirrors. Check your internet connection or try again shortly.");
    }

    private static async Task<string?> ReadBoundedStringAsync(HttpResponseMessage res, int maxBytes, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength is { } cl && cl > maxBytes) return null;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + read > maxBytes) return null;
            ms.Write(buf, 0, read);
        }
        // ReadAsStringAsync dropped a UTF-8 byte-order mark; the JSON parser rejects one left in the text
        var bytes = ms.GetBuffer();
        var length = (int)ms.Length;
        var start = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, start, length - start);
    }

    // Every directory call used here (countries, station search) answers with a JSON array.
    private static bool IsJsonArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Fetches countries with active radio stations from radio-browser.info.</summary>
    public static async Task<IReadOnlyList<RadioCountryInfo>> GetCountriesAsync(CancellationToken ct, HttpClient? client = null) =>
        ParseCountries(await GetDirectoryJsonAsync("countries?hidebroken=true", ct, client));

    /// <summary>Parses and deduplicates radio-browser.info country entries by ISO code.</summary>
    internal static IReadOnlyList<RadioCountryInfo> ParseCountries(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var map = new Dictionary<string, RadioCountryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var code = Str(el, "iso_3166_1")?.Trim().ToUpperInvariant();
            var name = Str(el, "name")?.Trim();
            var count = Int(el, "stationcount");
            if (string.IsNullOrEmpty(code) || code.Length != 2 || string.IsNullOrEmpty(name) || count <= 0) continue;
            if (!map.TryGetValue(code, out var prev) || count > prev.StationCount)
                map[code] = new RadioCountryInfo(code, name, count);
        }
        return map.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fetches popular stations for a 2-letter ISO country code from radio-browser.info.</summary>
    public static async Task<IReadOnlyList<RadioStationCandidate>> GetStationsByCountryAsync(
        string countryCode, int limit, CancellationToken ct, HttpClient? client = null)
    {
        var cc = (countryCode ?? "").Trim().ToUpperInvariant();
        if (cc.Length != 2 || !cc.All(char.IsLetter)) return [];
        var max = Math.Clamp(limit <= 0 ? 40 : limit, 1, 100);
        var rel = $"stations/search?countrycode={Uri.EscapeDataString(cc)}&hidebroken=true&order=clickcount&reverse=true&limit={max * 2}";
        return ParseStations(await GetDirectoryJsonAsync(rel, ct, client), cc, max);
    }

    /// <summary>Parses, validates against SSRF rules, and deduplicates radio station search results.</summary>
    internal static IReadOnlyList<RadioStationCandidate> ParseStations(string json, string countryCode, int max)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RadioStationCandidate>();
        var cc = countryCode.ToLowerInvariant();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var rawName = Str(el, "name")?.Trim();
            var resolved = Str(el, "url_resolved")?.Trim();
            var streamUrl = !string.IsNullOrEmpty(resolved) ? resolved : Str(el, "url")?.Trim();
            if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(streamUrl)) continue;
            if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) || !IsSafePublicUri(uri)) continue;
            var cleanName = CleanStationName(rawName);
            if (string.IsNullOrEmpty(cleanName) || seenNames.Contains(cleanName)) continue;
            var streamKey = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
            if (!seenUrls.Add(streamKey)) continue;
            seenNames.Add(cleanName);

            var favicon = Str(el, "favicon")?.Trim();
            var logoUrl = !string.IsNullOrEmpty(favicon) && Uri.TryCreate(favicon, UriKind.Absolute, out var favUri)
                && IsSafePublicUri(favUri) ? favicon : null;
            var clicks = Int(el, "clickcount");
            var votes = Int(el, "votes");
            var slug = Slugify(cleanName);
            var id = $"radio-{cc}-{slug}";
            results.Add(new RadioStationCandidate(id, cleanName, streamUrl, logoUrl, clicks, votes));
            if (results.Count >= max) break;
        }
        return results;
    }

    /// <summary>Probes configured radio stream URLs concurrently while enforcing public-network safety checks.</summary>
    public static async Task<IReadOnlyList<RadioCheckResult>> CheckStreamsAsync(
        IReadOnlyList<RadioCheckItem> items, CancellationToken ct, HttpClient? client = null)
    {
        if (items is null || items.Count == 0) return [];
        using var owned = client is null ? CreateClient() : null;
        var http = client ?? owned!;
        using var gate = new SemaphoreSlim(6);
        var tasks = items.Take(100).Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { return await ProbeSingleAsync(http, item, ct); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task<RadioCheckResult> ProbeSingleAsync(HttpClient http, RadioCheckItem item, CancellationToken ct)
    {
        if (item is null) return new RadioCheckResult("", false, "Invalid URL");
        if (!Uri.TryCreate(item.StreamUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new RadioCheckResult(item.Id, false, "Invalid URL");
        if (!IsSafePublicUri(uri))
            return new RadioCheckResult(item.Id, false, "local address, not checked");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var res = await SendPublicGetAsync(http, uri, cts.Token);
            if (res is null)
                return new RadioCheckResult(item.Id, false, "Invalid redirect");
            if (!res.IsSuccessStatusCode)
                return new RadioCheckResult(item.Id, false, $"HTTP {(int)res.StatusCode}");
            var ctHeader = res.Content.Headers.ContentType?.MediaType ?? "";
            if (ctHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return new RadioCheckResult(item.Id, false, "HTML page (not a stream)");
            return new RadioCheckResult(item.Id, true, "Live");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RadioCheckResult(item.Id, false, "Timed out");
        }
        catch (Exception ex) when (IsNonPublicError(ex))
        {
            return new RadioCheckResult(item.Id, false, "local address, not checked");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException se)
        {
            var reason = se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "Host not found",
                SocketError.ConnectionRefused => "Connection refused",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Unreachable",
                _ => "Connection failed",
            };
            return new RadioCheckResult(item.Id, false, reason);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new RadioCheckResult(item.Id, false, "Connection failed");
        }
    }

    private static bool IsNonPublicError(Exception ex) =>
        ex is NonPublicAddressException || (ex.InnerException is not null && IsNonPublicError(ex.InnerException));

    private static async Task<HttpResponseMessage?> SendPublicGetAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsSafePublicUri(current)) throw new NonPublicAddressException();
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
            var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and <= 399 && res.Headers.Location is { } loc)
            {
                res.Dispose();
                if (!Uri.TryCreate(current, loc, out var next) || next.Scheme is not ("http" or "https")) return null;
                if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp) return null;
                if (!IsSafePublicUri(next)) throw new NonPublicAddressException();
                current = next;
                continue;
            }
            return res;
        }
        return null;
    }

    /// <summary>
    /// A stream URL from the public directory that may be imported: http(s) to a public address. Anyone can add a
    /// station there, so names that resolve inside a home network are refused too: single-label hosts ("router"),
    /// local suffixes (".lan", ".home.arpa", ".internal"…) and names that carry a private address
    /// ("192.168.1.1.nip.io", "10-0-0-1.sslip.io"). The stream check also checks the address it connects to.
    /// </summary>
    internal static bool IsSafePublicUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host, out var ip))
            return IsPublicIpAddress(ip);
        if (!host.Contains('.')) return false;
        foreach (var suffix in LocalSuffixes)
            if (host.Equals(suffix[1..], StringComparison.OrdinalIgnoreCase) || host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
        foreach (System.Text.RegularExpressions.Match m in EmbeddedIpv4().Matches(host))
            if (IPAddress.TryParse(m.Value.Replace('-', '.'), out var embedded) && !IsPublicIpAddress(embedded))
                return false;
        return true;
    }

    private static readonly string[] LocalSuffixes =
        [".localhost", ".local", ".lan", ".home", ".home.arpa", ".internal", ".intranet", ".localdomain", ".corp", ".private", ".test", ".invalid"];

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![0-9])(?:[0-9]{1,3}[.-]){3}[0-9]{1,3}(?![0-9])")]
    private static partial System.Text.RegularExpressions.Regex EmbeddedIpv4();

    internal static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127) return false;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return false;
            if (b[0] == 192 && b[1] == 88 && b[2] == 99) return false;
            if (b[0] == 198 && b[1] is 18 or 19) return false;
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            if (b[0] >= 224) return false;
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xE0) != 0x20) return false; // global unicast 2000::/3 only
            if (b[0] == 0x3F && b[1] == 0xFF && b[2] < 0x10) return false; // 3fff::/20 documentation (RFC 9637)
            if (b[0] == 0x20 && b[1] == 0x01)
            {
                if (b[2] == 0x00 && b[3] == 0x00) return false; // 2001:0000::/32 Teredo
                if (b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) return false; // 2001:0002::/48 benchmarking
                if (b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:0db8::/32 documentation
            }
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPublicIpAddress(new IPAddress(b.AsSpan(2, 4))); // 2002::/16 6to4 must wrap a public IPv4
            return true;
        }
        return false;
    }

    private static async ValueTask<Stream> ConnectPublicEndpointAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var parsed)
            ? [parsed]
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublicIpAddress).ToArray();
        if (allowed.Length == 0)
            throw new NonPublicAddressException();
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

    internal static string CleanStationName(string name)
    {
        var s = MultiSpaceRegex().Replace(name.Trim(), " ");
        return s.Length > 48 ? s[..48].Trim() : s;
    }

    internal static string Slugify(string text)
    {
        var lower = text.Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant()
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
            .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');
        var slug = NonAlphaNumRegex().Replace(lower, "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "station" : (slug.Length > 28 ? slug[..28].Trim('-') : slug);
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumRegex();
}
