namespace PhoneBox.Agent;

/// <summary>
/// WorkBuddy answers the same PermissionRequest hook Claude Code does — its settings file is
/// ~/.workbuddy/settings.json with the same shape, so the install/remove logic is the Claude one
/// pointed at the WorkBuddy path. The endpoint is its own one: WorkBuddy's prompts must follow
/// WorkBuddy's switches (phoneApprovals, onlyWhenAway, timeoutSec), never Claude's.
/// </summary>
public static class WorkBuddyHook
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string SettingsPath = Path.Combine(Home, ".workbuddy", "settings.json");
    public const string Endpoint = "/api/hooks/workbuddy/permission";

    internal static bool Allowed(AgentConfig cfg) =>
        cfg.WorkBuddy.PhoneApprovals &&
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "sessions" && w.Bool("workBuddyApprovals") == true);

    public static bool Installed() => ClaudeHook.Installed(SettingsPath, Endpoint);

    private static readonly Lock CacheLock = new();
    private static bool _cachedInstalled;
    private static long _cachedAt = long.MinValue;
    internal const long CacheMs = 5000;

    /// <summary>
    /// <see cref="Installed"/> read at most once every <see cref="CacheMs"/>: the "work" feed asks on every tick for
    /// every stream, and each read parses ~/.workbuddy/settings.json. An Install or Remove from the settings page
    /// clears it; an edit by hand shows within a few seconds. The settings page's own status read stays uncached.
    /// </summary>
    public static bool InstalledCached() => InstalledCached(Environment.TickCount64, Installed);

    internal static bool InstalledCached(long nowMs, Func<bool> read)
    {
        lock (CacheLock)
        {
            if (_cachedAt != long.MinValue && nowMs - _cachedAt < CacheMs) return _cachedInstalled;
            _cachedInstalled = read();
            _cachedAt = nowMs;
            return _cachedInstalled;
        }
    }

    internal static void ForgetCached() { lock (CacheLock) _cachedAt = long.MinValue; }

    /// <summary>Installs or removes the hook in ~/.workbuddy/settings.json. Returns an error message, or null on success.</summary>
    public static string? Configure(bool install, int port, int timeoutSec)
    {
        var error = ClaudeHook.Configure(install, port, timeoutSec, SettingsPath, Endpoint);
        ForgetCached();
        return error;
    }
}
