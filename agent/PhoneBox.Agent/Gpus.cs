using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// The GPU list from several sources: nvidia-smi, LibreHardwareMonitor's web server, the Windows graphics kernel
/// (D3DKMT and the GPU performance counters, the same data Task Manager shows), Linux sysfs (and DRM fdinfo for Intel's
/// load) and macOS ioreg.
/// The same card can come from more than one source; the entries are matched by name and the earlier source wins,
/// a later one only fills what the earlier one left empty (except the Windows adapter's integrated flag, see Merge).
/// </summary>
internal static partial class Gpus
{
    private const double GiB = 1024.0 * 1024 * 1024;

    /// <summary>Merges the sources (highest priority first) and puts the main GPU first: discrete before integrated, then the most video memory.</summary>
    public static List<GpuInfo> Compose(params IReadOnlyList<GpuInfo>?[] sources)
    {
        var list = new List<GpuInfo>();
        foreach (var src in sources)
        {
            if (src is null) continue;
            var taken = new HashSet<int>(); // two identical cards: each entry is matched once per source
            foreach (var g in src)
            {
                var key = Key(g.Name);
                var i = -1;
                for (var j = 0; j < list.Count && i < 0; j++)
                    if (!taken.Contains(j) && Key(list[j].Name) == key) i = j;
                if (i < 0) { list.Add(g); taken.Add(list.Count - 1); }
                else { list[i] = Merge(list[i], g); taken.Add(i); }
            }
        }
        return list.Select((g, i) => (g, i))
            .OrderBy(t => t.g.Integrated)
            .ThenByDescending(t => t.g.VramTotal ?? 0)
            .ThenBy(t => t.i)
            .Select(t => t.g)
            .ToList();
    }

    /// <summary>
    /// Fields of <paramref name="a"/> win; <paramref name="b"/> fills the gaps. One exception: the Windows adapter list
    /// (merged last) marking a GPU integrated is kept, since Windows knows a hybrid laptop's integrated GPU that the name
    /// heuristic of the sources before it can miss. A guess such as sysfs's small-VRAM rule doesn't override an earlier source.
    /// </summary>
    public static GpuInfo Merge(GpuInfo a, GpuInfo b) => a with
    {
        Util = a.Util ?? b.Util,
        Temp = a.Temp ?? b.Temp,
        Power = a.Power ?? b.Power,
        PowerLimit = a.PowerLimit ?? b.PowerLimit,
        VramUsed = a.VramUsed ?? b.VramUsed,
        VramTotal = a.VramTotal ?? b.VramTotal,
        Fan = a.Fan ?? b.Fan,
        Clock = a.Clock ?? b.Clock,
        Vendor = a.Vendor ?? b.Vendor,
        Source = a.Source is null ? b.Source : b.Source is null || a.Source.Contains(b.Source) ? a.Source : a.Source + "+" + b.Source,
        Integrated = a.Integrated || (b.Integrated && b.Source == "windows"),
        HotSpot = a.HotSpot ?? b.HotSpot,
        MemTemp = a.MemTemp ?? b.MemTemp,
        MemClock = a.MemClock ?? b.MemClock,
        SharedUsed = a.SharedUsed ?? b.SharedUsed,
        SharedTotal = a.SharedTotal ?? b.SharedTotal,
        PowerPct = a.PowerPct ?? b.PowerPct,
        FanRpm = a.FanRpm ?? b.FanRpm,
        TempMax = a.TempMax ?? b.TempMax,
        Driver = a.Driver ?? b.Driver,
    };

    /// <summary>"AMD Radeon(TM) Graphics" and "AMD Radeon Graphics" are the same card.</summary>
    public static string Key(string name) =>
        NonAlnum().Replace(name.ToLowerInvariant().Replace("(tm)", "").Replace("(r)", ""), "");

    public static string? VendorOf(string name) =>
        name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || name.Contains("geforce", StringComparison.OrdinalIgnoreCase) ? "nvidia"
        : name.Contains("amd", StringComparison.OrdinalIgnoreCase) || name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ? "amd"
        : name.Contains("intel", StringComparison.OrdinalIgnoreCase) ? "intel"
        : null;

    public static string? VendorOf(uint pciVendor) => pciVendor switch
    {
        0x10de => "nvidia",
        0x1002 or 0x1022 => "amd",
        0x8086 => "intel",
        0x106b => "apple",
        0x5143 => "qualcomm",
        _ => null,
    };

    /// <summary>
    /// Integrated graphics by name: AMD APUs are "Radeon(TM) Graphics", "Radeon 780M Graphics", "Radeon RX Vega 11 Graphics";
    /// Intel's are everything but the Arc A/B cards ("Arc(TM) Graphics" and "Arc(TM) 140V GPU" are integrated too).
    /// </summary>
    public static bool LooksIntegrated(string name)
    {
        if (name.Contains("intel", StringComparison.OrdinalIgnoreCase))
            return !IntelDiscrete().IsMatch(name);
        if (name.Contains("radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))
            return name.TrimEnd().EndsWith("Graphics", StringComparison.OrdinalIgnoreCase) && !AmdDiscrete().IsMatch(name);
        return false;
    }

    // ---------------------------------------------------------------- nvidia-smi

    /// <summary>One line of <c>--query-gpu=index,utilization.gpu,temperature.gpu,power.draw,power.limit,memory.used,memory.total,fan.speed,clocks.gr,clocks.mem</c>.</summary>
    public static (int Index, GpuInfo Gpu)? ParseNvidiaLine(string line, IReadOnlyDictionary<int, (string Name, string? Driver)> names)
    {
        var f = line.Split(',').Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).ToArray();
        if (f.Length < 9 || f[0] is not { } idx || idx < 0) return null;
        var (name, driver) = names.TryGetValue((int)idx, out var n) ? n : ("NVIDIA GPU", null);
        return ((int)idx, new GpuInfo(name, f[1], f[2], f[3], f[4], f[5] / 1024, f[6] / 1024, f[7], f[8],
            Vendor: "nvidia", Source: "nvidia-smi", MemClock: f.Length > 9 ? f[9] : null, Driver: driver));
    }

    /// <summary>The <c>--query-gpu=index,name,driver_version</c> answer.</summary>
    public static Dictionary<int, (string Name, string? Driver)> ParseNvidiaNames(string text)
    {
        var map = new Dictionary<int, (string, string?)>();
        foreach (var line in text.Split('\n'))
        {
            var first = line.IndexOf(',');
            if (first <= 0 || !int.TryParse(line[..first].Trim(), CultureInfo.InvariantCulture, out var i)) continue;
            var last = line.LastIndexOf(',');
            var name = (last > first ? line[(first + 1)..last] : line[(first + 1)..]).Trim();
            if (name.Length == 0) continue;
            var drv = last > first ? line[(last + 1)..].Trim() : "";
            map[i] = (name, drv.Length > 0 && drv != "[N/A]" ? drv : null);
        }
        return map;
    }

    // ---------------------------------------------------------------- LibreHardwareMonitor (web server, /data.json)

    /// <summary>
    /// The GPUs in LibreHardwareMonitor's tree. A GPU node is recognised by its HardwareId ("/gpu-amd/0", "/gpu-nvidia/0",
    /// "/gpu-intel-integrated/…") or, on older versions without ids, by its icon (ati.png, nvidia.png, intel.png).
    /// Values are formatted strings in the user's culture ("45,0 °C", "1.5 GB", "8192.0 MB"), so they are read with their unit.
    /// </summary>
    public static List<GpuInfo> ParseLhm(JsonElement root)
    {
        var list = new List<GpuInfo>();
        Walk(root);
        return list;

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (LhmGpuVendor(node) is { } vendor && Str(node, "Text") is { Length: > 0 } name)
            {
                list.Add(LhmGpu(node, name, vendor));
                return;
            }
            if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) Walk(k);
        }
    }

    private static string? LhmGpuVendor(JsonElement node)
    {
        if (Str(node, "HardwareId") is { } id)
            return id.StartsWith("/gpu-nvidia", StringComparison.Ordinal) ? "nvidia"
                : id.StartsWith("/gpu-amd", StringComparison.Ordinal) || id.StartsWith("/atigpu", StringComparison.Ordinal) ? "amd"
                : id.StartsWith("/gpu-intel", StringComparison.Ordinal) ? "intel"
                : id.StartsWith("/gpu", StringComparison.Ordinal) ? VendorOf(Str(node, "Text") ?? "") ?? "gpu"
                : null;
        if (Str(node, "SensorId") is not null) return null;
        return Str(node, "ImageURL") switch
        {
            { } u when u.EndsWith("/nvidia.png", StringComparison.Ordinal) => "nvidia",
            { } u when u.EndsWith("/ati.png", StringComparison.Ordinal) => "amd",
            { } u when u.EndsWith("/intel.png", StringComparison.Ordinal) => "intel",
            _ => null,
        };
    }

    private sealed record LhmSensor(string Name, string Kind, double Value);

    private static GpuInfo LhmGpu(JsonElement hw, string name, string vendor)
    {
        var sensors = new List<LhmSensor>();
        Collect(hw, null);

        double? Get(string kind, params string[] names)
        {
            foreach (var n in names)
                if (sensors.FirstOrDefault(s => s.Kind == kind && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } s) return s.Value;
            return null;
        }

        var fanRpm = sensors.FirstOrDefault(s => s.Kind == "rpm" && s.Name.StartsWith("GPU Fan", StringComparison.OrdinalIgnoreCase))?.Value;
        var integrated = Str(hw, "HardwareId")?.Contains("integrated", StringComparison.Ordinal) == true || LooksIntegrated(name);
        return new GpuInfo(name,
            Util: Get("load", "GPU Core"),
            Temp: Get("temp", "GPU Core"),
            Power: Get("power", "GPU Package", "GPU Total", "GPU Power", "GPU PPT", "GPU Core"),
            PowerLimit: null,
            VramUsed: Get("data", "GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotal: Get("data", "GPU Memory Total", "D3D Dedicated Memory Total"),
            Fan: Get("control", "GPU Fan"),
            Clock: Get("clock", "GPU Core"),
            Vendor: vendor,
            Source: "lhm",
            Integrated: integrated,
            HotSpot: Get("temp", "GPU Hot Spot"),
            MemTemp: Get("temp", "GPU Memory"),
            MemClock: Get("clock", "GPU Memory"),
            SharedUsed: Get("data", "D3D Shared Memory Used"),
            SharedTotal: Get("data", "D3D Shared Memory Total"),
            FanRpm: fanRpm);

        void Collect(JsonElement node, string? group)
        {
            if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array && kids.GetArrayLength() > 0)
            {
                var g = Str(node, "Text");
                foreach (var k in kids.EnumerateArray()) Collect(k, g);
                return;
            }
            if (Str(node, "Text") is not { } text || Str(node, "Value") is not { } value) return;
            if (ParseLhmValue(value, Str(node, "Type") ?? group) is { } parsed)
                sensors.Add(new LhmSensor(text, parsed.Kind, parsed.Value));
        }
    }

    /// <summary>"45,0 °C" → (temp, 45). Data values come back in GB; % values are load unless the sensor is a control (fan duty).</summary>
    internal static (string Kind, double Value)? ParseLhmValue(string value, string? typeOrGroup)
    {
        var m = LhmValue().Match(value);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var unit = m.Groups[2].Value;
        var control = typeOrGroup is "Control" or "Controls";
        return unit switch
        {
            "°C" => ("temp", v),
            "°F" => ("temp", Math.Round((v - 32) / 1.8, 1)),
            "%" => (control ? "control" : "load", v),
            "MHz" => ("clock", v),
            "GHz" => ("clock", v * 1000),
            "W" => ("power", v),
            "RPM" => ("rpm", v),
            "B" => ("data", v / GiB),
            "KB" => ("data", v / (1024.0 * 1024)),
            "MB" => ("data", Math.Round(v / 1024, 3)),
            "GB" => ("data", v),
            "TB" => ("data", v * 1024),
            _ => null,
        };
    }

    private static string? Str(JsonElement node, string key) =>
        node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- Linux sysfs (amdgpu, i915 / xe) and DRM fdinfo

    /// <summary>Keeps the previous fdinfo sample between calls, for the Intel load.</summary>
    private static readonly DrmUsage IntelUsage = new();

    /// <summary>
    /// AMD and Intel GPUs from <c>/sys/class/drm/card*/device</c>. amdgpu reports load, VRAM and GTT (shared) memory,
    /// clocks and its hwmon sensors; Intel's i915 and xe report the clock and, on Arc, a temperature, and their load is
    /// added up from the DRM fdinfo of the processes in <paramref name="procRoot"/> (see <see cref="DrmUsage"/>), so it
    /// shows from the second call on. NVIDIA cards are left to nvidia-smi.
    /// </summary>
    public static List<GpuInfo> ReadSysfs(string drmRoot = "/sys/class/drm", string? pciIds = null, string procRoot = "/proc", DrmUsage? usage = null)
    {
        var list = new List<GpuInfo>();
        if (!Directory.Exists(drmRoot)) return list;
        var intelLoad = new List<(int Index, string Pdev)>(); // Intel cards without gpu_busy_percent
        foreach (var card in Directory.GetDirectories(drmRoot).Where(d => CardDir().IsMatch(Path.GetFileName(d))).Order(StringComparer.Ordinal))
        {
            try
            {
                var dev = Path.Combine(card, "device");
                if (Hex(Read(dev, "vendor")) is not { } vendorId) continue;
                var vendor = VendorOf(vendorId);
                if (vendor is not ("amd" or "intel")) continue;
                var deviceId = Hex(Read(dev, "device")) ?? 0;
                var hw = Directory.Exists(Path.Combine(dev, "hwmon")) ? Directory.GetDirectories(Path.Combine(dev, "hwmon")).Order(StringComparer.Ordinal).FirstOrDefault() : null;
                var name = Read(dev, "product_name") is { Length: > 0 } pn ? pn : PciName(vendorId, deviceId, pciIds) ?? (vendor == "amd" ? "AMD GPU" : "Intel GPU");
                if (vendor == "amd" && !name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) name = "AMD " + name;
                if (vendor == "intel" && !name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) name = "Intel " + name;

                var vramTotal = Num(dev, "mem_info_vram_total");
                var temps = HwmonTemps(hw);
                double? Temp(string label, int index) => temps.TryGetValue(label, out var t) ? t : temps.TryGetValue("temp" + index, out t) ? t : null;
                var power = Num(hw, "power1_average") ?? Num(hw, "power1_input");
                var cap = Num(hw, "power1_cap");
                var fanMax = Num(hw, "fan1_max");
                var fanRpm = Num(hw, "fan1_input");
                var clock = vendor == "amd" ? DpmCurrent(Read(dev, "pp_dpm_sclk"))
                    : Num(card, "gt_cur_freq_mhz") ?? Num(dev, Path.Combine("tile0", "gt0", "freq0", "cur_freq"));

                var util = Num(dev, "gpu_busy_percent");
                list.Add(new GpuInfo(name,
                    Util: util,
                    Temp: Temp("edge", 1),
                    Power: power is { } p ? Math.Round(p / 1e6, 1) : null,
                    PowerLimit: cap is { } c && c > 0 ? Math.Round(c / 1e6) : null,
                    VramUsed: Num(dev, "mem_info_vram_used") is { } vu ? vu / GiB : null,
                    VramTotal: vramTotal is { } vt ? vt / GiB : null,
                    Fan: fanRpm is { } r && fanMax is { } mx && mx > 0 ? Math.Round(100 * r / mx) : null,
                    Clock: clock,
                    Vendor: vendor,
                    Source: "sysfs",
                    // an APU's VRAM is a small carve-out of the RAM; Intel is integrated unless it is an Arc card
                    Integrated: vendor == "amd" ? vramTotal is { } v && v < 4 * GiB && !AmdDiscrete().IsMatch(name) : !IntelDiscrete().IsMatch(name) && !name.Contains("DG2", StringComparison.Ordinal),
                    HotSpot: temps.TryGetValue("junction", out var j) ? j : null,
                    MemTemp: temps.TryGetValue("mem", out var mt) ? mt : null,
                    MemClock: vendor == "amd" ? DpmCurrent(Read(dev, "pp_dpm_mclk")) : null,
                    SharedUsed: Num(dev, "mem_info_gtt_used") is { } gu ? gu / GiB : null,
                    SharedTotal: Num(dev, "mem_info_gtt_total") is { } gt ? gt / GiB : null,
                    FanRpm: fanRpm,
                    TempMax: Num(hw, "temp1_crit") is { } tc && tc > 0 ? tc / 1000 : null));
                if (util is null && vendor == "intel" && PciAddress(dev) is { } pdev) intelLoad.Add((list.Count - 1, pdev));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (intelLoad.Count > 0)
        {
            var load = (usage ?? IntelUsage).Sample(procRoot, intelLoad.Select(c => c.Pdev).ToList());
            foreach (var (i, pdev) in intelLoad)
                if (load.TryGetValue(pdev, out var u)) list[i] = list[i] with { Util = u };
        }
        return list;
    }

    /// <summary>
    /// The card's PCI address ("0000:00:02.0"), as DRM fdinfo names it in <c>drm-pdev</c>: from the device's uevent, or the
    /// name the <c>device</c> link points to.
    /// </summary>
    internal static string? PciAddress(string dev)
    {
        foreach (var line in (Read(dev, "uevent") ?? "").Split('\n'))
            if (line.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal) && line["PCI_SLOT_NAME=".Length..].Trim() is var slot && PciSlot().IsMatch(slot))
                return slot.ToLowerInvariant();
        try
        {
            var target = new DirectoryInfo(dev).LinkTarget;
            var name = target is null ? null : Path.GetFileName(target.TrimEnd('/'));
            return name is not null && PciSlot().IsMatch(name) ? name.ToLowerInvariant() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, double> HwmonTemps(string? hw)
    {
        var temps = new Dictionary<string, double>();
        if (hw is null) return temps;
        for (var i = 1; i <= 8; i++)
        {
            if (Num(hw, $"temp{i}_input") is not { } milli) continue;
            temps[Read(hw, $"temp{i}_label")?.ToLowerInvariant() ?? "temp" + i] = Math.Round(milli / 1000, 1);
            temps.TryAdd("temp" + i, Math.Round(milli / 1000, 1));
        }
        return temps;
    }

    /// <summary>"0: 500Mhz\n1: 2482Mhz *" → 2482.</summary>
    internal static double? DpmCurrent(string? text)
    {
        if (text is null) return null;
        foreach (var line in text.Split('\n'))
            if (line.TrimEnd().EndsWith('*') && DpmLine().Match(line) is { Success: true } m)
                return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, uint, uint), string?> PciNames = new();

    /// <summary>Looks a device up in pci.ids (once per card: the file is over a megabyte).</summary>
    internal static string? PciName(uint vendor, uint device, string? pciIds)
    {
        pciIds ??= new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" }.FirstOrDefault(File.Exists);
        if (pciIds is null || !File.Exists(pciIds)) return null;
        return PciNames.GetOrAdd((pciIds, vendor, device), k => LookUpPciName(k.Item2, k.Item3, k.Item1));
    }

    /// <summary>The vendor line, then a tab-indented device line under it.</summary>
    private static string? LookUpPciName(uint vendor, uint device, string pciIds)
    {
        var v = vendor.ToString("x4", CultureInfo.InvariantCulture);
        var d = device.ToString("x4", CultureInfo.InvariantCulture);
        var inVendor = false;
        foreach (var line in File.ReadLines(pciIds))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] != '\t') { if (inVendor) return null; inVendor = line.StartsWith(v + "  ", StringComparison.OrdinalIgnoreCase); continue; }
            if (inVendor && line.Length > 6 && line[1] != '\t' && line.AsSpan(1, 4).Equals(d, StringComparison.OrdinalIgnoreCase))
            {
                var name = line[5..].Trim();
                // "Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]" → the marketing name in brackets reads better
                var br = Bracket().Match(name);
                return br.Success ? br.Groups[1].Value : name;
            }
        }
        return null;
    }

    private static string? Read(string? dir, string file)
    {
        if (dir is null) return null;
        var p = Path.Combine(dir, file);
        try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double? Num(string? dir, string file) =>
        double.TryParse(Read(dir, file), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static uint? Hex(string? s) =>
        s is not null && uint.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"Arc(?:\(TM\)|™)?\s+(?:Pro\s+)?[AB]\d{2,3}", RegexOptions.IgnoreCase)] private static partial Regex IntelDiscrete();
    [GeneratedRegex(@"\bRX\s+\d{3,4}|\bPro\s+W\d|\bW\d{4}", RegexOptions.IgnoreCase)] private static partial Regex AmdDiscrete();
    [GeneratedRegex(@"^\s*(-?\d+(?:[.,]\d+)?)\s*(°C|°F|%|MHz|GHz|W|RPM|B|KB|MB|GB|TB)\s*$")] private static partial Regex LhmValue();
    [GeneratedRegex(@"^card\d+$")] private static partial Regex CardDir();
    [GeneratedRegex(@"^\s*\d+:\s*(\d+)\s*[Mm][Hh]z")] private static partial Regex DpmLine();
    [GeneratedRegex(@"\[([^\]]+)\]")] private static partial Regex Bracket();
    [GeneratedRegex(@"^[0-9a-fA-F]{4,}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]$")] private static partial Regex PciSlot();
}
