using System.Reflection;

namespace PhoneBox.Agent;

/// <summary>File locations. The install folder is replaced on update; settings and logs are kept.</summary>
public static class AppPaths
{
    public const string AppName = "PhoneBox";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Home, fallback);

    /// <summary>%APPDATA%\PhoneBox — user settings (config.json).</summary>
    /// <summary>Windows: %APPDATA%\PhoneBox. Linux: ~/.config/PhoneBox. macOS: ~/Library/Application Support/PhoneBox.</summary>
    public static readonly string ConfigDir = Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_CONFIG_HOME", ".config"), AppName);

    /// <summary>%LOCALAPPDATA%\PhoneBox — logs and the installed binaries.</summary>
    /// <summary>Windows: %LOCALAPPDATA%\PhoneBox. Linux: ~/.local/share/PhoneBox. macOS: same folder as the settings.</summary>
    public static readonly string LocalDir = Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), AppName);

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public static readonly string PluginsStatePath = Path.Combine(ConfigDir, "plugins.json");
    public static readonly string LogPath = Path.Combine(LocalDir, "agent.log");

    /// <summary>Where install scripts put the agent; self-update only works for this copy.</summary>
    public static readonly string InstallDir = Path.Combine(LocalDir, "app");

    /// <summary>Installed community plugins directory; preserved across agent updates.</summary>
    public static readonly string PluginsDir = Path.Combine(LocalDir, "plugins");

    /// <summary>"windows", "linux" or "macos".</summary>
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Release asset suffix for this machine: win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64.</summary>
    public static string Rid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    public static readonly string StartupShortcut =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Writes the default settings file on first run.</summary>
    public static void EnsureConfig()
    {
        if (File.Exists(ConfigPath)) return;
        Directory.CreateDirectory(ConfigDir);
        var name = OperatingSystem.IsMacOS() ? "config.default.macos.json" : OperatingSystem.IsLinux() ? "config.default.linux.json" : "config.default.json";
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var dst = File.Create(ConfigPath);
        src.CopyTo(dst);
    }

    public static Stream? Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    /// <summary>Neutral working directory for child processes, so they never lock the install folder.</summary>
    public static string NeutralCwd => Path.GetTempPath();
}
