namespace PhoneBox.Agent;

/// <summary>
/// "Something the phone shows has just changed" (a volume or media control, a new track, a pause): the live streams send
/// a state at once instead of at the next whole second. Slow-moving numbers (CPU, network) keep the 1 s clock.
/// </summary>
public static class StateSignal
{
    private static long _version;

    /// <summary>Counts pokes; part of the key of the shared state frame, so a poke always means a rebuilt frame.</summary>
    public static long Version => Interlocked.Read(ref _version);

    public static event Action? Changed;

    public static void Poke()
    {
        Interlocked.Increment(ref _version);
        // one subscriber's failure (a stream that just closed) must not stop the others or the code that changed something
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action>())
            try { h(); } catch { }
    }

    /// <summary>Whether a media reading differs from the last one in a way the phone should see now (not just time passing).</summary>
    public static bool MediaChanged(MediaSnapshot? before, MediaSnapshot? after)
    {
        if (before is null || after is null) return before is not null || after is not null;
        if (before.Title != after.Title || before.Artist != after.Artist || before.App != after.App
            || before.Playing != after.Playing || before.DurMs != after.DurMs || before.ArtId != after.ArtId) return true;
        // a seek: the position moved by clearly more than the second or two of playback between two readings
        return Math.Abs(after.PosMs - before.PosMs) > 3000;
    }
}
