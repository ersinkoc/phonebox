using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Kind">headphones, mouse, keyboard, controller, laptop or other (for the icon).</param>
public sealed record DeviceBattery(string Name, int Level, string Kind, bool? Charging);

/// <summary>
/// Battery levels of wireless devices: headphones, mice, keyboards, controllers (and a laptop's own battery on Linux).
///  - Windows: Bluetooth battery property via SetupAPI (what Settings › Bluetooth shows) and Razer USB HID devices.
///  - Linux: UPower (BlueZ devices, Logitech receivers, the laptop battery).
///  - macOS: ioreg (Magic Mouse / Keyboard / Trackpad) and system_profiler (AirPods and other headphones).
/// Read once a minute, only while a phone shows the widget.
/// </summary>
public sealed partial class BatteryMonitor(ILogger<BatteryMonitor> log) : BackgroundService
{
    private const long NoRequest = long.MinValue;
    private volatile IReadOnlyList<DeviceBattery> _devices = [];
#if WINDOWS
    private readonly object _publishLock = new();
    private IReadOnlyList<DeviceBattery> _btDevices = [];
    private IReadOnlyList<DeviceBattery> _razerDevices = [];
    private Task? _razerTask;
#endif
    private long _lastRequest = NoRequest;
    private long? _readAt;

    public IReadOnlyList<DeviceBattery> Snapshot()
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        return _devices;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lastRequest = Interlocked.Read(ref _lastRequest);
            var now = Environment.TickCount64;
            var wanted = lastRequest != NoRequest && now - lastRequest < 120_000;
            if (wanted && (_readAt is null || now - _readAt.Value > 60_000))
            {
                _readAt = now;
#if WINDOWS
                try
                {
                    var bt = await Task.Run(Windows, ct);
                    lock (_publishLock)
                    {
                        _btDevices = bt;
                        _devices = SortDevices(MergeRazerIntoBluetooth(_btDevices, _razerDevices));
                    }
                }
                catch (Exception ex) { log.LogDebug("batteries: {Msg}", ex.Message); }

                if (_razerTask is null || _razerTask.IsCompleted)
                {
                    _razerTask = Task.Run(() =>
                    {
                        var (razer, error) = WindowsRazer();
                        if (error is not null) log.LogDebug("batteries (razer): {Msg}", error);
                        lock (_publishLock)
                        {
                            _razerDevices = razer;
                            _devices = SortDevices(MergeRazerIntoBluetooth(_btDevices, _razerDevices));
                        }
                    }, ct);
                }
#else
                try { _devices = await Task.Run(Read, ct); }
                catch (Exception ex) { log.LogDebug("batteries: {Msg}", ex.Message); }
#endif
            }
            await Task.Delay(1000, ct);
        }
    }

    private static List<DeviceBattery> SortDevices(IEnumerable<DeviceBattery> list)
        => list.OrderBy(d => d.Kind == "laptop" ? 1 : 0).ThenBy(d => d.Name).ToList();

#if !WINDOWS
    private static IReadOnlyList<DeviceBattery> Read()
        => SortDevices(OperatingSystem.IsMacOS() ? MacOs() : Linux());
#endif

    /// <summary>Guesses the kind of device from its name.</summary>
    public static string KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"buds|airpods|headphone|headset|earbud|earphone|\bwh-|\bwf-|jabra|bose|beats|sennheiser|soundcore|\bqc\d|xm\d|arctis|hyperx cloud|blackshark|barracuda|kraken|nari|kaira|opus")) return "headphones";
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"mouse|mx master|mx anywhere|pebble|trackball|trackpad|\bm\d{3}\b|g\d{3} |basilisk|viper|deathadder|naga|orochi|cobra|lancehead|mamba")) return "mouse";
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"keyboard|keys|\bk\d{3}\b|slim|craft|magic keyboard|blackwidow|huntsman|deathstalker|ornata|tartarus")) return "keyboard";
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"controller|gamepad|xbox|dualsense|dualshock|joy-con|8bitdo")) return "controller";
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"pencil|stylus|pen\b")) return "pen";
        return "other";
    }

#if WINDOWS
    // ---------------------------------------------------------------- Windows
    private static List<DeviceBattery> Windows()
    {
        var found = new List<(string Name, int Level, string Id)>();
        var battery = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
        var friendly = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4); // DIGCF_PRESENT | DIGCF_ALLCLASSES
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return [];
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new byte[1024];
            var idBuf = new char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, idBuf.Length, out _)) continue;
                var id = new string(idBuf).TrimEnd('\0');
                if (!id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SetupDiGetDevicePropertyW(set, ref data, ref battery, out _, buf, (uint)buf.Length, out var size, 0) || size < 1) continue;
                int level = buf[0];
                if (level > 100) continue;
                var name = SetupDiGetDevicePropertyW(set, ref data, ref friendly, out _, buf, (uint)buf.Length, out size, 0)
                    ? System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0') : "Bluetooth device";
                found.Add((name, level, id));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        // the same model paired twice (e.g. a mouse on two Easy-Switch channels): tell them apart by the address
        return found.Select(f =>
        {
            var dupes = found.Count(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)) > 1;
            var addr = System.Text.RegularExpressions.Regex.Match(f.Id, @"DEV_([0-9A-F]{12})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var name = dupes && addr.Success ? $"{f.Name} ({addr.Groups[1].Value[^4..]})" : f.Name;
            return new DeviceBattery(name, f.Level, KindOf(f.Name), null);
        }).ToList();
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

#else
    // ---------------------------------------------------------------- Linux
    private static List<DeviceBattery> Linux()
    {
        var list = new List<DeviceBattery>();
        foreach (var path in (Sh.Run("upower", "-e") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (path.EndsWith("DisplayDevice") || path.Contains("line_power")) continue;
            var info = Sh.Run("upower", "-i", path) ?? "";
            string? Field(string key) => info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + ":"))?[(key.Length + 1)..].Trim();
            if (!int.TryParse(Field("percentage")?.TrimEnd('%').Split(',', '.')[0], out var level)) continue;
            if (Field("present") == "no") continue;
            var file = Path.GetFileName(path);
            var kind = file.StartsWith("battery_") ? "laptop" : file.StartsWith("mouse_") ? "mouse" : file.StartsWith("keyboard_") ? "keyboard"
                : file.StartsWith("headset_") || file.StartsWith("headphones_") ? "headphones" : file.StartsWith("gaming_input_") ? "controller" : null;
            var name = Field("model") is { Length: > 0 } m ? m : kind == "laptop" ? "Laptop" : file;
            if (kind == "laptop") name = "This laptop";
            var state = Field("state");
            list.Add(new DeviceBattery(name, Math.Clamp(level, 0, 100), kind ?? KindOf(name), state is null ? null : state is "charging" or "fully-charged"));
        }
        return list;
    }

    // ---------------------------------------------------------------- macOS
    private static List<DeviceBattery> MacOs()
    {
        var list = new List<DeviceBattery>();
        // Magic Mouse / Keyboard / Trackpad and other HID devices that report BatteryPercent
        var io = Sh.Run("ioreg", "-r", "-l", "-k", "BatteryPercent") ?? "";
        string? product = null;
        foreach (var raw in io.Split('\n'))
        {
            var line = raw.Trim();
            var p = System.Text.RegularExpressions.Regex.Match(line, "\"Product\" = \"(.+)\"");
            if (p.Success) product = p.Groups[1].Value;
            var b = System.Text.RegularExpressions.Regex.Match(line, "\"BatteryPercent\" = (\\d+)");
            if (b.Success && product is not null) { list.Add(new DeviceBattery(product, int.Parse(b.Groups[1].Value), KindOf(product), null)); product = null; }
        }
        // AirPods and other headphones: system_profiler lists their battery levels
        try
        {
            var json = Sh.RunWithTimeout(8000, "system_profiler", "SPBluetoothDataType", "-json");
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                AddMacBluetoothBatteries(doc.RootElement, list);
            }
        }
        catch { }
        return list;
    }

    internal static void AddMacBluetoothBatteries(JsonElement root, List<DeviceBattery> list)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("SPBluetoothDataType", out var sections) || sections.ValueKind != JsonValueKind.Array) return;

        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object ||
                !section.TryGetProperty("device_connected", out var connected) || connected.ValueKind != JsonValueKind.Array) continue;

            foreach (var device in connected.EnumerateArray())
            {
                if (device.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in device.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    int? Level(string key) => prop.Value.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString()?.TrimEnd('%'), out var level) ? level : null;

                    var levels = new[] { Level("device_batteryLevelLeft"), Level("device_batteryLevelRight"), Level("device_batteryLevelMain") }.Where(x => x is not null).ToList();
                    if (levels.Count == 0 || list.Any(d => d.Name == prop.Name)) continue;
                    var kind = KindOf(prop.Name);
                    list.Add(new DeviceBattery(prop.Name, levels.Min()!.Value, kind is "other" ? "headphones" : kind, null));
                }
            }
        }
    }
#endif
}
