namespace PhoneBox.Agent;

/// <summary>
/// The home folders of the running WSL distros' default users, as seen from Windows (\\wsl.localhost\&lt;distro&gt;\...),
/// so Claude Code and Cursor running inside WSL can be watched like the native ones. Same rules as OpenCode in WSL
/// (<see cref="OpenCodeServices"/>): only distros that are running right now, and only the default user's home. A share
/// of a stopped distro is never touched, because opening it would start the distro: callers ask for a fresh list
/// (<see cref="Running"/>) right before they read files under it.
/// </summary>
public static class WslHomes
{
    private const int MaxDistros = 8;

    /// <param name="Distro">The distro's name, as wsl.exe lists it.</param>
    /// <param name="Root">The distro's share: \\wsl.localhost\&lt;distro&gt;.</param>
    /// <param name="Home">The default user's home under the share.</param>
    public sealed record Entry(string Distro, string Root, string Home);

    // Looking the default user's home up reads two files and the registry, so a found home is kept per distro for a
    // minute: a default user changed in /etc/wsl.conf takes over within that. A failed lookup (a share still waking
    // up) is not kept, so the next refresh tries again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Home, long At)> HomeCache = new();
    private const int HomeCacheMs = 60_000;

    /// <summary>
    /// The running distros' homes, from a `wsl.exe --list --running` query made now (Windows only; elsewhere, and
    /// without WSL, empty). Tests give the distro names and the share root.
    /// </summary>
    public static IReadOnlyList<Entry> Running(IReadOnlyList<string>? distroNames = null, string? shareRoot = null)
    {
        if (distroNames is null && !OperatingSystem.IsWindows()) return [];
        var names = distroNames ?? OpenCodeServices.ParseRunningDistros(OpenCodeServices.RunningListOutput());
        var homes = new List<Entry>();
        foreach (var name in names.Take(MaxDistros))
        {
            var root = (shareRoot ?? @"\\wsl.localhost\") + name;
            var now = Environment.TickCount64;
            string? home;
            if (HomeCache.TryGetValue(root, out var cached) && now - cached.At < HomeCacheMs) home = cached.Home;
            else
            {
                home = OpenCodeServices.DefaultUserHome(root, name);
                if (home is null) { HomeCache.TryRemove(root, out _); continue; }
                HomeCache[root] = (home, now);
            }
            // the home from /etc/passwd is distro-absolute (/home/user, /root): it sits under the share root, written
            // with this system's separators (\\wsl.localhost\<distro>\home\user, not ...\home/user)
            homes.Add(new Entry(name, root, Path.Combine(root, home.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar))));
        }
        return homes;
    }

    /// <summary>Whether a distro runs right now (a fresh `wsl.exe --list --running` query; false without WSL).</summary>
    public static bool IsRunning(string distro) =>
        OperatingSystem.IsWindows()
        && OpenCodeServices.ParseRunningDistros(OpenCodeServices.RunningListOutput()).Contains(distro, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether Linux process <paramref name="pid"/> runs in the distro behind <paramref name="root"/>, read from its
    /// /proc. With <paramref name="procStart"/> (Claude Code writes the process's start time in clock ticks, field 22 of
    /// /proc/&lt;pid&gt;/stat) a process that only reuses the PID of a session that was killed does not count.
    /// </summary>
    public static bool Alive(string root, int pid, long? procStart)
    {
        if (pid <= 0) return false;
        try
        {
            var stat = File.ReadAllText(Path.Combine(root, "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat"));
            return procStart is not { } expected || StartTime(stat) is not { } actual || actual == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Field 22 (starttime) of a /proc/&lt;pid&gt;/stat line, or null when the line cannot be read. The
    /// command name (field 2) is in parentheses and may itself hold spaces and ")", so fields are counted from the last ")".</summary>
    internal static long? StartTime(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        // after ") " come fields 3, 4, ...: starttime (22) is the 20th of them
        var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length >= 20 && long.TryParse(rest[19], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
    }
}
