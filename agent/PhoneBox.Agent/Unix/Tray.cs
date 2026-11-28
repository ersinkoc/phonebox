using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Linux / macOS: no tray icon (the agent usually runs as a systemd user service or a launchd agent). The settings live
/// in the browser at http://127.0.0.1:8765; this just says so in the log.
/// </summary>
public sealed class Tray(ILogger<Tray> log, AgentEndpoint endpoint) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        // inside PhoneBox.app the menu bar icon opens them (MacMenuBar); elsewhere there is nothing on screen to click
        if (UpdateService.RunningInBundle) log.LogInformation("settings: {Url}  (also from the phone icon in the menu bar)", endpoint.SettingsUrl);
        else log.LogInformation("settings: {Url}  (no tray icon on this system; run PhoneBoxAgent again to open the settings)", endpoint.SettingsUrl);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Says why the agent stops before it starts (#710): on stderr, and as a notification where there is a desktop.</summary>
    public static void ShowPortTaken(ConfigStore config, int port, string? program)
    {
        var text = $"PhoneBox can't start: port {port} is used by {program ?? "another program"}. Close that program and start PhoneBox again.";
        Console.Error.WriteLine(text);
        // false when there is no osascript or notify-send, or no desktop session to show it in
        _ = (OperatingSystem.IsMacOS()
            ? new OsaScriptNotifier().PostAsync("PhoneBox", "", text, null, null, CancellationToken.None)
            : OsaScriptNotifier.Run("notify-send", ["PhoneBox", text], 5000, CancellationToken.None)).GetAwaiter().GetResult();
    }
}

/// <summary>
/// Runs the web host with the macOS menu bar icon when PhoneBox.app is wanted to have one (see <see cref="MacMenuBar"/>).
/// Windows has its own tray (Tray.cs) and a stub of this class, so Program stays the same on every system.
/// </summary>
public static class MenuBarHost
{
    /// <summary>True when the host ran here, with the icon, until it stopped; false when the caller should just run it.</summary>
    public static bool TryRun(WebApplication app, string[] args, AgentEndpoint endpoint, string configDir)
    {
        try
        {
            // inside the try: the system libraries behind CanRun may not load, and then the agent should simply run without an icon
            if (!MacMenuBar.Wanted(args, UpdateService.RunningInBundle, Environment.GetEnvironmentVariable) || !MacMenuBar.CanRun()) return false;
        }
        catch (Exception ex) { app.Logger.LogWarning("menu bar icon not available: {Msg}", ex.Message); return false; }
        var host = app.RunAsync();
        try
        {
            MacMenuBar.Run(MacMenuBar.Entries(app.Services.GetRequiredService<AdbReverse>(), app.Services.GetRequiredService<UpdateService>(),
                app.Lifetime, endpoint.SettingsUrl, configDir), () => host.IsCompleted, app.Lifetime.ApplicationStopping, app.Lifetime.StopApplication,
                () => MacMenuBar.OpenUrl(endpoint.SettingsUrl), app.Logger);
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "menu bar icon failed"); }
        host.GetAwaiter().GetResult();
        return true;
    }
}

/// <summary>Windows ties helper processes to the agent with a job object; elsewhere they are stopped on shutdown.</summary>
public static class JobObject
{
    public static void Track(Process p) { }
}
