using System.Buffers.Binary;
using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Level meters on Linux: one <c>parec</c> per output (its monitor source) and per application stream
/// (<c>--monitor-stream</c>), at 8 kHz, reading only the peak of what plays. A meter starts when the deck asks for its
/// level and stops when nobody has asked for <see cref="IdleMs"/>, so nothing runs while no phone shows a deck widget.
/// The level is the loudest sample of the last second, linear 0–1 like Windows' IAudioMeterInformation, which suits the
/// panel's once-a-second refresh. Microphones are not metered: reading one would record it and light the desktop's
/// "microphone in use" indicator whenever a deck page is on screen.
/// </summary>
internal sealed class PulseMeters : IDisposable
{
    internal const int Rate = 8000, Channels = 2, ChunkFrames = 400; // 50 ms per chunk
    internal const int WindowChunks = 20, WindowMs = 1000; // the level covers the last second
    internal const int IdleMs = 5000, MaxMeters = 32;
    /// <summary>A meter that could not start, or whose capture ended within <see cref="QuickEndMs"/> (a device that is
    /// gone, a refused connection), is not started again for <see cref="RetryMs"/>: listings come every ~400 ms with
    /// ducking on, and up to 32 meters could otherwise be respawned each time.</summary>
    internal const int QuickEndMs = 2000, RetryMs = 5000;
    private readonly Func<IReadOnlyList<string>, Feed?> _start;
    private readonly Func<long> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Meter> _meters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _retryAt = new(StringComparer.Ordinal);
    private readonly Timer _sweep;
    private bool _disposed;

    /// <summary>A running capture: its raw s16le stereo output and how to stop it.</summary>
    internal sealed record Feed(Stream Output, Action Stop);

    internal PulseMeters(Func<IReadOnlyList<string>, Feed?> start, Func<long>? now = null)
    {
        _start = start;
        _now = now ?? (() => Environment.TickCount64);
        _sweep = new Timer(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The meters when this is Linux with parec (same package as pactl), or null.</summary>
    public static PulseMeters? TryCreate() =>
        OperatingSystem.IsLinux() && Sh.Which("parec") is { } path ? new(args => Start(path, args)) : null;

    /// <summary>The arguments of a meter on an output's monitor source.</summary>
    internal static IReadOnlyList<string> OutputArgs(string monitorSource) => [.. Common, "--device=" + monitorSource];
    /// <summary>The arguments of a meter on one application stream (a sink input).</summary>
    internal static IReadOnlyList<string> StreamArgs(string sinkInput) => [.. Common, "--monitor-stream=" + sinkInput];

    private static readonly string[] Common =
    [
        "--raw", "--format=s16le", $"--rate={Rate}", $"--channels={Channels}", "--latency-msec=50",
        "--client-name=PhoneBox", "--stream-name=PhoneBox level meter",
        // When the output or the application stream goes away, PipeWire moves a capture elsewhere, and a closed
        // application's --monitor-stream capture landed on the default source, the microphone (seen on PipeWire 1.6).
        // pacat has no --no-move; this stream property makes PipeWire leave the capture unlinked instead, and
        // Retain() stops it at the next listing (also on PulseAudio, which ignores the property).
        "--property=node.dont-reconnect=true",
    ];

    private static Feed? Start(string path, IReadOnlyList<string> args)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppPaths.NeutralCwd,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            process = Process.Start(psi);
            if (process is null) return null;
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return new(process.StandardOutput.BaseStream, () =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            });
        }
        catch
        {
            // started, then a later line threw: nothing may keep capturing
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The level of a meter, 0–1, starting it if it is not running (its first answer is 0). <paramref name="args"/> is
    /// only called to start one.
    /// </summary>
    public double Level(string key, Func<IReadOnlyList<string>> args)
    {
        lock (_gate)
        {
            if (_disposed) return 0;
            if (!_meters.TryGetValue(key, out var meter))
            {
                var now = _now();
                if (_retryAt.TryGetValue(key, out var retry) && now < retry) return 0;
                _retryAt.Remove(key);
                if (_meters.Count >= MaxMeters) return 0;
                if (_start(args()) is not { } feed) { _retryAt[key] = now + RetryMs; return 0; }
                meter = new Meter(this, key, feed) { StartedAt = now };
                _meters[key] = meter;
                meter.Run();
                _sweep.Change(1000, 1000);
            }
            meter.AskedAt = _now();
            return meter.Level;
        }
    }

    /// <summary>How many meters are running, for the tests.</summary>
    internal int Running { get { lock (_gate) return _meters.Count; } }

    /// <summary>Stops the meters nobody asked for within <see cref="IdleMs"/>; the timer stops with the last one.</summary>
    internal void Sweep()
    {
        List<Meter> idle;
        lock (_gate)
        {
            var now = _now();
            idle = [.. _meters.Values.Where(m => now - m.AskedAt > IdleMs)];
            foreach (var m in idle) _meters.Remove(m.Key);
            if (_meters.Count == 0 && !_disposed) _sweep.Change(Timeout.Infinite, Timeout.Infinite);
        }
        foreach (var m in idle) m.Stop();
    }

    private void Ended(Meter meter)
    {
        lock (_gate)
        {
            if (!_meters.TryGetValue(meter.Key, out var current) || !ReferenceEquals(current, meter)) return;
            _meters.Remove(meter.Key);
            var now = _now();
            if (now - meter.StartedAt < QuickEndMs) _retryAt[meter.Key] = now + RetryMs;
        }
    }

    /// <summary>
    /// Stops every meter whose output or application stream is not in the current listing (unplugged, closed, paused):
    /// what it captures now is no longer what it was started for.
    /// </summary>
    public void Retain(IReadOnlySet<string> keys)
    {
        List<Meter> gone;
        lock (_gate)
        {
            gone = [.. _meters.Values.Where(m => !keys.Contains(m.Key))];
            foreach (var m in gone) _meters.Remove(m.Key);
            foreach (var k in _retryAt.Keys.Where(k => !keys.Contains(k)).ToList()) _retryAt.Remove(k);
        }
        foreach (var m in gone) m.Stop();
    }

    public void Dispose()
    {
        List<Meter> all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = [.. _meters.Values];
            _meters.Clear();
        }
        _sweep.Dispose();
        foreach (var m in all) m.Stop();
    }

    /// <summary>The loudest sample of a chunk of s16le samples, 0–1.</summary>
    internal static double Peak(ReadOnlySpan<byte> chunk)
    {
        var peak = 0;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            int s = BinaryPrimitives.ReadInt16LittleEndian(chunk[i..]);
            if (s < 0) s = -s; // -32768 becomes 32768: full scale
            if (s > peak) peak = s;
        }
        return Math.Min(1.0, peak / 32768.0);
    }

    private sealed class Meter(PulseMeters owner, string key, Feed feed)
    {
        private readonly double[] _window = new double[WindowChunks];
        /// <summary>When each peak was read: a capture that stalls without closing must not hold its last peak.</summary>
        private readonly long[] _readAt = new long[WindowChunks];
        private int _next;
        private int _stopped;
        public string Key => key;
        public long AskedAt;
        public long StartedAt;
        /// <summary>The loudest peak read within the last <see cref="WindowMs"/>.</summary>
        public double Level
        {
            get
            {
                var now = owner._now();
                lock (_window)
                {
                    var level = 0.0;
                    for (var i = 0; i < _window.Length; i++)
                        if (now - _readAt[i] <= WindowMs && _window[i] > level) level = _window[i];
                    return level;
                }
            }
        }

        public void Run() => new Thread(Read) { IsBackground = true, Name = "PhoneBox meter" }.Start();

        private void Read()
        {
            var chunk = new byte[ChunkFrames * Channels * 2];
            try
            {
                while (Volatile.Read(ref _stopped) == 0)
                {
                    var filled = 0;
                    while (filled < chunk.Length)
                    {
                        var n = feed.Output.Read(chunk, filled, chunk.Length - filled);
                        if (n <= 0) return; // parec ended: the stream or the device went away
                        filled += n;
                    }
                    var peak = Peak(chunk);
                    var at = owner._now();
                    lock (_window) { _window[_next] = peak; _readAt[_next] = at; _next = (_next + 1) % _window.Length; }
                }
            }
            catch { }
            finally { owner.Ended(this); Stop(); }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) feed.Stop();
        }
    }
}
