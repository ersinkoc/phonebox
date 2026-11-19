using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The "state" frame of the live stream, built and serialized once for every phone that asks within a moment of each
/// other (the streams all wake on the same second, see Program.cs) instead of once per connection. A frame is reused only
/// while it is younger than <paramref name="maxAgeMs"/> AND no panel event was emitted and nothing poked
/// <see cref="StateSignal"/> since it was built: the frame carries the event counter, and a frame older than an event the
/// phone already got would look like an agent restart; a poke means something just changed and the frame is out of date.
/// The lifetime is short on purpose: the media position's age (<see cref="MediaSnapshot.AgeMs"/>) is fixed when the frame is
/// serialized, so a reused frame understates it by how long it has waited. Streams woken together are served within a few
/// milliseconds; 100 ms keeps the error well under the 0.3 s the phone ignores.
/// </summary>
internal sealed class StateFrameCache(Func<object> build, JsonSerializerOptions options, long maxAgeMs = 100, Func<long>? clock = null)
{
    private readonly Func<long> _now = clock ?? (() => Environment.TickCount64);
    private readonly object _gate = new();
    private string? _frame;
    private long _builtAt;
    private long _builtSeq = -1;
    private long _builtSignal = -1;
    public int Builds { get; private set; }

    /// <summary>The ready-to-write SSE block (<c>event: state</c> + data) for the state as of now.</summary>
    public string Get(long eventSeq, long signal = 0)
    {
        lock (_gate)
        {
            var now = _now();
            if (_frame is null || eventSeq != _builtSeq || signal != _builtSignal || now - _builtAt >= maxAgeMs)
            {
                _frame = $"event: state\ndata: {JsonSerializer.Serialize(build(), options)}\n\n";
                _builtAt = now;
                _builtSeq = eventSeq;
                _builtSignal = signal;
                Builds++;
            }
            return _frame;
        }
    }
}
