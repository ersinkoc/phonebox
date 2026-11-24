using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record DiscordShareApp(int Pid, string Name);

/// <param name="Code">What the panel shows in its own language (see pc.js); <paramref name="Error"/> is for other callers and the log.</param>
public sealed record DiscordShareError(string Code, string Error);

/// <summary>
/// Screen share from the phone (Settings › General › Discord › screen share, off by default; it adds the
/// rpc.screenshare.read / .write scopes). Discord's TOGGLE_SCREENSHARE starts a share of a given process, or with no
/// process shows Discord's own picker on the computer (or goes live with the game Discord detects), and the same command
/// stops a share. SCREENSHARE_STATE_UPDATE says whether you are sharing: once right after subscribing, then on changes.
/// Which apps Discord can share directly is left to Discord: PhoneBox doesn't look games up anywhere, it asks and watches
/// whether the share starts.
/// </summary>
public sealed partial class DiscordMonitor
{
    /// <summary>The most apps listed.</summary>
    internal const int ShareAppsMax = 30;
    /// <summary>Discord's builds on Windows: the process name and the folder under %LOCALAPPDATA% that holds its Update.exe.</summary>
    internal static readonly (string Process, string Folder)[] DiscordBuilds = [("Discord", "Discord"), ("DiscordPTB", "DiscordPTB"), ("DiscordCanary", "DiscordCanary")];

    /// <summary>The builds to bring forward, the one the agent is connected to first (it opened the picker), then the others in order.</summary>
    internal static IEnumerable<(string Process, string Folder)> BuildsFor(string? connected) =>
        DiscordBuilds.OrderBy(b => string.Equals(b.Process, connected, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    /// <summary>How long a direct share may take to start before the phone is told to use the picker.</summary>
    internal TimeSpan ShareStartWait = TimeSpan.FromSeconds(4); // internal: tests wait less
    /// <summary>How long a stop waits for a late "ended" event from Discord before it sends the toggle.</summary>
    internal TimeSpan ShareStopSettle = TimeSpan.FromMilliseconds(600); // internal: tests wait less
    /// <summary>The wait itself; a test holds it open until it has delivered Discord's late "ended" event.</summary>
    internal Func<Task>? ShareStopWait;
    internal int ShareAppsCacheMs = 10_000; // internal: tests make it old

    // Windows that are not apps you would share (the shell, Discord itself, PhoneBox).
    private static readonly HashSet<string> ShareSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "explorer", "textinputhost", "shellexperiencehost", "searchhost",
        "startmenuexperiencehost", "lockapp", "applicationframehost", "systemsettings", "phoneboxagent",
    };

    // guarded by _lock
    private bool? _sharing; // null: Discord has not said yet
    private string? _shareName, _sharePicked; // what Discord says is shared; the listed app the shared pid belongs to
    private int? _sharePid;
    private List<DiscordShareApp> _shareApps = []; // last listed for the phone: the only ones it may share
    private long _shareAppsAt;

    private volatile bool _shareSubscribed; // only touched by the connection's own loop and, to false, by a timed-out action
    private volatile bool _shareRefused;   // Discord refused the subscription: off for this connection
    // Beside _act: a share that takes its time to start holds up only other share actions, not mute or push to talk.
    private readonly SemaphoreSlim _shareAct = new(1, 1);
    internal Func<List<DiscordShareApp>> ListApps = () => RunningApps(); // internal: tests list their own

    /// <summary>
    /// The phone may see and do this: the setting is on, and a Discord widget has control on, on an enabled page.
    /// <see cref="DiscordConfig.AllowControl"/> is not enough: it is for the voice actions only.
    /// </summary>
    internal static bool ShareWanted(AgentConfig cfg) => cfg.Discord is { Enabled: true, ScreenShare: true } && WidgetControl(cfg);

    /// <summary>
    /// Whether you share, for the current-channel bar's Live badge and Stop button: the setting is on and the Discord
    /// widget or the bar has Control on. The app list and starting a share still need <see cref="ShareWanted"/>.
    /// </summary>
    internal static bool ShareStateWanted(AgentConfig cfg) => cfg.Discord is { Enabled: true, ScreenShare: true } && (WidgetControl(cfg) || BarControl(cfg));

    private bool ShareGranted => !ShareScopes.Except(_granted).Any();
    private bool ShareOn => !_shareRefused && ShareGranted && config.Get().Discord is { Enabled: true, ScreenShare: true };

    private void ClearShare()
    {
        lock (_lock) { _sharing = null; _shareName = _sharePicked = null; _sharePid = null; _shareApps = []; _shareAppsAt = 0; }
    }

    /// <summary>Follows (or stops following) the share state with the setting. Discord refusing it keeps screen share off for this connection, nothing else.</summary>
    internal async Task FollowShare(CancellationToken ct)
    {
        var on = ShareOn;
        if (on == _shareSubscribed) return;
        try
        {
            _shareSubscribed = on;
            if (!on) ClearShare();
            await Request(on ? "SUBSCRIBE" : "UNSUBSCRIBE", new JsonObject(), "SCREENSHARE_STATE_UPDATE", ct);
        }
        catch (TimeoutException) when (on) { _shareSubscribed = false; } // asked again next pass
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException))
        {
            if (!on) return; // still sent, but no longer listened to
            log.LogInformation("discord: the screen share state can't be followed ({Msg}); screen share stays off until Discord reconnects", ex.Message);
            _shareRefused = true;
            _shareSubscribed = false;
            ClearShare();
        }
    }

    internal void OnShareState(JsonNode? data)
    {
        if (!ShareOn || data is not JsonObject d || d["active"] is not JsonValue v || !v.TryGetValue<bool>(out var active)) return;
        lock (_lock)
        {
            _sharing = active;
            _shareName = active ? JsonStr((d["application"] as JsonObject)?["name"]) : null;
            var pid = active ? Pid(d["pid"]) : null;
            if (pid != _sharePid) _sharePicked = null; // another app: the name of the one before it is not its name
            _sharePid = pid;
            // Discord leaves the application empty for a share started by process (seen with a real one): the pid then
            // tells which listed app it is. The name is kept, as the app may leave the list (it is listed again later).
            if (active && _shareName is null) _sharePicked = _shareApps.FirstOrDefault(a => a.Pid == _sharePid)?.Name ?? _sharePicked;
        }
    }

    /// <summary>A process id, as a number or as text (Discord's share events are not documented).</summary>
    private static int? Pid(JsonNode? n) =>
        n is not JsonValue v ? null : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out i) ? i : null;

    /// <summary>Whether you are sharing, as Discord last said; null before it has, and between a stop and Discord's next word.</summary>
    internal bool? Sharing { get { lock (_lock) return _sharing; } }

    /// <summary>The apps with a window on this computer, by name only (no window titles), one per process. Windows only.</summary>
    internal static List<DiscordShareApp> RunningApps(int max = ShareAppsMax)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var self = Environment.ProcessId;
        var apps = new List<DiscordShareApp>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.SessionId == 0 || ShareSkip.Contains(p.ProcessName) || p.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string? name = null;
                    try { name = p.MainModule?.FileVersionInfo.FileDescription; } catch { } // elevated or protected: the process name will do
                    apps.Add(new(p.Id, string.IsNullOrWhiteSpace(name) ? p.ProcessName : name.Trim()));
                }
                catch { } // exited meanwhile
            }
        }
        // alphabetical, not by window order: the list must not reshuffle while you tap it
        return [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Pid).Take(max)];
    }

    private bool ShareAppsOld() => _shareAppsAt == 0 || Environment.TickCount64 - _shareAppsAt > ShareAppsCacheMs; // under _lock

    /// <summary>
    /// Whether the phone may share this process: it is in the list the phone was shown. An old list is read again first,
    /// since a pid may belong to another program by now: it has to be the app of that name still.
    /// </summary>
    private bool ShareListed(int? pid)
    {
        string? name;
        bool old;
        lock (_lock) { name = _shareApps.FirstOrDefault(a => a.Pid == pid)?.Name; old = ShareAppsOld(); }
        if (name is null || !old) return name is not null;
        var list = ListApps();
        lock (_lock)
        {
            if (!ShareOn || !ShareWanted(config.Get())) return false; // turned off meanwhile: not kept
            _shareApps = list;
            _shareAppsAt = Environment.TickCount64;
            return list.Any(a => a.Pid == pid && a.Name == name);
        }
    }

    /// <summary>What the phone's share view shows. Apps are listed only when <paramref name="apps"/> is asked (the view is open), at most every 10 s.</summary>
    public object Share(bool apps)
    {
        if (!ShareStateWanted(config.Get())) return new { enabled = false };
        apps = apps && ShareWanted(config.Get()); // the bar alone gets the state, never the apps
        var s = _snap;
        if (!s.Connected || !ShareOn) return new { enabled = true, ready = false, refused = _shareRefused, error = s.Error };
        bool old;
        lock (_lock) old = ShareAppsOld();
        var list = apps && old ? ListApps() : null;
        lock (_lock)
        {
            // turned off (or disconnected) while the apps were read: their names are neither kept nor sent
            if (!ShareStateWanted(config.Get())) return new { enabled = false };
            // asked again after the apps were read: a Discord widget's Control turned off meanwhile (the bar's still on)
            // must not get this list, nor keep it
            if (apps && !ShareWanted(config.Get())) { apps = false; list = null; }
            if (!ShareOn || !_snap.Connected) return new { enabled = true, ready = false, refused = _shareRefused, error = _snap.Error };
            if (list is not null) { _shareApps = list; _shareAppsAt = Environment.TickCount64; }
            if (_sharing == true && _shareName is null) _sharePicked = _shareApps.FirstOrDefault(a => a.Pid == _sharePid)?.Name ?? _sharePicked;
            return new { enabled = true, ready = true, inCall = s.ChannelId is not null, active = _sharing,
                // the name of what is shared goes with the share view only, like the app list (PRIVACY.md); a state-only
                // answer (the Live badge during a call) says only whether you share
                name = apps ? _shareName ?? _sharePicked : null, apps = apps ? _shareApps.ToList() : null };
        }
    }

    /// <summary>
    /// "app" shares a listed app (<paramref name="pid"/>), "pick" asks Discord for its picker on the computer, "stop"
    /// ends the share. It needs a widget's control (see <see cref="ShareWanted"/>), screen share on and, to start, a call.
    /// </summary>
    public async Task<DiscordShareError?> ShareActAsync(string action, int? pid)
    {
        if (Refusal() is { } refusal) return refusal;
        // one at a time, and no queue behind a share that is starting: a press that waited could mean the opposite by then
        if (!await _shareAct.WaitAsync(0)) return new("busy", "another screen share action is running");
        try
        {
            var listed = action == "app" && ShareListed(pid);
            if (Refusal() is { } late) return late;
            var s = _snap;
            if (!s.Connected) return new("failed", "Discord is not connected");
            if (action is not ("stop" or "pick" or "app")) return new("failed", "unknown action");
            // The same command starts and stops, so it is only sent when Discord has said which of the two it would do.
            if (Sharing is not { } sharing) return new("unknown", "Discord has not said yet whether you are sharing");
            if (action == "stop")
            {
                if (!sharing) return null;
                // The same command starts a share, and Discord reports a share ended on the computer about a third of a
                // second later. Wait a moment and ask again, so a Stop pressed in that gap doesn't open the picker.
                await (ShareStopWait?.Invoke() ?? Task.Delay(ShareStopSettle));
                if (Sharing != true) return null;
            }
            else
            {
                if (s.ChannelId is null) return new("noCall", "join a voice channel first");
                if (sharing) return new("sharing", "you are already sharing");
                if (action == "app" && !listed) return new("gone", "this app is not in the list");
            }
            try
            {
                // checked again once the pipe is free: a share started or stopped on the computer meanwhile would be undone
                await Request("TOGGLE_SCREENSHARE", action == "app" ? new JsonObject { ["pid"] = pid } : new JsonObject(), null, CancellationToken.None,
                    stillValid: () => Refusal() is null && Sharing == sharing);
            }
            catch (StaleRequestException)
            {
                if (Refusal() is { } refused) return refused; // control or screen share turned off meanwhile
                return Sharing switch
                {
                    false when action == "stop" => null, // it has ended, as asked
                    true => new("sharing", "you are already sharing"),
                    _ => new("unknown", "Discord has not said yet whether you are sharing"),
                };
            }
            catch (TimeoutException)
            {
                // it may or may not have been done: ask Discord for the state again before anything else is sent
                lock (_lock) { _sharing = null; _shareName = _sharePicked = null; _sharePid = null; }
                _shareSubscribed = false;
                throw;
            }
            log.LogInformation("discord: share {Action}", action);
            // Until Discord's next state event (about a third of a second later) a second stop would start the share
            // again. Not after "pick": a cancelled picker sends no event.
            if (action == "stop") lock (_lock) if (_sharing == true) _sharing = null;
            // Discord draws its picker inside its own window: bring that window up, or the picker opens unseen behind
            // other windows (or with Discord in the tray)
            if (action == "pick") _ = Task.Run(ShowDiscordAsync);
            if (action != "app") return null;
            // Discord starts a direct share only for what it can capture that way (games it detects); for anything else it
            // quietly does nothing. Watch for the share to start rather than guessing beforehand.
            var until = Environment.TickCount64 + (long)ShareStartWait.TotalMilliseconds;
            while (Sharing == false && Environment.TickCount64 < until) await Task.Delay(100);
            return Sharing == true ? null : new("notDirect", "Discord can't share this app directly: use \"Choose on PC\"");
        }
        catch (Exception ex) { return new("failed", ex.Message); }
        finally { _shareAct.Release(); }

        async Task ShowDiscordAsync()
        {
#if WINDOWS
            try
            {
                // the build this agent talks to first: that is where Discord opened the picker
                var connected = _ipcProcess;
                foreach (var (process, folder) in BuildsFor(connected))
                {
                    var running = Process.GetProcessesByName(process);
                    var any = running.Length > 0;
                    foreach (var p in running) p.Dispose();
                    if (!any) continue;
                    if (WindowsDeckNative.Focus(process)) return;
                    // In the tray it has no window to raise: starting it again from its own Update.exe shows the running
                    // one (Discord stays single-instance). A fixed path; nothing from the request.
                    var update = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder, "Update.exe");
                    if (!File.Exists(update)) return;
                    var start = new ProcessStartInfo(update) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("--processStart");
                    start.ArgumentList.Add(process + ".exe");
                    // It hands over to the running Discord and exits at once. One that hangs is ended after 15 s, or
                    // when the agent stops, so it is never left behind.
                    using var updater = Process.Start(start);
                    if (updater is null) return;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_life);
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    try { await updater.WaitForExitAsync(wait.Token); }
                    catch (OperationCanceledException) { try { updater.Kill(); } catch { } } // exited meanwhile
                    return;
                }
            }
            catch (Exception ex) { log.LogInformation("discord: could not bring Discord forward ({Msg})", ex.Message); }
#endif
        }

        DiscordShareError? Refusal()
        {
            var cfg = config.Get();
            // the current-channel bar may only stop a share; starting one needs the Discord widget's own Control
            if (!WidgetControl(cfg) && !(action == "stop" && BarControl(cfg))) return new("control", "controlling Discord is turned off in the widget's settings");
            if (!cfg.Discord.ScreenShare) return new("off", "screen share is turned off");
            return ShareOn ? null : new("notAllowed", "Discord has not allowed PhoneBox to share the screen");
        }
    }
}
