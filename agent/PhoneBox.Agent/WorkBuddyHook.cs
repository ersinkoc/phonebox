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

    /// <summary>Installs or removes the hook in ~/.workbuddy/settings.json. Returns an error message, or null on success.</summary>
    public static string? Configure(bool install, int port, int timeoutSec) =>
        ClaudeHook.Configure(install, port, timeoutSec, SettingsPath, Endpoint);
}
