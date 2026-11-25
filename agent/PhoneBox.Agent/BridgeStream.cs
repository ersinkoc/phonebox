namespace PhoneBox.Agent;

/// <param name="Origin">The device id (X-PhoneBox-Device) of the phone that sent it; null when it came from this computer.</param>
/// <param name="AtMs">When it was published (Unix milliseconds), so a reconnecting phone is given only recent events.</param>
public sealed record BridgeEvent(long Seq, string Name, string Text, string? Origin, long AtMs = 0);

/// <summary>
/// The last few bridge events, in memory only, for the phones' bridge streams to read. It is separate from the panel's
/// PushStore because bridge text must never be kept in data.json, and because the panel's stream may stay paused while a
/// phone's bridge connection stays open.
/// </summary>
public sealed class BridgeStream(TimeProvider? time = null)
{
    private const int MaxEvents = 20;
    /// <summary>
    /// How old an event may be and still be replayed to a phone that reconnects with Last-Event-ID. A Wi-Fi stream reconnects
    /// within seconds, so this covers it; an older copy is more likely to overwrite something the owner has copied on the
    /// phone since than to be wanted.
    /// </summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly LinkedList<(BridgeEvent Event, long PublishedAt)> _events = new();
    private long _seq;

    public long Seq { get { lock (_lock) return _seq; } }

    /// <summary>Raised after every <see cref="Publish"/>, so a stream handler can wake up and send.</summary>
    public event Action? Emitted;

    public BridgeEvent Publish(string name, string text, string? origin)
    {
        BridgeEvent e;
        lock (_lock)
        {
            e = new BridgeEvent(++_seq, name, text, origin, _time.GetUtcNow().ToUnixTimeMilliseconds());
            RemoveExpired();
            _events.AddLast((e, _time.GetTimestamp()));
            while (_events.Count > MaxEvents) _events.RemoveFirst();
        }
        Emitted?.Invoke();
        return e;
    }

    /// <summary>Events after <paramref name="seq"/>, oldest first, leaving out the ones <paramref name="forDevice"/> sent itself.</summary>
    public IReadOnlyList<BridgeEvent> Since(long seq, string? forDevice)
    {
        lock (_lock)
        {
            RemoveExpired();
            return EventsAfter(seq, forDevice);
        }
    }

    /// <summary>
    /// Where a new stream starts and what it is sent first. Without <paramref name="lastEventId"/> it hears only what happens
    /// from now on. With it, the events after that id that are still kept (the last 20), younger than <see cref="ReplayWindow"/>,
    /// and not sent by <paramref name="forDevice"/> itself. An id above the current one comes from before an agent restart (the
    /// numbers start again at 1): it counts as no id, since replaying from it would skip every event of this run.
    /// </summary>
    public (long Start, IReadOnlyList<BridgeEvent> Replay) Resume(long? lastEventId, string? forDevice)
    {
        lock (_lock)
        {
            RemoveExpired();
            if (lastEventId is not { } last || last > _seq) return (_seq, []);
            return (_seq, EventsAfter(last, forDevice));
        }
    }

    internal bool StillQueued(long seq)
    {
        lock (_lock)
        {
            RemoveExpired();
            return _events.Any(entry => entry.Event.Seq == seq);
        }
    }

    internal void PruneExpired()
    {
        lock (_lock) RemoveExpired();
    }

    private IReadOnlyList<BridgeEvent> EventsAfter(long sequence, string? device) => _events.Select(entry => entry.Event)
        .Where(e => e.Seq > sequence && (device is null || e.Origin != device)).ToList();

    private void RemoveExpired()
    {
        var now = _time.GetTimestamp();
        while (_events.First is { } first && _time.GetElapsedTime(first.Value.PublishedAt, now) >= ReplayWindow)
            _events.RemoveFirst();
    }

    /// <summary>Forgets every kept event (the switch was turned off), so no copied text stays in memory. The numbering goes on.</summary>
    public void Clear()
    {
        lock (_lock) _events.Clear();
    }
}
