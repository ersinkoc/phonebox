using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Is something recording from the microphone or the camera? The same facts as the orange and green dots in the
/// menu bar, read from CoreAudio and CoreMediaIO: no recording is made, no permission is needed, and nothing is asked
/// of the apps.
///  - Microphone: macOS 14.2 and later list the processes that are capturing, so the apps are named. Older systems only
///    say that an input device is running; it counts when that device has no output side (a USB or built-in microphone).
///  - Camera: macOS says which camera is running, not which app runs it, so the camera's name stands in for the app.
/// </summary>
internal static class MacPrivacyProbe
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreMediaIO = "/System/Library/Frameworks/CoreMediaIO.framework/CoreMediaIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint Global = 0x676c6f62;          // 'glob'
    private const uint Input = 0x696e7074;           // 'inpt'
    private const uint Devices = 0x64657623;         // 'dev#'
    private const uint ProcessList = 0x70727323;     // 'prs#'
    private const uint ProcessPid = 0x70706964;      // 'ppid'
    private const uint ProcessBundleId = 0x70626964; // 'pbid'
    private const uint ProcessRunningInput = 0x70697269; // 'piri'
    private const uint RunningSomewhere = 0x676f6e65;    // 'gone'
    private const uint StreamConfiguration = 0x736c6179; // 'slay'
    private const uint Name = 0x6c6e616d;            // 'lnam'
    private const uint AudioSystemObject = 1;
    private const uint MediaSystemObject = 1;
    private const uint Utf8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    [DllImport(CoreAudio)] private static extern int AudioObjectGetPropertyDataSize(uint id, ref Address address, uint qualifierSize, nint qualifier, out uint size);
    [DllImport(CoreAudio)] private static extern int AudioObjectGetPropertyData(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, nint data);
    [DllImport(CoreMediaIO)] private static extern int CMIOObjectGetPropertyDataSize(uint id, ref Address address, uint qualifierSize, nint qualifier, out uint size);
    [DllImport(CoreMediaIO)] private static extern int CMIOObjectGetPropertyData(uint id, ref Address address, uint qualifierSize, nint qualifier, uint size, out uint used, nint data);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    public static (List<string> Mic, List<string> Camera) Read() => (Microphone(), Camera());

    private static List<string> Microphone()
    {
        var apps = CapturingProcesses();
        if (apps is not null) return apps;
        var devices = new List<string>();
        foreach (var id in Objects(AudioSystemObject, Devices))
        {
            if (Value(id, RunningSomewhere) != 1 || !HasInputChannels(id) || HasOutputChannels(id)) continue;
            devices.Add("microphone");
            break;
        }
        return devices;
    }

    /// <summary>The apps that are capturing audio right now; null before macOS 14.2, which cannot list them.</summary>
    private static List<string>? CapturingProcesses()
    {
        var address = new Address(ProcessList, Global, 0);
        if (AudioObjectGetPropertyDataSize(AudioSystemObject, ref address, 0, 0, out _) != 0) return null;
        var apps = new List<string>();
        var self = Environment.ProcessId;
        foreach (var id in Objects(AudioSystemObject, ProcessList))
        {
            if (Value(id, ProcessRunningInput) is not > 0) continue;
            var pid = (int?)Value(id, ProcessPid);
            if (pid == self) continue;
            apps.Add(AppName(pid, Text(id, ProcessBundleId)));
        }
        return apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> Camera()
    {
        var cameras = new List<string>();
        var address = new Address(Devices, Global, 0);
        if (CMIOObjectGetPropertyDataSize(MediaSystemObject, ref address, 0, 0, out var size) != 0 || size == 0) return cameras;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CMIOObjectGetPropertyData(MediaSystemObject, ref address, 0, 0, size, out var used, buffer) != 0) return cameras;
            used = Math.Min(used, size); // the ids that fit in what was allocated
            for (var i = 0; i < used / sizeof(uint); i++)
            {
                var id = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
                if (MediaValue(id, RunningSomewhere) is > 0) cameras.Add(MediaText(id, Name) ?? "camera");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return cameras.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The name a rule can match: "zoom.us", not "us.zoom.xos".</summary>
    private static string AppName(int? pid, string? bundleId)
    {
        if (pid is > 0)
        {
            try { using var p = Process.GetProcessById(pid.Value); if (p.ProcessName is { Length: > 0 } name) return name; }
            catch { /* it has just quit */ }
        }
        return bundleId is { Length: > 0 } ? bundleId[(bundleId.LastIndexOf('.') + 1)..] : "microphone";
    }

    private static uint[] Objects(uint owner, uint selector)
    {
        var address = new Address(selector, Global, 0);
        if (AudioObjectGetPropertyDataSize(owner, ref address, 0, 0, out var size) != 0 || size < sizeof(uint)) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(owner, ref address, 0, 0, ref size, buffer) != 0) return [];
            var ids = new uint[size / sizeof(uint)];
            for (var i = 0; i < ids.Length; i++) ids[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            return ids;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint? Value(uint id, uint selector)
    {
        var address = new Address(selector, Global, 0);
        uint size = sizeof(uint);
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try { return AudioObjectGetPropertyData(id, ref address, 0, 0, ref size, buffer) == 0 ? (uint)Marshal.ReadInt32(buffer) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? Text(uint id, uint selector)
    {
        var address = new Address(selector, Global, 0);
        uint size = (uint)nint.Size;
        var buffer = Marshal.AllocHGlobal(nint.Size);
        try
        {
            if (AudioObjectGetPropertyData(id, ref address, 0, 0, ref size, buffer) != 0) return null;
            return CfString(Marshal.ReadIntPtr(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint? MediaValue(uint id, uint selector)
    {
        var address = new Address(selector, Global, 0);
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try { return CMIOObjectGetPropertyData(id, ref address, 0, 0, sizeof(uint), out _, buffer) == 0 ? (uint)Marshal.ReadInt32(buffer) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? MediaText(uint id, uint selector)
    {
        var address = new Address(selector, Global, 0);
        var buffer = Marshal.AllocHGlobal(nint.Size);
        try
        {
            if (CMIOObjectGetPropertyData(id, ref address, 0, 0, (uint)nint.Size, out _, buffer) != 0) return null;
            return CfString(Marshal.ReadIntPtr(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Reads (and releases) a CFString that a property getter returned.</summary>
    private static string? CfString(nint value)
    {
        if (value == 0) return null;
        try
        {
            var bytes = new byte[512];
            return CFStringGetCString(value, bytes, bytes.Length, Utf8) ? Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0)) : null;
        }
        finally { CFRelease(value); }
    }

    private static bool HasInputChannels(uint device) => Channels(device, Input) > 0;

    private static bool HasOutputChannels(uint device) => Channels(device, 0x6f757470 /* 'outp' */) > 0;

    /// <summary>The channels of a device in one direction: the buffers of its AudioBufferList.</summary>
    private static uint Channels(uint device, uint scope)
    {
        var address = new Address(StreamConfiguration, scope, 0);
        if (AudioObjectGetPropertyDataSize(device, ref address, 0, 0, out var size) != 0 || size < sizeof(uint)) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(device, ref address, 0, 0, ref size, buffer) != 0) return 0;
            // { UInt32 count; { UInt32 channels; UInt32 bytes; void* data; } buffers[count] }, the buffers aligned to 8 bytes
            // never trust the count past what the call returned: a driver that reports more buffers than fit would be read out of bounds
            var count = Math.Min((uint)Marshal.ReadInt32(buffer), size >= 8 ? (size - 8) / 16 : 0);
            uint channels = 0;
            for (var i = 0; i < count; i++) channels += (uint)Marshal.ReadInt32(buffer, 8 + i * 16);
            return channels;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
