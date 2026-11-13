using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// While a phone is connected over USB, keeps an "adb reverse tcp:8765 tcp:8765" rule in place so that
/// 127.0.0.1:8765 on the phone reaches this agent. When a phone is newly plugged in, wakes it and opens the panel.
/// </summary>
public sealed class AdbReverse(ConfigStore cfg, ILogger<AdbReverse> log, AgentEndpoint? endpoint = null) : BackgroundService
{
    public const string PanelActivity = "net.oxog.phonebox.deskpanel/.MainActivity";
    private const string HomeAlias = "net.oxog.phonebox.deskpanel/.HomeAlias";
    private readonly HashSet<string> _homeSet = [];
    private readonly HashSet<string> _awakeSet = [];
    /// <summary>Phones this agent process has already woken since it started (see LightPanel).</summary>
    private readonly HashSet<string> _seen = [];
    private volatile string[] _missingApp = [];

    /// <summary>Connected phones that do not have the PhoneBox app yet (installed from Settings, e.g. after winget / Scoop).</summary>
    public string[] MissingApp => _missingApp;
    private const string Rule = "tcp:8765";
    private string TargetRule => $"tcp:{endpoint?.Port ?? 8765}";

    private string _lastState = "";
    private volatile string[] _devices = [];
    private volatile string[] _all = [];

    public bool Connected => _devices.Length > 0;
    /// <summary>Why the phone is missing from adb although it reports in over Wi-Fi (see <see cref="UsbDoctor"/>), or null.</summary>
    public string? UsbProblem => UsbDoctor.Diagnose(ChargeGuard.LastReport, Connected, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private string? _lastProblem;
    public string Devices => string.Join(", ", _devices);
    /// <summary>All attached devices, before the deviceSerial filter (for the settings page).</summary>
    public string[] AllDevices => _all;
    public event Action? StateChanged;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Ensure(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Report($"adb error: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
    }

    public async Task OpenPanelAsync()
    {
        var adb = FindAdb(cfg.Get().AdbPath);
        if (adb is null) return;
        foreach (var d in _devices)
        {
            await Adb(adb, $"-s {d} shell input keyevent KEYCODE_WAKEUP", CancellationToken.None);
            await Adb(adb, $"-s {d} shell am start -n {PanelActivity}", CancellationToken.None);
        }
    }

    private ScreenSynchronizer? _screenSync;
    private ScreenSynchronizer ScreenSync => LazyInitializer.EnsureInitialized(ref _screenSync,
        () => new ScreenSynchronizer(MacSessionLock.IsLocked, ApplyScreenAsync));

    public Task<bool> SyncSessionAsync(CancellationToken ct, bool force = false) => ScreenSync.ReconcileAsync(_devices, ct, force);
    public Task ResetSessionAsync(CancellationToken ct) => ScreenSync.ResetAsync(ct);

    /// <summary>
    /// Light a phone this agent has not woken yet in this session and bring the panel to the front. The trio
    /// (wake key, wm dismiss-keyguard, am start) mirrors the session-sync path below: an app cannot dismiss
    /// its own keyguard while the display is off, and Android 10+ may refuse the sleeping app's request to
    /// come forward ("Background activity start" is logged and blocked) - shell commands via adb are neither.
    /// </summary>
    private async Task LightPanel(string device, string adb, string why, CancellationToken ct)
    {
        // A newly plugged-in phone must not light up while the computer is locked.
        if (cfg.Get().Display.FollowComputerLock && OperatingSystem.IsMacOS())
        {
            await SyncSessionAsync(ct, force: true);
            return;
        }
        await Adb(adb, $"-s {device} shell input keyevent KEYCODE_WAKEUP", ct);
        await Adb(adb, $"-s {device} shell wm dismiss-keyguard", ct);
        await Adb(adb, $"-s {device} shell am start -n {PanelActivity}", ct);
        log.LogInformation("{Device}: {Why}, panel opened", device, why);
    }

    private async Task ApplyScreenAsync(string device, bool locked, CancellationToken ct)
    {
        var adb = FindAdb(cfg.Get().AdbPath);
        if (adb is null) throw new IOException("ADB is not available");
        if (locked) await Adb(adb, $"-s {device} shell input keyevent KEYCODE_SLEEP", ct);
        else
        {
            await Adb(adb, $"-s {device} shell input keyevent KEYCODE_WAKEUP", ct);
            // A secure PIN/password keyguard still needs the user's unlock.
            await Adb(adb, $"-s {device} shell wm dismiss-keyguard", ct);
            await Adb(adb, $"-s {device} shell am start -n {PanelActivity}", ct);
        }
        log.LogInformation("{Device}: phone screen {State}", device, locked ? "asleep" : "awake");
    }

    private async Task Ensure(CancellationToken ct)
    {
        var adb = FindAdb(cfg.Get().AdbPath);
        if (adb is null)
        {
            SetDevices([]);
            Report("adb not found: " + (OperatingSystem.IsWindows() ? "run 'winget install Google.PlatformTools'"
                : OperatingSystem.IsMacOS() ? "run 'brew install android-platform-tools'" : "run 'sudo apt install adb' (or your distribution's package)") + " or set adbPath in the settings");
            return;
        }

        var serial = cfg.Get().DeviceSerial;
        var devices = (await Adb(adb, "devices", ct))
            .Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2 && p[1] == "device")
            .Select(p => p[0])
            .ToArray();
        _all = devices;
        devices = devices.Where(s => string.IsNullOrEmpty(serial) || s == serial).ToArray();

        SetDevices(devices);
        // _seen shrinks with the list here, before the empty-list return below: after an unplug the
        // phone must count as "not yet woken by this process" again, or the reconnect wake further
        // down is skipped while the adb server silently re-applies its cached reverse rules.
        _seen.IntersectWith(devices);
        var problem = UsbProblem;
        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            if (problem is not null) log.LogWarning("USB: {Problem}", problem);
            if (problem is null && devices.Length > 0) log.LogInformation("USB: the phone is back on USB");
        }
        if (devices.Length == 0) { Report("no phone connected, waiting..."); return; }

        _missingApp = _missingApp.Where(devices.Contains).ToArray();
        foreach (var d in devices)
        {
            var list = await Adb(adb, $"-s {d} reverse --list", ct);
            if (list.Contains(Rule) && !_missingApp.Contains(d))
            {
                // A re-plug inside one live agent looks different from a boot: the adb SERVER caches the
                // reverse rules and re-applies them the moment the transport re-attaches, so the rule never
                // appears missing here - yet the display is exactly as asleep as on a cold boot (measured
                // 11-13 14:49:28: charge edge true, five seconds of keyguard flash, dark). Any device this
                // process has not woken since it started gets the same treatment as the cold path.
                if (!_seen.Contains(d)) await LightPanel(d, adb, "phone reconnected", ct);
                _seen.Add(d);
                continue;
            }
            // is the app there at all? (the agent may have been installed on its own, with winget or Scoop)
            var installed = (await Adb(adb, $"-s {d} shell pm path net.oxog.phonebox.deskpanel", ct, anyExit: true)).Contains("package:");
            _missingApp = installed ? _missingApp.Where(x => x != d).ToArray() : _missingApp.Union([d]).ToArray();
            if (!installed) { Report($"{d}: the PhoneBox app is not installed (Settings › General › Install the phone app)"); continue; }
            if (list.Contains(Rule)) continue;
            await Adb(adb, $"-s {d} reverse {Rule} {TargetRule}", ct);
            await LightPanel(d, adb, "adb reverse set up", ct);
            _seen.Add(d);
        }
        // Settings › Display › home screen: the app offers itself as a home app; make it the default one (no root needed).
        if (cfg.Get().Display.HomeScreen)
        {
            foreach (var d in devices.Where(d => !_homeSet.Contains(d)))
            {
                var r = await Adb(adb, $"-s {d} shell cmd package set-home-activity {HomeAlias}", ct);
                if (!r.Contains("Success")) continue; // the app has not enabled its home entry yet; try again shortly
                _homeSet.Add(d);
                log.LogInformation("{Device}: PhoneBox is now the home app", d);
            }
        }
        else _homeSet.Clear();
        // Settings › Display › keep awake: Android's screen saver (Daydream) starts on the dock or charger and covers the
        // panel; turn it off and keep the screen on while plugged in (plain adb settings, no root). One exception:
        // a standby panel must stay able to sleep while 5 V is alive (ErP off, a charge-capped port), and
        // stay_on_while_plugged_in is system-level - PowerManagerService would outvote the app releasing its own
        // keep-awake flag (measured: the app released the hold, the timeout fired, the screen stayed up until the
        // setting was 0). The app cannot clear it (WRITE_SETTINGS), so the agent writes 7 while standby is off -
        // today's behaviour, unchanged - and 0 the moment standby is on. The value rides in the key, so flipping
        // the setting re-applies it without restarting the agent.
        var keepAwake = cfg.Get().Display.KeepAwake;
        var stayOn = cfg.Get().Display.Standby == "off" ? "7" : "0";
        // the applied-state key carries which branch applied it: the keep-awake-off branch writes only
        // stay_on, and sharing one key would let that half-apply silence the screen-saver writes later
        if (keepAwake)
        {
            foreach (var d in devices.Where(d => !_awakeSet.Contains($"{d}:{stayOn}:on") && !_missingApp.Contains(d)))
            {
                await Adb(adb, $"-s {d} shell settings put secure screensaver_enabled 0", ct);
                await Adb(adb, $"-s {d} shell settings put secure screensaver_activate_on_dock 0", ct);
                await Adb(adb, $"-s {d} shell settings put secure screensaver_activate_on_sleep 0", ct);
                await Adb(adb, $"-s {d} shell settings put global stay_on_while_plugged_in {stayOn}", ct);
                _awakeSet.Add($"{d}:{stayOn}:on");
                log.LogInformation("{Device}: screen saver off, stay_on_while_plugged_in={StayOn}", d, stayOn);
            }
        }
        else if (cfg.Get().Display.Standby != "off")
        {
            // "keep awake off" used to mean "never touch the phone's power settings" - but one stale value
            // still breaks standby with that off: stay_on_while_plugged_in survives reboots, an earlier
            // keepAwake=true run may have left 7 behind, and some stock ROMs ship it non-zero. While the
            // phone reports plugged, that system flag outvotes the app releasing its own hold even though
            // the app was never the one holding the screen on. The standby override therefore lands
            // independently of KeepAwake; the screen-saver writes stay the optional part.
            foreach (var d in devices.Where(d => !_awakeSet.Contains($"{d}:{stayOn}:off") && !_missingApp.Contains(d)))
            {
                await Adb(adb, $"-s {d} shell settings put global stay_on_while_plugged_in {stayOn}", ct);
                _awakeSet.Add($"{d}:{stayOn}:off");
                log.LogInformation("{Device}: standby cleared stay_on_while_plugged_in (keep awake off)", d);
            }
        }
        else _awakeSet.Clear();
        _awakeSet.IntersectWith(devices.Select(d => $"{d}:{stayOn}:{(keepAwake ? "on" : "off")}"));
        Report($"phone connected: {Devices}");
    }

    private void SetDevices(string[] devices)
    {
        var changed = string.Join(",", devices) != string.Join(",", _devices);
        _devices = devices;
        if (changed) StateChanged?.Invoke();
    }

    private void Report(string state)
    {
        if (state == _lastState) return;
        _lastState = state;
        log.LogInformation("{State}", state);
    }

    public static string? FindAdb(string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var candidates = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(dir, "adb"));
            candidates.AddRange([Path.Combine(home, "Android", "Sdk", "platform-tools", "adb"), Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb"),
                "/opt/homebrew/bin/adb", "/usr/local/bin/adb", "/usr/bin/adb", "/usr/lib/android-sdk/platform-tools/adb"]);
            return candidates.FirstOrDefault(File.Exists);
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            candidates.AddRange(Directory.GetDirectories(winget, "Google.PlatformTools*")
                .Select(d => Path.Combine(d, "platform-tools", "adb.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static async Task<string> Adb(string adb, string args, CancellationToken ct, bool anyExit = false)
    {
        var psi = new ProcessStartInfo(adb, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        var result = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(8), ct);
        if (result.ExitCode != 0 && !anyExit) throw new IOException($"ADB exited with code {result.ExitCode}");
        return result.Output;
    }
}
