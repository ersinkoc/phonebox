using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Small helpers for running command-line tools on Linux and macOS.</summary>
public static class Sh
{
    private static readonly Dictionary<string, string?> Found = [];

    /// <summary>Full path of a tool in PATH (or a few usual places), or null.</summary>
    public static string? Which(string tool)
    {
        lock (Found)
        {
            if (Found.TryGetValue(tool, out var hit) && hit is not null && File.Exists(hit)) return hit;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(["/usr/bin", "/bin", "/usr/local/bin", "/opt/homebrew/bin", "/usr/sbin", "/sbin", "/snap/bin"]);
            hit = dirs.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
            Found[tool] = hit;
            return hit;
        }
    }

    /// <summary>Runs a tool and returns its standard output, or null if it is missing, fails or takes too long.</summary>
    public static string? Run(string tool, params string[] args) => RunWithTimeout(2000, tool, args);

    public static string? RunWithTimeout(int timeoutMs, string tool, params string[] args)
    {
        var path = tool.Contains('/') ? tool : Which(tool);
        if (path is null) return null;
        try
        {
            var psi = new ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
            // The subprocess and its output pipes share one deadline. Run off the caller's context
            // because this API waits synchronously for the asynchronous process helper.
            var result = Task.Run(() => ToolProcess.RunAsync(psi, TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None))
                .GetAwaiter().GetResult();
            return result.ExitCode == 0 ? result.Output : null;
        }
        catch { return null; }
    }

    public static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint geteuid();
    [DllImport("libc", EntryPoint = "access")] private static extern int access(string path, int mode);

    /// <summary>Whether this process may list a directory (read and search permission), asked with access(2) so a folder
    /// it may not read costs no exception.</summary>
    public static bool CanList(string directory)
    {
        try { return access(directory, 4 /*R_OK*/ | 1 /*X_OK*/) == 0; }
        catch { return true; } // no libc access (never on Linux / macOS): let the caller try, as before
    }

    /// <summary>The effective user id of the agent (to tell the user's own processes from others').</summary>
    public static readonly uint Uid = OperatingSystem.IsWindows() ? 0 : geteuid();
}
