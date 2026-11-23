using System.Diagnostics;
using System.IO;

namespace PhoneBox.Agent;

/// <summary>
/// Where OpenCode's background-service announcements (~/.local/state/opencode/service.json) are watched. Besides the
/// native one, OpenCode inside WSL is served exactly like the native one (sessions, permission prompts, agent
/// questions) — but only for distros that are already running, and only while the user turned WSL watching on: a
/// stopped distro must never be started for a check. A scan therefore asks `wsl.exe --list --running` first — a
/// query that does not touch the VM — and only touches the shares it just returned: it reads the default user's
/// own service.json and nothing else. The discovered services are cached for a minute; between scans no share is
/// touched at all, and the callers talk plain HTTP to the services, which never wakes anything.
/// </summary>
internal static class OpenCodeServices
{
    private const int MaxDistros = 8;   // more WSL distros than anyone runs OpenCode in
    private const int CacheMs = 60_000; // how long the discovered WSL services are reused between scans

    private static readonly object CacheLock = new();
    private static long _nextScan = -CacheMs;   // the first scan goes out at once
    private static List<(string Path, OpenCodeApprovals.ServiceInfo Info)> _cached = [];

    /// <summary>The running distros' OpenCode services (path and parsed service.json), re-scanned at most once a
    /// minute. A 401 (a restarted service with a new password) invalidates the cache for an earlier re-scan.
    /// The distro names come from the caller in tests.</summary>
    internal static IReadOnlyList<(string Path, OpenCodeApprovals.ServiceInfo Info)> WslServices(bool enabled,
        IReadOnlyList<string>? distroNames = null, string? shareRoot = null)
    {
        if (!enabled) return [];
        lock (CacheLock)
        {
            if (Environment.TickCount64 >= _nextScan)
            {
                _nextScan = Environment.TickCount64 + CacheMs;
                var found = new List<(string, OpenCodeApprovals.ServiceInfo)>();
                // the query is milliseconds old when the shares are touched: a distro that stopped since is not on
                // this list and is not touched
                var names = distroNames ?? ParseRunningDistros(RunningListOutput());
                foreach (var name in names.Take(MaxDistros))
                {
                    var root = (shareRoot ?? @"\\wsl.localhost\") + name;
                    var home = DefaultUserHome(root, name);
                    if (home is null) continue;
                    // the home from /etc/passwd is distro-absolute (/home/user, /root): it sits under the share root
                    var path = Path.Combine(Path.Combine(root, home.TrimStart('/', '\\')), ".local", "state", "opencode", "service.json");
                    if (File.Exists(path) && OpenCodeApprovals.ReadService(path) is { } info)
                        found.Add((path, info));
                }
                _cached = found;
            }
            return _cached;
        }
    }

    /// <summary>Drops the cached WSL services so the next <see cref="WslServices"/> call re-scans (a service.json
    /// that changed underneath us, signalled by a 401). Rate-limited: a persistent 401 must not scan every poll.</summary>
    internal static void InvalidateWsl()
    {
        lock (CacheLock) _nextScan = Math.Min(_nextScan, Environment.TickCount64 + 5000);
    }

    /// <summary>
    /// The home directory of the distro's default user — the one OpenCode in that distro would run under — so
    /// another user's own loopback service is never read. Resolved from /etc/wsl.conf's [user] default= first, then
    /// the registry's DefaultUid for the distro, each mapped to a home through /etc/passwd; a /home/&lt;name&gt; that
    /// exists is the fallback for a named user. null = could not be determined: the distro is skipped.
    /// </summary>
    internal static string? DefaultUserHome(string root, string distroName)
    {
        try
        {
            var name = WslConfDefaultUser(root);
            var uid = name is null ? RegistryDefaultUid(distroName) : null;
            if (name is null && uid is null) return null;
            var passwd = ReadSmallFile(Path.Combine(root, "etc", "passwd"));
            if (passwd is not null)
            {
                foreach (var line in passwd.Split('\n'))
                {
                    var f = line.TrimEnd('\r').Split(':');
                    if (f.Length < 6) continue;
                    var matchesUser = name is not null && f[0] == name;
                    var matchesUid = uid is int u && f.Length > 2 && int.TryParse(f[2], out var fu) && fu == u;
                    if (matchesUser || matchesUid) return f[5].Length > 0 ? f[5] : null;
                }
            }
            if (name is not null)
            {
                // distro-absolute like the passwd answer: the caller puts it under the share root
                if (Directory.Exists(Path.Combine(root, "home", name))) return "/home/" + name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The default= name under [user] in the distro's /etc/wsl.conf, or null.</summary>
    private static string? WslConfDefaultUser(string root)
    {
        var conf = ReadSmallFile(Path.Combine(root, "etc", "wsl.conf"));
        if (conf is null) return null;
        var inUser = false;
        foreach (var raw in conf.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { inUser = line.Equals("[user]", StringComparison.OrdinalIgnoreCase); continue; }
            if (inUser && line.StartsWith("default=", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["default=".Length..].Trim().Trim('"');
                return name.Length > 0 ? name : null;
            }
        }
        return null;
    }

    /// <summary>The distro's DefaultUid from the registry (no share access, no VM start), or null.</summary>
    private static int? RegistryDefaultUid(string distroName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return null;
            foreach (var guid in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(guid);
                if ((key?.GetValue("DistributionName") as string) == distroName)
                    return key?.GetValue("DefaultUid") as int?;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Security.SecurityException) { }
        return null;
    }

    private static string? ReadSmallFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Every service.json path worth watching on this machine: the native one first (the file may not exist yet —
    /// OpenCode writes it when it starts). The WSL roots come from the caller in tests, and then every user's home
    /// under them is checked; production WSL discovery goes through <see cref="WslServices"/>, which reads the
    /// default user's home only. <paramref name="wslEnabled"/> turns the WSL half off.
    /// </summary>
    internal static List<string> Discover(string? homeDir = null, string? xdgStateHome = null, string? localAppData = null,
        IReadOnlyList<string>? wslDistroRoots = null, bool wslEnabled = true)
    {
        var paths = new List<string>();
        var native = OpenCodeApprovals.ServicePath(homeDir, xdgStateHome, localAppData);
        if (native is not null) paths.Add(native);
        if (!wslEnabled) return paths;
        var roots = wslDistroRoots ?? [];
        foreach (var root in roots.Take(MaxDistros))
        {
            try
            {
                // the distro's users' homes; a half-awake share throws, which just means "not this round"
                foreach (var user in Directory.EnumerateDirectories(Path.Combine(root, "home")).Take(8))
                {
                    var path = Path.Combine(user, ".local", "state", "opencode", "service.json");
                    if (File.Exists(path)) paths.Add(path);
                }
                var rootUser = Path.Combine(root, "root", ".local", "state", "opencode", "service.json");
                if (File.Exists(rootUser)) paths.Add(rootUser);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return paths;
    }

    /// <summary>stdout of `wsl.exe --list --running --quiet`, or null when the query could not run (no WSL, no
    /// wsl.exe). --quiet prints just the names: no localized header, no " (Default)" marker, no "no distributions"
    /// line that a parser could mistake for a name.</summary>
    internal static string? RunningListOutput()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                ArgumentList = { "--list", "--running", "--quiet" }, // a query: it never starts the VM or a distro
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            // Drained asynchronously, so a wsl.exe that hangs with stdout open cannot block past the deadline
            // below — a synchronous ReadToEnd would wait forever before the timeout even runs. A complete
            // --list --running answer is a few hundred bytes and lands long before the deadline; whatever has
            // arrived by then is what gets parsed.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                read.Wait(1000); // let the reader finish so the handles close; the output is not used
                return null;
            }
            return read.GetAwaiter().GetResult(); // the process has exited: the pipe closes and the read completes
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return null; // no WSL installed, or the query could not start
        }
    }

    /// <summary>The distro names in `wsl.exe` list output: NULs (piped UTF-16) and any header or footer line
    /// dropped. --quiet makes both unlikely, but older builds may not honour it.</summary>
    internal static List<string> ParseRunningDistros(string? output)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(output)) return names;
        foreach (var raw in output.Replace("\0", "").Split('\r', '\n'))
        {
            var name = raw.Trim();
            if (name.Length == 0 || name.Contains(':')) continue; // "Windows Subsystem for Linux Distributions:"
            var cut = name.IndexOf(" (Default)", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) name = name[..cut].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }
}
