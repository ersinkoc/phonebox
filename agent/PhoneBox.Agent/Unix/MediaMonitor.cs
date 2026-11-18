using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Now playing on Linux (MPRIS through playerctl) and macOS (system media through nowplaying-cli, with
/// Spotify and Music AppleScript fallback). Same API as the Windows MediaMonitor.
/// </summary>
public sealed class MediaMonitor(ILogger<MediaMonitor> log) : BackgroundService
{
    private const int MaxArtBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan ArtDeadline = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Http = CreatePublicHttpClient();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private volatile MediaSnapshot? _snap;
    private string _artUrl = "";
    private int _artId;
    private bool _warned;
    private volatile bool _macSystemMedia;
    private string _macArtKey = "";
    private string _macArtEncoded = "";
    private string _macPositionKey = "";
    private long _macPositionMs;
    private long _macLastRawPositionMs;
    private DateTimeOffset _macPositionAt;
    private string _macSourceKey = "";
    private string _macSource = "";
    private DateTimeOffset _macSourceAt;

    public AlbumArt? Art { get; private set; }
    public MediaSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Poll(); }
            catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); _snap = null; }
            await Task.Delay(1000, ct);
        }
    }

    private async Task Poll()
    {
        await _pollGate.WaitAsync();
        try
        {
            var (snap, art) = OperatingSystem.IsMacOS() ? ReadMac() : ReadLinux();
            if (snap is null) { _snap = null; Art = null; _macArtKey = ""; _macArtEncoded = ""; _macPositionKey = ""; _artUrl = ""; return; }
            if (_macSystemMedia)
            {
                _snap = snap with { ArtId = Art is null ? 0 : _artId };
                return;
            }
            if (_macArtKey.Length > 0) { _macArtKey = ""; _macArtEncoded = ""; _artUrl = "\0"; }
            if (art != _artUrl)
            {
                _artUrl = art;
                var loaded = await LoadArt(art);
                if (loaded is null) Art = null;
                else if (Art is null || Art.Mime != loaded.Mime || !Art.Bytes.AsSpan().SequenceEqual(loaded.Bytes)) { Art = loaded; _artId++; }
            }
            _snap = snap with { ArtId = Art is null ? 0 : _artId };
        }
        finally
        {
            _pollGate.Release();
        }
    }

    // ---------------------------------------------------------------- Linux: playerctl (MPRIS)
    private (MediaSnapshot?, string) ReadLinux()
    {
        if (Sh.Which("playerctl") is null)
        {
            if (!_warned) { _warned = true; log.LogInformation("media: install playerctl to show and control what is playing (sudo apt install playerctl)"); }
            return (null, "");
        }
        var line = Sh.Run("playerctl", "metadata", "--format",
            "{{playerName}}\t{{status}}\t{{title}}\t{{artist}}\t{{album}}\t{{position}}\t{{mpris:length}}\t{{mpris:artUrl}}");
        if (string.IsNullOrWhiteSpace(line)) return (null, "");
        var f = line.TrimEnd('\n').Split('\t');
        if (f.Length < 8) return (null, "");
        long Us(string s) => long.TryParse(s, out var v) ? v / 1000 : 0;
        var app = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f[0].Split('.')[0]);
        return (new MediaSnapshot(app, f[2], f[3], f[4], f[1] == "Playing", Us(f[5]), Us(f[6]), 0, Us(f[6]) > 0), f[7]);
    }

    // ---------------------------------------------------------------- macOS: system media, then AppleScript fallback
    private (MediaSnapshot?, string) ReadMac()
    {
        var system = ReadMacSystemMedia();
        _macSystemMedia = system.Item1 is not null;
        return _macSystemMedia ? system : ReadMacAppleScript();
    }

    internal (MediaSnapshot?, string) ReadMacSystemMedia(string? suppliedJson = null, DateTimeOffset? sampledAt = null)
    {
        var json = suppliedJson ?? Sh.Run("nowplaying-cli", "get-raw");
        if (string.IsNullOrWhiteSpace(json)) { _macPositionKey = ""; return (null, ""); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement;
            if (data.ValueKind != JsonValueKind.Object) { _macPositionKey = ""; return (null, ""); }
            const string prefix = "kMRMediaRemoteNowPlayingInfo";
            string Str(string key) => data.TryGetProperty(prefix + key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            double Num(string key) => data.TryGetProperty(prefix + key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
            var title = Str("Title");
            if (string.IsNullOrWhiteSpace(title)) { _macPositionKey = ""; return (null, ""); }
            var bundle = Str("ClientBundleIdentifier");
            var app = bundle switch
            {
                "com.apple.WebKit.GPU" or "com.apple.Safari" => "Safari",
                "com.google.Chrome" => "Chrome",
                "org.mozilla.firefox" => "Firefox",
                "com.apple.Music" => "Music",
                "com.spotify.client" => "Spotify",
                "com.github.th-ch.youtube-music" => "YouTube Music",
                _ => string.IsNullOrEmpty(bundle) ? "Mac" : bundle.Split('.').Last(),
            };
            var sourceKey = bundle + "\n" + title;
            if (_macSourceKey != sourceKey || DateTimeOffset.UtcNow - _macSourceAt > TimeSpan.FromSeconds(10))
            {
                _macSourceKey = sourceKey;
                _macSourceAt = DateTimeOffset.UtcNow;
                _macSource = ReadYouTubeSource(bundle, title);
            }
            if (_macSource.Length > 0) app = _macSource;

            var duration = Math.Max(0, Num("Duration"));
            var playing = Num("PlaybackRate") > 0;
            var rawPosition = (long)(Math.Max(0, Num("ElapsedTime")) * 1000);
            var positionKey = sourceKey + "\n" + Num("UniqueIdentifier").ToString(CultureInfo.InvariantCulture);
            var now = sampledAt ?? DateTimeOffset.UtcNow;
            if (positionKey != _macPositionKey)
            {
                _macPositionKey = positionKey;
                _macPositionMs = rawPosition;
                _macLastRawPositionMs = rawPosition;
            }
            else
            {
                var predicted = _macPositionMs + (playing ? (long)(now - _macPositionAt).TotalMilliseconds : 0);
                _macPositionMs = rawPosition != _macLastRawPositionMs ? rawPosition : predicted;
                _macLastRawPositionMs = rawPosition;
            }
            _macPositionAt = now;
            var durationMs = (long)(duration * 1000);
            if (durationMs > 0) _macPositionMs = Math.Min(_macPositionMs, durationMs);

            var artKey = positionKey;
            if (artKey != _macArtKey) { _macArtKey = artKey; _macArtEncoded = ""; Art = null; }
            var encoded = data.TryGetProperty("kMRMediaRemoteNowPlayingInfoArtworkData", out var artProp) && artProp.ValueKind == JsonValueKind.String
                ? artProp.GetString() ?? "" : "";
            if (encoded.Length == 0)
            {
                _macArtEncoded = "";
                Art = null;
            }
            else if (encoded != _macArtEncoded)
            {
                var nextArt = DecodeMacArt(encoded);
                if (nextArt is null)
                {
                    _macArtEncoded = "";
                    Art = null;
                }
                else
                {
                    _macArtEncoded = encoded;
                    if (Art is null || Art.Mime != nextArt.Mime || !Art.Bytes.AsSpan().SequenceEqual(nextArt.Bytes)) { Art = nextArt; _artId++; }
                }
            }
            return (new MediaSnapshot(app, title, Str("Artist"), Str("Album"), playing,
                _macPositionMs, durationMs, Art is null ? 0 : _artId, duration > 0), "");
        }
        catch (JsonException) { _macPositionKey = ""; return (null, ""); }
    }

    private static string ReadYouTubeSource(string bundle, string title)
    {
        var script = bundle switch
        {
            "com.apple.WebKit.GPU" or "com.apple.Safari" => """
                if application "Safari" is not running then return ""
                set matches to ""
                tell application "Safari"
                  repeat with w in windows
                    repeat with t in tabs of w
                      try
                        set u to URL of t
                        if u contains "youtube.com/" then
                          set sourceName to "YouTube"
                          if u contains "music.youtube.com/" then set sourceName to "YouTube Music"
                          set matches to matches & sourceName & (ASCII character 9) & (name of t) & linefeed
                        end if
                      end try
                    end repeat
                  end repeat
                end tell
                return matches
                """,
            "com.google.Chrome" => """
                if application "Google Chrome" is not running then return ""
                set matches to ""
                tell application "Google Chrome"
                  repeat with w in windows
                    repeat with t in tabs of w
                      try
                        set u to URL of t
                        if u contains "youtube.com/" then
                          set sourceName to "YouTube"
                          if u contains "music.youtube.com/" then set sourceName to "YouTube Music"
                          set matches to matches & sourceName & (ASCII character 9) & (title of t) & linefeed
                        end if
                      end try
                    end repeat
                  end repeat
                end tell
                return matches
                """,
            _ => null,
        };
        if (script is null) return "";
        var lines = Sh.Run("osascript", "-e", script)?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var tabs = lines.Select(line => line.TrimEnd('\r').Split('\t', 2)).Where(parts => parts.Length == 2).ToArray();
        var match = tabs.FirstOrDefault(parts => parts[1].Contains(title, StringComparison.OrdinalIgnoreCase));
        return match?[0] ?? "";
    }

    private static AlbumArt? DecodeMacArt(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 8_000_000) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
                return new AlbumArt(bytes, "image/jpeg");
            if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47 }))
                return new AlbumArt(bytes, "image/png");
            if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                return new AlbumArt(bytes, "image/webp");

            // Safari often publishes TIFF artwork; Android WebView cannot display TIFF.
            var input = Path.GetTempFileName();
            var output = input + ".png";
            try
            {
                File.WriteAllBytes(input, bytes);
                if (Sh.RunWithTimeout(5000, "sips", "-s", "format", "png", input, "--out", output) is null || !File.Exists(output))
                    return null;
                return new AlbumArt(File.ReadAllBytes(output), "image/png");
            }
            finally
            {
                File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }
        catch (Exception) { return null; }
    }

    private static (MediaSnapshot?, string) ReadMacAppleScript()
    {
        const string script = """
            if application "Spotify" is running then
              tell application "Spotify"
                if player state is stopped then return ""
                set t to current track
                return "Spotify" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) / 1000 as string) & tab & (artwork url of t)
              end tell
            else if application "Music" is running then
              tell application "Music"
                if player state is stopped then return ""
                set t to current track
                return "Music" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) as string) & tab & ""
              end tell
            end if
            return ""
            """;
        var line = Sh.Run("osascript", "-e", script)?.Trim();
        if (string.IsNullOrEmpty(line)) return (null, "");
        var f = line.Split('\t');
        if (f.Length < 8) return (null, "");
        long Sec(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)(v * 1000) : 0;
        return (new MediaSnapshot(f[0], f[2], f[3], f[4], f[1] == "playing", Sec(f[5]), Sec(f[6]), 0, true), f[7]);
    }

    private static HttpClient CreatePublicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (ctx, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
                var publicIps = addresses.Where(WidgetFeedProxy.IsPublicAddress)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();
                if (publicIps.Length == 0)
                    throw new HttpRequestException("Refused non-public cover art host");
                Exception? last = null;
                foreach (var ip in publicIps)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { socket.Dispose(); throw; }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw last ?? new HttpRequestException("Could not connect to cover art host");
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
    }

    internal static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47 }))
            return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    private static bool HasAllowedArtExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";

    /// <summary>
    /// The body of a cover art URL, or null when the server refuses it or it is larger than <see cref="MaxArtBytes"/>.
    /// One deadline covers the request and every body read: HttpClient.Timeout ends at the headers, and a server that
    /// stops mid-body would otherwise hold the poll (and every media control waiting on it) for good.
    /// </summary>
    internal static async Task<byte[]?> DownloadArt(HttpClient http, string url, TimeSpan deadline)
    {
        using var cts = new CancellationTokenSource(deadline);
        using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!r.IsSuccessStatusCode || r.Content.Headers.ContentLength > MaxArtBytes) return null;
        await using var stream = await r.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            if (ms.Length + read > MaxArtBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private async Task<AlbumArt?> LoadArt(string url)
    {
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsUnc) return null;
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (!File.Exists(path) && uri.IsFile) path = uri.LocalPath;
                if (!HasAllowedArtExtension(path) || !File.Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaxArtBytes) return null;
                var bytes = await File.ReadAllBytesAsync(path);
                return bytes.Length <= MaxArtBytes && SniffImageMime(bytes) is { } mime ? new AlbumArt(bytes, mime) : null;
            }
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await DownloadArt(Http, url, ArtDeadline);
                return bytes is not null && SniffImageMime(bytes) is { } mime ? new AlbumArt(bytes, mime) : null;
            }
        }
        catch (Exception ex) { log.LogDebug("cover art: {Msg}", ex.Message); }
        return null;
    }

    public async Task<bool> Control(string action, long? ms)
    {
        bool ok;
        if (OperatingSystem.IsMacOS())
        {
            if (_macSystemMedia)
            {
                string[]? args = action switch
                {
                    "playpause" => ["togglePlayPause"],
                    "next" => ["next"],
                    "prev" => ["previous"],
                    "seek" when ms is { } m => ["seek", (m / 1000.0).ToString(CultureInfo.InvariantCulture)],
                    _ => null,
                };
                ok = args is not null && Sh.Run("nowplaying-cli", args) is not null;
            }
            else
            {
                var app = _snap?.App ?? "Spotify";
                var cmd = action switch
                {
                    "playpause" => "playpause",
                    "next" => "next track",
                    "prev" => "previous track",
                    "seek" when ms is { } m => $"set player position to {(m / 1000.0).ToString(CultureInfo.InvariantCulture)}",
                    _ => null,
                };
                ok = cmd is not null && Sh.Run("osascript", "-e", $"tell application \"{app}\" to {cmd}") is not null;
            }
        }
        else
        {
            string[]? args = action switch
            {
                "playpause" => ["play-pause"],
                "next" => ["next"],
                "prev" => ["previous"],
                "seek" when ms is { } m => ["position", (m / 1000.0).ToString(CultureInfo.InvariantCulture)],
                _ => null,
            };
            ok = args is not null && Sh.Run("playerctl", args) is not null;
        }
        try { await Poll(); } catch { }
        return ok;
    }

    public async Task<bool> PauseAsync()
    {
        var snap = _snap;
        if (snap is not { Playing: true }) return false;
        bool ok;
        if (OperatingSystem.IsMacOS())
        {
            if (_macSystemMedia) ok = Sh.Run("nowplaying-cli", "pause") is not null;
            else if (snap.App is "Spotify" or "Music")
                ok = Sh.Run("osascript", "-e", $"tell application \"{snap.App}\" to pause") is not null;
            else return false;
        }
        else ok = Sh.Run("playerctl", "pause") is not null;
        try { await Poll(); } catch { }
        return ok;
    }
}
