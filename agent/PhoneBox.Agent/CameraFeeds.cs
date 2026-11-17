using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>IP cameras for the Camera widget (Settings › General › Cameras). The addresses carry the cameras'
/// passwords, so they stay on this computer: the phone only ever gets the picture.</summary>
public sealed class CameraConfig
{
    /// <summary>Full path of ffmpeg; empty = look for it in PATH.</summary>
    public string Ffmpeg { get; set; } = "";
    public List<CameraSource> Sources { get; set; } = [];
}

public sealed class CameraSource
{
    /// <summary>Random, not derived from the name: widgets point at it, and a renamed camera must keep its widgets.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>rtsp:// or rtsps:// only, usually with user:password@ in it.</summary>
    public string Url { get; set; } = "";
    /// <summary>"tcp" (default, works through most networks) or "udp" (for cameras that only stream over UDP).</summary>
    public string Transport { get; set; } = "tcp";
}

/// <summary>
/// The pictures of the cameras the panel shows. One ffmpeg per camera turns its RTSP stream into a couple of JPEG frames a
/// second and only the latest is kept. A camera is pulled only while a widget on an enabled page asks for it, and stops
/// a few seconds after the last request. ffmpeg's own messages can contain the address (and with it the password), so
/// they never reach the phone: it gets one of a few fixed states, and the log gets a masked line.
/// </summary>
public sealed partial class CameraFeeds : IDisposable
{
    public const string WidgetType = "camera";
    public const int MaxCameras = 8;
    /// <summary>At most this many cameras are pulled at the same time (each one is a decoder running on this computer).</summary>
    public const int MaxRunning = 4;
    /// <summary>Frames a second that ffmpeg produces, and how often the panel asks.</summary>
    public const int Fps = 2;
    internal const long KeepMs = 10_000;      // a camera stops this long after the last request
    internal const long StallMs = 20_000;     // no frame for this long: ffmpeg is restarted
    internal const long FreshMs = 5_000;      // an older frame isn't shown as live
    internal const long MaxBackoffMs = 60_000;

    /// <summary>Starts ffmpeg for a camera; frames go to the callback. Replaceable so the tests never start a process.</summary>
    internal delegate ICameraRun Launcher(string ffmpeg, CameraSource source, Action<byte[]> onFrame);

    private readonly ConfigStore _config;
    private readonly ILogger<CameraFeeds> _log;
    private readonly Launcher _launch;
    private readonly Func<long> _now;
    private readonly Func<string?> _findFfmpeg;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Feed> _feeds = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public CameraFeeds(ConfigStore config, ILogger<CameraFeeds> log) : this(config, log, null, null, null) { }

    internal CameraFeeds(ConfigStore config, ILogger<CameraFeeds> log, Launcher? launch, Func<long>? now, Func<string?>? findFfmpeg, bool timer = true)
    {
        _config = config;
        _log = log;
        _launch = launch ?? ((ffmpeg, source, onFrame) => FfmpegRun.Start(ffmpeg, source, onFrame));
        _now = now ?? (() => Environment.TickCount64);
        _findFfmpeg = findFfmpeg ?? (() => FindFfmpeg(_config.Get().Cameras.Ffmpeg));
        _timer = new System.Threading.Timer(_ => { try { Tick(); } catch (Exception ex) { _log.LogDebug("camera: {Msg}", ex.Message); } }, null,
            timer ? 1000 : Timeout.Infinite, timer ? 1000 : Timeout.Infinite);
    }

    private sealed class Feed(CameraSource source)
    {
        public CameraSource Source = source;
        public ICameraRun? Run;
        public byte[]? Frame;
        public long FrameAt, StartedAt, WantedUntil, RetryAt, Backoff;
        /// <summary>Which start the running ffmpeg is: a stopped one's last buffered frame mustn't count for the next.</summary>
        public int Generation;
        public bool GotFrame;
        public string State = "starting";
    }

    /// <summary>
    /// The latest picture for the Camera widget the phone names ("pageId/widgetId"). 200 with a JPEG, or a status with one
    /// of the states the panel shows: "unknown" (no such widget on an enabled page), "pick" (no camera chosen, or it was
    /// removed), "starting", "no-ffmpeg", "unreachable", "login", "no-picture", "busy" (too many cameras at once).
    /// </summary>
    public (int Status, byte[]? Jpeg, string State) Frame(string? deviceId, string? widgetKey)
    {
        var cfg = _config.Get();
        if (PanelWidgets.Find(cfg, deviceId, widgetKey, WidgetType) is not { } widget) return (404, null, "unknown");
        var id = widget.Str("camera");
        if (string.IsNullOrEmpty(id) || cfg.Cameras.Sources.FirstOrDefault(s => s.Id == id) is not { } source) return (404, null, "pick");
        var now = _now();
        lock (_gate)
        {
            if (_disposed) return (503, null, "starting");
            if (!_feeds.TryGetValue(id, out var feed))
            {
                if (_feeds.Count >= MaxRunning) return (503, null, "busy");
                _feeds[id] = feed = new Feed(source);
            }
            feed.WantedUntil = now + KeepMs;
            if (feed.Run is null && now >= feed.RetryAt) StartLocked(feed, now);
            if (feed.Frame is { } jpeg && now - feed.FrameAt <= FreshMs) return (200, jpeg, "live");
            return (503, null, feed.State);
        }
    }

    /// <summary>The settings were saved: a camera whose address changed starts again, a removed one stops.</summary>
    public void SettingsSaved()
    {
        var sources = _config.Get().Cameras.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        List<ICameraRun> stop = [];
        lock (_gate)
        {
            foreach (var (id, feed) in _feeds.ToList())
            {
                if (sources.TryGetValue(id, out var now) && now.Url == feed.Source.Url && now.Transport == feed.Source.Transport) { feed.Source = now; continue; }
                if (feed.Run is not null) stop.Add(feed.Run);
                _feeds.Remove(id);
            }
        }
        foreach (var run in stop) run.Dispose();
    }

    /// <summary>Once a second: stops cameras nobody asks for, restarts a stalled or ended ffmpeg (with a growing pause).</summary>
    internal void Tick()
    {
        var now = _now();
        List<ICameraRun> stop = [];
        lock (_gate)
        {
            foreach (var (id, feed) in _feeds.ToList())
            {
                if (now >= feed.WantedUntil)
                {
                    if (feed.Run is not null) { stop.Add(feed.Run); _log.LogInformation("camera {Name}: stopped (not on screen)", feed.Source.Name); }
                    _feeds.Remove(id);
                    continue;
                }
                if (feed.Run is { } run)
                {
                    var ended = run.HasEnded;
                    var stalled = now - (feed.GotFrame ? feed.FrameAt : feed.StartedAt) > StallMs;
                    if (!ended && !stalled) continue;
                    // ffmpeg can wait on an unreachable camera for a long time without a word (and a timeout option
                    // isn't usable: its meaning changed between ffmpeg versions), so silence before any picture counts
                    // as unreachable
                    feed.State = run.LastError is { } said ? Classify(said) : feed.GotFrame ? "no-picture" : "unreachable";
                    _log.LogInformation("camera {Name}: {What} ({Detail})", feed.Source.Name, ended ? "ffmpeg ended" : "no picture for a while",
                        Mask(run.LastError ?? "", feed.Source.Url));
                    stop.Add(run);
                    feed.Run = null;
                    feed.Generation++;
                    feed.Frame = null;
                    feed.Backoff = Math.Min(MaxBackoffMs, feed.Backoff == 0 ? 2000 : feed.Backoff * 2);
                    feed.RetryAt = now + feed.Backoff;
                }
                else if (now >= feed.RetryAt) StartLocked(feed, now);
            }
        }
        foreach (var r in stop) r.Dispose();
    }

    private void StartLocked(Feed feed, long now)
    {
        var ffmpeg = _findFfmpeg();
        if (ffmpeg is null) { feed.State = "no-ffmpeg"; feed.RetryAt = now + 10_000; return; }
        try
        {
            feed.StartedAt = now;
            if (feed.State != "no-picture" && feed.State != "unreachable" && feed.State != "login") feed.State = "starting";
            var f = feed;
            var generation = ++feed.Generation;
            feed.GotFrame = false;
            feed.Run = _launch(ffmpeg, feed.Source, jpeg =>
            {
                lock (_gate)
                {
                    if (f.Generation != generation || f.Run is null) return; // from an ffmpeg that was stopped since
                    f.Frame = jpeg; f.FrameAt = _now(); f.GotFrame = true; f.State = "live"; f.Backoff = 0;
                }
            });
            _log.LogInformation("camera {Name}: started", feed.Source.Name);
        }
        catch (Exception ex)
        {
            feed.Run = null;
            feed.State = "no-ffmpeg";
            feed.RetryAt = now + 10_000;
            _log.LogWarning("camera {Name}: cannot start ffmpeg: {Msg}", feed.Source.Name, ex.Message);
        }
    }

    /// <summary>ffmpeg's last words, as one of the states the phone shows; the text itself never leaves this class.</summary>
    internal static string Classify(string? error)
    {
        var e = (error ?? "").ToLowerInvariant();
        // not a bare "401": ffmpeg's text has pointers and the address in it ([rtsp @ 0x55d4401a2b0c], :5401, /stream401)
        if (e.Contains("unauthorized") || e.Contains("authorization failed") || e.Contains("403 forbidden")) return "login";
        if (e.Contains("connection refused") || e.Contains("timed out") || e.Contains("no route to host") || e.Contains("unreachable")
            || e.Contains("could not resolve") || e.Contains("name or service not known") || e.Contains("no such host") || e.Contains("failed to resolve")
            || e.Contains("connection reset") || e.Contains("i/o error") || e.Contains("error number -138")) return "unreachable";
        return "no-picture";
    }

    /// <summary>A line for the log with the address and any password in it hidden.</summary>
    internal static string Mask(string text, string url)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!string.IsNullOrEmpty(url)) text = text.Replace(url, "<camera>", StringComparison.OrdinalIgnoreCase);
        // secrets first: a host that happens to be a word like "token" mustn't break "token=" before it is hidden
        text = UserInfo().Replace(text, "://***@");
        text = SecretParam().Replace(text, m => m.Groups[1].Value + "***");
        // ffmpeg also names the host on its own ("Connection to tcp://10.0.0.9:554 failed"); a very short host could
        // match inside other words, so only from 3 characters on
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length >= 3) text = text.Replace(u.Host, "<host>", StringComparison.OrdinalIgnoreCase);
        return text.Length > 300 ? text[..300] : text;
    }

    [GeneratedRegex(@"://[^/\s@?#]*@")]
    private static partial Regex UserInfo();
    [GeneratedRegex(@"((?:pass(?:word)?|pwd|token|key|auth|secret)=)[^&\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParam();

    /// <summary>The ffmpeg to run: the path from the settings if it exists, otherwise the first one in PATH.</summary>
    internal static string? FindFfmpeg(string? configured)
    {
        var p = (configured ?? "").Trim().Trim('"');
        if (p.Length > 0) return File.Exists(p) ? p : null;
        return ToolProcess.Find("ffmpeg");
    }

    /// <summary>Whether an address may be handed to ffmpeg: only RTSP, so a pasted file:, concat: or http: input is refused.</summary>
    internal static bool ValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "rtsp" || u.Scheme == "rtsps") && !string.IsNullOrEmpty(u.Host);

    public void Dispose()
    {
        List<ICameraRun> stop;
        lock (_gate)
        {
            _disposed = true;
            stop = _feeds.Values.Select(f => f.Run).OfType<ICameraRun>().ToList();
            _feeds.Clear();
        }
        _timer.Dispose();
        foreach (var r in stop) r.Dispose();
    }
}

/// <summary>A running camera decoder (ffmpeg, or a fake in the tests).</summary>
internal interface ICameraRun : IDisposable
{
    bool HasEnded { get; }
    /// <summary>The last thing it reported (raw: may contain the address), or null.</summary>
    string? LastError { get; }
}

/// <summary>Splits a byte stream of back-to-back JPEGs (ffmpeg's image2pipe output) into single images.</summary>
internal sealed class JpegSplitter(Action<byte[]> onFrame, int maxBytes = JpegSplitter.DefaultMax)
{
    public const int DefaultMax = 4 * 1024 * 1024;
    private byte[] _buf = new byte[Math.Min(64 * 1024, maxBytes)];
    private int _len;

    public void Push(ReadOnlySpan<byte> data)
    {
        // an image that never ends within the limit: start over (and a single read larger than the limit is skipped)
        if (_len + data.Length > maxBytes) { _len = 0; if (data.Length > maxBytes) return; }
        if (_len + data.Length > _buf.Length) Array.Resize(ref _buf, Math.Min(maxBytes, Math.Max(_buf.Length * 2, _len + data.Length)));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var span = _buf.AsSpan(0, _len);
            var start = span.IndexOf([(byte)0xFF, (byte)0xD8]);
            if (start < 0) { _len = _len > 0 && _buf[_len - 1] == 0xFF ? Keep(_len - 1) : 0; return; }
            if (start > 0) { _len = Keep(start); continue; }
            // inside a JPEG, 0xFF in the data is followed by 0x00, so the first FF D9 after the start ends the image
            var end = span[2..].IndexOf([(byte)0xFF, (byte)0xD9]);
            if (end < 0) return;
            var size = end + 4;
            onFrame(span[..size].ToArray());
            _len = Keep(size);
        }
    }

    private int Keep(int from)
    {
        var rest = _len - from;
        Buffer.BlockCopy(_buf, from, _buf, 0, rest);
        return rest;
    }
}

/// <summary>ffmpeg pulling one camera: RTSP in, a couple of JPEG frames a second on stdout, scaled to at most 1280 wide.</summary>
internal sealed class FfmpegRun : ICameraRun
{
    private readonly Process _p;
    private readonly Queue<string> _errors = new();
    private volatile bool _disposed;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;

    private FfmpegRun(Process p) => _p = p;

    /// <summary>Only once both pipes are read to the end, so the last error line is in before the run counts as over.</summary>
    public bool HasEnded => _disposed || (_stdout.IsCompleted && _stderr.IsCompleted);
    public string? LastError { get { lock (_errors) return _errors.Count == 0 ? null : string.Join(" | ", _errors); } }

    public static FfmpegRun Start(string ffmpeg, CameraSource source, Action<byte[]> onFrame)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            UseShellExecute = false, CreateNoWindow = true,
        };
        // rtsps is TLS, which needs TCP
        var transport = source.Transport == "udp" && !source.Url.StartsWith("rtsps:", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-rtsp_transport", transport,
                     "-i", source.Url, "-an", "-sn", "-dn", "-vf", $"fps={CameraFeeds.Fps},scale='min(1280,iw)':-2",
                     "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "6", "pipe:1" })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        if (OperatingSystem.IsWindows()) JobObject.Track(p); // ends with the agent, even if the agent is killed
        var run = new FfmpegRun(p);
        run._stdout = Task.Run(async () =>
        {
            var split = new JpegSplitter(onFrame);
            var buf = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await p.StandardOutput.BaseStream.ReadAsync(buf)) > 0) split.Push(buf.AsSpan(0, n));
            }
            catch { }
        });
        // drained all the time: ffmpeg blocks once a full pipe isn't read
        run._stderr = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    // kept whole (an address is at most 2048 characters): cutting it before Mask could leave half a password
                    lock (run._errors) { run._errors.Enqueue(line.Length > 8192 ? line[..8192] : line); while (run._errors.Count > 3) run._errors.Dequeue(); }
                }
            }
            catch { }
        });
        return run;
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
        _p.Dispose();
    }
}
