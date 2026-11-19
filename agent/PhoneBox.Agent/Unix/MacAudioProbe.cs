using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Reads the currently selected macOS output. DDC is only used when that output is a matching display.</summary>
internal static class MacAudioProbe
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Global = 0x676c6f62; // 'glob'
    private const uint Output = 0x6f757470; // 'outp'
    private const uint DefaultOutput = 0x644f7574; // 'dOut'
    private const uint Name = 0x6c6e616d; // 'lnam'
    private const uint Volume = 0x766f6c6d; // 'volm'
    private const uint Mute = 0x6d757465; // 'mute'
    private const uint VirtualMain = 0x766d7663; // 'vmvc': the volume System Settings and AppleScript's "output volume" show

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetUInt(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out uint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetFloat(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out float value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetPointer(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out nint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int SetFloat(uint id, ref Address address, uint qualifierSize, nint qualifier, uint size, ref float value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int SetUInt(uint id, ref Address address, uint qualifierSize, nint qualifier, uint size, ref uint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectIsPropertySettable")]
    private static extern int IsSettable(uint id, ref Address address, out byte settable);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectHasProperty")]
    private static extern byte HasProperty(uint id, ref Address address);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    private static uint? UInt(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(uint);
        return GetUInt(id, ref address, 0, 0, ref size, out var value) == 0 ? value : null;
    }

    private static float? Float(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(float);
        return GetFloat(id, ref address, 0, 0, ref size, out var value) == 0 && float.IsFinite(value) ? value : null;
    }

    private static bool WriteFloat(uint device, uint selector, uint element, float value)
    {
        var address = new Address(selector, Output, element);
        return SetFloat(device, ref address, 0, 0, sizeof(float), ref value) == 0;
    }

    private static bool WriteUInt(uint device, uint selector, uint element, uint value)
    {
        var address = new Address(selector, Output, element);
        return SetUInt(device, ref address, 0, 0, sizeof(uint), ref value) == 0;
    }

    /// <summary>
    /// Sets the selected output's volume (0-100) and unmutes it, in-process. The same as AppleScript's
    /// "set volume output volume N without output muted" but without starting osascript, which takes seconds on a busy
    /// computer. Returns the level the output ended on (null when it has no controllable volume: the caller falls back to
    /// osascript), which is the request unless channel scaling stopped short.
    /// </summary>
    public static int? SetVolume(int level)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var device = UInt(1, DefaultOutput, Global, 0);
            if (device is null or 0) return null;
            var value = Math.Clamp(level, 0, 100) / 100f;
            // the virtual main volume first (what the system itself moves), then the main element, then the channels
            var ok = WriteFloat(device.Value, VirtualMain, 0, value) || WriteFloat(device.Value, Volume, 0, value);
            var reached = Math.Clamp(level, 0, 100); // what the output will report: the request, unless channels clip below
            if (!ok)
            {
                // an output with only per-channel volumes: each channel moves by the same factor, so the balance the user set
                // (left 0.2, right 0.8) survives; a channel that does not exist (the second of a mono output) is skipped, but
                // one that exists and refuses the write makes the whole set fail, so the caller's fallback runs
                var current = new[] { Float(device.Value, Volume, Output, 1), Float(device.Value, Volume, Output, 2) };
                var targets = ScaleChannels(current.Where(c => c is not null).Select(c => c!.Value).ToArray(), value);
                var next = 0;
                bool? Channel(uint element) => current[element - 1] is null ? null : WriteFloat(device.Value, Volume, element, targets[next++]);
                ok = AllWritten([Channel(1), Channel(2)]);
                reached = EffectiveLevel(targets); // a channel that stopped at 1 leaves the average below the request
            }
            // an output that stays muted is not "set": see Unmuted
            if (ok) ok = Unmuted(UInt(device.Value, Mute, Output, 0), Exists(device.Value, Mute, 0), () => WriteUInt(device.Value, Mute, 0, 0));
            return ok ? reached : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether the output ends up unmuted: not muted already, or muted and cleared. A mute that cannot be read is only fine
    /// when the output has no mute property at all (<paramref name="exists"/> false). If it has one (even a read-only one
    /// that cannot be read), or the answer is not known (null), the state is unknown and the caller falls back to osascript.
    /// </summary>
    internal static bool Unmuted(uint? muted, bool? exists, Func<bool> clear) => muted is { } m ? m == 0 || clear() : exists == false;

    /// <summary>
    /// The levels to write to channels that are at <paramref name="current"/> so their average becomes
    /// <paramref name="target"/>. Each channel first moves by the same factor, which keeps the balance; a channel that would
    /// pass 1 stops there and what it could not take goes to the others, so the level is reached whenever it can be.
    /// Reaching the requested level comes before keeping the balance exactly: asking for 90 % should give about 90 %.
    /// </summary>
    internal static float[] ScaleChannels(float[] current, float target)
    {
        if (current.Length == 0) return [];
        var average = current.Average();
        if (average <= 0) return current.Select(_ => target).ToArray(); // silent: no balance to keep
        var factor = target / average;
        var levels = current.Select(c => Math.Clamp(c * factor, 0f, 1f)).ToArray();
        for (var round = 0; round < levels.Length; round++)
        {
            var missing = target * levels.Length - levels.Sum();
            var open = levels.Count(l => l < 1f);
            if (missing <= 1e-6f || open == 0) break;
            for (var i = 0; i < levels.Length; i++) if (levels[i] < 1f) levels[i] = Math.Min(1f, levels[i] + missing / open);
        }
        return levels;
    }

    /// <summary>The level an output reports for these channel values: their average, in percent (what SelectedOutput reads back).</summary>
    internal static int EffectiveLevel(float[] written) => written.Length == 0 ? 0 : (int)Math.Round(Math.Clamp(written.Average(), 0f, 1f) * 100);

    /// <summary>Whether the output has the property at all (it can exist and still be read-only or unreadable).</summary>
    private static bool Exists(uint device, uint selector, uint element)
    {
        var address = new Address(selector, Output, element);
        return HasProperty(device, ref address) != 0;
    }

    /// <summary>Channel writes, in order: null for a channel that does not exist. Done when at least one exists and all written.</summary>
    internal static bool AllWritten(IEnumerable<bool?> results)
    {
        var existing = results.Where(r => r is not null).ToArray();
        return existing.Length > 0 && existing.All(r => r == true);
    }

    /// <summary>Flips the selected output's mute in-process; false when it cannot be read or written.</summary>
    public static bool ToggleMute()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var device = UInt(1, DefaultOutput, Global, 0);
            if (device is null or 0 || UInt(device.Value, Mute, Output, 0) is not { } muted) return false;
            return WriteUInt(device.Value, Mute, 0, muted == 0 ? 1u : 0u);
        }
        catch { return false; }
    }

    public static bool CanControl(uint device)
    {
        bool Writable(uint selector, uint element)
        {
            var address = new Address(selector, Output, element);
            return IsSettable(device, ref address, out var writable) == 0 && writable != 0;
        }
        return (Writable(Volume, 0) || Writable(Volume, 1) || Writable(Volume, 2)) && Writable(Mute, 0);
    }

    private static string? DeviceName(uint device)
    {
        var address = new Address(Name, Global, 0);
        uint size = (uint)IntPtr.Size;
        if (GetPointer(device, ref address, 0, 0, ref size, out var cfString) != 0 || cfString == 0) return null;
        try
        {
            var buffer = new byte[512];
            if (!CFStringGetCString(cfString, buffer, buffer.Length, 0x08000100)) return null;
            var length = Array.IndexOf(buffer, (byte)0);
            return length >= 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }
        finally { CFRelease(cfString); }
    }

    public static (uint Id, string Name, int? Volume, bool? Muted)? SelectedOutput()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var device = UInt(1, DefaultOutput, Global, 0);
            if (device is null or 0) return null;
            var scalar = Float(device.Value, VirtualMain, Output, 0) ?? Float(device.Value, Volume, Output, 0);
            if (scalar is null)
            {
                var channels = new[] { Float(device.Value, Volume, Output, 1), Float(device.Value, Volume, Output, 2) }
                    .Where(value => value is not null).Select(value => value!.Value).ToArray();
                if (channels.Length > 0) scalar = channels.Average();
            }
            var muted = UInt(device.Value, Mute, Output, 0);
            return (device.Value, DeviceName(device.Value) ?? "", scalar is { } s ? (int)Math.Round(Math.Clamp(s, 0, 1) * 100) : null,
                muted is { } m ? m != 0 : null);
        }
        catch { return null; }
    }

    private static uint _displayDevice;
    private static string _displayName = "";
    private static int? _displayIndex;
    private static long _displayAt;

    public static int? DisplayVolume(uint device, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Sh.Which("m1ddc") is null) return null;
        if (_displayDevice != device || _displayName != name || Environment.TickCount64 - _displayAt > 30_000)
        {
            _displayDevice = device;
            _displayName = name;
            _displayAt = Environment.TickCount64;
            _displayIndex = null;
            var list = Sh.RunWithTimeout(5000, "m1ddc", "display", "list");
            if (list is not null)
            {
                foreach (var line in list.Split('\n'))
                {
                    var match = Regex.Match(line, @"^\[(\d+)\] (.+?) \(");
                    if (match.Success && string.Equals(match.Groups[2].Value.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(match.Groups[1].Value, out var index)) { _displayIndex = index; break; }
                }
            }
        }
        if (_displayIndex is not { } display) return null;
        var current = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "get", "volume")?.Trim();
        var maximum = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "max", "volume")?.Trim();
        return int.TryParse(current, out var value) && int.TryParse(maximum, out var max) && max > 0
            ? (int)Math.Round(Math.Clamp(value / (double)max, 0, 1) * 100) : null;
    }
}
