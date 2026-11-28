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
    IReadOnlyList<GpuInfo>? Gpus = null, double? CpuPower = null, long Seq = 0, string? RunId = null);

/// <summary>Samples CPU, RAM, network, disk and GPU metrics once a second.</summary>
public sealed partial class SystemMonitor(ILogger<SystemMonitor> log, ConfigStore? config = null) : BackgroundService
{
    // LibreHardwareMonitor's web server often takes 600 ms or more to answer on a busy machine (#442)
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly string _cpuName = ReadCpuName();

    private volatile PcSnapshot? _snap;
    // GPU sources, highest priority first: nvidia-smi, LibreHardwareMonitor, the OS (D3DKMT + counters / sysfs / ioreg)
    private volatile IReadOnlyList<GpuInfo>? _nvidia, _lhmGpus, _osGpus;
    private double? _cpuTemp;
    private double? _cpuPower; // package watts: LibreHardwareMonitor on Windows, RAPL on Linux; none on macOS
    private double? _appleGpuTemp = null; // macOS: the GPU's temperature from the SMC (stays null on Windows), added to the ioreg GPU
    private IReadOnlyList<DiskInfo> _disks = [];
    // counts samples, so the panel can tell a new one from the same one sent again (with a media or volume change)
    private long _seq;
    private readonly string _runId = Guid.NewGuid().ToString("N");

#if WINDOWS
    private volatile string _lhmStatus = "checking";
    public string? LhmStatus => _lhmStatus;
#else
    public string? LhmStatus => null;
#endif

    public PcSnapshot? Snapshot() => _snap;

    internal void PublishSample(PcSnapshot sample) =>
        _snap = sample with { Seq = ++_seq, RunId = _runId };

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

            PublishSample(new PcSnapshot(
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
                Gpus: gpus,
                CpuPower: _cpuPower));
        }
    }

    private Task GpuLoop(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS()) return AppleGpuLoop(ct);
        _ = Task.Run(() => OsGpuLoop(ct), ct);
        return NvidiaLoop(ct);
    }

    // Keeps nvidia-smi running and reads one line per GPU per second, unless Settings turned it off (#716): then it is
    // never started, and a running one is stopped within a second.
    private async Task NvidiaLoop(CancellationToken ct)
    {
        bool Allowed() => config?.Get().NvidiaSmi != false;
        while (!ct.IsCancellationRequested)
        {
            if (!Allowed())
            {
                _nvidia = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                var names = Gpus.ParseNvidiaNames(await RunAsync("nvidia-smi", "--query-gpu=index,name,driver_version --format=csv,noheader", ct));
                if (!Allowed()) continue; // turned off while the names were read: the stream is not started
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=index,utilization.gpu,temperature.gpu,power.draw,power.limit,memory.used,memory.total,fan.speed,clocks.gr,clocks.mem " +
                    "--format=csv,noheader,nounits -lms 1000")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
                using var p = Process.Start(psi)!;
                JobObject.Track(p);
                using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
                // turned off in Settings: stopped within a second even if nvidia-smi has gone quiet
                using var watch = new System.Threading.Timer(_ => { if (!Allowed()) try { p.Kill(); } catch { } }, null, 1000, 1000);
                var cards = new SortedDictionary<int, GpuInfo>();
                while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (!Allowed()) { try { p.Kill(); } catch { } break; }
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

    /// <summary>macOS has no nvidia-smi; the GPU's load and memory come from the IOAccelerator entries of ioreg.</summary>
    private async Task AppleGpuLoop(CancellationToken ct)
    {
        // A GPU with memory of its own (an Intel Mac's Radeon or Intel GPU) has its name and size on its PCI device. The
        // PCI devices are read only once such a GPU has shown up, and again only when the set of them changes (an eGPU
        // plugged in): never on Apple silicon, whose GPU has no memory of its own. Each ioreg start costs WindowServer a
        // Mach port (#695), so the poll below stays the only regular one.
        IReadOnlyDictionary<string, MacPciGpu> cards = new Dictionary<string, MacPciGpu>();
        var cardsFor = "";          // the accelerators the PCI read found every card for
        var triesFor = ""; var tries = 0; var failures = 0;
        var retryPci = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // "" after a timeout: no GPU this round
                var text = await RunIoreg(ct, "-r", "-d", "1", "-w", "0", "-c", "IOAccelerator");
                var own = OwnMemoryAccelerators(text);
                if (own.Length > 0 && own != cardsFor && DateTime.UtcNow >= retryPci)
                {
                    if (own != triesFor) { triesFor = own; tries = 0; }
                    // "" after a timeout; ioreg can also print headings without properties, which finds no card
                    var found = ParseMacPciGpus(await RunIoreg(ct, "-r", "-d", "2", "-w", "0", "-c", "IOPCIDevice"));
                    cards = MergeCards(cards, found);
                    var next = NextPciRead(found.Count > 0, HasEveryCard(cards, own), ref tries, ref failures);
                    if (next is null) cardsFor = own;
                    else retryPci = DateTime.UtcNow + next.Value;
                }
                // the SMC's GPU temperature exists only on Apple silicon; a GPU with memory of its own brings its own
                var gpus = ParseAppleGpus(text, Memory().Total, cards);
                _osGpus = gpus.Count == 0 ? null : gpus.Select(g => g.Integrated && g.Vendor == "apple" ? g with { Temp = _appleGpuTemp ?? g.Temp } : g).ToList();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogDebug("apple-gpu: {Msg}", ex.Message); _osGpus = null; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

#if !WINDOWS
    /// <summary>
    /// macOS: CPU and GPU temperature from the SMC every 2 s. Without an SMC or its temperature keys (Intel, macOS 13
    /// and older) it stops and both stay null; a failed read clears them and opens the SMC again 30 s later.
    /// </summary>
    private async Task AppleTempLoop(CancellationToken ct)
    {
        AppleSmc? smc = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    smc ??= AppleSmc.Open();
                    if (smc is null) { log.LogInformation("CPU temperature: no SMC temperature sensors on this Mac"); return; }
                    (_cpuTemp, _appleGpuTemp) = smc.Read();
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogDebug("apple-smc: {Msg}", ex.Message);
                    (_cpuTemp, _appleGpuTemp) = (null, null);
                    smc?.Dispose();
                    smc = null;
                    await Task.Delay(30_000, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { smc?.Dispose(); }
    }
#endif

    /// <summary>
    /// Runs ioreg with fixed arguments. A run that takes over 5 s is killed and gives ""; one the caller cancels is
    /// killed too, and the cancellation is passed on.
    /// </summary>
    private static async Task<string> RunIoreg(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("ioreg")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return text;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch { }
            if (ct.IsCancellationRequested) throw;
            return "";
        }
    }

    /// <summary>A graphics card with memory of its own on the PCI bus. <c>Vendor</c> is its PCI vendor id (0x1002 AMD, 0x8086 Intel), 0 when unknown.</summary>
    internal readonly record struct MacPciGpu(string Name, double Bytes, int Vendor);

    /// <summary>The registry id from an ioreg entry's heading (<c>&lt;class X, id 0x10000039e, …&gt;</c>), or "".</summary>
    private static string RegistryId(string entry) =>
        Regex.Match(entry, "<class [^,>]+, id (0x[0-9a-fA-F]+)") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : "";

    /// <summary>The accelerator entries of <c>ioreg -r -d 1 -c IOAccelerator</c>, once each (ioreg can list one twice).</summary>
    private static List<string> AcceleratorEntries(string ioreg)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>();
        foreach (var entry in ioreg.Split("+-o ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.Contains("\"Device Utilization %\"", StringComparison.Ordinal)) continue;
            var id = RegistryId(entry);
            if (id.Length > 0 && !seen.Add(id)) continue;
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// The ids of the accelerators with memory of their own (they report <c>inUseVidMemoryBytes</c>), sorted and joined:
    /// "" on Apple silicon. A change means a GPU came or went, so its PCI device must be read.
    /// </summary>
    internal static string OwnMemoryAccelerators(string ioreg) => string.Join(",", AcceleratorEntries(ioreg)
        .Where(e => e.Contains("\"inUseVidMemoryBytes\"", StringComparison.Ordinal))
        .Select(e => RegistryId(e) is { Length: > 0 } id ? id : AcceleratorClass(e))
        .Order(StringComparer.Ordinal));

    /// <summary>
    /// The graphics cards among the PCI devices (<c>ioreg -r -d 2 -c IOPCIDevice</c>, which lists each device's own
    /// children), keyed by the registry id of each child: an accelerator's id finds the card it belongs to. A card is a
    /// device with "VRAM,totalMB"; its name and vendor come from the same device.
    /// </summary>
    internal static IReadOnlyDictionary<string, MacPciGpu> ParseMacPciGpus(string ioreg)
    {
        var cards = new Dictionary<string, MacPciGpu>();
        // a device starts at the beginning of a line; its children's headings are indented under it
        foreach (var device in Regex.Split(ioreg, "(?m)^(?=\\+-o )"))
        {
            var vram = Regex.Match(device, "\"VRAM,totalMB\"\\s*=\\s*(\\d+)");
            if (!vram.Success) continue;
            var mb = double.Parse(vram.Groups[1].Value, CultureInfo.InvariantCulture);
            if (mb <= 0) continue;
            var model = Regex.Match(device, "\"model\"\\s*=\\s*<?\"([^\"]+)\"");
            // ioreg prints the id as little-endian bytes: <02100000> is 0x1002
            var id = Regex.Match(device, "\"vendor-id\"\\s*=\\s*<([0-9a-fA-F]{2})([0-9a-fA-F]{2})");
            var card = new MacPciGpu(model.Success ? model.Groups[1].Value : "GPU", mb * 1024 * 1024,
                id.Success ? Convert.ToInt32(id.Groups[2].Value + id.Groups[1].Value, 16) : 0);
            foreach (Match child in Regex.Matches(device, "(?m)^[ |]+\\+-o [^<]*<class [^,>]+, id (0x[0-9a-fA-F]+)"))
                cards[child.Groups[1].Value.ToLowerInvariant()] = card;
        }
        return cards;
    }

    /// <summary>The cards an earlier PCI read found, with a new read's added on top: a partial read never loses a card.</summary>
    internal static IReadOnlyDictionary<string, MacPciGpu> MergeCards(IReadOnlyDictionary<string, MacPciGpu> before, IReadOnlyDictionary<string, MacPciGpu> found)
    {
        if (found.Count == 0) return before;
        var merged = new Dictionary<string, MacPciGpu>(before);
        foreach (var (id, card) in found) merged[id] = card;
        return merged;
    }

    /// <summary>
    /// When to read the PCI devices again, or null when the read is done. A read that found no card at all failed (a
    /// timeout, or headings without properties): it is tried again after 1, 2, 4, … minutes, at most 30, for as long as it
    /// takes. A read that found cards but not every one counts as a try: three, a minute apart, then it is done, so a GPU
    /// whose PCI device has no "VRAM,totalMB" doesn't cost an ioreg start every minute.
    /// </summary>
    internal static TimeSpan? NextPciRead(bool foundAny, bool foundEvery, ref int tries, ref int failures)
    {
        if (foundEvery) { failures = 0; return null; }
        if (!foundAny) return TimeSpan.FromMinutes(Math.Min(30, 1 << Math.Min(failures++, 5)));
        failures = 0;
        return ++tries >= 3 ? null : TimeSpan.FromMinutes(1);
    }

    /// <summary>Whether every accelerator of <see cref="OwnMemoryAccelerators"/> has found its card.</summary>
    internal static bool HasEveryCard(IReadOnlyDictionary<string, MacPciGpu> cards, string ownMemoryAccelerators) =>
        ownMemoryAccelerators.Split(',', StringSplitOptions.RemoveEmptyEntries).All(cards.ContainsKey);

    /// <summary>
    /// Every GPU in the ioreg text, each read from its own accelerator entry. On Apple silicon the memory is unified:
    /// the total is the machine's RAM, the used part what the GPU holds. A GPU with memory of its own (an Intel Mac's
    /// Radeon or Intel GPU) reports its video memory in use, temperature and power; its name and total come from its own
    /// PCI device in <paramref name="cards"/>, found by the accelerator's registry id, so two cards never mix.
    /// </summary>
    internal static List<GpuInfo> ParseAppleGpus(string ioreg, double totalMemoryBytes, IReadOnlyDictionary<string, MacPciGpu>? cards = null)
    {
        const double Gb = 1024.0 * 1024 * 1024;
        var gpus = new List<GpuInfo>();
        foreach (var entry in AcceleratorEntries(ioreg))
        {
            var util = Regex.Match(entry, "\"Device Utilization %\"\\s*=\\s*(\\d+)");
            if (!util.Success) continue;
            var load = double.Parse(util.Groups[1].Value, CultureInfo.InvariantCulture);
            var model = Regex.Match(entry, "\"model\"\\s*=\\s*\"([^\"]+)\"");
            var vid = Regex.Match(entry, "\"inUseVidMemoryBytes\"\\s*=\\s*(\\d+)");
            if (!vid.Success)
            {
                var alloc = Regex.Match(entry, "\"Alloc system memory\"\\s*=\\s*(\\d+)");
                gpus.Add(new GpuInfo(model.Success ? model.Groups[1].Value : "Apple GPU", load, null, null, null,
                    alloc.Success ? Math.Round(double.Parse(alloc.Groups[1].Value, CultureInfo.InvariantCulture) / Gb, 1) : null,
                    totalMemoryBytes > 0 ? Math.Round(totalMemoryBytes / Gb, 1) : null, null, null,
                    Vendor: "apple", Source: "ioreg", Integrated: true));
                continue;
            }
            // a sensor that reads 0 (stopped fan, idle clock) shows as nothing, like a missing one
            double? Stat(string key) => Regex.Match(entry, "\"" + Regex.Escape(key) + "\"\\s*=\\s*(\\d+)") is { Success: true } m
                && double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is > 0 and var v ? v : null;
            var accel = AcceleratorClass(entry);
            var vendor = accel.StartsWith("AMD", StringComparison.Ordinal) ? "amd" : accel.StartsWith("Intel", StringComparison.Ordinal) ? "intel" : null;
            MacPciGpu? card = cards is not null && cards.TryGetValue(RegistryId(entry), out var c) ? c : null;
            gpus.Add(new GpuInfo(card?.Name ?? (model.Success ? model.Groups[1].Value : "GPU"), load,
                Stat("Temperature(C)"), Stat("Total Power(W)"), null,
                Math.Round(double.Parse(vid.Groups[1].Value, CultureInfo.InvariantCulture) / Gb, 1),
                card is { } k ? Math.Round(k.Bytes / Gb, 1) : null, Stat("Fan Speed(%)"), Stat("Core Clock(MHz)"),
                // an Intel GPU's memory is carved out of the machine's RAM
                Vendor: vendor, Source: "ioreg", Integrated: vendor == "intel", MemClock: Stat("Memory Clock(MHz)"),
                FanRpm: Stat("Fan Speed(RPM)")));
        }
        return gpus;
    }

    /// <summary>The first GPU of <see cref="ParseAppleGpus"/>, or null.</summary>
    internal static GpuInfo? ParseAppleGpu(string ioreg, double totalMemoryBytes) =>
        ParseAppleGpus(ioreg, totalMemoryBytes) is { Count: > 0 } gpus ? gpus[0] : null;

    private static string AcceleratorClass(string block) =>
        Regex.Match(block, "\"IOClass\"\\s*=\\s*\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : "";

    // CPU temperature: Windows from LibreHardwareMonitor's web server, when it is running; Linux from hwmon;
    // macOS on Apple silicon from the SMC (CPU and GPU). The same LibreHardwareMonitor answer carries the GPU sensors
    // too (hot spot, board power, fan, clocks).
    private async Task CpuTempLoop(CancellationToken ct)
    {
#if !WINDOWS
        _lhmGpus = null; // LibreHardwareMonitor is Windows only
        if (OperatingSystem.IsMacOS()) { await AppleTempLoop(ct); return; }
        var rapl = new RaplPower();
        while (!ct.IsCancellationRequested)
        {
            _cpuTemp = UnixSystem.CpuTemp();
            _cpuPower = rapl.Read(Stopwatch.GetTimestamp());
            await Task.Delay(_cpuTemp is null && _cpuPower is null ? 30_000 : 2000, ct);
        }
        return;
#else
#pragma warning disable CS0162
        var failures = 0;
        int? previousPort = null;
        while (!ct.IsCancellationRequested)
        {
            var port = config?.Get().LibreHardwareMonitorPort ?? AgentConfig.DefaultLibreHardwareMonitorPort;
            if (previousPort is { } oldPort && oldPort != port)
            {
                failures = 0;
                ClearLhmReadings("checking");
            }
            previousPort = port;
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(LhmDataUrl(port), ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Children", out var children) || children.ValueKind != JsonValueKind.Array)
                    throw new JsonException("LibreHardwareMonitor sensor tree is missing.");
                _cpuTemp = FindCpuTemp(doc.RootElement);
                _cpuPower = FindCpuPower(doc.RootElement);
                failures = 0;
                _lhmStatus = "connected";
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
                (var clear, delay) = LhmFailed(ref failures);
                if (clear) ClearLhmReadings("unavailable");
            }
            await Task.Delay(delay, ct);
        }
#endif
    }

    internal static string LhmDataUrl(int port) => $"http://127.0.0.1:{port}/data.json";

#if WINDOWS
    private void ClearLhmReadings(string status)
    {
        _cpuTemp = null;
        _cpuPower = null;
        _lhmGpus = null;
        _lhmStatus = status;
    }
#endif

    /// <summary>
    /// After a failed LibreHardwareMonitor read: a slow answer now and then keeps the last values and asks again soon;
    /// <see cref="LhmKeepFailures"/> failures in a row (it isn't running) clear them and wait 30 s before the next try.
    /// </summary>
    internal static (bool Clear, TimeSpan Delay) LhmFailed(ref int failures) =>
        ++failures >= LhmKeepFailures ? (true, TimeSpan.FromSeconds(30)) : (false, TimeSpan.FromSeconds(2));

    internal const int LhmKeepFailures = 3;

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
            // a null or non-object entry is skipped, not allowed to throw away the readings after it
            if (node.ValueKind != JsonValueKind.Object) return;
            inCpu = inCpu || IsCpuNode(node);
            if (inCpu && StrProp(node, "Text") is { } name && CoreSensor().IsMatch(name)
                && ReadCelsius(node) is { } t && (hottest is null || t > hottest))
                hottest = t;
            if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) Visit(k, inCpu);
        }
        Visit(root, false);
        return hottest;
    }

    /// <summary>
    /// The CPU package power from LibreHardwareMonitor's data.json: "CPU Package" (Intel) or "Package" (AMD) in watts.
    /// Only a CPU's own node counts, so a GPU's "GPU Package" can't be taken; the unit tells it from the temperature.
    /// </summary>
    internal static double? FindCpuPower(JsonElement root)
    {
        double? Visit(JsonElement node, bool inCpu)
        {
            if (node.ValueKind != JsonValueKind.Object) return null;
            inCpu = inCpu || IsCpuNode(node);
            if (inCpu && StrProp(node, "Text") is "CPU Package" or "Package" && StrProp(node, "Value") is { } value
                && Gpus.ParseLhmValue(value, StrProp(node, "Type")) is { Kind: "power" } power)
                return power.Value;
            if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray())
                    if (Visit(k, inCpu) is { } watts) return watts;
            return null;
        }
        return Visit(root, false);
    }

    /// <summary>A string property of an object node; null when it is missing or not a string.</summary>
    private static string? StrProp(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsCpuNode(JsonElement node) =>
        (StrProp(node, "HardwareId") is { } hw
            && (hw.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase) || hw.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase)))
        || (StrProp(node, "ImageURL") is { } url && url.EndsWith("/cpu.png", StringComparison.OrdinalIgnoreCase));

    /// <summary>A temperature value such as "54,0 °C" or "140.0 °F"; load and clock sensors share the core names but have no °.</summary>
    private static double? ReadCelsius(JsonElement node)
    {
        if (!node.TryGetProperty("Value", out var val) || val.ValueKind != JsonValueKind.String || val.GetString() is not { } s || !s.Contains('°')) return null;
        // an older reply has no type; one that names a type must name a temperature
        if (node.TryGetProperty("Type", out var type) && type.ValueKind != JsonValueKind.Null
            && (type.ValueKind != JsonValueKind.String || type.GetString() != "Temperature")) return null;
        return Gpus.ParseLhmValue(s, "Temperature") is { Kind: "temp" } parsed ? parsed.Value : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:CPU )?Core #\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CoreSensor();

    private static double? FindTemp(JsonElement node, string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String && names.Contains(text.GetString())
            && ReadCelsius(node) is { } temp)
            return temp;
        if (node.TryGetProperty("Children", out var kids) && kids.ValueKind == JsonValueKind.Array)
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
