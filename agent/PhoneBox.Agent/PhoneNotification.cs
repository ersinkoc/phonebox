namespace PhoneBox.Agent;

/// <summary>One notification the phone's app reports. <paramref name="Category"/> is "call" (an incoming call), "missed_call", or anything else (a message or an app).</summary>
public sealed record PhoneNotification(string Key, string App, string Package, string Title, string Text, string Category, long Time, byte[]? IconPng = null);

/// <summary>The posting app's icon, as the phone sends it (base64 of a small PNG). Anything that is not a small PNG is ignored, never trusted.</summary>
public static class NotificationIcon
{
    public const int MaxBytes = 32 * 1024;
    public const int MaxPixels = 256;

    public static byte[]? TryDecode(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > MaxBytes * 4 / 3 + 8) return null;
        byte[] png;
        try { png = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        return IsSmallPng(png) ? png : null;
    }

    /// <summary>The PNG signature, then an IHDR chunk whose width and height are between 1 and <see cref="MaxPixels"/>.</summary>
    public static bool IsSmallPng(byte[] png)
    {
        if (png.Length < 33 || png.Length > MaxBytes) return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png.AsSpan(0, 8).SequenceEqual(signature)) return false;
        if (!png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width is >= 1 and <= MaxPixels && height is >= 1 and <= MaxPixels;
    }
}

public enum NotifyResult { Queued, Duplicate, Off }
