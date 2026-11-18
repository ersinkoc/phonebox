using System.Diagnostics;
using System.Globalization;

namespace PhoneBox.Agent;

/// <summary>
/// One DRM client (an open file description of a GPU device) as its <c>/proc/&lt;pid&gt;/fdinfo/&lt;fd&gt;</c> shows it.
/// <see cref="Busy"/> is per engine class: busy nanoseconds for i915 (<c>drm-engine-&lt;class&gt;</c>), busy GPU cycles for xe
/// (<c>drm-cycles-&lt;class&gt;</c>), in which case <see cref="TotalCycles"/> has the cycles that passed for the same class.
/// </summary>
internal sealed record DrmClient(string Driver, string ClientId, string? Pdev,
    IReadOnlyDictionary<string, ulong> Busy, IReadOnlyDictionary<string, ulong> TotalCycles, IReadOnlyDictionary<string, int> Capacity);

/// <summary>
/// GPU load of Intel cards on Linux. i915 and xe have no <c>gpu_busy_percent</c> in sysfs; like nvtop and intel_gpu_top,
/// the load is added up from the usage counters every client's DRM fdinfo has (kernel doc: drm-usage-stats), between two
/// samples. Only the counters are read, and only from processes this user may look at: without root, other users'
/// processes are not counted.
/// </summary>
internal sealed class DrmUsage(Func<double>? clock = null)
{
    /// <summary>fdinfo files read per sample at most; the scan stops there.</summary>
    internal const int MaxFdinfoReads = 4096;
    /// <summary>Open files looked at per sample at most (most are skipped by their link target without being read).</summary>
    internal const int MaxFds = 200_000;

    private readonly Lock _lock = new();
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private List<DrmClient>? _prev;
    private double _prevAt;
    private string? _prevRoot;
    // cards a client with busy counters was seen on: an older kernel (i915 before 5.19, early xe) has none, and its card
    // stays without a value instead of showing 0
    private readonly HashSet<string> _counting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scans <paramref name="procRoot"/> and returns the load in % of each of <paramref name="pdevs"/> since the previous
    /// call. The first call only takes the baseline, and a card whose clients never showed busy counters has no value
    /// either: such a card is missing from the answer (no value, not 0).
    /// </summary>
    public Dictionary<string, double> Sample(string procRoot, IReadOnlyCollection<string> pdevs)
    {
        lock (_lock)
        {
            var now = _clock();
            var cur = Scan(procRoot);
            if (_prevRoot != procRoot) _counting.Clear();
            foreach (var c in cur)
                if (c.Pdev is { } p && c.Busy.Count > 0) _counting.Add(p);
            var load = _prev is not null && _prevRoot == procRoot
                ? Utilization(_prev, cur, now - _prevAt, pdevs.Where(_counting.Contains).ToList())
                : [];
            (_prev, _prevAt, _prevRoot) = (cur, now, procRoot);
            return load;
        }
    }

    /// <summary>
    /// The load in % per card: per engine class the busy share summed over the clients and divided by the class's engine
    /// count, and the card's load is its busiest class (what nvtop and intel_gpu_top show). A client counts only when it is in
    /// both samples; a counter that went backwards (the fd was reused) is skipped for this interval.
    /// </summary>
    internal static Dictionary<string, double> Utilization(IEnumerable<DrmClient> prev, IEnumerable<DrmClient> cur, double seconds, IEnumerable<string> pdevs)
    {
        var load = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!(seconds > 0)) return load;
        var before = Index(prev);
        var now = Index(cur);
        foreach (var pdev in pdevs)
        {
            var classes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var ((p, _), c) in now)
            {
                if (!string.Equals(p, pdev, StringComparison.OrdinalIgnoreCase)) continue;
                if (!before.TryGetValue((p, c.ClientId), out var b)) continue;
                foreach (var (cls, busy) in c.Busy)
                {
                    if (!b.Busy.TryGetValue(cls, out var busy0) || busy < busy0) continue;
                    double share;
                    if (c.TotalCycles.TryGetValue(cls, out var total))
                    {
                        // xe: busy cycles over the GPU cycles that passed for this client, independent of the wall clock
                        if (!b.TotalCycles.TryGetValue(cls, out var total0) || total <= total0) continue;
                        share = (busy - busy0) / (double)(total - total0);
                    }
                    else share = (busy - busy0) / (seconds * 1e9);
                    var engines = c.Capacity.TryGetValue(cls, out var n) && n > 1 ? n : 1;
                    classes[cls] = classes.GetValueOrDefault(cls) + share / engines;
                }
            }
            var max = classes.Count > 0 ? classes.Values.Max() : 0;
            load[pdev] = Math.Round(Math.Clamp(max * 100, 0, 100), 1);
        }
        return load;
    }

    /// <summary>One entry per client: several fds can share one open file description and show the same counters.</summary>
    private static Dictionary<(string Pdev, string ClientId), DrmClient> Index(IEnumerable<DrmClient> clients)
    {
        var map = new Dictionary<(string, string), DrmClient>();
        foreach (var c in clients)
            if (c.Pdev is { } p) map.TryAdd((p.ToLowerInvariant(), c.ClientId), c);
        return map;
    }

    /// <summary>
    /// The DRM part of one fdinfo file, or null when it isn't a DRM client. Memory (<c>drm-memory-*</c>, <c>drm-total-*</c>,
    /// <c>drm-resident-*</c>…) and other lines are ignored.
    /// </summary>
    internal static DrmClient? ParseFdinfo(string text)
    {
        string? driver = null, client = null, pdev = null;
        var engineNs = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var cycles = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var total = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var capacity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!key.StartsWith("drm-", StringComparison.Ordinal) || value.Length == 0) continue;
            switch (key)
            {
                case "drm-driver": driver = value; continue;
                case "drm-client-id": client = value; continue;
                case "drm-pdev": pdev = value.ToLowerInvariant(); continue;
            }
            // the longer prefixes first: "drm-engine-capacity-video" is not an engine called "capacity-video"
            if (Class(key, "drm-engine-capacity-") is { } capClass)
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) capacity[capClass] = n;
            }
            else if (Class(key, "drm-engine-") is { } engClass)
            {
                if (Counter(value, "ns") is { } ns) engineNs[engClass] = ns;
            }
            else if (Class(key, "drm-total-cycles-") is { } totClass)
            {
                if (Counter(value, null) is { } t) total[totClass] = t;
            }
            else if (Class(key, "drm-cycles-") is { } cycClass)
            {
                if (Counter(value, null) is { } cy) cycles[cycClass] = cy;
            }
        }
        if (driver is null || client is null) return null;

        // cycles need their total to mean anything; a class with both kinds of counter is read as cycles
        var busy = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var totals = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (cls, cy) in cycles)
            if (total.TryGetValue(cls, out var t)) { busy[cls] = cy; totals[cls] = t; }
        foreach (var (cls, ns) in engineNs) busy.TryAdd(cls, ns);
        return new DrmClient(driver, client, pdev, busy, totals, capacity);
    }

    private static string? Class(string key, string prefix) =>
        key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : null;

    /// <summary>"25662044495 ns" → 25662044495; a unit other than <paramref name="unit"/> (or any unit, when null) is not a counter.</summary>
    private static ulong? Counter(string value, string? unit)
    {
        var space = value.IndexOfAny([' ', '\t']);
        var number = space < 0 ? value : value[..space];
        var rest = space < 0 ? "" : value[space..].Trim();
        if (rest.Length > 0 && rest != unit) return null;
        return ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// The i915 and xe clients of every process this user can read. Pids are visited in order and the work is bounded, so a
    /// capped scan sees the same processes each time. Anything unreadable (another user's process, one that just exited) is skipped.
    /// </summary>
    internal static List<DrmClient> Scan(string procRoot)
    {
        var list = new List<DrmClient>();
        string[] dirs;
        try { dirs = Directory.GetDirectories(procRoot); }
        catch (IOException) { return list; }
        catch (UnauthorizedAccessException) { return list; }

        var pids = dirs.Select(d => long.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : -1)
            .Where(pid => pid >= 0).Order();
        int reads = 0, fds = 0;
        foreach (var pid in pids)
        {
            var proc = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture));
            string[] files;
            try { files = Directory.GetFiles(Path.Combine(proc, "fdinfo")); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (++fds > MaxFds || reads >= MaxFdinfoReads) return list;
                if (!MaybeGpu(Path.Combine(proc, "fd", Path.GetFileName(file)))) continue;
                reads++;
                try
                {
                    var text = File.ReadAllText(file);
                    if (!text.Contains("drm-driver:", StringComparison.Ordinal)) continue;
                    if (ParseFdinfo(text) is { Driver: "i915" or "xe", Pdev: not null } c) list.Add(c);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return list;
    }

    /// <summary>
    /// An fd whose link points elsewhere than <c>/dev/dri</c> (a socket, a pipe, a regular file) is not a GPU client, so its
    /// fdinfo isn't read. When the link can't be read, the fdinfo decides.
    /// </summary>
    private static bool MaybeGpu(string fdLink)
    {
        try { return new FileInfo(fdLink).LinkTarget is not { } target || target.StartsWith("/dev/dri/", StringComparison.Ordinal); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
