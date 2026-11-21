using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// While a phone is connected over USB, keeps an "adb reverse tcp:8765 tcp:&lt;USB port&gt;" rule in place so that
/// 127.0.0.1:8765 on the phone reaches this agent's USB port, and reads the key the phone's app sends there (see
/// <see cref="UsbLink"/>). When a phone is newly plugged in, wakes it and opens the panel
/// (unless Settings › Display › open on connect is off).
/// </summary>
public sealed class AdbReverse(ConfigStore cfg, ILogger<AdbReverse> log, AgentEndpoint? endpoint = null, UsbLink? usb = null) : BackgroundService
{
    public const string PanelActivity = "net.oxog.phonebox.deskpanel/.MainActivity";
    private const string HomeAlias = "net.oxog.phonebox.deskpanel/.HomeAlias";
    private readonly HashSet<string> _homeSet = [];
    private readonly HashSet<string> _awakeSet = [];
    private volatile string[] _missingApp = [];

    /// <summary>Connected phones that do not have the PhoneBox app yet (installed from Settings, e.g. after winget / Scoop).</summary>
    public string[] MissingApp => _missingApp;
    private const string Rule = "tcp:8765";
    private string TargetRule => $"tcp:{usb?.Port ?? endpoint?.Port ?? 8765}";
    private readonly Dictionary<string, long> _keyTriedAt = []; // serial -> when its key was last asked for (Ensure only)
    private readonly HashSet<string> _oldApp = [];              // phones already told that their app is too old
    private readonly HashSet<string> _reverseSet = [];          // phones whose reverse rule this agent set (read only for Android 5 and 6)
    private readonly Dictionary<string, int> _sdk = [];         // serial -> Android API level, asked once per connection
    private readonly Dictionary<string, string> _transport = []; // serial -> adb transport id its remembered rule was set on
    internal const long KeyRetryMs = 30_000;
    // asked again now and then even when known: an app whose data was cleared has a new key and no pin, and can't say so
    internal const long KeyRefreshMs = 120_000;

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

    /// <summary>
    /// True for a minute after Windows starts ending the session (shutdown, restart, sign-out). A process started then
    /// can't initialize, and Windows shows "adb.exe - Application Error (0xc0000142)" for it (#179), so no adb is
    /// started. Windows sends no event when a shutdown is cancelled (an app refused to close); if the agent is still
    /// running after the minute, the session went on, and adb runs again.
    /// </summary>
    internal static bool SessionEnding
    {
        get { var since = Interlocked.Read(ref _endingSince); return since != 0 && Clock() - since < EndingWindowMs; }
    }

    internal static void MarkSessionEnding() => Interlocked.Exchange(ref _endingSince, Math.Max(1, Clock()));
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;
    internal const long EndingWindowMs = 60_000;
    internal static void ResetSessionEnding() => Interlocked.Exchange(ref _endingSince, 0); // tests
    private static long _endingSince;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
#if WINDOWS
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => MarkSessionEnding();
#endif
        while (!ct.IsCancellationRequested)
        {
            // while the session is ending nothing is started; the loop goes on, for a shutdown that is cancelled
            if (SessionEnding) { await Task.Delay(TimeSpan.FromSeconds(4), ct); continue; }
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
        usb?.Retain(devices);
        foreach (var gone in _keyTriedAt.Keys.Except(devices).ToList()) _keyTriedAt.Remove(gone); // plugged in again: ask at once
        _oldApp.IntersectWith(devices);
        // a request came with a key the agent doesn't know (the app was reinstalled, its data cleared): ask again
        var refreshKeys = usb?.TakeUnknownKey() ?? false;
        var problem = UsbProblem;
        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            if (problem is not null) log.LogWarning("USB: {Problem}", problem);
            if (problem is null && devices.Length > 0) log.LogInformation("USB: the phone is back on USB");
        }
        _missingApp = _missingApp.Where(devices.Contains).ToArray();
        if (devices.Length == 0) { Report("no phone connected, waiting..."); return; }

        // A phone that did not answer "reverse --list" sits out the rest of this round (home screen, keep awake): one
        // more failure there would end the round before the phones after it. The next round tries it again.
        var unreachable = new HashSet<string>();
        Dictionary<string, string>? transports = null; // read at most once a round, only for Android 5 and 6
        foreach (var d in devices)
        {
            // adbd on Android 5 and 6 (API 21-23) answers "reverse --list" with "protocol fault", and adb then drops and
            // re-attaches the phone (the phone's own USB dialog covers the panel); later calls list nothing although the
            // rule is there. Those phones are never asked: the agent remembers the rule it set on them instead.
            if (!_sdk.TryGetValue(d, out var sdk))
            {
                try
                {
                    var answer = await Adb(adb, $"-s {d} shell getprop ro.build.version.sdk", ct, anyExit: true);
                    if (int.TryParse(answer.Trim(), out sdk)) _sdk[d] = sdk;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    log.LogWarning("{Device}: could not read the Android version ({Message})", d, ex.Message);
                    unreachable.Add(d);
                    continue;
                }
            }
            var listable = sdk is <= 0 or > 23;
            var list = "";
            if (listable)
            {
                try { list = await Adb(adb, $"-s {d} reverse --list", ct); }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    log.LogWarning("{Device}: could not list adb reverse rules ({Message})", d, ex.Message);
                    unreachable.Add(d);
                    continue;
                }
            }
            if (!listable)
            {
                // A phone unplugged and plugged in again between two rounds keeps its serial but loses its rule (adb binds
                // reverse rules to the connection). adb gives each connection a new transport id: a new one means set it again.
                transports ??= await Transports(adb, ct);
                // no id on record (adb could not say when the rule was set): set it once more to have one
                if (transports.TryGetValue(d, out var now) && (!_transport.TryGetValue(d, out var then) || now != then))
                    _reverseSet.Remove(d);
            }
            var ruled = listable ? HasOwnRule(list) : _reverseSet.Contains(d);
            var keyed = usb is null || KeyAskedRecently(d, usb.HasKey(d) && !refreshKeys ? KeyRefreshMs : KeyRetryMs);
            if (ruled && keyed && !_missingApp.Contains(d)) continue;
            // is the app there at all? (the agent may have been installed on its own, with winget or Scoop)
            var installed = (await Adb(adb, $"-s {d} shell pm path net.oxog.phonebox.deskpanel", ct, anyExit: true)).Contains("package:");
            _missingApp = installed ? _missingApp.Where(x => x != d).ToArray() : _missingApp.Union([d]).ToArray();
            if (!installed) { Report($"{d}: the PhoneBox app is not installed (Settings › General › Install the phone app)"); continue; }
            if (!keyed) await ReadKeyAsync(adb, d, ct);
            if (ruled) continue;
            // the phone had a rule already: this agent restarted (its USB port changed), the phone was not plugged in now
            var moved = HasAnyRule(list);
            await Adb(adb, $"-s {d} reverse {Rule} {TargetRule}", ct);
            _reverseSet.Add(d);
            if (transports?.TryGetValue(d, out var transport) == true) _transport[d] = transport;
            if (moved) { log.LogInformation("{Device}: adb reverse moved to this agent", d); continue; }
            // A newly plugged-in phone must not light up while the computer is locked.
            if (cfg.Get().Display.FollowComputerLock && OperatingSystem.IsMacOS())
            {
                await SyncSessionAsync(ct, force: true);
            }
            else if (cfg.Get().Display.OpenOnConnect)
            {
                await Adb(adb, $"-s {d} shell input keyevent KEYCODE_WAKEUP", ct);
                await Adb(adb, $"-s {d} shell am start -n {PanelActivity}", ct);
                log.LogInformation("{Device}: adb reverse set up, panel opened", d);
            }
            // Settings › Display › open on connect is off: the phone stays as it is (the panel opens when the user starts it).
            else log.LogInformation("{Device}: adb reverse set up", d);
        }
        // Settings › Display › home screen: the app offers itself as a home app; make it the default one (no root needed).
        if (cfg.Get().Display.HomeScreen)
        {
            foreach (var d in devices.Where(d => !_homeSet.Contains(d) && !unreachable.Contains(d)))
            {
                // Android answers a refusal with exit code 1; that must not end the round for the other phones and keep-awake.
                var r = await Adb(adb, $"-s {d} shell cmd package set-home-activity {HomeAlias}", ct, anyExit: true);
                if (!r.Contains("Success")) continue; // the app has not enabled its home entry yet; try again shortly
                _homeSet.Add(d);
                log.LogInformation("{Device}: PhoneBox is now the home app", d);
            }
        }
        else _homeSet.Clear();
        // Settings › Display › keep awake: Android's screen saver (Daydream) starts on the dock or charger and covers the
        // panel; turn it off and keep the screen on while plugged in (plain adb settings, no root).
        if (cfg.Get().Display.KeepAwake)
        {
            foreach (var d in devices.Where(d => !_awakeSet.Contains(d) && !_missingApp.Contains(d) && !unreachable.Contains(d)))
            {
                await Adb(adb, $"-s {d} shell settings put secure screensaver_enabled 0", ct);
                await Adb(adb, $"-s {d} shell settings put secure screensaver_activate_on_dock 0", ct);
                await Adb(adb, $"-s {d} shell settings put secure screensaver_activate_on_sleep 0", ct);
                await Adb(adb, $"-s {d} shell settings put global stay_on_while_plugged_in 7", ct);
                _awakeSet.Add(d);
                log.LogInformation("{Device}: screen saver off, stays awake while plugged in", d);
            }
        }
        else _awakeSet.Clear();
        Report($"phone connected: {Devices}");
    }

    /// <summary>
    /// Reads the phone app's USB key and gives the app this agent's certificate fingerprint (see <see cref="UsbLink"/>),
    /// once the key provider is known to be the app's own. An app from before the key has no provider: it can't use
    /// USB until it is updated, which the settings page offers once it knows the app's version.
    /// </summary>
    private async Task ReadKeyAsync(string adb, string d, CancellationToken ct)
    {
        if (usb is null) return;
        _keyTriedAt[d] = Environment.TickCount64;
        var dump = await Adb(adb, $"-s {d} shell dumpsys package {UsbLink.Package}", ct, anyExit: true);
        var key = UsbLink.OwnsKeyProvider(dump)
            ? UsbLink.ParseKey(await Adb(adb, $"-s {d} shell content query --uri {UsbLink.KeyUri}", ct, anyExit: true))
            : null;
        if (key is not null)
        {
            await Adb(adb, $"-s {d} shell content insert --uri {UsbLink.PinUri} --bind fp:s:{usb.Pin}", ct, anyExit: true);
            usb.SetKey(d, key);
            _oldApp.Remove(d);
            return;
        }
        usb.Forget(d);
        if (!_oldApp.Add(d)) return;
        log.LogWarning("{Device}: the PhoneBox app on the phone is too old for this agent's USB connection; update it from the settings page", d);
        // it can't report its version over USB any more, so the update check takes it from here
        if (UsbLink.ParseVersionName(dump) is { } version) UpdateService.AppVersion ??= version;
    }

    private bool KeyAskedRecently(string d, long window) =>
        _keyTriedAt.TryGetValue(d, out var at) && Environment.TickCount64 - at < window;

    /// <summary>True when "reverse --list" has any rule for the phone's tcp:8765, whatever it points at.</summary>
    internal static bool HasAnyRule(string list) => list.Split('\n')
        .Select(l => l.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
        .Any(p => p.Length >= 2 && p[^2] == Rule);

    /// <summary>
    /// True when "reverse --list" has this agent's rule: phone tcp:8765 to this agent's port. A rule left by an agent on
    /// another port (--port) points the phone at a dead port; lines read "&lt;transport&gt; &lt;phone&gt; &lt;computer&gt;".
    /// </summary>
    private bool HasOwnRule(string list) => list.Split('\n')
        .Select(l => l.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
        .Any(p => p.Length >= 2 && p[^2] == Rule && p[^1] == TargetRule);

    private void SetDevices(string[] devices)
    {
        _awakeSet.IntersectWith(devices);
        _reverseSet.IntersectWith(devices); // plugged in again: the rule has to be set again
        foreach (var gone in _sdk.Keys.Except(devices).ToList()) _sdk.Remove(gone);
        foreach (var gone in _transport.Keys.Except(devices).ToList()) _transport.Remove(gone);
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
            candidates.Add(Path.Combine(dir.Trim().Trim('"'), "adb.exe")); // PATH entries may be quoted
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            candidates.AddRange(Directory.GetDirectories(winget, "Google.PlatformTools*")
                .Select(d => Path.Combine(d, "platform-tools", "adb.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>serial -> transport id from "adb devices -l" (adb 1.0.40 and later); empty when adb does not say.</summary>
    private static async Task<Dictionary<string, string>> Transports(string adb, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var line in (await Adb(adb, "devices -l", ct)).Split('\n').Skip(1))
            {
                var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && parts.FirstOrDefault(x => x.StartsWith("transport_id:", StringComparison.Ordinal)) is { } id)
                    map[parts[0]] = id["transport_id:".Length..];
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        return map;
    }

    internal static async Task<string> Adb(string adb, string args, CancellationToken ct, bool anyExit = false)
    {
        if (SessionEnding) throw new OperationCanceledException("the session is ending");
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
