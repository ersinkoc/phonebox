namespace PhoneBox.Agent;

/// <summary>
/// Explains why a phone that still talks to the agent (over Wi-Fi) is missing from adb, from the phone's own view of
/// its USB link (sent with its battery report). The usual cause is a cable or port that carries power but no data.
/// </summary>
public static class UsbDoctor
{
    /// <summary>A report older than this says nothing about the link any more.</summary>
    public const long FreshMs = 2 * 60_000;

    /// <summary>A sentence for the settings page and the log, or null when there is nothing to explain.</summary>
    public static string? Diagnose(PhoneBatteryReport? r, bool adbSeesPhone, long now)
    {
        if (adbSeesPhone || r is null || now - r.At is < 0 or > FreshMs) return null;
        if (r.Usb is null) return null; // an older app that does not report its USB link
        var usb = r.Usb.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var connected = usb.Contains("connected");
        if (connected && usb.Contains("adb"))
            return "The phone says it is connected over USB with debugging on, but adb does not see it. "
                + "Try another USB port (straight on the computer, not a hub), or restart adb (adb kill-server).";
        if (connected)
            return "The phone is connected over USB but USB debugging is off. Turn it on in the phone's Developer options.";
        if (r.Plugged && r.Source != "wireless")
            return "The phone is charging but has no USB data connection: the cable or the port only carries power. "
                + "Try another cable or port; until then the panel runs over Wi-Fi.";
        return null;
    }
}
