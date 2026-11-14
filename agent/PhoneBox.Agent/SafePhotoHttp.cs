using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Outbound HTTPS client and validators for cloud photo providers: blocks loopback/private/link-local/CGNAT IPs
/// via ConnectCallback, rejects https->http downgrades, strips auth headers across hosts, caps body sizes,
/// and validates raster image magic bytes so HTML/SVG responses are never served as photos.
/// </summary>
public static class SafePhotoHttp
{
    public const long MaxJsonBytes = 8 * 1024 * 1024;
    public const long MaxImageBytes = 25 * 1024 * 1024;

    private static readonly string[] SensitiveHeaders =
    [
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
        "Dropbox-API-Arg",
        "X-Api-Key",
    ];

    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectPublicOnlyAsync,
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(25),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox-Agent/1.0");
        return client;
    }

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!IsPublicAddress(literal))
                throw new HttpRequestException($"Blocked connection to non-public address {literal}");
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
                throw new HttpRequestException($"Could not resolve host '{host}'");
            foreach (var addr in addresses)
            {
                if (!IsPublicAddress(addr))
                    throw new HttpRequestException($"Blocked connection to resolved non-public address {addr}");
            }
        }

        Exception? lastError = null;
        foreach (var addr in addresses)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addr, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastError ?? new HttpRequestException($"Could not connect to '{host}'");
    }

    public static bool TryValidateHttpsUri(string? raw, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "URL is empty";
            return false;
        }
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Only HTTPS URLs are allowed";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URLs must not contain embedded credentials";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "URL host is missing";
            return false;
        }
        if (IPAddress.TryParse(parsed.DnsSafeHost, out var ip) && !IsPublicAddress(ip))
        {
            error = "Private or loopback IP addresses are not allowed";
            return false;
        }
        if (string.Equals(parsed.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || parsed.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || parsed.DnsSafeHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || parsed.DnsSafeHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "Local hostnames are not allowed";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>True only for globally routable unicast IPs; rejects loopback, RFC1918, CGNAT, link-local, ULA, multicast, Teredo, and private mapped addresses.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            var a = b[0];
            var sec = b[1];
            var third = b[2];

            if (a == 0) return false;                                     // 0.0.0.0/8
            if (a == 10) return false;                                    // 10.0.0.0/8
            if (a == 100 && sec is >= 64 and <= 127) return false;        // 100.64.0.0/10 (CGNAT)
            if (a == 127) return false;                                   // 127.0.0.0/8 (loopback)
            if (a == 169 && sec == 254) return false;                     // 169.254.0.0/16 (link-local / metadata)
            if (a == 172 && sec is >= 16 and <= 31) return false;         // 172.16.0.0/12
            if (a == 192 && sec == 0 && third == 0) return false;         // 192.0.0.0/24
            if (a == 192 && sec == 0 && third == 2) return false;         // 192.0.2.0/24 (TEST-NET-1)
            if (a == 192 && sec == 88 && third == 99) return false;       // 192.88.99.0/24 (6to4 relay)
            if (a == 192 && sec == 168) return false;                     // 192.168.0.0/16
            if (a == 198 && sec is 18 or 19) return false;                // 198.18.0.0/15 (benchmarking)
            if (a == 198 && sec == 51 && third == 100) return false;      // 198.51.100.0/24 (TEST-NET-2)
            if (a == 203 && sec == 0 && third == 113) return false;       // 203.0.113.0/24 (TEST-NET-3)
            if (a >= 224) return false;                                   // multicast (224+) & reserved (240+) & broadcast
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || IPAddress.IsLoopback(ip)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;

            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false;                      // fc00::/7 ULA
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return false;      // fe80::/10 link-local
            if (b[0] == 0xff) return false;                               // ff00::/8 multicast
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 docs
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // Teredo 2001::/32

            // NAT64 local-use 64:ff9b:1::/48
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01) return false;

            // NAT64 well-known 64:ff9b::/96 -> validate embedded IPv4
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b
                && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            {
                return IsPublicAddress(new IPAddress(b[12..16]));
            }

            // IPv4-compatible ::/96 -> validate embedded IPv4
            if (b[..12].All(x => x == 0))
                return IsPublicAddress(new IPAddress(b[12..16]));

            // 6to4 2002::/16 -> validate embedded IPv4
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPublicAddress(new IPAddress(b[2..6]));

            return (b[0] & 0xe0) == 0x20; // require 2000::/3 global unicast
        }

        return false;
    }

    /// <summary>Sends a request following up to <paramref name="maxRedirects"/> HTTPS redirects while stripping credentials cross-host.</summary>
    public static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient http,
        HttpRequestMessage initial,
        CancellationToken ct,
        int maxRedirects = 5,
        Func<Uri, bool>? extraHostValidator = null)
    {
        var current = initial;
        byte[]? cachedBody = null;
        string? cachedContentType = null;
        if (initial.Content is not null)
        {
            cachedContentType = initial.Content.Headers.ContentType?.ToString();
            cachedBody = await initial.Content.ReadAsByteArrayAsync(ct);
        }

        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            string? urlErr = null;
            if (current.RequestUri is null || !TryValidateHttpsUri(current.RequestUri.AbsoluteUri, out _, out urlErr))
            {
                if (!ReferenceEquals(current, initial)) current.Dispose();
                throw new IOException(urlErr ?? "Invalid request URL");
            }
            if (extraHostValidator is not null && !extraHostValidator(current.RequestUri))
            {
                if (!ReferenceEquals(current, initial)) current.Dispose();
                throw new IOException($"Blocked request to disallowed host '{current.RequestUri.Host}'");
            }

            var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var nextUri = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                var prevUri = current.RequestUri!;
                var prevMethod = current.Method;

                if (prevUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                    && !nextUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    if (!ReferenceEquals(current, initial)) current.Dispose();
                    throw new IOException("Blocked insecure HTTP redirect downgrade");
                }
                if (!TryValidateHttpsUri(nextUri.AbsoluteUri, out var validatedNext, out var nextErr))
                {
                    response.Dispose();
                    if (!ReferenceEquals(current, initial)) current.Dispose();
                    throw new IOException(nextErr ?? "Blocked redirect target");
                }

                var preserveMethod = code is 307 or 308;
                var nextMethod = preserveMethod ? prevMethod : (prevMethod == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get);
                var sameOrigin = string.Equals(prevUri.Host, validatedNext!.Host, StringComparison.OrdinalIgnoreCase)
                    && prevUri.Port == validatedNext.Port;

                var nextReq = new HttpRequestMessage(nextMethod, validatedNext);
                foreach (var header in current.Headers)
                {
                    if (!sameOrigin && SensitiveHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                        continue;
                    nextReq.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (preserveMethod && cachedBody is not null)
                {
                    nextReq.Content = new ByteArrayContent(cachedBody);
                    if (cachedContentType is not null)
                        nextReq.Content.Headers.TryAddWithoutValidation("Content-Type", cachedContentType);
                }

                response.Dispose();
                if (!ReferenceEquals(current, initial)) current.Dispose();
                current = nextReq;
                continue;
            }

            if (!ReferenceEquals(current, initial)) current.Dispose();
            return response;
        }

        if (!ReferenceEquals(current, initial)) current.Dispose();
        throw new IOException("Too many HTTP redirects");
    }

    public static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new IOException($"Response size ({declared} bytes) exceeds limit ({maxBytes} bytes)");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream(content.Headers.ContentLength is { } len and > 0 and <= 1024 * 1024 ? (int)len : 16384);
        var buffer = new byte[16384];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new IOException($"Response exceeded size limit ({maxBytes} bytes)");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public static async Task<string> ReadBoundedStringAsync(HttpContent content, long maxBytes, CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadBoundedBytesAsync(content, maxBytes, ct));

    /// <summary>Inspects leading magic bytes so only safe raster images are returned to the phone (never SVG/HTML/scripts).</summary>
    public static string? DetectRasterMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        if (bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
            return "image/gif";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return "image/bmp";
        return null;
    }
}
