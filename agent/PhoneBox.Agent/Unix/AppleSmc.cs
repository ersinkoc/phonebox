using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// CPU and GPU temperature on a Mac with Apple silicon, read from the System Management Controller (AppleSMC) without
/// root. Apple documents neither the keys nor their meaning; this follows the rule macmon uses (MIT,
/// github.com/vladkens/macmon): the average of the "Tp", "Te" and "Ts" float keys (not "Ts*P", which don't follow the
/// load) is the CPU, the average of the "Tg" keys the GPU. The SMC exposes these keys from macOS 14; on older systems,
/// on Intel Macs or when anything here fails, both readings are null and the panel shows no temperature, as before.
/// </summary>
internal sealed class AppleSmc : IDisposable
{
    private const int StructSize = 80; // SMCKeyData_t
    private const uint HandleYpcEvent = 2; // kSMCHandleYPCEvent
    private const byte CmdReadKey = 5, CmdReadIndex = 8, CmdReadKeyInfo = 9;
    private const uint FloatType = 0x666C7420; // "flt "
    private const int MaxKeys = 4096; // a sane bound for "#KEY": the M4 has ~1350

    private uint _conn;
    private readonly List<uint> _cpuKeys = [], _gpuKeys = [];

    private AppleSmc(uint conn) => _conn = conn;

    /// <summary>Opens the SMC and finds the temperature keys, or null where there is no SMC or no such key.</summary>
    public static AppleSmc? Open()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return null;
        var service = IOServiceGetMatchingService(0, IOServiceMatching("AppleSMC"));
        if (service == 0) return null;
        try
        {
            if (IOServiceOpen(service, mach_task_self(), 0, out var conn) != 0 || conn == 0) return null;
            var smc = new AppleSmc(conn);
            if (!smc.FindKeys()) { smc.Dispose(); return null; }
            return smc;
        }
        finally { IOObjectRelease(service); }
    }

    /// <summary>
    /// The CPU and GPU averages in °C, rounded to a tenth; either is null without a valid reading. Throws when no key
    /// answers at all: every one of them did when the SMC was opened, so the connection is gone and must be opened again.
    /// </summary>
    public (double? Cpu, double? Gpu) Read()
    {
        var cpu = _cpuKeys.Select(ReadFloat).ToList();
        var gpu = _gpuKeys.Select(ReadFloat).ToList();
        if (cpu.Concat(gpu).All(v => v is null)) throw new IOException("the SMC answered no temperature key");
        return (Average(cpu), Average(gpu));
    }

    // "Ts*P" keys don't follow the CPU load (macmon), so they are left out of the CPU average.
    internal static bool IsCpuKey(string key) =>
        key.Length == 4 && (key.StartsWith("Tp", StringComparison.Ordinal) || key.StartsWith("Te", StringComparison.Ordinal)
            || (key.StartsWith("Ts", StringComparison.Ordinal) && key[3] != 'P'));

    internal static bool IsGpuKey(string key) => key.Length == 4 && key.StartsWith("Tg", StringComparison.Ordinal);

    /// <summary>Average of the readings a die can give: a sensor that is off reads 0 or a few degrees.</summary>
    internal static double? Average(IEnumerable<double?> values)
    {
        var valid = values.OfType<double>().Where(v => double.IsFinite(v) && v > 10 && v <= 150).ToList();
        return valid.Count == 0 ? null : Math.Round(valid.Average(), 1);
    }

    internal static uint FourCc(string key) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(key));
    internal static string KeyName(uint key)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, key);
        return System.Text.Encoding.ASCII.GetString(b);
    }

    private bool FindKeys()
    {
        var count = ReadRaw(FourCc("#KEY"), out var data) && data.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(data) : 0;
        for (uint i = 0; i < Math.Min(count, MaxKeys); i++)
        {
            var input = new byte[StructSize];
            input[42] = CmdReadIndex;
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(44), i);
            if (Call(input) is not { } output) continue;
            var key = BinaryPrimitives.ReadUInt32LittleEndian(output);
            var name = KeyName(key);
            var cpu = IsCpuKey(name);
            if (!cpu && !IsGpuKey(name)) continue;
            if (ReadFloat(key) is null) continue; // not a float, or unreadable: never part of an average
            (cpu ? _cpuKeys : _gpuKeys).Add(key);
        }
        return _cpuKeys.Count > 0 || _gpuKeys.Count > 0;
    }

    private double? ReadFloat(uint key)
    {
        if (!ReadRaw(key, out var data, FloatType) || data.Length != 4) return null;
        return BinaryPrimitives.ReadSingleLittleEndian(data);
    }

    /// <summary>Reads one key's bytes; with <paramref name="type"/>, only a key of that data type.</summary>
    private bool ReadRaw(uint key, out byte[] data, uint? type = null)
    {
        data = [];
        var input = new byte[StructSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input, key);
        input[42] = CmdReadKeyInfo;
        if (Call(input) is not { } info || info[40] != 0) return false;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var dataType = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32));
        if (size is 0 or > 32 || (type is { } t && dataType != t)) return false;

        input[42] = CmdReadKey;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(28), size);
        if (Call(input) is not { } output || output[40] != 0) return false;
        data = output.AsSpan(48, (int)size).ToArray();
        return true;
    }

    private byte[]? Call(byte[] input)
    {
        var output = new byte[StructSize];
        nuint outSize = StructSize;
        return _conn != 0 && IOConnectCallStructMethod(_conn, HandleYpcEvent, input, StructSize, output, ref outSize) == 0 ? output : null;
    }

    public void Dispose()
    {
        if (_conn != 0) IOServiceClose(_conn);
        _conn = 0;
    }

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] private static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
    [DllImport(IOKit)] private static extern int IOServiceClose(uint connect);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)]
    private static extern int IOConnectCallStructMethod(uint connection, uint selector, byte[] input, nuint inputSize, byte[] output, ref nuint outputSize);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern uint mach_task_self();
}
