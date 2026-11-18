using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// One GPU. Units: load and fan duty in %, temperatures in °C, power in W, memory in GB, clocks in MHz.
/// PowerPct is the board power as a share of its limit (all Windows reports for cards without a vendor tool);
/// Shared* is system memory the GPU uses (what an integrated GPU mostly lives on).
/// </summary>
public sealed record GpuInfo(string Name, double? Util, double? Temp, double? Power, double? PowerLimit,
    double? VramUsed, double? VramTotal, double? Fan, double? Clock,
    string? Vendor = null, string? Source = null, bool Integrated = false,
    double? HotSpot = null, double? MemTemp = null, double? MemClock = null,
    double? SharedUsed = null, double? SharedTotal = null, double? PowerPct = null, double? FanRpm = null,
    double? TempMax = null, string? Driver = null);

public sealed record DiskInfo(string Name, double UsedGb, double TotalGb);

public sealed record PcSnapshot(double? Cpu, double? CpuTemp, string CpuName, double RamUsedGb, double RamTotalGb,
    GpuInfo? Gpu, double NetDownBps, double NetUpBps, IReadOnlyList<DiskInfo> Disks, long UptimeSec,
    IReadOnlyList<GpuInfo>? Gpus = null);

/// <summary>Samples CPU, RAM, network, disk and GPU metrics once a second.</summary>
public sealed partial class SystemMonitor(ILogger<SystemMonitor> log, ConfigStore? config = null) : BackgroundService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(600) };
    private readonly string _cpuName = ReadCpuName();

    private volatile PcSnapshot? _snap;
    // GPU sources, highest priority first: nvidia-smi, LibreHardwareMonitor, the OS (D3DKMT + counters / sysfs / ioreg)
    private volatile IReadOnlyList<GpuInfo>? _nvidia, _lhmGpus, _osGpus;
    private double? _cpuTemp;
    private IReadOnlyList<DiskInfo> _disks = [];

    public PcSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => GpuLoop(ct), ct);
        _ = Task.Run(() => CpuTempLoop(ct), ct);

        var (idle0, total0) = CpuTimes();
        var net0 = NetBytes(Chosen());
        var t0 = Stopwatch.GetTimestamp();
        var tick = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            var (idle1, total1) = CpuTimes();
            var total = total1 - total0;
            double? cpu = total > 0 ? Math.Clamp(100.0 * (total - (idle1 - idle0)) / total, 0, 100) : null;
            (idle0, total0) = (idle1, total1);

            var net1 = NetBytes(Chosen());
            var t1 = Stopwatch.GetTimestamp();
            var secs = Math.Max(0.001, (t1 - t0) / (double)Stopwatch.Frequency);
            var (down, up) = NetworkRates(net0, net1, secs);
            (net0, t0) = (net1, t1);

            if (tick++ % 15 == 0) _disks = ReadDisks();

            var (memUsed, memTotal) = Memory();
            const double Gb = 1024d * 1024 * 1024;
            var gpus = Gpus.Compose(_nvidia, _lhmGpus, _osGpus);

            _snap = new PcSnapshot(
                Cpu: cpu is { } c ? Math.Round(c, 1) : null,
                CpuTemp: _cpuTemp,
                CpuName: _cpuName,
                RamUsedGb: Math.Round(memUsed / Gb, 1),
                RamTotalGb: Math.Round(memTotal / Gb, 1),
                Gpu: gpus.FirstOrDefault(),
                NetDownBps: Math.Round(down),
                NetUpBps: Math.Round(up),
                Disks: _disks,
                UptimeSec: Environment.TickCount64 / 1000,
                Gpus: gpus);
        }
    }

    private Task GpuLoop(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS()) return AppleGpuLoop(ct);
        _ = Task.Run(() => OsGpuLoop(ct), ct);
        return NvidiaLoop(ct);
    }

    // Keeps nvidia-smi running and reads one line per GPU per second.
    private async Task NvidiaLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var names = Gpus.ParseNvidiaNames(await RunAsync("nvidia-smi", "--query-gpu=index,name,driver_version --format=csv,noheader", ct));
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=index,utilization.gpu,temperature.gpu,power.draw,power.limit,memory.used,memory.total,fan.speed,clocks.gr,clocks.mem " +
                    "--format=csv,noheader,nounits -lms 1000")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
                using var p = Process.Start(psi)!;
                JobObject.Track(p);
                using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
                var cards = new SortedDictionary<int, GpuInfo>();
                while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (Gpus.ParseNvidiaLine(line, names) is not { } row) continue;
                    cards[row.Index] = row.Gpu;
                    _nvidia = cards.Values.ToList();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogDebug("nvidia-smi: {Msg}", ex.Message); }
            _nvidia = null;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    // Every vendor's GPU through the OS: Windows' graphics kernel and GPU counters, Linux sysfs (amdgpu, i915/xe).
    private async Task OsGpuLoop(CancellationToken ct)
    {
#if WINDOWS
        using var win = new WindowsGpu();
#endif
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
#if WINDOWS
                _osGpus = win.Sample();
#else
                _osGpus = Gpus.ReadSysfs();
#endif
                failures = 0;
            }
            catch (Exception ex)
            {
                if (failures++ == 0) log.LogWarning("gpu: {Msg}", ex.Message);
                _osGpus = null;
            }
            // nothing to read (no GPU driver, a VM): look again now and then
            await Task.Delay(_osGpus is { Count: > 0 } ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <summary>macOS has no nvidia-smi; the GPU's load and memory come from the IOAccelerator entry of ioreg.</summary>
    private async Task AppleGpuLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo("ioreg", "-r -d 1 -w 0 -c IOAccelerator")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppPaths.NeutralCwd,
                };
                using var p = Process.Start(psi)!;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
                    await p.WaitForExitAsync(cts.Token);
                    _osGpus = ParseAppleGpu(text, Memory().Total) is { } g ? [g] : null;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { p.Kill(); } catch { } _osGpus = null; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogDebug("apple-gpu: {Msg}", ex.Message); _osGpus = null; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>Reads the GPU from the ioreg text. The memory is unified: the total is the machine's RAM, the used part what the GPU holds.</summary>
    internal static GpuInfo? ParseAppleGpu(string ioreg, double totalMemoryBytes)
    {
        var util = Regex.Match(ioreg, "\"Device Utilization %\"\\s*=\\s*(\\d+)");
        if (!util.Success) return null; // no accelerator entry: nothing to show
        var model = Regex.Match(ioreg, "\"model\"\\s*=\\s*\"([^\"]+)\"");
        var alloc = Regex.Match(ioreg, "\"Alloc system memory\"\\s*=\\s*(\\d+)");
        const double Gb = 1024.0 * 1024 * 1024;
        return new GpuInfo(model.Success ? model.Groups[1].Value : "Apple GPU",
            double.Parse(util.Groups[1].Value, CultureInfo.InvariantCulture), null, null, null,
            alloc.Success ? Math.Round(double.Parse(alloc.Groups[1].Value, CultureInfo.InvariantCulture) / Gb, 1) : null,
            totalMemoryBytes > 0 ? Math.Round(totalMemoryBytes / Gb, 1) : null, null, null,
            Vendor: "apple", Source: "ioreg", Integrated: true);
    }

    // CPU temperature: Windows from LibreHardwareMonitor's web server (port 8085), when it is running; Linux from hwmon.
    // The same LibreHardwareMonitor answer carries the GPU sensors too (hot spot, board power, fan, clocks).
    private async Task CpuTempLoop(CancellationToken ct)
    {
#if !WINDOWS
        _lhmGpus = null; // LibreHardwareMonitor is Windows only
        while (!ct.IsCancellationRequested)
        {
            _cpuTemp = UnixSystem.CpuTemp();
            await Task.Delay(_cpuTemp is null ? 30_000 : 2000, ct);
        }
        return;
#else
#pragma warning disable CS0162
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync("http://127.0.0.1:8085/data.json", ct));
                _cpuTemp = FindCpuTemp(doc.RootElement);
                try { _lhmGpus = Gpus.ParseLhm(doc.RootElement); }
                catch (Exception ex)
                {
                    log.LogDebug("lhm gpu: {Msg}", ex.Message);
                    _lhmGpus = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                _cpuTemp = null;
                _lhmGpus = null;
                delay = TimeSpan.FromSeconds(30);
            }
            await Task.Delay(delay, ct);
        }
#endif
    }

    private static readonly string[] CpuTempNames = ["Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Package", "Tdie", "Tctl", "Core (Tdie)"];

    /// <summary>
    /// The CPU temperature from LibreHardwareMonitor's data.json: the package (Intel) or Tctl/Tdie (AMD) reading when
    /// there is one; otherwise the hottest "CPU Core #n" / "Core #n" temperature of a CPU, which is all that older
    /// Intel processors report. Core sensors count only under a CPU's own node, so a GPU or disk can't be mistaken.
    /// </summary>
    internal static double? FindCpuTemp(JsonElement root)
    {
        if (FindTemp(root, CpuTempNames) is { } package) return package;
        double? hottest = null;
        void Visit(JsonElement node, bool inCpu)
        {
            inCpu = inCpu || IsCpuNode(node);
            if (inCpu && node.TryGetProperty("Text", out var text) && text.GetString() is { } name && CoreSensor().IsMatch(name)
                && ReadCelsius(node) is { } t && (hottest is null || t > hottest))
                hottest = t;
            if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) Visit(k, inCpu);
        }
        Visit(root, false);
        return hottest;
    }

    private static bool IsCpuNode(JsonElement node) =>
        (node.TryGetProperty("HardwareId", out var id) && id.GetString() is { } hw
            && (hw.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase) || hw.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase)))
        || (node.TryGetProperty("ImageURL", out var img) && img.GetString() is { } url && url.EndsWith("/cpu.png", StringComparison.OrdinalIgnoreCase));

    /// <summary>A temperature value such as "54,0 °C"; load and clock sensors share the core names but have no °.</summary>
    private static double? ReadCelsius(JsonElement node)
    {
        if (!node.TryGetProperty("Value", out var val) || val.GetString() is not { } s || !s.Contains('°')) return null;
        if (node.TryGetProperty("Type", out var type) && type.GetString() is { } ty && ty != "Temperature") return null;
        var num = s.Split(' ')[0].Replace(',', '.');
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:CPU )?Core #\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CoreSensor();

    private static double? FindTemp(JsonElement node, string[] names)
    {
        if (node.TryGetProperty("Text", out var text) && names.Contains(text.GetString())
            && node.TryGetProperty("Value", out var val) && val.GetString() is { } s && s.Contains('°'))
        {
            var num = s.Split(' ')[0].Replace(',', '.');
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) return t;
        }
        if (node.TryGetProperty("Children", out var kids))
            foreach (var k in kids.EnumerateArray())
                if (FindTemp(k, names) is { } t) return t;
        return null;
    }

    private IReadOnlyList<string> Chosen() => config?.Get().Ping.Adapters ?? [];

    /// <summary>Whether the network meters count this adapter: the chosen ones by name, or else the automatic choice.</summary>
    internal static bool Counted(string name, string description, NetworkInterfaceType type, IReadOnlyList<string> chosen)
    {
        if (chosen.Count > 0) return chosen.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) return false;
        var d = description + " " + name;
        if (!OperatingSystem.IsWindows() && (name.StartsWith("docker") || name.StartsWith("veth") || name.StartsWith("br-")
            || name.StartsWith("virbr") || name.StartsWith("utun") || name.StartsWith("awdl") || name.StartsWith("llw"))) return false;
        return !(d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || d.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || d.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || d.Contains("WSL", StringComparison.OrdinalIgnoreCase) || d.Contains("TAP", StringComparison.Ordinal)
            || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Connected adapters for Settings › Ping & network (names and descriptions only; local page).</summary>
    public static IReadOnlyList<object> Adapters(IReadOnlyList<string> chosen) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(ni => (object)new { name = ni.Name, description = ni.Description, auto = Counted(ni.Name, ni.Description, ni.NetworkInterfaceType, []) })
        .ToList();

    private static Dictionary<string, (long Rx, long Tx)> NetBytes(IReadOnlyList<string> chosen)
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (!Counted(ni.Name, ni.Description, ni.NetworkInterfaceType, chosen)) continue;
            try
            {
                var s = ni.GetIPStatistics();
                counters[ni.Id] = (s.BytesReceived, s.BytesSent);
            }
            catch { }
        }
        return counters;
    }

    internal static (double Down, double Up) NetworkRates(
        IReadOnlyDictionary<string, (long Rx, long Tx)> before,
        IReadOnlyDictionary<string, (long Rx, long Tx)> after, double seconds)
    {
        double received = 0, sent = 0;
        foreach (var (id, now) in after)
        {
            // Newly active adapters already carry lifetime totals. Only compare
            // counters for adapters sampled in both readings.
            if (!before.TryGetValue(id, out var previous)) continue;
            if (now.Rx >= previous.Rx) received += now.Rx - previous.Rx;
            if (now.Tx >= previous.Tx) sent += now.Tx - previous.Tx;
        }
        return (received / seconds, sent / seconds);
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        const double Gb = 1024d * 1024 * 1024;
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && SafeSize(d) > 2 * Gb);
        if (!OperatingSystem.IsWindows())
            // Linux / macOS: real volumes only (no snaps, boot partitions or macOS system snapshots)
            drives = drives.Where(d => d.Name == "/" || d.Name.StartsWith("/home") || d.Name.StartsWith("/mnt/") || d.Name.StartsWith("/media/")
                                       || d.Name.StartsWith("/Volumes/") || d.Name.StartsWith("/data"))
                .Where(d => !d.Name.Contains("/wsl") && !d.Name.Contains("docker") && !d.Name.StartsWith("/mnt/c") && d.Name.Length > 0)
                .DistinctBy(d => (SafeSize(d), d.TotalFreeSpace / (1L << 30)));
        return drives
            .Select(d => new DiskInfo(DiskLabel(d.Name), Math.Round((d.TotalSize - d.TotalFreeSpace) / Gb), Math.Round(d.TotalSize / Gb)))
            .Take(6)
            .ToList();
    }

    private static long SafeSize(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }

    private static string DiskLabel(string name) =>
        OperatingSystem.IsWindows() ? name.TrimEnd('\\') : name == "/" ? "/" : Path.GetFileName(name.TrimEnd('/'));

    private static string ReadCpuName()
    {
#if !WINDOWS
        return UnixSystem.CpuName();
#else
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
#endif
    }

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }

#if WINDOWS
    private static (long Idle, long Total) CpuTimes()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        return (idle, kernel + user); // kernel time includes idle time
    }

    private static (double Used, double Total) Memory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        return (mem.ullTotalPhys - mem.ullAvailPhys, mem.ullTotalPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
#else
    private static (long Idle, long Total) CpuTimes() => UnixSystem.CpuTimes();
    private static (double Used, double Total) Memory() => UnixSystem.Memory();
#endif
}
