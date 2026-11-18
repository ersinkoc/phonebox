using System.Net;
using System.Net.Sockets;

namespace PhoneBox.Agent;

/// <summary>
/// Strict local/USB-only HTTPS proxy for built-in public widget feeds when the docked phone lacks direct internet access.
/// </summary>
public static class WidgetFeedProxy
{
    public const int MaxResponseBytes = 1_048_576;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.open-meteo.com",
        "air-quality-api.open-meteo.com",
        "hacker-news.firebaseio.com",
        "api.frankfurter.dev",
        "api.coingecko.com",
    };

    private static readonly HttpClient SharedClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = async (ctx, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
                var publicIps = addresses.Where(IsPublicAddress)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();
                if (publicIps.Length == 0)
                    throw new HttpRequestException("Refused non-public address for feed host");
                Exception? last = null;
                foreach (var publicIp in publicIps)
                {
                    // created inside the try: a host without IPv4 (or IPv6) sockets moves on to the next address
                    Socket? socket = null;
                    try
                    {
                        socket = new Socket(publicIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        await socket.ConnectAsync(new IPEndPoint(publicIp, ctx.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        socket?.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        socket?.Dispose();
                        last = ex;
                    }
                }
                throw last ?? new HttpRequestException("Could not connect to feed host");
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool TryValidateTarget(string? rawUrl, out Uri? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 2048) return false;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!AllowedHosts.Contains(uri.Host)) return false;
        target = uri;
        return true;
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return false; // 100.64.0.0/10 CGNAT
            if (b[0] == 169 && b[1] == 254) return false; // 169.254.0.0/16 link-local
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false; // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return false; // IETF / TEST-NET-1
            if (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) return false; // benchmark / TEST-NET-2
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false; // TEST-NET-3
            if (b[0] >= 224) return false; // multicast + reserved
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)
                || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo)
                return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xE0) != 0x20) return false; // global unicast 2000::/3 only (excludes ::/8, 64:ff9b:: NAT64, 100::/64, fc00::/7)
            if (b[0] == 0x20 && b[1] == 0x01)
            {
                if (b[2] == 0x00 && b[3] == 0x00) return false; // 2001:0000::/32 Teredo
                if (b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) return false; // 2001:0002::/48 benchmarking
                if (b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:0db8::/32 documentation
            }
            if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0) return false; // 3fff::/20 documentation (RFC 9637)
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPublicAddress(new IPAddress(b.AsSpan(2, 4))); // 2002::/16 6to4 must wrap a public IPv4
            return true;
        }

        return false;
    }

    public static async Task<IResult> FetchAsync(string? rawUrl, CancellationToken ct, HttpClient? client = null)
    {
        if (!TryValidateTarget(rawUrl, out var target) || target is null)
            return Results.BadRequest(new { error = "Disallowed or invalid feed URL" });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            using var res = await (client ?? SharedClient).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)res.StatusCode;
            if (code is >= 300 and < 400)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            if (res.Content.Headers.ContentLength is { } cl && cl > MaxResponseBytes)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buf, cts.Token);
                if (read == 0) break;
                if (ms.Length + read > MaxResponseBytes)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                ms.Write(buf, 0, read);
            }

            var contentType = res.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
            return new ProxyResult(ms.ToArray(), contentType, code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private sealed class ProxyResult(byte[] body, string contentType, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}
