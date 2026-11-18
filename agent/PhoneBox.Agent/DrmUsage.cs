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
/// Walking every fd of every process is the expensive part, so a full discovery pass runs at most every
/// <c>discoverySeconds</c>; the samples in between re-read only the fdinfo files that pass found, and a new GPU client is
/// picked up by the next pass.
/// </summary>
internal sealed class DrmUsage(Func<double>? clock = null, double discoverySeconds = 30)
{
    /// <summary>fdinfo files read per discovery pass at most; the pass stops there.</summary>
    internal const int MaxFdinfoReads = 4096;
    /// <summary>Open files looked at per discovery pass at most (most are skipped by their link target without being read).</summary>
    internal const int MaxFds = 200_000;

    private readonly Lock _lock = new();
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private readonly Meter _meter = new();
    private string? _root;
    // the fdinfo files of the GPU clients the last discovery pass found, with the client each one showed, and when it ran
    private List<(string Path, (string Pdev, string ClientId) Client)>? _known;
    private double _discoveredAt;

    /// <summary>Full discovery passes so far (for tests).</summary>
    internal int Discoveries { get; private set; }

    /// <summary>
    /// Reads the GPU clients under <paramref name="procRoot"/> and returns the load in % of those of <paramref name="pdevs"/>
    /// that have one. A card has a value once one of its clients is measured against a baseline from an earlier sample, so the
    /// first call (and the first sample after a card's clients start showing busy counters) only takes baselines; a card whose
    /// clients never show busy counters has no value at all (missing from the answer: no value, not 0).
    /// </summary>
    public Dictionary<string, double> Sample(string procRoot, IReadOnlyCollection<string> pdevs)
    {
        lock (_lock)
        {
            var now = _clock();
            if (_root != procRoot) { _meter.Reset(); _known = null; _root = procRoot; }
            List<DrmClient> cur;
            HashSet<(string, string)>? unread = null;
            if (_known is null || !(now - _discoveredAt < discoverySeconds))
            {
                var found = Discover(procRoot);
                cur = found.Select(f => f.Client).ToList();
                var known = found.Select(f => (f.Path, Meter.KeyOf(f.Client))).ToList();
                if (_known is not null)
                {
                    // a known client the walk didn't get (unreadable for a moment, or past the cap) is tried once more and
                    // kept while it isn't confirmed gone, so that it keeps its baseline
                    var walked = known.Select(k => k.Path).ToHashSet(StringComparer.Ordinal);
                    var missed = _known.Where(k => !walked.Contains(k.Path)).ToList();
                    cur.AddRange(Reread(missed, out unread));
                    known.AddRange(missed);
                }
                _known = known;
                _discoveredAt = now;
                Discoveries++;
            }
            else cur = Reread(_known, out unread);
            return _meter.Update(cur, now, pdevs, unread);
        }
    }

    /// <summary>
    /// The clients behind the fdinfo files a discovery pass found. A file that is gone (the fd was closed, the process exited)
    /// or no longer is an i915 / xe client is dropped from <paramref name="known"/>; one that can't be read this time stays,
    /// and its client goes to <paramref name="unread"/> so that it keeps its baseline instead of looking idle.
    /// </summary>
    private static List<DrmClient> Reread(List<(string Path, (string, string) Client)> known, out HashSet<(string, string)> unread)
    {
        var list = new List<DrmClient>(known.Count);
        var keep = new List<(string, (string, string))>(known.Count);
        unread = [];
        foreach (var (path, client) in known)
        {
            var (c, gone) = ReadClient(path);
            if (c is not null) { list.Add(c); keep.Add((path, Meter.KeyOf(c))); }
            else if (!gone) { unread.Add(client); keep.Add((path, client)); }
        }
        known.Clear();
        known.AddRange(keep);
        return list;
    }

    /// <summary>The i915 / xe client in one fdinfo file; <c>Gone</c> when the file no longer exists or holds no such client.</summary>
    private static (DrmClient? Client, bool Gone) ReadClient(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("drm-driver:", StringComparison.Ordinal) && ParseFdinfo(text) is { Driver: "i915" or "xe", Pdev: not null } c
                ? (c, false)
                : (null, true);
        }
        catch (FileNotFoundException) { return (null, true); }
        catch (DirectoryNotFoundException) { return (null, true); }
        catch (IOException) { return (null, false); }
        catch (UnauthorizedAccessException) { return (null, false); }
    }

    /// <summary>
    /// The load arithmetic, without any I/O; <see cref="DrmUsage"/> calls it under its lock. Per client and engine class it
    /// keeps the highest busy counter accepted so far with its reference (the read time for i915, the total cycles for xe) and
    /// the sample it came from. Only a baseline from the sample just before counts, so every share added up covers the same
    /// interval; a client that missed a sample (unreadable, newly found, no counters) takes a new baseline and counts from
    /// the next one. A counter that reads lower is ignored until it catches up again, as drm-usage-stats asks of userspace.
    /// Per engine class the busy shares are summed over the clients and divided by the class's engine count, and a card's
    /// load is its busiest class (what nvtop and intel_gpu_top show).
    /// </summary>
    internal sealed class Meter
    {
        private readonly Dictionary<(string Pdev, string ClientId, string Class), (ulong Busy, ulong Total, double At, long Sample)> _base = [];
        // cards with a valid measurement so far: from then on a card with no clients left is idle (0); a card whose clients
        // never show busy counters (i915 before 5.19, early xe) stays without a value
        private readonly HashSet<string> _measured = [];
        private long _sample;

        /// <summary>A client is one open file description: several fds can share it and show the same counters.</summary>
        internal static (string Pdev, string ClientId) KeyOf(DrmClient c) => ((c.Pdev ?? "").ToLowerInvariant(), c.ClientId);

        public void Reset()
        {
            _base.Clear();
            _measured.Clear();
        }

        /// <summary>
        /// Takes the clients read at <paramref name="now"/> (seconds) and returns the load in % of those of
        /// <paramref name="pdevs"/> that have a value. Clients in <paramref name="unread"/> couldn't be read this time, and a
        /// client without busy counters this time (xe prints none when it can't wake the GPU) is in the same state: both keep
        /// their baselines and count from their next sample. Any other client that is missing has gone, and its baselines
        /// with it. A card has a value when one of its clients was measured over this interval (0 when they were all idle), or
        /// when a card measured before has no clients left; otherwise its load is unknown and it is left out.
        /// </summary>
        public Dictionary<string, double> Update(IEnumerable<DrmClient> clients, double now, IEnumerable<string> pdevs,
            IReadOnlySet<(string, string)>? unread = null)
        {
            var sample = ++_sample;
            var shares = new Dictionary<(string Pdev, string Class), double>();
            var counted = new HashSet<(string, string)>(); // clients read with busy counters
            var unknown = new HashSet<(string Pdev, string ClientId)>(unread ?? new HashSet<(string, string)>());
            var present = new HashSet<string>(); // cards with a client this time
            var measuredNow = new HashSet<string>();
            var seen = new HashSet<(string, string)>();
            // two descriptors of one client count once; the one with counters wins (xe can print a descriptor without them)
            foreach (var c in clients.OrderBy(c => c.Busy.Count == 0))
            {
                if (c.Pdev is null || !seen.Add(KeyOf(c))) continue;
                var (pdev, id) = KeyOf(c);
                present.Add(pdev);
                if (c.Busy.Count == 0) { unknown.Add((pdev, id)); continue; }
                counted.Add((pdev, id));
                foreach (var (cls, busy) in c.Busy)
                {
                    var xe = c.TotalCycles.TryGetValue(cls, out var total);
                    if (!_base.TryGetValue((pdev, id, cls), out var b))
                    {
                        _base[(pdev, id, cls)] = (busy, total, now, sample); // first sight: a baseline, nothing to measure yet
                        continue;
                    }
                    if (busy < b.Busy) continue; // keep the higher value until the counter catches up
                    if (b.Sample != sample - 1)
                    {
                        _base[(pdev, id, cls)] = (busy, total, now, sample); // missed a sample: its delta spans another interval
                        continue;
                    }
                    // xe: busy cycles over the GPU cycles that passed for this client, independent of the wall clock
                    var passed = xe ? total > b.Total : now > b.At;
                    if (!passed)
                    {
                        _base[(pdev, id, cls)] = b with { Sample = sample }; // no time passed: same baseline, nothing measured
                        continue;
                    }
                    var share = xe ? (busy - b.Busy) / (double)(total - b.Total) : (busy - b.Busy) / ((now - b.At) * 1e9);
                    _base[(pdev, id, cls)] = (busy, total, now, sample);
                    var engines = c.Capacity.TryGetValue(cls, out var n) && n > 1 ? n : 1;
                    shares[(pdev, cls)] = shares.GetValueOrDefault((pdev, cls)) + share / engines;
                    _measured.Add(pdev);
                    measuredNow.Add(pdev);
                }
            }
            foreach (var key in _base.Keys.Where(k => !counted.Contains((k.Pdev, k.ClientId)) && !unknown.Contains((k.Pdev, k.ClientId))).ToList())
                _base.Remove(key);

            var load = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in pdevs)
            {
                var pdev = card.ToLowerInvariant();
                var idle = _measured.Contains(pdev) && !present.Contains(pdev) && !unknown.Any(k => k.Pdev == pdev);
                if (!measuredNow.Contains(pdev) && !idle) continue;
                var max = shares.Where(s => s.Key.Pdev == pdev).Select(s => s.Value).DefaultIfEmpty(0).Max();
                load[card] = Math.Round(Math.Clamp(max * 100, 0, 100), 1);
            }
            return load;
        }
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

    /// <summary>The i915 and xe clients of every process this user can read (one discovery pass).</summary>
    internal static List<DrmClient> Scan(string procRoot) => Discover(procRoot).Select(f => f.Client).ToList();

    /// <summary>
    /// The discovery pass: the i915 and xe clients of every process this user can read, with their fdinfo files. Pids are
    /// visited in order and the work is bounded, so a capped pass sees the same processes each time. Anything unreadable
    /// (another user's process, one that just exited) is skipped.
    /// </summary>
    private static List<(string Path, DrmClient Client)> Discover(string procRoot)
    {
        var list = new List<(string, DrmClient)>();
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
                if (ReadClient(file).Client is { } c) list.Add((file, c));
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
