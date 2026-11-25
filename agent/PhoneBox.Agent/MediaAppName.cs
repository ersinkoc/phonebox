namespace PhoneBox.Agent;

/// <summary>
/// The player name Now playing shows (and the lyrics widget's per-player timing offset matches on). Kept apart from
/// the Windows MediaMonitor so it compiles, and is tested, on every platform.
/// </summary>
internal static class MediaAppName
{
    /// <summary>A friendly name for well-known players; anything else is the app's ID without its last dotted part.</summary>
    internal static string Friendly(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("applemusic")) return "Apple Music"; // AppleInc.AppleMusicWin_…!App, otherwise reported as "AppleInc"
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox") || l.Contains("308046b0af4a39cb")) return "Firefox";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("zune") || l.Contains("media")) return "Media Player";
        if (l.Contains("vlc")) return "VLC";
        return Path.GetFileNameWithoutExtension(id.Split('!')[0]);
    }
}
