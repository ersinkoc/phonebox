using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// While a phone is connected over USB, keeps an "adb reverse tcp:8765 tcp:&lt;USB port&gt;" rule in place so that
/// 127.0.0.1:8765 on the phone reaches this agent's USB port, and reads the key the phone's app sends there (see
/// <see cref="UsbLink"/>). When a phone is newly plugged in, wakes it and opens the panel
/// (unless Settings › Display › open on connect is off).
/// </summary>
public sealed class AdbReverse(ConfigStore cfg, ILogger<AdbReverse> log, AgentEndpoint? endpoint = null, UsbLink? usb = null,
    IHostApplicationLifetime? lifetime = null) : BackgroundService
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
    // serial -> API level of each attached phone this agent set its rule on, kept when Settings › Phone connection
    // selects another phone (_reverseSet and _sdk follow the selection), so a stop can still take that rule off
    private readonly Dictionary<string, int> _setHere = [];
    // phones a "reverse" command was sent to that has not answered (cancelled by a stop, or timed out): the adb server
    // may have applied it, so a stop removes it too (removing a rule that isn't there does no harm)
    private readonly HashSet<string> _pending = [];
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
    /// <summary>Connected phones after the deviceSerial filter (the phone mic picks the first one).</summary>
    public string[] DeviceList => _devices;
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
        // rounds end on their own token too: a stop cancels the one that is running, so the rule can come off before
        // the agent's port closes instead of after it (RemoveBeforeThePortCloses)
        using var rounds = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rounds = rounds;
        lifetime?.ApplicationStopping.Register(RemoveBeforeThePortCloses);
        TakeHandoff(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        while (!ct.IsCancellationRequested && !_stopping)
        {
            // while the session is ending nothing is started; the loop goes on, for a shutdown that is cancelled
            if (SessionEnding) { await Task.Delay(TimeSpan.FromSeconds(4), ct); continue; }
            try { await _round.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            try { if (!_stopping) await Ensure(rounds.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Report($"adb error: {ex.Message}"); }
            finally { _round.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
    }

    private volatile bool _stopping;
    private volatile CancellationTokenSource? _rounds;
    private readonly SemaphoreSlim _round = new(1, 1); // one Ensure round at a time, and none while the rule comes off

    /// <summary>
    /// A regular stop (Quit in the tray, an update, a restart) takes this agent's rule off the phone. Left behind, it
    /// points at a port nobody listens on any more; the panel keeps connecting there, and the adb server stops
    /// answering everyone (the next agent included) until it is restarted. It has to happen when the stop begins:
    /// the agent's port closes before the services are stopped, and the adb server hung within a second of that.
    /// </summary>
    private void RemoveBeforeThePortCloses()
    {
        _stopping = true; // a round that is still running sets no rule from here on (see Ensure)
        // That round also uses _reverseSet and _sdk, which are not shared safely, so it is cancelled (its adb calls end
        // at once) and the rule comes off once it has stopped. The callbacks of the cancel run on the thread pool, not
        // on this thread (the tray's UI thread on Quit). Should it still not have stopped within the (bounded) wait,
        // the rule is tried again in StopAsync, after the loop ended.
        try { _ = _rounds?.CancelAsync(); }
        catch (ObjectDisposedException) { } // the loop has already ended
        if (!_round.Wait(RoundStopWait)) return;
        try
        {
            // on the thread pool: Quit in the tray stops the agent from the UI thread, whose context the awaits
            // inside would wait for while this blocks it. Not done within StopBudget and a second (a busy thread
            // pool), StopAsync waits for the rest; no round can start meanwhile, the loop ends on _stopping.
            _cleanup = Task.Run(() => RemoveOwnRulesAsync(CancellationToken.None));
            _cleanup.Wait(StopBudget + TimeSpan.FromSeconds(1));
        }
        catch (AggregateException) { }
        finally { _round.Release(); }
    }

    private volatile Task? _cleanup; // the removal started when the stop began

    /// <summary>How long a stop may spend taking the rule off the phones, all of them together (tests give more).</summary>
    internal TimeSpan StopBudget { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long the start of a stop waits for a cancelled round to end before leaving the rule to StopAsync.</summary>
    internal TimeSpan RoundStopWait { get; init; } = TimeSpan.FromSeconds(2);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        await base.StopAsync(cancellationToken);
        if (_cleanup is { } started)
        {
            try { await started.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { } // the host's own shutdown deadline
        }
        // no lifetime (the tests), or a round was still running when the stop began: the loop has ended now
        else await RemoveOwnRulesAsync(cancellationToken);
    }

    internal async Task RemoveOwnRulesAsync(CancellationToken ct)
    {
        // The selected phones, and any other attached phone this agent set its rule on: Settings › Phone connection may
        // have switched to another phone since, which leaves the earlier one out of _devices with the rule still on it.
        var attached = _all;
        var phones = _devices.Union(_setHere.Keys.Where(attached.Contains)).Union(_pending.Where(attached.Contains)).ToArray();
        if (SessionEnding || phones.Length == 0 || FindAdb(cfg.Get().AdbPath) is not { } adb) return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StopBudget); // the agent is closing: never hold that up for long
        // Android 5 and 6 can't list their rules (see Ensure; asking even drops the connection): the one this agent set
        // is remembered instead. So is the rule on a phone whose version isn't known (its version read was cancelled):
        // it may be one of those. Only a phone known to be newer is asked. A phone no longer selected keeps its level
        // in _setHere. Read here, before the phones run side by side: the sets are only ever touched from one thread
        // at a time.
        int Level(string d) => _sdk.TryGetValue(d, out var sdk) && sdk > 0 ? sdk : _setHere.GetValueOrDefault(d);
        var remembered = phones.ToDictionary(d => d, d => Level(d) > 23
            ? (bool?)null
            : _reverseSet.Contains(d) || _setHere.ContainsKey(d) || _pending.Contains(d));
        // all phones at once, each with the whole budget: a slow phone must not leave the next one its stale rule
        var removed = await Task.WhenAll(phones.Select(async d =>
        {
            try
            {
                // on the others the rule must still be this agent's: a newer agent may have taken the phone over
                var ours = remembered[d] ?? HasOwnRule(await Adb(adb, ["-s", d, "reverse", "--list"], limit.Token));
                if (!ours) return null;
                await Adb(adb, ["-s", d, "reverse", "--remove", Rule], limit.Token);
                log.LogInformation("{Device}: adb reverse removed (the agent is stopping)", d);
                return d;
            }
            // Win32Exception: adb is there but can't be started; the other phones are still tried
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                log.LogInformation("{Device}: could not remove adb reverse ({Message})", d, ex.Message);
                return null;
            }
        }));
        foreach (var d in removed.OfType<string>())
        {
            _reverseSet.Remove(d);
            _setHere.Remove(d);
            _pending.Remove(d);
        }
        if (removed.OfType<string>().ToArray() is { Length: > 0 } handedOver) WriteHandoff(handedOver);
    }

    /// <summary>
    /// The phones whose rule a stop took off, for the agent that starts next (a restart, an update): it sets the rule
    /// again without waking the phone or opening the panel, as it did when the rule was still there. Kept only briefly.
    /// </summary>
    internal string HandoffPath => Path.Combine(Path.GetDirectoryName(cfg.Path) ?? AppPaths.ConfigDir, "usb-handoff.json");

    private sealed record Handoff(long Until, string[] Serials);

    private void WriteHandoff(string[] serials)
    {
        try
        {
            var until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + QuietAfterRestartMs;
            AppPaths.WriteAllTextPrivate(HandoffPath, System.Text.Json.JsonSerializer.Serialize(new Handoff(until, serials)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogInformation("could not note the phones for the next start ({Message})", ex.Message);
        }
    }

    /// <summary>Reads (and deletes) what the previous agent left in <see cref="HandoffPath"/>.</summary>
    internal void TakeHandoff(long nowUnixMs)
    {
        Handoff? handoff;
        try
        {
            if (!File.Exists(HandoffPath)) return;
            handoff = System.Text.Json.JsonSerializer.Deserialize<Handoff>(File.ReadAllText(HandoffPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { handoff = null; }
        try { File.Delete(HandoffPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // expired, or from a clock that has jumped: the phones count as newly plugged in
        if (handoff?.Serials is not { } serials || handoff.Until - nowUnixMs is not (> 0 and <= QuietAfterRestartMs) || serials.Length == 0) return;
        _quiet.UnionWith(serials.Where(x => !string.IsNullOrEmpty(x)));
        _quietUntil = Environment.TickCount64 + (handoff.Until - nowUnixMs);
    }

    private readonly AdbWatchdog _watchdog = new();

    /// <summary>
    /// The adb server took the "devices" call but never answered it for a minute: it is hung (an agent that was killed
    /// left its rule behind, for one) and nothing gets through until it is restarted. adb's own kill-server asks the
    /// server itself, which a hung one may never answer, so its process is ended too; the next round's adb starts a
    /// fresh server, which also drops every reverse rule.
    /// </summary>
    internal async Task RestartAdbServerAsync(string adb, CancellationToken ct)
    {
        if (AdbServerPort() is not { } port)
        {
            // ADB_SERVER_SOCKET or ANDROID_ADB_SERVER_ADDRESS: a server somewhere else, not ours to restart
            if (!_remoteLogged) log.LogWarning("adb server has not answered for a minute; it is not a local one (ADB_SERVER_SOCKET or "
                + "ANDROID_ADB_SERVER_ADDRESS is set), so the agent doesn't restart it");
            _remoteLogged = true;
            return;
        }
        var pid = await FindServerPid(port, ct);
        // an install may have begun meanwhile (the lookup takes a moment): ending the server would cut it short. Nor
        // is adb started while the session ends (see SessionEnding; RunAdb doesn't cover the kill-server below).
        // Nothing awaits between here and kill-server.
        if (CalledOffForInstall()) return;
        if (SessionEnding) { log.LogInformation("the session is ending: the adb server restart is called off"); return; }
        if (pid is null)
            // kill-server is all that is left, and a hung server may not answer it: the log must not claim a restart
            log.LogWarning("adb server has not answered for a minute, and its process on port {Port} could not be found: "
                + "only asking it to stop (adb kill-server)", port);
        else log.LogWarning("adb server has not answered for a minute: restarting it (process {Pid})", pid);
        // the phones that had their rule keep their screen as it is when it is set again: not woken, no panel opened
        _quietUntil = Environment.TickCount64 + QuietAfterRestartMs;
        _quiet.Clear();
        _quiet.UnionWith(_all);
        _quiet.UnionWith(_setHere.Keys);
        // A fresh server has no reverse rules and numbers its transports from 1 again, so with one phone the transport
        // id can come back unchanged: on Android 5 and 6 nothing else would tell that the remembered rule is gone.
        _reverseSet.Clear();
        _transport.Clear();
        _setHere.Clear();
        _pending.Clear();
        _restarted = true;
        try { await ToolProcess.RunAsync(new ProcessStartInfo(adb) { ArgumentList = { "kill-server" } }, TimeSpan.FromSeconds(3), ct); }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
        if (pid is not { } id || CalledOffForInstall()) return;
        try
        {
            using var process = Process.GetProcessById(id);
            // only adb itself: whatever else may hold the port is left alone
            if (!process.HasExited && process.ProcessName.Equals("adb", StringComparison.OrdinalIgnoreCase)) process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogInformation("could not end the adb server ({Message})", ex.Message);
        }
    }

    private bool _remoteLogged;

    /// <summary>An app install started after the restart was decided on: the restart (or what is left of it) is called off.</summary>
    private bool CalledOffForInstall()
    {
        if (!InstallRunning) return false;
        log.LogInformation("an app install started: the adb server restart is called off");
        return true;
    }

    /// <summary>
    /// The adb server's port as adb itself picks it, or null when ADB_SERVER_SOCKET or a non-local
    /// ANDROID_ADB_SERVER_ADDRESS points adb at a server elsewhere.
    /// </summary>
    internal static int? AdbServerPort()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET"))) return null;
        if (Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS") is { Length: > 0 } address
            && address.Trim() is not ("localhost" or "127.0.0.1" or "::1" or "[::1]")) return null;
        return int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : 5037;
    }

    private bool _restarted; // the next "devices" answer (or its absence) is logged: did the restart help?

    // Phones attached when the adb server was restarted: their rule is set again without waking them (an automatic
    // restart at night must not light up the phones). Kept for a while rather than until the first "devices" answer,
    // which may come before a phone is listed again; one plugged in again meanwhile is not woken either.
    private readonly HashSet<string> _quiet = [];
    private long _quietUntil;
    internal const long QuietAfterRestartMs = 120_000;

    private int _installs;
    private bool _heldForInstall;

    /// <summary>An "adb install" is running (see <see cref="UpdateService"/>): a restart of the adb server would end it.</summary>
    public bool InstallRunning => Volatile.Read(ref _installs) > 0;

    /// <summary>Marks an "adb install" as running until the returned scope is disposed.</summary>
    internal IDisposable Installing()
    {
        Interlocked.Increment(ref _installs);
        return new InstallScope(this);
    }

    private sealed class InstallScope(AdbReverse owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._installs); }
    }

    /// <summary>
    /// "devices" timed out: whether the adb server is restarted now. Not while an app install is running: the restart
    /// would cut it short, and an install streaming to an old phone can keep the server busy for a while.
    /// </summary>
    internal bool RestartDue(long now)
    {
        if (InstallRunning)
        {
            if (!_heldForInstall) log.LogInformation("adb server does not answer, but an app install is running: not restarting it meanwhile");
            _heldForInstall = true;
            _watchdog.Answered(); // time spent installing doesn't count: the minute starts again after the install
            return false;
        }
        _heldForInstall = false;
        return _watchdog.Silent(now);
    }

    /// <summary>Finds the adb server's process (the tests replace it, so they never touch a real adb server).</summary>
    internal static Func<int, CancellationToken, Task<int?>> FindServerPid { get; set; } = AdbServerPidAsync;

    /// <summary>The process listening on the adb server's port, or null when that can't be told.</summary>
    private static async Task<int?> AdbServerPidAsync(int port, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // by its full path: a lookup on PATH starts in the agent's own folder, where a planted netstat.exe would win
                var netstat = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netstat.exe"))
                {
                    ArgumentList = { "-ano", "-p", "TCP" },
                    WorkingDirectory = AppPaths.NeutralCwd,
                };
                return ListenerFromNetstat((await ToolProcess.RunAsync(netstat, TimeSpan.FromSeconds(5), ct)).Output, port);
            }
            // by their full paths too (a bare name is looked up in the agent's folder first); many Linux distributions
            // don't install lsof, while ss comes with iproute2
            if (FirstExisting("/usr/sbin/lsof", "/usr/bin/lsof", "/sbin/lsof", "/bin/lsof") is { } lsofPath)
            {
                var lsof = new ProcessStartInfo(lsofPath) { ArgumentList = { "-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-t" } };
                // lsof that can't start or hangs leaves ss to try (a stop's cancellation still ends the lookup)
                try
                {
                    var output = (await ToolProcess.RunAsync(lsof, TimeSpan.FromSeconds(5), ct)).Output;
                    if (int.TryParse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(), out var pid)) return pid;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
            }
            if (OperatingSystem.IsLinux() && FirstExisting("/usr/bin/ss", "/bin/ss", "/usr/sbin/ss", "/sbin/ss") is { } ssPath)
            {
                var ss = new ProcessStartInfo(ssPath) { ArgumentList = { "-Hltnp", "sport", "=", $":{port}" } };
                return ListenerFromSs((await ToolProcess.RunAsync(ss, TimeSpan.FromSeconds(5), ct)).Output);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>
    /// The process in "ss -Hltnp" output: users:(("adb",pid=1234,fd=6)). ss shows it only for the user's own processes
    /// (the adb server normally is one); an empty answer, or one without a process, gives null.
    /// </summary>
    internal static int? ListenerFromSs(string output) =>
        System.Text.RegularExpressions.Regex.Match(output, @"\bpid=(\d+)") is { Success: true } m
        && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
        && pid > 0 ? pid : null;

    /// <summary>
    /// The listener on <paramref name="port"/> in "netstat -ano" output. The state column is translated on a Windows
    /// in another language (ABHÖREN …), so a listener is told by its remote address instead, which is all zeros.
    /// </summary>
    internal static int? ListenerFromNetstat(string output, int port)
    {
        foreach (var line in output.Split('\n'))
        {
            var p = line.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 5 && p[0] == "TCP" && p[1].EndsWith(":" + port, StringComparison.Ordinal)
                && p[2] is "0.0.0.0:0" or "[::]:0" && int.TryParse(p[^1], out var pid) && pid > 0)
                return pid;
        }
        return null;
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

    /// <summary>Puts connected phone screens to sleep immediately (e.g. when the lock shortcut is triggered).</summary>
    public async Task SleepPhonesAsync(CancellationToken ct = default)
    {
        var adb = FindAdb(cfg.Get().AdbPath);
        if (adb is null) return;
        // after the running round: a phone it just connected gets its wake-up before this sleep, not after.
        // A round that hangs does not hold the lock shortcut back for long.
        var held = await _round.WaitAsync(SleepRoundWait, ct);
        try
        {
            // the stop cancels the rounds' token: these calls end with it, so the stop gets the round in time to take
            // the rule off before the port closes (RemoveBeforeThePortCloses)
            CancellationToken stop;
            try { stop = _rounds?.Token ?? CancellationToken.None; }
            catch (ObjectDisposedException) { return; } // the loop has ended: the agent is stopping
            using var calls = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
            foreach (var d in _devices)
            {
                if (_stopping) return;
                try
                {
                    await Adb(adb, ["-s", d, "shell", "input", "keyevent", "KEYCODE_SLEEP"], calls.Token);
                    log.LogInformation("{Device}: phone screen asleep (lock shortcut)", d);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "{Device}: failed to sleep phone screen", d);
                }
            }
        }
        finally { if (held) _round.Release(); }
    }
    internal TimeSpan SleepRoundWait { get; init; } = TimeSpan.FromSeconds(30);

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
        string listed;
        try { listed = await Adb(adb, "devices", ct); }
        catch (TimeoutException)
        {
            if (_restarted) { _restarted = false; log.LogWarning("adb server still does not answer after the restart"); }
            if (RestartDue(Environment.TickCount64)) await RestartAdbServerAsync(adb, ct);
            throw;
        }
        if (_restarted) { _restarted = false; log.LogInformation("adb server answers again after the restart"); }
        _watchdog.Answered();
        var devices = listed
            .Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2 && p[1] == "device")
            .Select(p => p[0])
            .ToArray();
        _all = devices;
        foreach (var gone in _setHere.Keys.Except(devices).ToList()) _setHere.Remove(gone); // unplugged: adb dropped its rule
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
                {
                    _reverseSet.Remove(d);
                    _setHere.Remove(d);
                }
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
            if (_stopping) return; // the agent is closing: a rule set now would be left behind (RemoveBeforeThePortCloses)
            // noted before it is sent: a stop that cancels it may come after the adb server applied it
            _pending.Add(d);
            try { await Adb(adb, $"-s {d} reverse {Rule} {TargetRule}", ct); }
            catch (IOException) { _pending.Remove(d); throw; } // adb refused it: no rule was set
            _pending.Remove(d);
            _reverseSet.Add(d);
            _setHere[d] = sdk;
            if (transports?.TryGetValue(d, out var transport) == true) _transport[d] = transport;
            if (moved) { log.LogInformation("{Device}: adb reverse moved to this agent", d); continue; }
            // the adb server was restarted: the phone was set up already, and is left as it is
            if (_quiet.Remove(d) && Environment.TickCount64 < _quietUntil)
            {
                log.LogInformation("{Device}: adb reverse set again after the adb server restart", d);
                continue;
            }
            // A newly plugged-in phone must not light up while the computer is locked.
            if (cfg.Get().Display.FollowComputerLock && OperatingSystem.IsMacOS())
            {
                await SyncSessionAsync(ct, force: true);
            }
            else if (cfg.Get().Display.OpenOnConnect)
            {
                await Adb(adb, $"-s {d} shell input keyevent KEYCODE_WAKEUP", ct);
                // Settings › Display › wake and unlock: the keyguard is dismissed only when the phone reports it
                // is showing one — no input goes to whatever else is on screen, and a PIN, pattern or password is
                // not passed: "wm dismiss-keyguard" only steps over swipe-to-unlock screens.
                if (cfg.Get().Display.WakeOnUnlock && await KeyguardShowingAsync(adb, d, ct))
                {
                    // optional: a dismiss that fails must not keep the panel from opening (the rule is set already, so
                    // the next round would not come back to open it)
                    try { await Adb(adb, $"-s {d} shell wm dismiss-keyguard", ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
                    { log.LogInformation("{Device}: the lock screen could not be stepped over ({Why})", d, ex.Message); }
                }
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

    /// <summary>
    /// True when a "dumpsys window" dump says a keyguard is on screen. Which flag says so depends on the build:
    /// "mShowingLockscreen=true" (Android 11 and older), "isKeyguardShowing=true" (Android 12 and newer), or only
    /// "showing=true" on devices whose window service prints neither — measured on a Redmi Note 7 (MIUI 12.5, Android
    /// 10), which when locked printed just "showing=true", "showingAndNotOccluded=true", "secure=false" and
    /// "mDreamingLockscreen=true", and when unlocked printed "showing=false". "showing=" is therefore compared as a
    /// whole trimmed line, so MIUI's "showingAndNotOccluded=true", which is not a keyguard state, cannot match. A dump
    /// that names no flag reads false: the unlock is then skipped, which is the safe direction, the phone being left as
    /// it is rather than acted on blindly.
    /// </summary>
    /// <summary>Whether the phone shows a keyguard; a dump that fails or takes too long reads false, so the unlock is
    /// skipped and the panel still opens.</summary>
    private async Task<bool> KeyguardShowingAsync(string adb, string d, CancellationToken ct)
    {
        try { return KeyguardShowing(await Adb(adb, $"-s {d} shell dumpsys window", ct, anyExit: true)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            log.LogInformation("{Device}: could not read the lock screen state ({Why}); not unlocking", d, ex.Message);
            return false;
        }
    }

    internal static bool KeyguardShowing(string dumpsysWindow) =>
        dumpsysWindow.Contains("mShowingLockscreen=true", StringComparison.Ordinal) ||
        dumpsysWindow.Contains("isKeyguardShowing=true", StringComparison.Ordinal) ||
        dumpsysWindow.Split('\n').Any(line => line.Trim() == "showing=true");

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

    internal static Task<string> Adb(string adb, string args, CancellationToken ct, bool anyExit = false) =>
        RunAdb(new ProcessStartInfo(adb, args), ct, anyExit);

    /// <summary>The same with each argument kept separate (a serial is passed as it is).</summary>
    internal static Task<string> Adb(string adb, IEnumerable<string> args, CancellationToken ct, bool anyExit = false)
    {
        var psi = new ProcessStartInfo(adb);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return RunAdb(psi, ct, anyExit);
    }

    private static async Task<string> RunAdb(ProcessStartInfo psi, CancellationToken ct, bool anyExit)
    {
        if (SessionEnding) throw new OperationCanceledException("the session is ending");
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = AppPaths.NeutralCwd;
        var result = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(8), ct);
        if (result.ExitCode != 0 && !anyExit) throw new IOException($"ADB exited with code {result.ExitCode}");
        return result.Output;
    }
}

/// <summary>
/// When the adb server counts as hung: its "devices" call has timed out for <paramref name="silentMs"/> in a row. A
/// restart is not repeated within <paramref name="cooldownMs"/>, so a server that is slow for some other reason is
/// not restarted over and over.
/// </summary>
internal sealed class AdbWatchdog(long silentMs = 60_000, long cooldownMs = 300_000)
{
    private long? _silentSince, _restartedAt;

    /// <summary>The server answered (or the silence doesn't count, as during an install): the minute starts over. The
    /// cooldown after a restart is kept.</summary>
    public void Answered() => _silentSince = null;

    /// <returns>True when the server should be restarted now.</returns>
    public bool Silent(long now)
    {
        _silentSince ??= now;
        if (now - _silentSince < silentMs || now - _restartedAt < cooldownMs) return false;
        _restartedAt = now;
        _silentSince = null; // a fresh minute before the next one
        return true;
    }
}
