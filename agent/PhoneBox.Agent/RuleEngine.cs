using System.Diagnostics;
#if WINDOWS
using System.Runtime.InteropServices;
#endif
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Automation rules (Settings › Rules): checks every rule once a second and, when its condition starts to hold, switches
/// the phone to a page, shows a notification or pushes a value. Rules marked "while active" undo their effect when the
/// condition ends: the phone goes back to the page it was on and a pushed value is removed again.
/// </summary>
public sealed class RuleEngine(ConfigStore config, PushStore push, SessionMonitor sessions, MediaMonitor media, SystemMonitor system,
    PrivacyMonitor privacy, CalendarMonitor calendar, ObsMonitor obs, QuotaMonitor quotas, GitHubMonitor github, BatteryMonitor batteries,
    PingMonitor ping, AudioControl audio, ILogger<RuleEngine> log) : BackgroundService
{
    /// <summary>CPU spikes are normal; the CPU rule waits this long unless the rule sets its own "for" time.</summary>
    private const int CpuSustainSec = 15;
    /// <summary>A condition has to stay off this long before a rule ends, so a flickering condition does not flap.</summary>
    public const int ReleaseSec = 4;

    private sealed class RuleState(RuleConfig rule)
    {
        public RuleConfig Rule { get; } = rule;
        public bool Active;
        public long TrueSince;
        public long FalseSince;
    }

    // keyed by the rule's serialized form, so editing a rule ends the old version and starts the new one
    private readonly Dictionary<string, RuleState> _states = [];
    // last time each rule (by id) fired, for the cooldown; survives edits of the rule
    private readonly Dictionary<string, long> _fired = [];
    private volatile string[] _active = [];
    private long _pausedUntil; // unix ms
    private HashSet<string>? _procs;
    private long _procsAt;
    private double _idle;
    private long _idleAt;

    /// <summary>Ids of the rules whose condition holds right now (the phone keeps a rule's page only while it is listed).</summary>
    public IReadOnlyList<string> Active() => _active;

    /// <summary>Unix ms until which rules are paused (from the phone or the settings), or null.</summary>
    public long? PausedUntil() => Interlocked.Read(ref _pausedUntil) is var t && t > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? t : null;

    /// <summary>Pauses all rules for a while (0 resumes). Rules that hold end now and fire again after the pause.</summary>
    public void Pause(int minutes)
    {
        Interlocked.Exchange(ref _pausedUntil, minutes <= 0 ? 0 : DateTimeOffset.UtcNow.AddMinutes(Math.Min(minutes, 24 * 60)).ToUnixTimeMilliseconds());
        if (minutes <= 0) log.LogInformation("rules resumed");
        else log.LogInformation("rules paused for {Min} min", minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct); // let the monitors take their first readings
        while (!ct.IsCancellationRequested)
        {
            try { Evaluate(); }
            catch (Exception ex) { log.LogWarning("rules: {Msg}", ex.Message); }
            await Task.Delay(1000, ct);
        }
    }

    internal void Evaluate() // internal: the tests drive it directly
    {
        var rules = config.Get().Rules.Where(r => r.Enabled).ToList();
        var keys = rules.ToDictionary(r => JsonSerializer.Serialize(r), r => r);
        var paused = PausedUntil() is not null;

        foreach (var (key, st) in _states.Where(kv => paused || !keys.ContainsKey(kv.Key)).ToList())
        {
            if (st.Active && st.Rule.WhileActive)
            {
                Undo(st.Rule);
                // the edit or the pause took the effect back, not the condition: the new version (or the rule after the
                // pause) may bring it back at once instead of waiting out the rest time
                _fired.Remove(st.Rule.Id);
            }
            _states.Remove(key);
        }
        if (paused) { _active = []; return; }

        var now = Environment.TickCount64;
        var clock = DateTime.Now;
        var active = new List<string>();
        foreach (var (key, rule) in keys)
        {
            if (!_states.TryGetValue(key, out var st)) _states[key] = st = new RuleState(rule);
            var (holds, detail) = Allowed(rule, clock) ? Check(rule) : (false, "");
            if (holds) { st.FalseSince = 0; if (st.TrueSince == 0) st.TrueSince = now; }
            else { st.TrueSince = 0; if (st.FalseSince == 0) st.FalseSince = now; }

            var sustainSec = rule.ForSec > 0 ? rule.ForSec : rule.When == "cpu-above" ? CpuSustainSec : 0;
            var sustained = holds && now - st.TrueSince >= sustainSec * 1000L;
            var cooled = !_fired.TryGetValue(rule.Id, out var last) || now - last >= rule.CooldownMin * 60_000L;

            if (sustained && !st.Active && cooled)
            {
                st.Active = true;
                _fired[rule.Id] = now;
                log.LogInformation("rule {Id} fired ({Detail})", rule.Id, detail);
                Do(rule, detail);
            }
            else if (!holds && st.Active && (now - st.FalseSince >= ReleaseSec * 1000L || rule.When == "time"))
            {
                st.Active = false;
                log.LogInformation("rule {Id} ended", rule.Id);
                if (rule.WhileActive) Undo(rule);
            }
            if (st.Active) active.Add(rule.Id);
        }
        _active = [.. active];
    }

    /// <summary>The rule's extra conditions: only on some days, only at some hours.</summary>
    public static bool Allowed(RuleConfig r, DateTime now) =>
        OnDay(r.Days, now.DayOfWeek) && (string.IsNullOrWhiteSpace(r.During) || InWindow(r.During, now.TimeOfDay));

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] WeekOrder = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>"mon,tue,wed" (empty = every day).</summary>
    public static bool OnDay(string? days, DayOfWeek day) =>
        string.IsNullOrWhiteSpace(days) || days.Split(',', StringSplitOptions.TrimEntries).Contains(DayNames[(int)day], StringComparer.OrdinalIgnoreCase);

    /// <summary>Cleans a day list: known day names in week order; null means every day.</summary>
    public static string? NormalizeDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days)) return null;
        var set = days.ToLowerInvariant().Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (set.Remove("weekdays")) set.UnionWith(["mon", "tue", "wed", "thu", "fri"]);
        if (set.Remove("weekends")) set.UnionWith(["sat", "sun"]);
        var list = WeekOrder.Where(set.Contains).ToList();
        return list.Count is 0 or 7 ? null : string.Join(',', list);
    }

    /// <summary>Runs a rule's action once, regardless of its condition (the Test button in the settings).</summary>
    public void Test(RuleConfig rule) => Do(rule, "test");

    private void Do(RuleConfig r, string detail)
    {
        switch (r.Then)
        {
            case "goto" when !string.IsNullOrWhiteSpace(r.Page):
                push.Emit("goto", JsonSerializer.SerializeToElement(new
                {
                    page = r.Page, rule = r.Id, hold = r.WhileActive, name = r.Name.Length > 0 ? r.Name : null,
                    detail, priority = r.Priority, countdown = r.CountdownSec,
                }));
                break;
            case "notify":
                var title = r.Name.Length > 0 ? r.Name : "PhoneBox";
                var text = string.IsNullOrWhiteSpace(r.Text) ? detail : r.Text.Replace("{detail}", detail);
                push.Emit("notify", JsonSerializer.SerializeToElement(new { title, text, level = r.Level ?? "info", wake = r.Level is "warn" or "error" }));
                break;
            case "push" when !string.IsNullOrWhiteSpace(r.Key) && PushStore.ValidKey(r.Key):
                var value = (r.PushValue ?? "true").Replace("{detail}", detail);
                push.Set(r.Key, PushStore.ParseValue(value), null);
                break;
            case "shortcut" when !string.IsNullOrWhiteSpace(r.Shortcut):
                var cfgNow = config.Get();
                if (r.WhileActive && cfgNow.Shortcuts.FirstOrDefault(s => s.Id == r.Shortcut) is { } target
                    && (target.Toggle || target.Cmd == "builtin:mute") && Shortcuts.State(target, push, audio) == true)
                    break;
                if (Shortcuts.Run(cfgNow, r.Shortcut, log, push, audio) is { } error) log.LogWarning("rule {Id}: {Error}", r.Id, error);
                break;
        }
    }

    private void Undo(RuleConfig r)
    {
        if (r.Then == "goto") push.Emit("back", JsonSerializer.SerializeToElement(new { rule = r.Id }));
        else if (r.Then == "push" && !string.IsNullOrWhiteSpace(r.Key)) push.Remove(r.Key);
        else if (r.Then == "shortcut")
        {
            // one settings snapshot for the lookup and the run: a save in between must not run another shortcut
            var cfgNow = config.Get();
            if (cfgNow.Shortcuts.FirstOrDefault(s => s.Id == r.Shortcut) is not { } sc
                || !(sc.Toggle || sc.Cmd == "builtin:mute") || Shortcuts.State(sc, push, audio) == false) return;
            // another "while active" rule still holds the same toggle on (Do skipped it because it was on already):
            // only the last one to end switches it off
            if (_states.Values.Any(o => !ReferenceEquals(o.Rule, r) && o.Active && o.Rule.WhileActive
                    && o.Rule.Then == "shortcut" && o.Rule.Shortcut == r.Shortcut)) return;
            if (Shortcuts.Run(cfgNow, sc.Id, log, push, audio) is { } error) // a toggle switches back off
                log.LogWarning("rule {Id}: could not switch {Shortcut} back off: {Error}", r.Id, sc.Id, error);
        }
    }

    private (bool Holds, string Detail) Check(RuleConfig r)
    {
        var match = r.Match?.Trim() ?? "";
        var names = match.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (r.When)
        {
            case "session-waiting":
            case "session-busy":
            {
                var status = r.When == "session-waiting" ? "waiting" : "busy";
                var s = sessions.Snapshot().FirstOrDefault(s => s.Status == status && (names.Length == 0
                    || names.Any(n => s.Project.Contains(n, StringComparison.OrdinalIgnoreCase) || s.Tool.Equals(n, StringComparison.OrdinalIgnoreCase))));
                return s is null ? (false, "") : (true, status == "waiting" ? $"{s.Project}: {s.WaitingFor ?? "needs you"}" : $"{s.Project} is working");
            }
            case "media-playing":
            {
                var m = media.Snapshot();
                var ok = m is { Playing: true } && (names.Length == 0 || names.Any(n => m.App.Contains(n, StringComparison.OrdinalIgnoreCase)));
                return ok ? (true, string.IsNullOrEmpty(m!.Artist) ? m.Title : $"{m.Title} · {m.Artist}") : (false, "");
            }
            case "process-running":
            {
                var procs = Processes();
                var hit = names.Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                    .FirstOrDefault(n => procs.Contains(n) || procs.Any(p => p.Length >= 15 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
                return hit is null ? (false, "") : (true, hit);
            }
            case "app-in-front":
            {
                var front = FrontApp();
                var hit = front is not null && names.Any(n => front.Contains(n, StringComparison.OrdinalIgnoreCase));
                return hit ? (true, front!) : (false, "");
            }
            case "mic-in-use":
            case "camera-in-use":
            {
                var p = privacy.Snapshot();
                var apps = (r.When == "mic-in-use" ? p?.MicApps : p?.CameraApps) ?? [];
                var hit = apps.FirstOrDefault(a => names.Length == 0 || names.Any(n => a.Contains(n, StringComparison.OrdinalIgnoreCase)));
                return hit is null ? (false, "") : (true, hit);
            }
            case "obs-live":
            {
                var o = obs.Snapshot();
                return obs.Live ? (true, o.Streaming ? "OBS is streaming" : "OBS is recording") : (false, "");
            }
            case "calendar-event":
            {
                var ev = calendar.Current(names);
                return ev is null ? (false, "") : (true, ev.Title);
            }
            case "cpu-above":
            {
                var cpu = system.Snapshot()?.Cpu;
                return cpu >= (r.Value ?? 85) ? (true, $"CPU {cpu:0}%") : (false, "");
            }
            case "idle":
            {
                var minutes = r.Value ?? 5;
                return IdleSeconds() >= minutes * 60 ? (true, $"away for {minutes:0} min") : (false, "");
            }
            case "time":
                return InWindow(match, DateTime.Now.TimeOfDay) ? (true, match) : (false, "");
            case "quota-above":
            {
                var limit = r.Value ?? 80;
                foreach (var q in quotas.Snapshot())
                {
                    if (names.Length > 0 && !names.Any(n => q.Id.Contains(n, StringComparison.OrdinalIgnoreCase) || q.Name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                    var hit = q.Limits.Where(l => l.Percent >= limit).OrderByDescending(l => l.Percent).FirstOrDefault();
                    if (hit is not null) return (true, $"{q.Name} {hit.Label} {hit.Percent:0}%");
                }
                return (false, "");
            }
            case "review-requested":
            {
                var matching = (github.Snapshot()?.ReviewRequests ?? [])
                    .Where(p => !p.Draft && (names.Length == 0 || names.Any(n => p.Repo.Contains(n, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                return matching.Count == 0 ? (false, "") : (true, matching.Count > 1 ? $"{matching.Count} reviews waiting" : $"{matching[0].Repo}#{matching[0].Number}");
            }
            case "ci-failed":
            {
                var pr = (github.Snapshot()?.MyPrs ?? []).FirstOrDefault(p => p.Ci?.ToUpperInvariant() is "FAILURE" or "ERROR"
                    && (names.Length == 0 || names.Any(n => p.Repo.Contains(n, StringComparison.OrdinalIgnoreCase))));
                return pr is null ? (false, "") : (true, $"CI failed: {pr.Repo}#{pr.Number}");
            }
            case "battery-low":
            {
                var limit = r.Value ?? 20;
                var b = batteries.Snapshot().FirstOrDefault(d => d.Level <= limit && d.Charging != true
                    && (names.Length == 0 || names.Any(n => d.Name.Contains(n, StringComparison.OrdinalIgnoreCase))));
                return b is null ? (false, "") : (true, $"{b.Name} {b.Level}%");
            }
            case "ping-high":
            {
                var p = ping.Snapshot();
                var limit = r.Value ?? 150;
                if (p is null) return (false, "");
                if (p.Ms is null || p.LossPct >= 50) return (true, $"{p.Host} unreachable");
                return p.Ms >= limit ? (true, $"{p.Host} {p.Ms:0} ms") : (false, "");
            }
            case "always":
                return (true, "");
            case "push":
            {
                if (r.Op == "stale")
                    return match.Length == 0 ? (false, "")
                        : Stale(match, push.Get(match), r.Arg, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), StartedAtMs);
                if (match.Length == 0 || !push.Snapshot().TryGetValue(match, out var item)) return (false, "");
                var d = item.Data;
                if (!string.IsNullOrEmpty(r.Op) && r.Op != "on") return Compare(d, r.Op, r.Arg) ? (true, Text(d)) : (false, "");
                // {"value": "off", "sub": …} is judged by its value, as Compare and a toggle shortcut's state read it
                var v = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("value", out var inner) ? inner : d;
                var truthy = v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Number => v.GetDouble() != 0,
                    // any case, like a toggle shortcut's state: "OFF" (MQTT), "False" (PowerShell's $false)
                    JsonValueKind.String => v.GetString() is { Length: > 0 } t && t.ToLowerInvariant() is not ("0" or "false" or "off" or "no"),
                    JsonValueKind.Object or JsonValueKind.Array => true,
                    _ => false,
                };
                return truthy ? (true, d.ValueKind == JsonValueKind.String ? d.GetString()! : d.ToString()) : (false, "");
            }
        }
        return (false, "");
    }

    private static string Text(JsonElement d) => d.ValueKind == JsonValueKind.String ? d.GetString()! : d.ToString();

    /// <summary>When the agent started (unix ms); the tests move it back to skip the start-up grace.</summary>
    internal long StartedAtMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// "Has not been updated for N seconds": holds when the value is older than that, or when it is not there at all
    /// (never pushed, deleted, or expired by its ttl). The time counts from the agent's start at the earliest, so a
    /// value kept from before a restart, or one whose sender has not had the chance to post yet, is not stale at once.
    /// </summary>
    internal static (bool Holds, string Detail) Stale(string key, PushItem? item, string? arg, long nowMs, long startedAtMs)
    {
        // a Match that cannot be a push key (a typo, a pasted URL with a token) never holds, so it never reaches the
        // log or the notification text through {detail}
        if (!PushStore.ValidKey(key)) return (false, "");
        if (!double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)
            || !double.IsFinite(sec) || sec <= 0)
            return (false, "");
        var since = Math.Max(item?.At ?? 0, startedAtMs);
        if (nowMs - since < sec * 1000) return (false, "");
        return (true, item is null ? $"{key}: no value" : $"{key}: no update for {Age(nowMs - item.At)}");
    }

    private static string Age(long ms) => ms switch
    {
        < 120_000 => $"{Math.Max(0, ms / 1000)} s",
        < 2 * 3_600_000 => $"{ms / 60_000} min",
        < 2 * 86_400_000L => $"{ms / 3_600_000} h",
        _ => $"{ms / 86_400_000L} days",
    };

    /// <summary>Compares a pushed value: above / at least / below / at most a number, or equals / differs from a text (case-insensitive).</summary>
    public static bool Compare(JsonElement d, string op, string? arg)
    {
        var text = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("value", out var inner) ? inner : d;
        double? num = text.ValueKind == JsonValueKind.Number ? text.GetDouble()
            : text.ValueKind == JsonValueKind.String && double.TryParse(text.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        var argNum = double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : (double?)null;
        var str = text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : text.ToString();
        return op switch
        {
            ">" => num is not null && argNum is not null && num > argNum,
            ">=" => num is not null && argNum is not null && num >= argNum,
            "<" => num is not null && argNum is not null && num < argNum,
            "<=" => num is not null && argNum is not null && num <= argNum,
            "=" => num is not null && argNum is not null ? num == argNum : string.Equals(str, arg ?? "", StringComparison.OrdinalIgnoreCase),
            "!=" => num is not null && argNum is not null ? num != argNum : !string.Equals(str, arg ?? "", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>"22:00-07:00" (may wrap past midnight) or a single "08:30" (that minute).</summary>
    public static bool InWindow(string window, TimeSpan now)
    {
        var parts = window.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length > 2 || !Clock(parts[0], out var from)) return false;
        var to = from + TimeSpan.FromMinutes(1);
        if (parts.Length == 2 && !Clock(parts[1], out to)) return false;
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }

    /// <summary>A time of day; "24:00" is the end of the day (TimeSpan.TryParse rejects it).</summary>
    private static bool Clock(string text, out TimeSpan time)
    {
        if (text is "24:00" or "24:00:00") { time = TimeSpan.FromDays(1); return true; }
        return TimeSpan.TryParse(text, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    private HashSet<string> Processes()
    {
        if (_procs is not null && Environment.TickCount64 - _procsAt < 3000) return _procs;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) { try { set.Add(p.ProcessName); } catch { } }
        _procsAt = Environment.TickCount64;
        return _procs = set;
    }

    private double IdleSeconds()
    {
        if (Environment.TickCount64 - _idleAt < 5000) return _idle;
        _idleAt = Environment.TickCount64;
        return _idle = Idle.Seconds();
    }

    /// <summary>For tests: a fake front-app reader instead of the OS probes.</summary>
    internal Func<string?>? FrontProbe;

    private string? _front;
    private long _frontAt;

    /// <summary>The name of the app that owns the foreground window, or null when it cannot be told.</summary>
    private string? FrontApp()
    {
        if (FrontProbe is not null) return FrontProbe();
        if (_frontAt != 0 && Environment.TickCount64 - _frontAt < 1500) return _front;
        // timed from the end of the probe: osascript or xdotool can take up to 2 s, and every app-in-front rule in
        // the same pass must reuse that answer instead of starting its own probe
        _front = FrontAppOs();
        _frontAt = Environment.TickCount64;
        return _front;
    }

#if WINDOWS
    private static string? FrontAppOs()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return null; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
#else
    private static string? FrontAppOs()
    {
        // macOS: System Events, same Accessibility permission as sending keys. Linux: xdotool on X11 only;
        // under Wayland there is no desktop-wide frontmost-app query, so the rule simply stays off.
        if (OperatingSystem.IsMacOS())
            return Sh.Run("osascript", "-e", "tell application \"System Events\" to get name of first process whose frontmost is true")?.Trim() is { Length: > 0 } app ? app : null;
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
            return Sh.Run("xdotool", "getactivewindow", "getwindowclassname")?.Trim() is { Length: > 0 } cls ? cls : null;
        return null;
    }
#endif
}
