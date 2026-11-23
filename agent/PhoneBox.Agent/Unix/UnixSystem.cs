using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>CPU, memory and temperature readings on Linux (/proc, /sys) and macOS (Mach, sysctl, vm_stat).</summary>
public static partial class UnixSystem
{
    /// <summary>Cumulative CPU ticks: (idle, total). The load is the change of busy ticks over the change of total ticks.</summary>
    public static (long Idle, long Total) CpuTimes()
    {
        if (OperatingSystem.IsMacOS())
        {
            var ticks = new int[4]; // user, system, idle, nice
            var count = 4;
            if (host_statistics(mach_host_self(), 3 /* HOST_CPU_LOAD_INFO */, ticks, ref count) != 0) return (0, 0);
            return (ticks[2], (long)ticks[0] + ticks[1] + ticks[2] + ticks[3]);
        }
        var line = File.ReadLines("/proc/stat").First(); // cpu user nice system idle iowait irq softirq steal …
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
        return (f[3] + f[4], f.Sum());
    }

    private static (double Used, double Total) _mem;
    private static long _memAt;

    /// <summary>(used, total) memory in bytes.</summary>
    public static (double Used, double Total) Memory()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (Environment.TickCount64 - _memAt < 5000 && _mem.Total > 0) return _mem;
            _memAt = Environment.TickCount64;
            var total = double.TryParse(Sh.Run("sysctl", "-n", "hw.memsize")?.Trim(), out var t) ? t : 0;
            var vm = Sh.Run("vm_stat") ?? "";
            var page = PageSizeRe().Match(vm) is { Success: true } ps ? double.Parse(ps.Groups[1].Value) : 4096;
            double Pages(string name) => Regex.Match(vm, $@"{Regex.Escape(name)}:\s+(\d+)") is { Success: true } m ? double.Parse(m.Groups[1].Value) : 0;
            var anon = Pages("Anonymous pages");
            var app = anon > 0 ? Math.Max(0, anon - Pages("Pages purgeable")) : Pages("Pages active");
            var used = (app + Pages("Pages wired down") + Pages("Pages occupied by compressor")) * page;
            return _mem = (used, total);
        }
        long Kb(string key) => File.ReadLines("/proc/meminfo").Where(l => l.StartsWith(key + ":"))
            .Select(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])).FirstOrDefault() * 1024;
        var tot = Kb("MemTotal");
        return (tot - Kb("MemAvailable"), tot);
    }

    public static string CpuName()
    {
        if (OperatingSystem.IsMacOS()) return Sh.Run("sysctl", "-n", "machdep.cpu.brand_string")?.Trim() is { Length: > 0 } n ? n : "CPU";
        var info = Sh.ReadFile("/proc/cpuinfo") ?? "";
        foreach (var key in new[] { "model name", "Model", "Hardware" })
            if (Regex.Match(info, $@"^{key}\s*:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m) return m.Groups[1].Value.Trim();
        return "CPU";
    }

    /// <summary>CPU package temperature in °C from Linux hwmon (k10temp, zenpower, coretemp…); null on macOS.</summary>
    public static double? CpuTemp()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/hwmon")) return null;
        string[] preferred = ["k10temp", "zenpower", "coretemp", "cpu_thermal", "soc_thermal", "acpitz"];
        var sensors = Directory.GetDirectories("/sys/class/hwmon")
            .Select(d => (Dir: d, Name: Sh.ReadFile(Path.Combine(d, "name"))?.Trim() ?? ""))
            .Where(s => preferred.Contains(s.Name))
            .OrderBy(s => Array.IndexOf(preferred, s.Name));
        foreach (var (dir, _) in sensors)
            if (double.TryParse(Sh.ReadFile(Path.Combine(dir, "temp1_input"))?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milli))
                return Math.Round(milli / 1000, 1);
        return null;
    }

    [DllImport("/usr/lib/libSystem.dylib")] private static extern uint mach_host_self();
    [DllImport("/usr/lib/libSystem.dylib")] private static extern int host_statistics(uint host, int flavor, [In, Out] int[] info, ref int count);

    [GeneratedRegex(@"page size of (\d+) bytes")] private static partial Regex PageSizeRe();
}

/// <summary>
/// Linux: the CPU package power in watts from RAPL's energy counter, averaged between two reads. Since the 2020 RAPL
/// fix (CVE-2020-8694) most distributions let only root read energy_uj, so for most agents this stays null.
/// </summary>
internal sealed class RaplPower(string zone = "/sys/class/powercap/intel-rapl:0")
{
    private (long Microjoules, long Timestamp)? _last;

    /// <summary>The watts since the previous read; null on the first read or when the counter can't be read.</summary>
    public double? Read(long timestamp)
    {
        if (!long.TryParse(Sh.ReadFile(Path.Combine(zone, "energy_uj"))?.Trim(), CultureInfo.InvariantCulture, out var uj))
        {
            _last = null;
            return null;
        }
        var last = _last;
        _last = (uj, timestamp);
        if (last is not { } before || timestamp <= before.Timestamp) return null;
        var used = uj - before.Microjoules;
        if (used < 0) // the counter wrapped around at max_energy_range_uj
        {
            if (!long.TryParse(Sh.ReadFile(Path.Combine(zone, "max_energy_range_uj"))?.Trim(), CultureInfo.InvariantCulture, out var range)) return null;
            used += range;
        }
        var seconds = (timestamp - before.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        return Math.Round(used / 1e6 / seconds, 1);
    }
}
