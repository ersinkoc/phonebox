using System.Net;
using System.Text;
#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
#endif

namespace PhoneBox.Agent;

internal static class HlsPlaylist
{
    private static readonly HttpClient SharedHttp = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/1.0");
        return client;
    }

    public static bool IsHlsUrl(Uri uri) =>
        uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static bool IsHlsContentType(string? mediaType) =>
        mediaType is not null
        && (mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeHlsManifest(string text)
    {
        var trimmed = text.AsSpan().TrimStart('\uFEFF').TrimStart();
        return trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal)
            && (trimmed.Contains("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)
                || trimmed.Contains("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
    }

    public static bool MatchesAudioRenderDevice(string? deviceInterfaceId, string? mmDeviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInterfaceId) || string.IsNullOrWhiteSpace(mmDeviceId)) return false;
        var target = mmDeviceId.Trim();
        return deviceInterfaceId.Equals(target, StringComparison.OrdinalIgnoreCase)
            || deviceInterfaceId.Contains($"#{target}#", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Probes an HTTP(S) URI with a bounded 512-byte prefix read to check if it serves an HLS manifest without blocking on infinite audio streams or stalled response bodies.
    /// </summary>
    public static bool ProbeManifest(Uri uri, HttpClient? client = null, TimeSpan? timeout = null)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        return ProbeManifestAsync(uri, client ?? SharedHttp, timeout ?? TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
    }

    private static async Task<bool> ProbeManifestAsync(Uri uri, HttpClient http, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            using var reg = cts.Token.Register(static s => ((IDisposable)s!).Dispose(), response);
            if (!response.IsSuccessStatusCode) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var prefix = new byte[512];
            var total = 0;
            while (total < prefix.Length)
            {
                var read = await stream.ReadAsync(prefix.AsMemory(total), cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
            var text = Encoding.UTF8.GetString(prefix.AsSpan(0, total));
            return LooksLikeHlsManifest(text)
                || (IsHlsContentType(response.Content.Headers.ContentType?.MediaType)
                    && text.AsSpan().TrimStart('\uFEFF').TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)
                    && text.Contains("#EXT-X-", StringComparison.Ordinal));
        }
        catch { return false; }
    }
}

#if WINDOWS
internal static class HlsPlayer
{
    public static Handle Open(Uri uri, string? deviceId, int volume, Action onStopped, ILogger? log = null)
    {
        using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var created = AdaptiveMediaSource.CreateFromUriAsync(uri).AsTask(openCts.Token).GetAwaiter().GetResult();
        if (created.Status != AdaptiveMediaSourceCreationStatus.Success || created.MediaSource is null)
            throw new InvalidOperationException($"HLS adaptive source failed ({created.Status})");

        var adaptive = created.MediaSource;
        MediaSource? mediaSource = null;
        MediaPlayer? player = null;
        try
        {
            mediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptive);
            player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                Volume = Math.Clamp(volume, 0, 100) / 100.0,
            };
            player.CommandManager.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    using var devCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var renders = DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector())
                        .AsTask(devCts.Token).GetAwaiter().GetResult();
                    var dev = renders.FirstOrDefault(d => HlsPlaylist.MatchesAudioRenderDevice(d.Id, deviceId));
                    if (dev is not null) player.AudioDevice = dev;
                    else log?.LogWarning("HLS output device {DeviceId} was not found among active render endpoints; using default output", deviceId);
                }
                catch (Exception ex)
                {
                    log?.LogWarning(ex, "Failed to select HLS output device {DeviceId}; using default output", deviceId);
                }
            }
            var handle = new Handle(player, mediaSource, adaptive, onStopped);
            player.MediaEnded += (_, _) => handle.NotifyStopped();
            player.MediaFailed += (_, args) =>
            {
                log?.LogDebug("HLS playback failed ({Error}): {Message}", args.Error, args.ErrorMessage);
                handle.NotifyStopped();
            };
            player.Source = mediaSource;
            player.Play();
            return handle;
        }
        catch
        {
            player?.Dispose();
            mediaSource?.Dispose();
            adaptive.Dispose();
            throw;
        }
    }

    internal sealed class Handle(MediaPlayer player, MediaSource source, AdaptiveMediaSource adaptive, Action onStopped) : IDisposable
    {
        private int _stopped;
        private int _disposed;

        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        public void NotifyStopped()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            _ = Task.Run(onStopped);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _stopped, 1);
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
            try { source.Dispose(); } catch { }
            try { adaptive.Dispose(); } catch { }
        }
    }
}
#endif
