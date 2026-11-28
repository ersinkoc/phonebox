using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    internal const int CorsairVendorId = 0x1B1C;
    // Recent Corsair wireless devices and receivers speak the "Bragi" protocol on this vendor collection.
    internal const int CorsairUsagePage = 0xFF42;
    internal const int CorsairUsage = 0x0001;
    // The device on the cable, or the receiver itself. Devices paired to a receiver start at 0x09.
    internal const byte CorsairDirectTarget = 0x08;
    internal const byte CorsairCmdGet = 0x02;
    internal const byte CorsairPropBatteryLevel = 0x0F; // tenths of a percent
    internal const byte CorsairPropBatteryState = 0x10; // 1 charging, 2 on battery, 3 full
    internal const byte CorsairPropProductId = 0x12;
    internal const byte CorsairPropConnectedSlots = 0x36; // asked of a receiver: bit N set when 0x08 + N is connected
    internal const byte CorsairStateCharging = 0x01;
    internal const byte CorsairStateFull = 0x03;
    internal const byte CorsairStatusOk = 0x00;

    // A headset behind a receiver takes up to about 190 ms to answer.
    internal const int CorsairReplyTimeoutMs = 300;
    // Overall budget for a Corsair scan. Twice Razer's, so that two devices behind one receiver are both read even when
    // each reply takes almost the whole timeout. WindowsCorsair runs on its own task, so it delays no other battery.
    internal const long CorsairTotalBudgetMs = 3000;
    // How often connected devices are identified again, in case another one was paired to a slot.
    internal const long CorsairReidentifyMs = 60_000;
    // A slot that leaves a request unanswered this many reads in a row is no longer asked.
    internal const int CorsairMaxMisses = 2;

    internal static readonly byte[] CorsairSlotTargets = [0x09, 0x0A, 0x0B, 0x0C, 0x0D];
    private const int CorsairSlotBits = 0x3E; // bits 1 to 5 of the connected bit field, one for each slot target
    // The tested receiver and headset refuse report id 0x00 (ERROR_INVALID_PARAMETER), which stays as a fallback.
    internal static readonly byte[] CorsairReportIds = [0x02, 0x00];
    private static readonly ConcurrentDictionary<string, CorsairInterfaceState> CorsairInterfaces =
        new(StringComparer.OrdinalIgnoreCase);

    // The receivers the devices below pair with, as OpenLinkHub devices.go lists them. One receiver can serve several
    // devices, so receivers have no name or kind of their own.
    private static readonly HashSet<int> CorsairReceivers =
    [
        0x0A3E, 0x0A40, 0x0A42, 0x0A44, 0x0A64, 0x0A6B, 0x0A73, // headset dongles
        0x1B65, 0x1B7F, 0x1B81, 0x1BA6, 0x1BDC, 0x2B00,         // Slipstream receivers
        0x2B07,                                                 // K65 Plus
    ];

    // Only these devices are shown, and only their batteries are read. The key is the product id of the device itself,
    // as reported on its receiver slot or by its USB cable. Only product ids and commercial names come from
    // OpenLinkHub.
    private static readonly Dictionary<int, (string Name, string Kind)> CorsairDevices = new()
    {
        // Headsets (PIDs from OpenLinkHub headsetdongle.go)
        [0x0A3D] = ("Corsair Virtuoso SE", "headphones"),
        [0x0A3F] = ("Corsair Virtuoso SE", "headphones"),
        [0x0A41] = ("Corsair Virtuoso", "headphones"),
        [0x0A43] = ("Corsair Virtuoso", "headphones"),
        [0x0A62] = ("Corsair Virtuoso RGB Wireless XT", "headphones"),
        [0x0A69] = ("Corsair HS80 RGB Wireless", "headphones"),

        // Mice (PIDs from OpenLinkHub slipstream.go)
        [0x1B4C] = ("Corsair Ironclaw RGB Wireless", "mouse"),
        [0x1B5E] = ("Corsair Harpoon Wireless", "mouse"),
        [0x1B7E] = ("Corsair Dark Core RGB Pro SE", "mouse"),
        [0x1B80] = ("Corsair Dark Core RGB Pro", "mouse"),
        [0x1B98] = ("Corsair Sabre RGB Pro Wireless", "mouse"),
        [0x1BB2] = ("Corsair Darkstar RGB Wireless", "mouse"),
        [0x1BB5] = ("Corsair M65 RGB Ultra Wireless", "mouse"),
        [0x1BB8] = ("Corsair Nightsabre Wireless", "mouse"),
        [0x1BD6] = ("Corsair M65 RGB Ultra Wireless", "mouse"),
        [0x1BDB] = ("Corsair Scimitar RGB Elite Wireless", "mouse"),
        [0x1BF1] = ("Corsair M75 Wireless", "mouse"),
        [0x1BF2] = ("Corsair M75 Air Wireless", "mouse"),
        [0x2B08] = ("Corsair M75 Wireless", "mouse"),
        [0x2B22] = ("Corsair Scimitar Elite Wireless SE", "mouse"),
        [0x2B2E] = ("Corsair Harpoon V2", "mouse"),
        [0x2B32] = ("Corsair Ironclaw Wireless SE", "mouse"),

        // Keyboards (PIDs from OpenLinkHub slipstream.go)
        [0x1B6E] = ("Corsair K57 RGB Wireless", "keyboard"),
        [0x1BAB] = ("Corsair K100 Air", "keyboard"),
        [0x1BB6] = ("Corsair K70 Pro Mini", "keyboard"),
        [0x2B02] = ("Corsair K70 Core TKL", "keyboard"),
        [0x2B04] = ("Corsair MAKR 75", "keyboard"),

        // Keyboards (PIDs from OpenLinkHub k65plusWdongle.go)
        [0x2B10] = ("Corsair K65 Plus Wireless", "keyboard"),
        [0x2B11] = ("Corsair K65 Plus Wireless", "keyboard"),
        [0x2B23] = ("Corsair K65 Plus Wireless", "keyboard"),
    };

    internal readonly record struct CorsairReply(byte Status, int Value);

    /// <summary>What one Bragi interface told us, kept between two reads.</summary>
    internal sealed class CorsairInterfaceState
    {
        public byte? ReportId { get; set; }
        public long? IdentifiedAt { get; set; }
        /// <summary>Receiver slots reported connected on the last read.</summary>
        public HashSet<byte> Connected { get; set; } = [];
        /// <summary>Reads in a row on which a slot left a request unanswered.</summary>
        public Dictionary<byte, int> Misses { get; } = [];
        /// <summary>Product id of the device on each connected receiver slot.</summary>
        public Dictionary<byte, int> ProductIds { get; } = [];
    }

    /// <summary>
    /// Report ids a request is written with: both until one has worked, then only that one, so that a stalled device
    /// is not sent the same request twice.
    /// </summary>
    internal static byte[] CorsairReportIdsFor(byte? known) => known is { } id ? [id] : CorsairReportIds;

    /// <summary>Builds an output report reading one property. It is the only kind of frame ever sent.</summary>
    internal static byte[] BuildCorsairRequest(int length, byte reportId, byte target, byte property)
    {
        var buf = new byte[length];
        buf[0] = reportId;
        buf[1] = target;
        buf[2] = CorsairCmdGet;
        buf[3] = property;
        return buf;
    }

    /// <summary>
    /// Returns the reply to a read sent to <paramref name="target"/>, or null for anything else. Windows hands every
    /// open handle all input reports, so notifications and the replies iCUE asked for arrive here too.
    /// </summary>
    internal static CorsairReply? ParseCorsairReply(ReadOnlySpan<byte> report, byte target)
    {
        if (report.Length < 6 || report[1] != target - CorsairDirectTarget || report[2] != CorsairCmdGet) return null;
        return new CorsairReply(report[3], report[4] | (report[5] << 8));
    }

    /// <summary>
    /// Pure battery query: level, state, then level again. A reply does not name the property it answers, so a value
    /// is kept only when it is plausible and both level reads agree. Levels under 1% are refused: such small numbers
    /// are what iCUE's other queries return.
    /// </summary>
    internal static (int? Level, bool? Charging) QueryCorsairBattery(
        byte target, Func<byte, byte, CorsairReply?> get)
    {
        if (get(target, CorsairPropBatteryLevel) is not { Status: CorsairStatusOk, Value: >= 10 and <= 1000 } level)
            return (null, null);
        if (get(target, CorsairPropBatteryState) is not { Status: CorsairStatusOk, Value: >= 1 and <= 3 } state)
            return (null, null);
        if (get(target, CorsairPropBatteryLevel) is not { Status: CorsairStatusOk } again || again.Value != level.Value)
            return (null, null);
        // "Full" has only been seen on external power: the tested mouse claims it, with 100%, as soon as it is plugged
        // in, whatever its charge. iCUE calls that charging, and so do we.
        var onPower = state.Value is CorsairStateCharging or CorsairStateFull;
        return ((level.Value + 5) / 10, onPower);
    }

    /// <summary>Product id of the device on a receiver slot, or null unless two reads agree.</summary>
    internal static int? QueryCorsairProductId(byte target, Func<byte, byte, CorsairReply?> get)
    {
        // The vendor id is what one of iCUE's own queries returns, never a product id.
        if (get(target, CorsairPropProductId) is not { Status: CorsairStatusOk } first
            || first.Value is 0 or CorsairVendorId) return null;
        return get(target, CorsairPropProductId) is { Status: CorsairStatusOk } again && again.Value == first.Value
            ? first.Value
            : null;
    }

    /// <summary>
    /// Targets of the devices connected to a receiver right now, from its bit field (bit 1 stands for target 0x09), or
    /// null unless two reads agree on a value with no bit outside the slots, which other replies would set.
    /// </summary>
    internal static byte[]? QueryCorsairConnectedTargets(Func<byte, byte, CorsairReply?> get)
    {
        if (get(CorsairDirectTarget, CorsairPropConnectedSlots) is not { Status: CorsairStatusOk } first
            || (first.Value & ~CorsairSlotBits) != 0) return null;
        if (get(CorsairDirectTarget, CorsairPropConnectedSlots) is not { Status: CorsairStatusOk } again
            || again.Value != first.Value) return null;
        return CorsairSlotTargets.Where(target => (first.Value & (1 << (target - CorsairDirectTarget))) != 0).ToArray();
    }

    /// <summary>
    /// Pure query of one Bragi interface: the battery of a listed device on its cable, or those of the listed devices
    /// connected to a listed receiver. Any other product is asked nothing, and an unlisted device behind a receiver is
    /// only asked which product it is.
    /// <para>
    /// A receiver keeps what is sent to a device that does not take it (on its cable, switched off, out of range) and
    /// delivers it all later, and enough of that stops the slot from taking any command, for the vendor's software too,
    /// until the receiver is plugged in again. So a slot is asked only once two reads in a row reported it connected,
    /// and no longer after <see cref="CorsairMaxMisses"/> reads in a row left a request unanswered, until it has been
    /// reported gone. No request is sent without the time to read its reply.
    /// </para>
    /// </summary>
    internal static List<(int ProductId, int Level, bool Charging)> QueryCorsairInterface(
        int interfacePid,
        CorsairInterfaceState state,
        Func<byte, byte, CorsairReply?> get,
        long? deadlineMs = null,
        Func<long>? clock = null)
    {
        var now = clock ?? (() => Environment.TickCount64);
        var found = new List<(int ProductId, int Level, bool Charging)>();
        bool OutOfTime() => deadlineMs is { } deadline && deadline - now() < CorsairReplyTimeoutMs;
        var unanswered = false;
        CorsairReply? Ask(byte target, byte property)
        {
            if (OutOfTime()) return null;
            var reply = get(target, property);
            unanswered |= reply is null;
            return reply;
        }

        if (CorsairDevices.ContainsKey(interfacePid))
        {
            var own = QueryCorsairBattery(CorsairDirectTarget, Ask);
            if (own.Level is { } ownLevel) found.Add((interfacePid, ownLevel, own.Charging == true));
            return found;
        }
        if (!CorsairReceivers.Contains(interfacePid)) return found;
        // Without a trustworthy list of connected devices, no slot is asked at all.
        if (QueryCorsairConnectedTargets(Ask) is not { } connected) return found;
        // A device that left may come back as another one, and gets another chance.
        foreach (var gone in state.ProductIds.Keys.Concat(state.Misses.Keys).Except(connected).ToArray())
        {
            state.ProductIds.Remove(gone);
            state.Misses.Remove(gone);
        }
        var settled = connected.Where(state.Connected.Contains).ToArray();
        state.Connected = [.. connected];

        var identify = state.IdentifiedAt is not { } last || now() - last >= CorsairReidentifyMs;
        foreach (var target in settled)
        {
            // An interrupted identification keeps its old date, so the next read does it again.
            if (OutOfTime()) return found;
            if (state.Misses.GetValueOrDefault(target) >= CorsairMaxMisses) continue;

            unanswered = false;
            if (identify || !state.ProductIds.ContainsKey(target))
            {
                // An id that cannot be read again is forgotten: another device may be on the slot now.
                if (QueryCorsairProductId(target, Ask) is { } id) state.ProductIds[target] = id;
                else state.ProductIds.Remove(target);
            }
            if (state.ProductIds.TryGetValue(target, out var pid) && CorsairDevices.ContainsKey(pid))
            {
                var battery = QueryCorsairBattery(target, Ask);
                if (battery.Level is { } level) found.Add((pid, level, battery.Charging == true));
            }
            state.Misses[target] = unanswered ? state.Misses.GetValueOrDefault(target) + 1 : 0;
        }

        if (identify) state.IdentifiedAt = now();
        return found;
    }

    internal static (string? Name, string? Kind) CorsairDeviceInfo(int pid)
        => CorsairDevices.TryGetValue(pid, out var info) ? info : (null, null);

    /// <summary>
    /// A reading of a listed device. On its cable and on a receiver slot it gets the same name, so both end up as one
    /// entry.
    /// </summary>
    internal static DeviceBattery CorsairDeviceBattery(int pid, int level, bool charging)
    {
        var (name, kind) = CorsairDevices[pid];
        return new DeviceBattery(name, level, kind, charging);
    }

#if WINDOWS
    private static (List<DeviceBattery> Devices, string? Error) WindowsCorsair()
    {
        var readings = new List<DeviceBattery>();
        var handledDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deadline = Environment.TickCount64 + CorsairTotalBudgetMs;
        string? lastError = null;

        try
        {
            var guid = HidInterfaceGuid;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return ([], null);

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; Environment.TickCount64 < deadline && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var req, IntPtr.Zero);
                    if (req == 0) continue;

                    var mem = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, mem, req, out _, ref devData)) continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(mem, 4));
                        if (string.IsNullOrEmpty(path) || !path.Contains("vid_1b1c", StringComparison.OrdinalIgnoreCase)) continue;

                        // One device exposing the collection twice is asked once.
                        var physicalId = GetPhysicalDeviceId(path, devData.DevInst);
                        if (handledDevices.Contains(physicalId)) continue;
                        if (ReadCorsairInterface(path, deadline) is not { } found) continue;
                        handledDevices.Add(physicalId);
                        readings.AddRange(found);
                    }
                    catch (Exception ex) { lastError ??= ex.Message; }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex) { lastError ??= ex.Message; }

        return (MergeRazerIntoBluetooth([], readings), lastError);
    }

    /// <summary>Readings of one interface, or null when it is not one to ask.</summary>
    private static List<DeviceBattery>? ReadCorsairInterface(string path, long deadline)
    {
        using var infoHandle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (infoHandle.IsInvalid) return null;

        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(infoHandle, ref attrs) || attrs.VendorID != CorsairVendorId) return null;
        int pid = attrs.ProductID;
        if (!CorsairDevices.ContainsKey(pid) && !CorsairReceivers.Contains(pid)) return null;
        if (!TryGetHidCaps(infoHandle, out var caps)
            || caps.UsagePage != CorsairUsagePage || caps.Usage != CorsairUsage
            || caps.InputReportByteLength == 0 || caps.OutputReportByteLength == 0) return null;

        // GENERIC_READ | GENERIC_WRITE, shared so that iCUE keeps its own handle, overlapped so every call can time out.
        using var rwHandle = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (rwHandle.IsInvalid) return null;

        var state = CorsairInterfaces.GetOrAdd(path, _ => new CorsairInterfaceState());
        var found = QueryCorsairInterface(pid, state,
            (target, property) => CorsairGet(rwHandle, caps, state, target, property), deadline);

        return found.Select(d => CorsairDeviceBattery(d.ProductId, d.Level, d.Charging)).ToList();
    }

    private static CorsairReply? CorsairGet(SafeFileHandle handle, HIDP_CAPS caps, CorsairInterfaceState state,
        byte target, byte property)
    {
        // Drop what is already queued, so that an earlier reply is not taken for this one.
        HidD_FlushQueue(handle);

        var sent = false;
        foreach (var id in CorsairReportIdsFor(state.ReportId))
        {
            var request = BuildCorsairRequest(caps.OutputReportByteLength, id, target, property);
            if (!WritePaOverlapped(handle, request, 200)) continue;
            state.ReportId = id;
            sent = true;
            break;
        }
        if (!sent) return null;

        var replyDeadline = Environment.TickCount64 + CorsairReplyTimeoutMs;
        while (Environment.TickCount64 < replyDeadline)
        {
            var wait = (int)Math.Max(1, replyDeadline - Environment.TickCount64);
            var (report, fatal) = ReadPaOverlapped(handle, caps.InputReportByteLength, wait);
            if (fatal) return null;
            if (report is not null && ParseCorsairReply(report, target) is { } reply) return reply;
        }
        return null;
    }

    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FlushQueue(SafeFileHandle handle);
#endif
}
