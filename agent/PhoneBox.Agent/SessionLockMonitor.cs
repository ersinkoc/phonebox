using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Follows the current OS console session while the option is enabled.</summary>
public sealed class SessionLockMonitor(ConfigStore cfg, AdbReverse adb, ILogger<SessionLockMonitor> log) : BackgroundService
{
    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (cfg.Get().Display.FollowComputerLock && IsSupported)
                {
                    var available = await adb.SyncSessionAsync(ct);
                    if (!available && !warned)
                    {
                        log.LogWarning("Lock sync is unavailable; start the agent in the GUI session");
                    }
                    warned = !available;
                }
                else
                {
                    await adb.ResetSessionAsync(ct);
                    warned = false;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not follow the session lock");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}

internal static class PlatformSessionLock
{
    public static bool? IsLocked()
    {
        if (OperatingSystem.IsMacOS()) return MacSessionLock.IsLocked();
        if (OperatingSystem.IsLinux()) return LinuxSessionLock.IsLocked();
        return null;
    }
}

internal static class LinuxSessionLock
{
    internal static Func<string, string[], string?> RunTool { get; set; } = Sh.Run;

    public static bool? IsLocked()
    {
        if (!OperatingSystem.IsLinux()) return null;

        // 1. Try loginctl show-session auto -p LockedHint (systemd-logind standard)
        var res = RunTool("loginctl", ["show-session", "auto", "-p", "LockedHint"]);
        var state = ParseLockState(res);
        if (state.HasValue) return state.Value;

        // 2. Try busctl to systemd-logind over D-Bus
        res = RunTool("busctl", ["get-property", "org.freedesktop.login1", "/org/freedesktop/login1/session/auto", "org.freedesktop.login1.Session", "LockedHint"]);
        state = ParseLockState(res);
        if (state.HasValue) return state.Value;

        return null;
    }

    internal static bool? ParseLockState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var trimmed = output.Trim();
        if (trimmed.StartsWith("LockedHint=", StringComparison.OrdinalIgnoreCase))
        {
            var val = trimmed["LockedHint=".Length..].Trim();
            if (val.Equals("yes", StringComparison.OrdinalIgnoreCase) || val.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (val.Equals("no", StringComparison.OrdinalIgnoreCase) || val.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (trimmed.StartsWith("b ", StringComparison.OrdinalIgnoreCase))
        {
            var val = trimmed[2..].Trim();
            if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (val.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }
}

/// <summary>Reads the caller's window-server session. An absent lock key means unlocked.</summary>
internal static class MacSessionLock
{
    public static bool? IsLocked()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var session = CGSessionCopyCurrentDictionary();
        if (session == IntPtr.Zero) return null;
        try
        {
            var onConsole = GetBoolean(session, "kCGSSessionOnConsoleKey");
            var locked = GetBoolean(session, "CGSSessionScreenIsLocked");
            return onConsole == false || locked == true;
        }
        finally { CFRelease(session); }
    }

    private static bool? GetBoolean(IntPtr dictionary, string name)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100); // UTF-8
        if (key == IntPtr.Zero) return null;
        try
        {
            var value = CFDictionaryGetValue(dictionary, key);
            return value == IntPtr.Zero || CFGetTypeID(value) != CFBooleanGetTypeID()
                ? null : CFBooleanGetValue(value) != 0;
        }
        finally { CFRelease(key); }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGSessionCopyCurrentDictionary();
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", CharSet = CharSet.Ansi)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFGetTypeID(IntPtr value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFBooleanGetTypeID();
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern byte CFBooleanGetValue(IntPtr value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
