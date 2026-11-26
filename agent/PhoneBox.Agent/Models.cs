namespace PhoneBox.Agent;

// Data shared by the Windows and the Linux / macOS implementations.

public sealed record MediaSnapshot(string App, string Title, string Artist, string Album, bool Playing,
    long PosMs, long DurMs, int ArtId, bool CanSeek, double PlaybackRate = 1.0)
{
    /// <summary>When the position was read (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>); 0 = not dated.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SampledAt { get; init; }

    /// <summary>
    /// How old PosMs is at the moment this is serialized. The phone adds it to what it counts from the arrival, so a
    /// sample that waited for the next 1 s tick (and one a second old when the monitor read it) is not shown late.
    /// </summary>
    public long AgeMs => SampledAt == 0 ? 0 : Math.Max(0, (long)(System.Diagnostics.Stopwatch.GetElapsedTime(SampledAt).TotalMilliseconds));
}

public static class MediaTimeline
{
    public static double NormalizePlaybackRate(double? rate) =>
        rate is { } r && double.IsFinite(r) && r > 0 ? r : 1.0;

    public static long AdvancePositionMs(long positionMs, double elapsedMs, bool playing, double? playbackRate, long durationMs)
    {
        var pos = (double)positionMs;
        if (playing && double.IsFinite(elapsedMs) && elapsedMs > 0)
            pos += elapsedMs * NormalizePlaybackRate(playbackRate);
        if (double.IsNaN(pos) || pos <= 0) return 0;
        if (durationMs > 0 && pos >= durationMs) return durationMs;
        return pos >= (double)long.MaxValue ? long.MaxValue : (long)Math.Round(pos);
    }
}

public sealed record AlbumArt(byte[] Bytes, string Mime);

public sealed record AudioSnapshot(int Volume, bool Muted, bool CanControl = true);
