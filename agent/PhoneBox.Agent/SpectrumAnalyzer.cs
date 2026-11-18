// WasapiLoopbackCapture is marked obsolete in NAudio 3 in favour of a builder, but system-wide loopback of the
// default output device is exactly what it does, with the simplest lifecycle.
#pragma warning disable CS0618
// _frame is only written by the Windows capture
#pragma warning disable CS0649
#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
#endif

namespace PhoneBox.Agent;

/// <summary>
/// Live audio spectrum of what the computer is playing, for the "Spectrum bars" widget. Windows only (WASAPI loopback
/// of the default output device). Capture runs only while a panel shows the widget: every <see cref="Want"/> keeps it
/// going for a few seconds, and only when an enabled page has the widget. The audio never leaves this class; the phone
/// gets 48 band levels per frame. Elsewhere it stays inactive.
/// </summary>
public sealed class SpectrumAnalyzer(ConfigStore config, ILogger<SpectrumAnalyzer> log) : IDisposable
{
    public const string WidgetType = "spectrum";
    /// <summary>Bands per frame; the widget merges them into its own number of bars.</summary>
    public const int Bands = 48;
    /// <summary>Frames per second sent on the live stream.</summary>
    public const int Fps = 30;
    /// <summary>Samples per analysis (a power of two): about 43 ms at 48 kHz, 23 Hz per bin.</summary>
    public const int FftSize = 2048;
    private const long KeepMs = 5000;
    private const double MinHz = 40, MaxHz = 16000;
    private const double FloorDb = -66, TopDb = -6; // shown range after the slope below
    private const double SlopeDbPerOctave = 3;      // music falls off towards the treble; lift it so the bars look even

    private long _wantedUntil;
    private volatile int[]? _frame;

    private static long Now => Environment.TickCount64;

    /// <summary>True while a panel wants frames and capture is running.</summary>
    public bool Active => Now < Interlocked.Read(ref _wantedUntil) && Running;

    /// <summary>The latest bands, 0–100 each, or null before the first one.</summary>
    public int[]? Frame() => _frame;

    public bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    /// <summary>
    /// A panel shows a visualizer: start capture (if needed) and keep it going for a few seconds. Refused (false) when no
    /// enabled page has the widget, so removing or hiding the widget turns capture off.
    /// </summary>
    public bool Want()
    {
        if (!Shown()) return false;
        Interlocked.Exchange(ref _wantedUntil, Now + KeepMs);
        Start();
        return true;
    }

    /// <summary>
    /// The band levels (0–100) of the last <see cref="FftSize"/> mono samples: Hann window, FFT, the loudest bin in each
    /// of <see cref="Bands"/> log-spaced bands from 40 Hz to 16 kHz, in dB with a treble lift, mapped to 0–100.
    /// </summary>
    internal static int[] Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length != FftSize) throw new ArgumentException($"needs {FftSize} samples", nameof(samples));
        var re = new double[FftSize];
        var im = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            var sample = samples[i];
            re[i] = float.IsFinite(sample) ? sample * Window[i] : 0;
        }
        Fft(re, im);
        var binHz = (double)sampleRate / FftSize;
        var top = Math.Min(MaxHz, sampleRate / 2.0 * 0.95);
        var bands = new int[Bands];
        for (var b = 0; b < Bands; b++)
        {
            var lo = MinHz * Math.Pow(top / MinHz, (double)b / Bands);
            var hi = MinHz * Math.Pow(top / MinHz, (double)(b + 1) / Bands);
            var i0 = Math.Max(1, (int)Math.Floor(lo / binHz));
            var i1 = Math.Max(i0, Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(hi / binHz) - 1));
            double peak = 0;
            for (var i = i0; i <= i1; i++) peak = Math.Max(peak, Math.Sqrt(re[i] * re[i] + im[i] * im[i]));
            // amplitude of a sine in this band: |X| * 2 / sum(window), and sum(Hann) = N / 2
            var amp = peak * 4 / FftSize;
            var db = 20 * Math.Log10(amp + 1e-9) + SlopeDbPerOctave * Math.Log2(Math.Sqrt(lo * hi) / 1000);
            bands[b] = (int)Math.Round(Math.Clamp((db - FloorDb) / (TopDb - FloorDb), 0, 1) * 100);
        }
        return bands;
    }

    private static readonly double[] Window = Hann(FftSize);

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }

    /// <summary>In-place radix-2 FFT; the length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

#if WINDOWS
    private readonly Lock _gate = new();
    private readonly float[] _ring = new float[FftSize * 2];
    private readonly float[] _last = new float[FftSize];
    private int _ringPos;
    private long _lastAudio;
    private int _ticking, _tickCount;
    private WasapiLoopbackCapture? _capture;
    // disposes the capture and the endpoint it was opened on (WasapiCapture.Dispose() does not release that), together
    // and once: from RecordingStopped, or from Stop when StopRecording throws
    private Action? _release;
    private string? _deviceId; // the output device being captured, to follow a change of the default output
    private System.Threading.Timer? _timer;
    private int _sampleRate = 48000;

    private bool Running { get { lock (_gate) return _capture is not null; } }

    /// <summary>The id of the default output device (the one loopback captures), or null when there is none.</summary>
    private static string? DefaultOutputId()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            // each lookup returns its own wrapper with a COM reference, and this runs once a second
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch { return null; }
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            MMDevice? device = null;
            WasapiLoopbackCapture? capture = null;
            try
            {
                using (var devices = new MMDeviceEnumerator())
                    device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                capture = new WasapiLoopbackCapture(device);
                // read before StartRecording: once the capture thread runs, a throw here would dispose it under _gate,
                // and its RecordingStopped handler waits for _gate
                var deviceId = device.ID;
                var fmt = capture.WaveFormat;
                _sampleRate = fmt.SampleRate;
                var (started, startedOn) = (capture, device);
                var released = 0;
                void Release()
                {
                    if (Interlocked.Exchange(ref released, 1) == 1) return;
                    started.Dispose();
                    startedOn.Dispose(); // after the capture: its thread uses the endpoint until it has stopped
                }
                capture.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, fmt);
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null) log.LogInformation("spectrum: capture stopped: {Msg}", e.Exception.Message);
                    lock (_gate)
                    {
                        if (_capture == started) { _capture = null; _release = null; _timer?.Dispose(); _timer = null; }
                    }
                    Release();
                };
                capture.StartRecording();
                _capture = capture;
                _release = Release;
                _deviceId = deviceId;
                _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000 / Fps);
                log.LogInformation("spectrum: capture started ({Rate} Hz, {Bits}-bit, {Ch} ch)", fmt.SampleRate, fmt.BitsPerSample, fmt.Channels);
            }
            catch (Exception ex)
            {
                // a capture that failed to start raises no RecordingStopped, so nothing else would dispose it or the endpoint
                capture?.Dispose();
                device?.Dispose();
                log.LogWarning("spectrum: cannot capture the output device: {Msg}", ex.Message);
            }
        }
    }

    private void Stop(string why = "no visualizer on screen")
    {
        WasapiLoopbackCapture? capture;
        Action? release;
        lock (_gate)
        {
            capture = _capture;
            release = _release;
            _capture = null;
            _release = null;
            _timer?.Dispose();
            _timer = null;
        }
        if (capture is null) return;
        // StopRecording only asks the capture thread to stop; its RecordingStopped disposes the capture and the endpoint
        // once the thread no longer uses them. If it throws, no RecordingStopped may follow, so release them here.
        try { capture.StopRecording(); } catch { release?.Invoke(); }
        _frame = null;
        log.LogInformation("spectrum: capture stopped ({Why})", why);
    }

    /// <summary>Downmixes 32-bit float or 16/32-bit PCM samples to mono in the ring buffer.</summary>
    private void OnData(byte[] buf, int bytes, WaveFormat fmt)
    {
        var ch = Math.Max(1, fmt.Channels);
        var encoding = (fmt is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : fmt).Encoding;
        var isFloat = encoding == WaveFormatEncoding.IeeeFloat;
        var bits = fmt.BitsPerSample;
        var frameBytes = fmt.BlockAlign;
        if (frameBytes <= 0 || (isFloat ? bits != 32 : encoding != WaveFormatEncoding.Pcm || bits is not (16 or 32))) return;
        var sampleBytes = bits / 8;
        lock (_ring)
        {
            for (var off = 0; off + frameBytes <= bytes; off += frameBytes)
            {
                double sum = 0;
                for (var c = 0; c < ch; c++)
                {
                    var sampleOffset = off + c * sampleBytes;
                    sum += isFloat ? BitConverter.ToSingle(buf, sampleOffset)
                        : bits == 32 ? BitConverter.ToInt32(buf, sampleOffset) / 2147483648.0
                        : BitConverter.ToInt16(buf, sampleOffset) / 32768.0;
                }
                _ring[_ringPos] = (float)(sum / ch);
                _ringPos = (_ringPos + 1) % _ring.Length;
            }
        }
        if (bytes > 0) Interlocked.Exchange(ref _lastAudio, Now);
    }

    private void Tick()
    {
        // timer callbacks can overlap when one runs long; they would share _last, so a late tick is skipped
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            // stop when no panel asked for a while, or the widget was removed or its page turned off since (the config
            // walk once a second is enough for that)
            var everySecond = ++_tickCount % Fps == 0;
            if (Now >= Interlocked.Read(ref _wantedUntil) || (everySecond && !Shown())) { Stop(); return; }
            // loopback stays on the device it was opened on: follow the user to a new default output (speakers to
            // headphones), or the bars would sit flat while the new device plays
            if (everySecond && DefaultOutputId() is { } id && id != _deviceId) { Stop("default output changed"); Start(); return; }
            // WASAPI loopback delivers nothing while the computer is silent: show silence instead of the last sound
            if (Now - Interlocked.Read(ref _lastAudio) > 250) { _frame = new int[Bands]; return; }
            lock (_ring)
            {
                var start = (_ringPos - FftSize + _ring.Length) % _ring.Length;
                for (var i = 0; i < FftSize; i++) _last[i] = _ring[(start + i) % _ring.Length];
            }
            _frame = Analyze(_last, _sampleRate);
        }
        catch (Exception ex) { log.LogDebug("spectrum: {Msg}", ex.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    public void Dispose() => Stop();
#else
    private static bool Running => false;
    private void Start() => log.LogDebug("spectrum: capturing the output device needs Windows");
    public void Dispose() { }
#endif
}
