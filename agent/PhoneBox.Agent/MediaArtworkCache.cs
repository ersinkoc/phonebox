namespace PhoneBox.Agent;

/// <summary>Refreshes delayed native thumbnails without invalidating clients for identical image bytes.</summary>
internal sealed class MediaArtworkCache(Func<long>? clock = null)
{
    private readonly Lock _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private string? _track;
    private long _revision, _generation, _started, _lastRead, _failedAt;
    private bool _reading, _hasRead;
    private int _failures;
    private AlbumArt? _current;
    private int _id;
    public AlbumArt? Current { get { lock (_gate) return _current; } }
    public int Id { get { lock (_gate) return _id; } }

    /// <summary>
    /// How long to wait after this many failed reads in a row: the first one is retried at once (a thumbnail that isn't
    /// ready yet), then the pause doubles from a second up to 30 s, so one that is always too large or unreadable isn't
    /// reopened on every poll.
    /// </summary>
    private static long FailureBackoff(int failures) => failures < 2 ? 0 : Math.Min(30_000, 1000L << Math.Min(failures - 2, 5));

    public async Task UpdateAsync(string track, long revision, Func<CancellationToken, Task<AlbumArt?>>? read, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long generation;
        long sampleTime;
        lock (_gate)
        {
            var now = _clock();
            if (_track != track || _revision != revision)
            {
                if (_track != track) _current = null;
                _track = track; _revision = revision; _generation++;
                _started = now; _hasRead = false; _reading = false; _failures = 0;
            }
            // Chromium may publish the previous video's thumbnail before the new one arrives.
            var interval = now - _started <= 8000 ? 1000 : 30000;
            if (read is null || _reading || (_hasRead && now - _lastRead < interval)
                || (_failures > 0 && now - _failedAt < FailureBackoff(_failures))) return;
            generation = _generation; sampleTime = now; _reading = true;
        }
        try
        {
            var art = await read(ct);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (generation != _generation) return;
                if (art is not { Bytes.Length: > 0 }) { Failed(generation, sampleTime); return; }
                _hasRead = true; _lastRead = sampleTime; _failures = 0;
                if (_current is null || _current.Mime != art.Mime || !_current.Bytes.AsSpan().SequenceEqual(art.Bytes))
                { _current = art; _id++; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate) Failed(generation, sampleTime);
            throw;
        }
        finally { lock (_gate) { if (generation == _generation) _reading = false; } }
    }

    /// <summary>Under <see cref="_gate"/>: a read of the current track found nothing usable.</summary>
    private void Failed(long generation, long sampleTime)
    {
        if (generation != _generation) return;
        _failures++; _failedAt = sampleTime;
    }

    public void Clear()
    {
        lock (_gate) { _generation++; _track = null; _current = null; _reading = false; _hasRead = false; _failures = 0; }
    }
}
