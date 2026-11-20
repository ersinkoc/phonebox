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
    private string _macAnchorKey = "";
    private bool _macAnchorPlaying;
    private DateTimeOffset _macAnchorAt;
    private static readonly TimeSpan AnchorEvery = TimeSpan.FromSeconds(5);

    /// <summary>The clock reading (Stopwatch ticks) at which the last now-playing position was computed.</summary>
    internal long LastSampleTicks { get; private set; }

    /// <summary>Where the player itself says playback is (ms), or null when it cannot be asked. Replaced in tests.</summary>
    internal Func<string, long?> AppPosition { get; set; } = ReadAppPosition;
    private string _macSourceKey = "";
    private string _macSource = "";
    private DateTimeOffset _macSourceAt;

    // Every reading starts nowplaying-cli or playerctl (and osascript when nothing is in Now Playing), and on macOS .NET
    // forks the whole agent to start each one. A panel on screen asks for the snapshot every second (the live stream's
    // state), and so does a "media-playing" rule while one exists. With nobody asking (the phone's screen is off, it is
    // unplugged, no panel is open) a reading every IdlePoll is enough, and the first request after that reads at once.
    internal static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);
    internal const long WantedForMs = 15_000;
    private long _lastRequest = -WantedForMs; // nobody has asked yet
    // when the last reading finished; a control acts on that reading, so it reads again first when this is old
    private long _lastReading = long.MinValue / 2;
    internal const long FreshForMs = 2_000;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public AlbumArt? Art { get; private set; }

    public MediaSnapshot? Snapshot()
    {
        var now = Environment.TickCount64;
        if (IsIdle(Interlocked.Exchange(ref _lastRequest, now), now)) Wake();
        return _snap;
    }

    internal static bool IsIdle(long lastRequest, long now) => now - lastRequest >= WantedForMs;
    internal static bool IsStale(long lastReading, long now) => now - lastReading > FreshForMs;
    private bool Idle => IsIdle(Interlocked.Read(ref _lastRequest), Environment.TickCount64);

    /// <summary>A request came back after a quiet spell and the next reading will not wait out IdlePoll.</summary>
    internal bool ReadingDue => _wake.CurrentCount > 0;

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>
    /// While nobody watches, the last reading can be up to IdlePoll old: read again before acting on it. Keyed on the
    /// reading, not the last request, because the phone's /api/media asks for the snapshot just before it acts.
    /// </summary>
    private async Task RefreshIfIdle()
    {
        if (!IsStale(Interlocked.Read(ref _lastReading), Environment.TickCount64)) return;
        try { await Poll(onlyIfStale: true); }
        catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Poll(); }
            catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); _snap = null; }
            await _wake.WaitAsync(Idle ? IdlePoll : ActivePoll, ct);
        }
    }

    private async Task Poll(bool onlyIfStale = false)
    {
        await _pollGate.WaitAsync();
        // a control that waited here for the loop's own reading uses that one instead of starting the tools again
        if (onlyIfStale && !IsStale(Interlocked.Read(ref _lastReading), Environment.TickCount64)) { _pollGate.Release(); return; }
        var before = _snap;
        // fresh from when the players were asked, not from when the artwork finished loading
        var asked = Environment.TickCount64;
        try
        {
            var (snap, art) = OperatingSystem.IsMacOS() ? ReadMac() : ReadLinux();
            // when the position was computed: for the now-playing info that is inside ReadMacSystemMedia, before the artwork is
            // decoded (a TIFF is converted with sips, which can take seconds); otherwise it was read just now
            var sampledAt = OperatingSystem.IsMacOS() && _macSystemMedia && LastSampleTicks != 0 ? LastSampleTicks : System.Diagnostics.Stopwatch.GetTimestamp();
            if (snap is null) { _snap = null; Art = null; _macArtKey = ""; _macArtEncoded = ""; _macPositionKey = ""; _artUrl = ""; return; }
            if (_macSystemMedia)
            {
                _snap = snap with { ArtId = Art is null ? 0 : _artId, SampledAt = sampledAt };
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
            _snap = snap with { ArtId = Art is null ? 0 : _artId, SampledAt = sampledAt };
        }
        finally
        {
            Interlocked.Exchange(ref _lastReading, asked);
            _pollGate.Release();
            // a new track, a pause or a seek reaches the phone now, not at its next whole-second state
            if (StateSignal.MediaChanged(before, _snap)) StateSignal.Poke();
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
            LastSampleTicks = System.Diagnostics.Stopwatch.GetTimestamp(); // the position is as of now, whatever the artwork below takes
            if (positionKey != _macPositionKey)
            {
                _macPositionKey = positionKey;
                _macPositionMs = rawPosition;
                _macLastRawPositionMs = rawPosition;
                _macAnchorAt = DateTimeOffset.MinValue; // a new position sequence (or the same track back after a gap): ask the player
            }
            else
            {
                var predicted = _macPositionMs + (playing ? (long)(now - _macPositionAt).TotalMilliseconds : 0);
                _macPositionMs = rawPosition != _macLastRawPositionMs ? rawPosition : predicted;
                _macLastRawPositionMs = rawPosition;
            }
            // ElapsedTime is only valid as of the Timestamp the source published it with (browsers publish it on play,
            // pause and seek, not while playing), so the real position is that plus the time since, at the playback rate
            if (MacPublishedAt(data, prefix + "Timestamp", now) is { } publishedAt)
            {
                var rate = Num("PlaybackRate");
                _macPositionMs = rawPosition + (playing ? (long)(Math.Max(0, (now - publishedAt).TotalMilliseconds) * rate) : 0);
                _macLastRawPositionMs = rawPosition;
            }
            else if (bundle is "com.spotify.client" or "com.apple.Music")
            {
                // Since macOS 15.4 these often report ElapsedTime 0 for the whole track and no Timestamp at all (Spotify on
                // macOS 26: checked), so nothing here says where playback is. The player does: ask it when the track or
                // play/pause changes and every few seconds (a seek), and count from that in between.
                if (positionKey != _macAnchorKey || playing != _macAnchorPlaying || now - _macAnchorAt >= AnchorEvery)
                {
                    _macAnchorKey = positionKey;
                    _macAnchorPlaying = playing;
                    _macAnchorAt = now; // also after a refusal: it is not asked again every second
                    if (AppPosition(bundle) is { } fromPlayer) { _macPositionMs = fromPlayer; _macLastRawPositionMs = rawPosition; }
                }
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

    /// <summary>
    /// When the source published the position, from the "Timestamp" of the now-playing info: a date string
    /// (<c>2026-11-19T10:00:00Z</c>) or seconds since 1970 (a number; a count from 2001 or in milliseconds is not
    /// guessed). Null when it is missing, unreadable or implausible, so the caller falls back to advancing the sample.
    /// </summary>
    internal static DateTimeOffset? MacPublishedAt(JsonElement data, string key, DateTimeOffset now)
    {
        if (!data.TryGetProperty(key, out var value)) return null;
        DateTimeOffset at;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) return null;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds)
                 && seconds is > 1e9 and < 1e11)
            at = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        else return null;
        // up to a few seconds ahead is clock noise (the date string is cut to whole seconds); further, or over half a day
        // back, means this is not a time we can rely on
        var age = now - at;
        return age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromHours(12) ? null : at;
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

    /// <summary>Spotify's or Music's own "player position" (about 150 ms; never launches the app).</summary>
    private static long? ReadAppPosition(string bundle)
    {
        var app = bundle == "com.spotify.client" ? "Spotify" : "Music";
        var text = Sh.Run("osascript", "-e",
            $"if application \"{app}\" is running then tell application \"{app}\" to return (player position as string)")?.Trim();
        // the decimal separator follows the Mac's language ("16,32")
        return !string.IsNullOrEmpty(text) && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.IsFinite(s) && s >= 0 ? (long)(s * 1000) : null;
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
        await RefreshIfIdle(); // which player to ask comes from the last reading
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
        // after a seek the position is no longer where the last check put it: the poll below asks the player again
        if (action == "seek") ForgetPlayerPosition();
        try { await Poll(); } catch { }
        return ok;
    }

    /// <summary>The next reading asks the player for its position instead of counting on from the last answer.</summary>
    internal void ForgetPlayerPosition() => _macAnchorAt = DateTimeOffset.MinValue;

    public async Task<bool> PauseAsync()
    {
        await RefreshIfIdle();
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
