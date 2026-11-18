using System.Collections.Concurrent;
using System.Diagnostics;
#if WINDOWS
using System.Drawing.Imaging;
#endif
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

public sealed record ProcGroup(string Name, string Title, int Count, double Cpu, double MemMb, bool Locked, bool HasIcon);

/// <param name="Session">Claude Code session name when the instance is a Claude Code session.</param>
/// <param name="Window">Main window title, if any.</param>
/// <param name="Args">Command-line arguments (without the executable path), shortened.</param>
public sealed record ProcInstance(int Pid, double Cpu, double MemMb, long? StartedAt, string? Session, string? Window, string? Args, bool Locked);

public sealed record ProcDetail(string Name, string Title, bool Locked, IReadOnlyList<ProcInstance> Instances);

public sealed record KillResult(int Killed, int Failed, string? Error);

/// <summary>
/// Groups processes by name and measures their CPU (%) and memory use. Sampling only runs
/// while the phone is actually asking for the list, to avoid needless load.
/// </summary>
public sealed class ProcessMonitor(ILogger<ProcessMonitor> log, SessionMonitor sessions) : BackgroundService
{
    // Processes Windows needs, or whose termination would break the user session.
#if !WINDOWS
    private static readonly HashSet<string> Protected = UnixProc.Protected;
#else
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "sihost", "ctfmon", "explorer", "taskhostw", "audiodg", "spoolsv",
        "Memory Compression", "Secure System", "LsaIso", "conhost", "dllhost", "WUDFHost", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "LockApp", "SecurityHealthService",
        "MsMpEng", "NisSrv", "smartscreen", "RuntimeBroker", "ApplicationFrameHost", "SystemSettingsBroker",
        "PhoneBoxAgent", "adb", "dotnet",
    };
#endif

    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly int _selfPid = Environment.ProcessId;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly ConcurrentDictionary<string, (string Title, string? Path)> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string?> _args = new();

    private Dictionary<int, TimeSpan> _lastCpu = [];
    private volatile Dictionary<int, double> _pidCpu = [];
    private long _lastTs;
    private volatile IReadOnlyList<ProcGroup> _groups = [];
    private long _lastRequest;

    public IReadOnlyList<ProcGroup> Snapshot()
    {
        Touch();
        return _groups;
    }

    private void Touch() => Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 15_000)
            {
                try
                {
                    // On a cold start there is no previous sample to diff against: take a second one right away.
                    if (_lastTs == 0) { Sample(); await Task.Delay(700, ct); }
                    Sample();
                }
                catch (Exception ex) { log.LogDebug("process sampling: {Msg}", ex.Message); }
                await Task.Delay(2000, ct);
            }
            else
            {
                if (_lastTs != 0)
                {
                    _lastCpu = []; // idle: start fresh on the next request
                    _pidCpu = [];
                    _lastTs = 0;
                    _groups = [];
                    _args.Clear();
                }
                await Task.Delay(250, ct); // wake up quickly once requests arrive
            }
        }
    }

    private void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastTs == 0 ? 0 : (now - _lastTs) / (double)Stopwatch.Frequency;
        _lastTs = now;

        var cpuNow = new Dictionary<int, TimeSpan>();
        var pidCpu = new Dictionary<int, double>();
        var acc = new Dictionary<string, (int Count, double Cpu, long Mem, bool Locked, int Pid)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var name = p.ProcessName;
                    if (p.Id == 0) continue;
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        cpuNow[p.Id] = t;
                        if (elapsed > 0 && _lastCpu.TryGetValue(p.Id, out var prev))
                            cpu = Math.Max(0, (t - prev).TotalSeconds / elapsed / _cores * 100);
                    }
                    catch { /* protected process: CPU time is not readable */ }
                    pidCpu[p.Id] = cpu;

                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }

                    var locked = p.Id == _selfPid || Protected.Contains(name) || !Mine(p);
                    acc.TryGetValue(name, out var a);
                    acc[name] = (a.Count + 1, a.Cpu + cpu, a.Mem + mem, a.Locked || locked, a.Pid == 0 ? p.Id : a.Pid);
                }
                catch { }
            }
        }
        _lastCpu = cpuNow;
        _pidCpu = pidCpu;

        _groups = acc
            .Select(kv =>
            {
                if (!_meta.TryGetValue(kv.Key, out var meta) || meta.Path is null)
                {
                    meta = ReadMeta(kv.Value.Pid, kv.Key);
                    if (meta.Path is not null) _meta[kv.Key] = meta;
                }
                return new ProcGroup(kv.Key, meta.Title, kv.Value.Count, Math.Round(kv.Value.Cpu, 1),
                    Math.Round(kv.Value.Mem / 1048576.0), kv.Value.Locked, meta.Path is not null);
            })
            .OrderByDescending(g => g.Cpu)
            .ThenByDescending(g => g.MemMb)
            .ToList();
    }

    /// <summary>Every instance of a process name, with what identifies each one.</summary>
    public ProcDetail Detail(string name)
    {
        Touch();
        var claudeByPid = sessions.Snapshot().Where(s => s.Pid is not null)
            .GroupBy(s => s.Pid!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Since).First().Name);
        var cpu = _pidCpu;
        var list = new List<ProcInstance>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }
                    long? started = null;
                    try { started = new DateTimeOffset(p.StartTime).ToUnixTimeMilliseconds(); } catch { }
                    string? window = null;
                    try { window = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { }
                    var locked = p.Id == _selfPid || Protected.Contains(name) || !Mine(p);
                    list.Add(new ProcInstance(p.Id, Math.Round(cpu.GetValueOrDefault(p.Id), 1), Math.Round(mem / 1048576.0),
                        started, claudeByPid.GetValueOrDefault(p.Id), window, _args.GetOrAdd(p.Id, ReadArgs), locked));
                }
                catch { }
            }
        }
        if ((!_meta.TryGetValue(name, out var meta) || meta.Path is null) && list.Count > 0)
        {
            meta = ReadMeta(list[0].Pid, name);
            if (meta.Path is not null) _meta[name] = meta;
        }
        else if (!_meta.TryGetValue(name, out meta)) meta = (name, null);
        var groupLocked = Protected.Contains(name) || (list.Count > 0 && list.All(i => i.Locked));
        return new ProcDetail(name, meta.Title, groupLocked,
            list.OrderByDescending(i => i.Cpu).ThenByDescending(i => i.MemMb).ToList());
    }

    /// <summary>Terminates every unprotected process with this name in the current user session.</summary>
    public KillResult Kill(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(0, 0, "no name given");
        if (Protected.Contains(name)) return new(0, 0, $"{name} is protected and cannot be terminated");

        int killed = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (p.Id == _selfPid || !Mine(p)) { failed++; continue; }
                try { p.Kill(); killed++; }
                catch { failed++; }
            }
        }
        log.LogInformation("terminated all {Name}: {Killed} ok, {Failed} failed", name, killed, failed);
        if (killed == 0 && failed == 0) return new(0, 0, "process not found");
        return new(killed, failed, killed == 0 ? "access denied (it may be running as administrator)" : null);
    }

    /// <summary>
    /// Terminates a single process together with its child processes. Every descendant passes the same checks as the
    /// process itself (protected names, other sessions or users, the agent): those are left running. Process.Kill's
    /// own tree mode checks none of that, so it would end, say, a protected dotnet or adb started from a terminal.
    /// </summary>
    public KillResult KillPid(int pid)
    {
        if (pid == _selfPid) return new(0, 0, "that is the PhoneBox agent itself");
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch { return new(0, 0, "process not found"); }
        using (p)
        {
            var name = p.ProcessName;
            if (Protected.Contains(name)) return new(0, 0, $"{name} is protected and cannot be terminated");
            if (!Mine(p)) return new(0, 0, "process belongs to another session or user");
            var descendants = Descendants(pid);
            try { p.Kill(); }
            catch (Exception ex) { return new(0, 1, "could not terminate: " + ex.Message); }
            int killed = 1, skipped = 0;
            foreach (var d in descendants)
            {
                try
                {
                    using var c = Process.GetProcessById(d);
                    if (d == _selfPid || Protected.Contains(c.ProcessName) || !Mine(c)) { skipped++; continue; }
                    c.Kill();
                    killed++;
                }
                catch (ArgumentException) { } // already gone
                catch { skipped++; }
            }
            log.LogInformation("terminated {Name} (pid {Pid}) and {Children} child processes ({Skipped} left running)", name, pid, killed - 1, skipped);
            return new(killed, skipped, null);
        }
    }

    /// <summary>
    /// Every process below <paramref name="root"/>. A child counts only if it started after its parent, so a parent
    /// PID that Windows or Unix has since given to an unrelated process does not pull that process in.
    /// </summary>
    public static List<int> Descendants(int root)
    {
        var started = new Dictionary<int, DateTime>();
        var children = new Dictionary<int, List<int>>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { started[p.Id] = p.StartTime; } catch { continue; } // not readable: not ours to end anyway
                if (ParentPid(p.Id) is { } parent && parent != p.Id)
                    (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(p.Id);
            }
        }
        var result = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent, out var kids) || !started.TryGetValue(parent, out var parentStart)) continue;
            foreach (var kid in kids)
                if (started[kid] >= parentStart && seen.Add(kid)) { result.Add(kid); queue.Enqueue(kid); }
        }
        return result;
    }

    /// <summary>Windows: same logon session as the agent. Linux / macOS: same user.</summary>
    private bool Mine(Process p)
    {
#if WINDOWS
        return p.SessionId == _session;
#else
        return UnixProc.IsOwn(p.Id);
#endif
    }

    public byte[]? Icon(string name)
    {
        if (_icons.TryGetValue(name, out var cached) && cached is not null) return cached;
        if (!_meta.TryGetValue(name, out var meta) || meta.Path is null)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (meta.Path is null)
                    {
                        try { meta = ReadMeta(p.Id, name); } catch { }
                        if (meta.Path is not null) _meta[name] = meta;
                    }
                }
            }
        }
        byte[]? png = null;
#if !WINDOWS
        png = UnixProc.Icon(name, meta.Path);
#else
        if (meta.Path is not null)
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(meta.Path);
                if (ico is not null)
                {
                    using var bmp = ico.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
            }
            catch { }
        }
#endif
        if (png is not null) _icons[name] = png;
        return png;
    }

    private static (string Title, string? Path) ReadMeta(int pid, string name)
    {
#if !WINDOWS
        string? upath = null;
        try { using var up = Process.GetProcessById(pid); upath = UnixProc.ImagePath(up); } catch { }
        return (UnixProc.Title(name, upath), upath);
#else
        var path = ImagePath(pid);
        var title = name;
        if (path is not null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) title = desc;
            }
            catch { }
        }
        return (title, path);
#endif
    }

    internal static string? ShortenArgs(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        // drop the executable (quoted or not)
        var rest = cmd.StartsWith('"') ? cmd[(cmd.IndexOf('"', 1) is var q && q > 0 ? q + 1 : cmd.Length)..]
                                      : cmd[(cmd.IndexOfAny([' ', '\t']) is var s && s > 0 ? s : cmd.Length)..];
        rest = rest.Trim();
        if (rest.Length == 0) return null;
        return rest.Length > 140 ? rest[..137] + "…" : rest;
    }

#if WINDOWS
    /// <summary>The parent's PID (PROCESS_BASIC_INFORMATION.InheritedFromUniqueProcessId), or null.</summary>
    private static int? ParentPid(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        var size = 6 * IntPtr.Size;
        var buf = Marshal.AllocHGlobal(size);
        try { return NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */, buf, size, out _) == 0 ? (int)Marshal.ReadIntPtr(buf, 5 * IntPtr.Size) : null; }
        finally { Marshal.FreeHGlobal(buf); CloseHandle(h); }
    }

    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Reads a process's command line (ProcessCommandLineInformation) and returns the arguments only.</summary>
    private static string? ReadArgs(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, 0, 0, out var len);
            if (len <= 0 || len > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
                var chars = (ushort)Marshal.ReadInt16(buf) / 2; // UNICODE_STRING.Length is an unsigned USHORT in bytes
                var cmd = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), chars);
                return ShortenArgs(cmd);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint h, int infoClass, nint info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);
#else
    private static string? ReadArgs(int pid) => UnixProc.Args(pid);
    private static int? ParentPid(int pid) => UnixProc.ParentPid(pid);
#endif
}
