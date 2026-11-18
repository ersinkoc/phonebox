namespace PhoneBox.Agent;

/// <summary>Refreshes delayed native thumbnails without invalidating clients for identical image bytes.</summary>
internal sealed class MediaArtworkCache(Func<long>? clock = null)
{
    private readonly Lock _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private string? _track;
    private long _revision, _generation, _started, _lastRead;
    private bool _reading, _hasRead;
    private AlbumArt? _current;
    private int _id;
    public AlbumArt? Current { get { lock (_gate) return _current; } }
    public int Id { get { lock (_gate) return _id; } }

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
                _started = now; _hasRead = false; _reading = false;
            }
            // Chromium may publish the previous video's thumbnail before the new one arrives.
            var interval = now - _started <= 8000 ? 1000 : 30000;
            if (read is null || _reading || (_hasRead && now - _lastRead < interval)) return;
            generation = _generation; sampleTime = now; _reading = true;
        }
        try
        {
            var art = await read(ct);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (generation != _generation || art is not { Bytes.Length: > 0 }) return;
                _hasRead = true; _lastRead = sampleTime;
                if (_current is null || _current.Mime != art.Mime || !_current.Bytes.AsSpan().SequenceEqual(art.Bytes))
                { _current = art; _id++; }
            }
        }
        finally { lock (_gate) { if (generation == _generation) _reading = false; } }
    }
    public void Clear()
    {
        lock (_gate) { _generation++; _track = null; _current = null; _reading = false; _hasRead = false; }
    }
}
