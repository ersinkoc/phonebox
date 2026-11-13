using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed class AgentConfig
{
    public CodexBarConfig CodexBar { get; set; } = new();
    public ControlDeckConfig Controls { get; set; } = new();
    public string? DeviceSerial { get; set; }
    public string? AdbPath { get; set; }
    public WeatherConfig Weather { get; set; } = new();
    public NightConfig Night { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public LanConfig Lan { get; set; } = new();
    public UpdateConfig Updates { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();
    public ClaudeConfig Claude { get; set; } = new();
    public List<PageConfig> Pages { get; set; } = [];
    public List<Shortcut> Shortcuts { get; set; } = [];
    public List<RuleConfig> Rules { get; set; } = [];
    public ChargeConfig Charge { get; set; } = new();
    /// <summary>Per-phone settings when several phones use this agent: a name and which pages each one shows.</summary>
    public List<PhoneConfig> Phones { get; set; } = [];
    /// <summary>iCalendar feeds for the Agenda widget; the URLs are secrets and never go to the phone.</summary>
    public List<CalendarSource> Calendars { get; set; } = [];
    /// <summary>Home Assistant address and long-lived access token (the token never goes to the phone).</summary>
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    /// <summary>OBS Studio's WebSocket server (obs-websocket 5).</summary>
    public ObsConfig Obs { get; set; } = new();

    /// <summary>The pages a phone shows: its own selection (Settings › Phones), or all of them.</summary>
    public IEnumerable<PageConfig> PagesFor(string? deviceId)
    {
        var ids = Phones.FirstOrDefault(p => p.Id == deviceId)?.Pages;
        if (ids is not { Count: > 0 }) return Pages;
        var chosen = Pages.Where(p => ids.Contains(p.Id)).ToList();
        return chosen.Count > 0 ? chosen : Pages;
    }

    /// <summary>The part sent to the phone: commands (shortcut cmd/args/cwd, command widget scripts) are never included.</summary>
    public object ToPublic(string? deviceId = null) => new
    {
        weather = Weather,
        codexBar = CodexBar,
        night = Night,
        display = Display,
        pages = PagesFor(deviceId).Select(p => new
        {
            p.Id, p.Title, p.Icon, p.Enabled, p.IdleSec, p.Appearance,
            widgets = (p.Widgets ?? []).Select(w => new
            {
                w.Id, w.Type, w.X, w.Y, w.W, w.H, w.Appearance,
                // command widgets: only what the panel shows; the scripts (command, commandUnix, …) stay on this computer
                options = StackWidgets.PublicOptions(w),
            }),
        }),
        shortcuts = Shortcuts.Select(s => new { s.Id, s.Label, s.Icon, s.Color, toggle = s.Toggle || s.Cmd == "builtin:mute" }),
        // names only (for the Rules widget); what a rule matches on stays here
        rules = Rules.Select(r => new { r.Id, r.Name, r.Enabled, r.When }),
        charge = new { Charge.Enabled, Charge.Min, Charge.Max },
    };

    /// <summary>Ending processes is allowed when an enabled page has a processes widget that allows it.</summary>
    public IEnumerable<WidgetConfig> Widgets(bool enabledPagesOnly = false) => Pages.Where(p => !enabledPagesOnly || p.Enabled)
        .SelectMany(p => p.Widgets ?? []).SelectMany(w => new[] { w }.Concat(StackWidgets.Children(w).Where(c => c.Enabled)
            .Select(c => new WidgetConfig { Type = c.Type, Options = c.Options })));

    public bool AllowKill() => Widgets(enabledPagesOnly: true)
        .Any(w => w.Type == "processes" && w.Bool("allowKill") != false);
}

public sealed class WeatherConfig
{
    public string City { get; set; } = "Tallinn";
    public double Lat { get; set; } = 59.437;
    public double Lon { get; set; } = 24.745;
}

public sealed class NightConfig
{
    public bool Enabled { get; set; } = true;
    public string Start { get; set; } = "00:30";
    public string End { get; set; } = "07:30";
    public double Brightness { get; set; } = 0.02;
}

public sealed class DisplayConfig
{
    public AppearanceConfig? Appearance { get; set; }
    public bool ShowSeconds { get; set; } = true;
    /// <summary>Return to the first page after this many idle seconds; 0 = never.</summary>
    public int IdleReturnSec { get; set; } = 90;
    /// <summary>Vibrate when a session finishes or needs input.</summary>
    public bool Vibrate { get; set; } = true;
    /// <summary>The phone's volume keys switch pages.</summary>
    public bool VolumeKeys { get; set; } = true;
    /// <summary>landscape, portrait or auto (follow the phone). In portrait the widgets are arranged automatically.</summary>
    public string Orientation { get; set; } = "landscape";
    /// <summary>default, amoled, midnight, warm or graphite.</summary>
    public string Theme { get; set; } = "default";
    /// <summary>Accent colour (background glow, page dots), e.g. "#2dd4bf".</summary>
    public string Accent { get; set; } = "#2dd4bf";
    /// <summary>Tint the background with the album art's colour while music plays.</summary>
    public bool ArtTint { get; set; } = true;
    /// <summary>Screen brightness follows the room (the phone's light sensor).</summary>
    public bool AutoBrightness { get; set; }
    /// <summary>Show the night screen whenever the room is dark, not only at night.</summary>
    public bool NightWhenDark { get; set; }
    /// <summary>A hand near the phone (proximity sensor) wakes the night screen.</summary>
    public bool ProximityWake { get; set; } = true;
    /// <summary>Screen pinning: Home and Recents stay on the panel.</summary>
    public bool Pin { get; set; }
    /// <summary>The panel is the phone's home app: it starts after a reboot and Home returns to it.</summary>
    public bool HomeScreen { get; set; }

    /// <summary>Over USB, turn off Android's screen saver (it starts on the dock / charger) and keep the phone awake while it is plugged in.</summary>
    public bool KeepAwake { get; set; } = true;
    /// <summary>On macOS, sleep the connected phone when the computer locks; wake it and open PhoneBox after unlock.</summary>
    public bool FollowComputerLock { get; set; }
    /// <summary>The page the panel returns to (idle return, Back); null = the first page.</summary>
    public string? HomePage { get; set; }
    /// <summary>Seconds the panel counts down ("Switching to … in 3") before a rule or the idle return changes the page; 0 = at once.</summary>
    public int SwitchCountdownSec { get; set; } = 3;
    /// <summary>Rules wait until the panel has not been touched for this many seconds, so they never pull a page away from under your finger.</summary>
    public int TouchGraceSec { get; set; } = 20;
    /// <summary>At least this many seconds between two automatic page switches, so rules firing one after another do not bounce the screen.</summary>
    public int MinDwellSec { get; set; } = 15;
    /// <summary>A rule keeps its page at most this many minutes, then the idle return may take over; 0 = as long as the rule lasts.</summary>
    public int HoldMaxMin { get; set; } = 30;
}

public sealed class LanConfig
{
    /// <summary>Also accept the phone over Wi-Fi (HTTPS + token, paired over USB). Requires an agent restart.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8766;
    /// <summary>Shared secret handed to the phone over USB. Empty = generate a new one (forces re-pairing).</summary>
    public string? Token { get; set; }
    /// <summary>Separate key for the webhook API from other machines (push/notify/goto/refresh only). May appear in URLs.</summary>
    public string? WebhookToken { get; set; }
}

public sealed class UpdateConfig
{
    /// <summary>Install new GitHub releases automatically (phone app over USB, agent in place).</summary>
    public bool Auto { get; set; } = true;
    public string Repo { get; set; } = "ersinkoc/phonebox";
}

public sealed class PluginsConfig
{
    /// <summary>Central marketplace repository (GitHub owner/repo serving index.json).</summary>
    public string Repo { get; set; } = "ersinkoc/phonebox-plugins";
}

public sealed class ClaudeConfig
{
    /// <summary>Show Claude Code permission prompts on the phone (needs the hook, see Settings › General).</summary>
    public bool PhoneApprovals { get; set; } = true;
    /// <summary>How long the phone may take before Claude Code asks in the terminal instead.</summary>
    public int TimeoutSec { get; set; } = 90;
    /// <summary>Only use the phone when the PC has been idle for a minute (you are away from the keyboard).</summary>
    public bool OnlyWhenAway { get; set; }
}

public sealed class PageConfig
{
    public AppearanceConfig? Appearance { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonConverter(typeof(IconJsonConverter))]
    public string Icon { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Return home after this many idle seconds on this page; null = the display setting, 0 = stay.</summary>
    public int? IdleSec { get; set; }
    /// <summary>Widgets on a 12 x 6 grid.</summary>
    public List<WidgetConfig>? Widgets { get; set; }

    /// <summary>Create the page from this template (templates.json) when it has no widgets yet.</summary>
    public string? Template { get; set; }
    /// <summary>Pre-0.6.2 fixed page type (home | ai | work | system | procs | deck); migrated to widgets on load.</summary>
    public string? Type { get; set; }
    /// <summary>Pre-0.6.2 page options; migrated to widget options on load.</summary>
    public Dictionary<string, JsonElement>? Options { get; set; }
}

public sealed class WidgetConfig
{
    public AppearanceConfig? Appearance { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public Dictionary<string, JsonElement>? Options { get; set; }

    public JsonElement? Opt(string key) => Options is not null && Options.TryGetValue(key, out var v) ? v : null;
    public string? Str(string key) => Opt(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public int? Int(string key) => Opt(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? (int)d : null;
    public bool? Bool(string key) => Opt(key) is { } v ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    public void Set(string key, object? value) => (Options ??= [])[key] = JsonSerializer.SerializeToElement(value);
    public WidgetConfig Clone() => new() { Id = Id, Type = Type, X = X, Y = Y, W = W, H = H, Appearance = Appearance?.Clone(), Options = Options?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
}

/// <summary>
/// An automation rule: when something happens on the PC, do something on the panel. With <see cref="WhileActive"/>
/// the effect lasts only as long as the condition (the phone goes back to its page, a pushed value is removed).
/// </summary>
public sealed class RuleConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>session-waiting, session-busy, media-playing, process-running, mic-in-use, camera-in-use, obs-live, calendar-event, cpu-above, idle, time, push.</summary>
    public string When { get; set; } = "session-waiting";
    /// <summary>App / process names ("zoom|Teams"), a time window ("22:00-07:00") or a push key, depending on <see cref="When"/>.</summary>
    public string? Match { get; set; }
    /// <summary>CPU percent, or idle minutes.</summary>
    public double? Value { get; set; }
    /// <summary>goto, notify or push.</summary>
    public string Then { get; set; } = "goto";
    public string? Page { get; set; }
    public string? Text { get; set; }
    public string? Level { get; set; }
    public string? Key { get; set; }
    public string? PushValue { get; set; }
    public bool WhileActive { get; set; } = true;
    /// <summary>Shortcut id for the "shortcut" action.</summary>
    public string? Shortcut { get; set; }

    /// <summary>0-9; a page switch of a higher rule wins and is not replaced by a lower one while it lasts.</summary>
    public int Priority { get; set; } = 5;
    /// <summary>The condition has to hold this many seconds before the rule acts (0 = at once; the CPU rule defaults to 15).</summary>
    public int ForSec { get; set; }
    /// <summary>After acting, the rule waits this many minutes before it may act again.</summary>
    public int CooldownMin { get; set; }
    /// <summary>Only on these days ("mon,tue,wed,thu,fri"); null = every day.</summary>
    public string? Days { get; set; }
    /// <summary>Only in this time window ("09:00-18:00"); null = all day.</summary>
    public string? During { get; set; }
    /// <summary>Seconds the phone counts down before switching pages; null = the phone's setting, 0 = at once.</summary>
    public int? CountdownSec { get; set; }
    /// <summary>Push trigger: compare the value instead of "is on" (&gt;, &lt;, =, !=) with <see cref="Arg"/>.</summary>
    public string? Op { get; set; }
    public string? Arg { get; set; }

    public static readonly string[] Triggers = ["session-waiting", "session-busy", "media-playing", "process-running", "mic-in-use", "camera-in-use", "obs-live", "calendar-event",
        "cpu-above", "idle", "time", "push", "quota-above", "review-requested", "ci-failed", "battery-low", "ping-high", "always"];
    public static readonly string[] Actions = ["goto", "notify", "push", "shortcut"];
}

/// <summary>Charge guard: keep the phone's battery between Min and Max by switching its power supply.</summary>
public sealed class ChargeConfig
{
    public bool Enabled { get; set; }
    public int Min { get; set; } = 50;
    public int Max { get; set; } = 80;
    /// <summary>"ha" (a Home Assistant switch entity) or "shortcuts" (one shortcut for power on, one for off).</summary>
    public string Method { get; set; } = "ha";
    public string? Entity { get; set; }
    public string? OnShortcut { get; set; }
    public string? OffShortcut { get; set; }
}

public sealed class ObsConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "ws://127.0.0.1:4455";
    public string? Password { get; set; }
}

public sealed class HomeAssistantConfig
{
    public string? Url { get; set; }
    public string? Token { get; set; }
}

public sealed class PhoneConfig
{
    /// <summary>The id the phone sends (X-PhoneBox-Device).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Page ids this phone shows, in the usual order; empty or null = all pages.</summary>
    public List<string>? Pages { get; set; }
}

public sealed class CalendarSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Color { get; set; }
}

public sealed class Shortcut
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    [JsonConverter(typeof(IconJsonConverter))]
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#6b7280";
    /// <summary>A program, address or file; or builtin:lock / monitor-off / mute / keys / text / macro.</summary>
    public string Cmd { get; set; } = "";
    /// <summary>Program arguments; the key combination for builtin:keys ("ctrl+shift+m"); the text for builtin:text.</summary>
    public string? Args { get; set; }
    public string? Cwd { get; set; }
    /// <summary>builtin:macro steps, one per entry: "keys:ctrl+c", "text:hello", "wait:300", "shortcut:other-id".</summary>
    public List<string>? Steps { get; set; }
    /// <summary>An on / off key: the phone shows its state.</summary>
    public bool Toggle { get; set; }
    /// <summary>Where a toggle's state comes from: a pushed value (truthy = on). Empty: the agent remembers the last tap.</summary>
    public string? StateKey { get; set; }
    /// <summary>For toggles: the command (and arguments) that switch it off; empty = the same as switching on.</summary>
    public string? OffCmd { get; set; }
    public string? OffArgs { get; set; }
}

/// <summary>Reads/writes config.json; external edits are picked up on the next request.</summary>
public sealed partial class ConfigStore(string path)
{
    /// <summary>Pages a fresh install (or an empty page list) starts with.</summary>
    public static readonly string[] DefaultPages = ["home", "ai", "work", "system", "procs", "deck"];

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep emoji and non-ASCII text readable
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();
    private AgentConfig _config = Normalize(new AgentConfig());
    private DateTime _stamp = DateTime.MinValue;
    private DateTime _seen = DateTime.MinValue; // write time of the file as last looked at, readable or not

    public string Path => path;

    /// <summary>Version stamp (file write time) so the phone can notice changes.</summary>
    public long Version { get { Get(); return _seen.Ticks; } }

    public AgentConfig Get()
    {
        lock (_lock)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                _seen = stamp;
                if (stamp != _stamp)
                {
                    _config = Normalize(JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ReadOpts) ?? new());
                    _stamp = stamp;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"could not read config ({path}): {ex.Message}");
            }
            return _config;
        }
    }

    /// <summary>Save refused: the file changed after the caller loaded it (<see cref="Save"/> with an expected version).</summary>
    public const string Conflict = "The settings were changed in another tab or window after this one loaded them. Reload the page to see them; the changes made here were not saved.";

    /// <summary>
    /// Validates and saves the settings. Returns an error message, or null on success. With
    /// <paramref name="expectedVersion"/> (the <see cref="Version"/> the caller loaded), a save over a newer file is
    /// refused with <see cref="Conflict"/> instead of silently undoing the newer change.
    /// </summary>
    public string? Save(string json, long? expectedVersion = null)
    {
        AgentConfig cfg;
        try { cfg = JsonSerializer.Deserialize<AgentConfig>(json, ReadOpts) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { return "Invalid JSON: " + ex.Message; }

        cfg = Normalize(cfg);
        foreach (var s in cfg.Shortcuts)
            if (string.IsNullOrWhiteSpace(s.Cmd)) return $"Shortcut '{s.Label}' has no command";
        if (cfg.Weather.Lat is < -90 or > 90 || cfg.Weather.Lon is < -180 or > 180) return "Invalid weather location";
        if (!HhMm().IsMatch(cfg.Night.Start ?? "") || !HhMm().IsMatch(cfg.Night.End ?? "")) return "Night mode times must be in HH:MM format";

        lock (_lock)
        {
            if (expectedVersion is { } expected && File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks != expected) return Conflict;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, WriteOpts));
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
            _config = cfg;
            _stamp = _seen = File.GetLastWriteTimeUtc(path);
        }
        return null;
    }

    /// <summary>Fills in defaults: builds pages from templates, migrates old pages, generates unique ids.</summary>
    private static AgentConfig Normalize(AgentConfig c)
    {
        c.Weather ??= new();
        c.CodexBar ??= new();
        c.CodexBar.Providers = (c.CodexBar.Providers ?? []).Where(p => p is "claude" or "codex" or "antigravity").Distinct().ToArray();
        c.CodexBar.PageSize = Math.Clamp(c.CodexBar.PageSize, 0, 6);
        c.CodexBar.RotateInterval = Math.Clamp(c.CodexBar.RotateInterval, 2, 300);
        c.Night ??= new();
        c.Controls ??= new();
        c.Controls.Actions ??= [];
        c.Controls.Profiles ??= [];
        c.Controls.Actions = c.Controls.Actions.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id).Take(512).ToList();
        foreach (var action in c.Controls.Actions) { action.Target ??= ""; action.Kind ??= "shortcut"; action.Label ??= action.Id; }
        c.Controls.Profiles = c.Controls.Profiles.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id)).DistinctBy(p => p.Id).Take(128).ToList();
        foreach (var profile in c.Controls.Profiles) { profile.Levels = (profile.Levels ?? []).Where(l => l is not null).Take(512).ToList(); profile.Defaults ??= []; profile.Actions = (profile.Actions ?? []).Where(a => !string.IsNullOrEmpty(a)).Take(64).ToList(); profile.Label ??= profile.Id; }
        c.Display ??= new();
        c.Display.Appearance?.Normalize();
        c.Display.IdleReturnSec = Math.Clamp(c.Display.IdleReturnSec, 0, 3600);
        c.Display.SwitchCountdownSec = Math.Clamp(c.Display.SwitchCountdownSec, 0, 30);
        c.Display.TouchGraceSec = Math.Clamp(c.Display.TouchGraceSec, 0, 600);
        c.Display.MinDwellSec = Math.Clamp(c.Display.MinDwellSec, 0, 600);
        c.Display.HoldMaxMin = Math.Clamp(c.Display.HoldMaxMin, 0, 1440);
        c.Display.HomePage = string.IsNullOrWhiteSpace(c.Display.HomePage) ? null : c.Display.HomePage.Trim();
        c.Lan ??= new();
        c.Updates ??= new();
        c.Plugins ??= new();
        c.Plugins.Repo = PluginManifestValidator.NormalizeRepo(c.Plugins.Repo);
        c.Claude ??= new();
        c.Claude.TimeoutSec = Math.Clamp(c.Claude.TimeoutSec, 10, 3600);
        if (string.IsNullOrWhiteSpace(c.Updates.Repo)) c.Updates.Repo = "ersinkoc/phonebox";
        c.Lan.Port = Math.Clamp(c.Lan.Port, 1024, 65535);
        if (string.IsNullOrWhiteSpace(c.Lan.Token)) c.Lan.Token = NewToken();
        if (string.IsNullOrWhiteSpace(c.Lan.WebhookToken)) c.Lan.WebhookToken = NewToken();
        c.Night.Brightness = Math.Clamp(c.Night.Brightness, 0.01, 1);

        c.Pages = NormalizePages(c.Pages);

        var used = new HashSet<string>();
        c.Shortcuts = (c.Shortcuts ?? []).Where(s => s is not null).ToList(); // "shortcuts": [null] in a hand-written file
        foreach (var s in c.Shortcuts)
        {
            s.Label = s.Label?.Trim() ?? "";
            s.Icon = IconText.Repair(s.Icon);
            var id = string.IsNullOrWhiteSpace(s.Id) ? Slug(s.Label) : Slug(s.Id);
            if (id == "") id = "shortcut";
            var unique = id;
            for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
            s.Id = unique;
            if (string.IsNullOrWhiteSpace(s.Args)) s.Args = null;
            if (string.IsNullOrWhiteSpace(s.Cwd)) s.Cwd = null;
            s.Steps = s.Steps?.Where(x => x is not null).Select(x => x.Trim()).Where(x => x.Length > 0).Take(50).ToList();
            if (s.Steps is { Count: 0 }) s.Steps = null;
            if (string.IsNullOrWhiteSpace(s.StateKey)) s.StateKey = null;
            if (string.IsNullOrWhiteSpace(s.OffCmd)) s.OffCmd = null;
            if (string.IsNullOrWhiteSpace(s.OffArgs)) s.OffArgs = null;
        }
        c.Phones = (c.Phones ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).Select(g => g.First()).Take(20).ToList();
        foreach (var ph in c.Phones) { ph.Name = ph.Name?.Trim() ?? ""; if (ph.Pages is { Count: 0 }) ph.Pages = null; }
        c.Obs ??= new();
        if (string.IsNullOrWhiteSpace(c.Obs.Url)) c.Obs.Url = "ws://127.0.0.1:4455";
        c.HomeAssistant ??= new();
        c.HomeAssistant.Url = string.IsNullOrWhiteSpace(c.HomeAssistant.Url) ? null : c.HomeAssistant.Url.Trim().TrimEnd('/');
        c.HomeAssistant.Token = string.IsNullOrWhiteSpace(c.HomeAssistant.Token) ? null : c.HomeAssistant.Token.Trim();
        c.Calendars = (c.Calendars ?? []).Where(x => x is not null).Take(20).ToList();
        foreach (var cal in c.Calendars) { cal.Name = cal.Name?.Trim() ?? ""; cal.Url = cal.Url?.Trim() ?? ""; }

        c.Charge ??= new();
        c.Charge.Min = Math.Clamp(c.Charge.Min, 5, 95);
        c.Charge.Max = Math.Clamp(c.Charge.Max, c.Charge.Min + 5, 100);
        c.Charge.Method = c.Charge.Method == "shortcuts" ? "shortcuts" : "ha";
        c.Charge.Entity = string.IsNullOrWhiteSpace(c.Charge.Entity) ? null : c.Charge.Entity.Trim();

        var ruleIds = new HashSet<string>();
        c.Rules = (c.Rules ?? []).Where(r => r is not null && RuleConfig.Triggers.Contains(r.When) && RuleConfig.Actions.Contains(r.Then)).Take(50).ToList();
        foreach (var r in c.Rules)
        {
            r.Name = r.Name?.Trim() ?? "";
            var id = Slug(string.IsNullOrWhiteSpace(r.Id) ? (r.Name == "" ? r.When : r.Name) : r.Id);
            if (id == "") id = "rule";
            var unique = id;
            for (var i = 2; !ruleIds.Add(unique); i++) unique = $"{id}-{i}";
            r.Id = unique;
            if (r.Value is { } v) r.Value = Math.Clamp(v, 0, r.When switch { "idle" => 1440, "ping-high" => 10000, _ => 100 });
            r.Level = r.Level is "ok" or "warn" or "error" ? r.Level : "info";
            r.Priority = Math.Clamp(r.Priority, 0, 9);
            r.ForSec = Math.Clamp(r.ForSec, 0, 3600);
            r.CooldownMin = Math.Clamp(r.CooldownMin, 0, 1440);
            if (r.CountdownSec is { } cd) r.CountdownSec = Math.Clamp(cd, 0, 30);
            r.Days = RuleEngine.NormalizeDays(r.Days);
            r.During = string.IsNullOrWhiteSpace(r.During) ? null : r.During.Trim();
            r.Op = r.Op is ">" or "<" or "=" or "!=" ? r.Op : null;
            r.Shortcut = string.IsNullOrWhiteSpace(r.Shortcut) ? null : r.Shortcut.Trim();
        }
        return c;
    }

    /// <summary>Expands templates, migrates the old fixed pages, keeps widgets on the grid and makes ids unique.</summary>
    private static List<PageConfig> NormalizePages(List<PageConfig>? input)
    {
        var src = input is { Count: > 0 } ? input : DefaultPages.Select(t => new PageConfig { Template = t }).ToList();
        var pages = new List<PageConfig>();
        var pageIds = new HashSet<string>();
        foreach (var p in src)
        {
            if (p is null) continue;
            p.Appearance?.Normalize();
            var tpl = Catalog.Template(p.Template ?? p.Type);
            if ((p.Widgets is null || p.Widgets.Count == 0) && tpl is not null)
            {
                p.Widgets = tpl.Widgets.Select(w => w.Clone()).ToList();
                if (p.Type is not null) MigrateLegacy(p);
            }
            if (string.IsNullOrWhiteSpace(p.Title)) p.Title = tpl?.Title ?? "Page";
            if (string.IsNullOrWhiteSpace(p.Icon)) p.Icon = tpl?.Icon ?? "▫️";
            p.Icon = IconText.Repair(p.Icon);
            p.Title = p.Title.Trim();
            if (p.IdleSec is { } idleSec) p.IdleSec = Math.Clamp(idleSec, 0, 86400);
            var id = Slug(string.IsNullOrWhiteSpace(p.Id) ? (tpl?.Id ?? p.Title) : p.Id);
            if (id == "") id = "page";
            var unique = id;
            for (var i = 2; !pageIds.Add(unique); i++) unique = $"{id}-{i}";
            p.Id = unique;
            p.Template = null;
            p.Type = null;
            p.Options = null;

            var widgetIds = new HashSet<string>();
            var widgets = new List<WidgetConfig>();
            foreach (var w in p.Widgets ?? [])
            {
                if (w?.Type is null || !Catalog.IsWidget(w.Type)) continue;
                w.Appearance?.Normalize(widget: true);
                var (minW, minH) = Catalog.MinSize(w.Type);
                w.W = Math.Clamp(w.W, Math.Min(minW, Catalog.Cols), Catalog.Cols);
                w.H = Math.Clamp(w.H, Math.Min(minH, Catalog.Rows), Catalog.Rows);
                w.X = Math.Clamp(w.X, 0, Catalog.Cols - w.W);
                w.Y = Math.Clamp(w.Y, 0, Catalog.Rows - w.H);
                var wid = Slug(string.IsNullOrWhiteSpace(w.Id) ? w.Type : w.Id);
                if (wid == "") wid = w.Type;
                var wu = wid;
                for (var i = 2; !widgetIds.Add(wu); i++) wu = $"{wid}-{i}";
                w.Id = wu;
                widgets.Add(w);
            }
            p.Widgets = widgets;
            pages.Add(p);
        }
        if (pages.Count > 0 && pages.All(p => !p.Enabled)) pages[0].Enabled = true;
        return pages;
    }

    /// <summary>Carries the options of a pre-0.6.2 fixed page over to the widgets of its template.</summary>
    private static void MigrateLegacy(PageConfig p)
    {
        bool On(string key, bool fallback = true) =>
            p.Options is not null && p.Options.TryGetValue(key, out var v) ? v.ValueKind != JsonValueKind.False : fallback;
        var ws = p.Widgets!;
        WidgetConfig? Find(string type) => ws.FirstOrDefault(w => w.Type == type);
        switch (p.Type)
        {
            case "home":
                var clock = Find("clock");
                clock?.Set("weather", On("weather"));
                var tiles = p.Options?.TryGetValue("tiles", out var t) == true && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : ["cpu", "gpu", "ram", "net"];
                ws.RemoveAll(w => w.Type == "stat");
                if (!On("media")) { ws.RemoveAll(w => w.Type == "media"); if (clock is not null) clock.W = 12; }
                foreach (var w in ws) w.H = tiles.Count > 0 ? 4 : 6;
                for (var i = 0; i < tiles.Count; i++)
                {
                    var tw = new WidgetConfig { Type = "stat", X = 12 * i / tiles.Count, Y = 4, H = 2 };
                    tw.W = 12 * (i + 1) / tiles.Count - tw.X;
                    tw.Set("metric", tiles[i]);
                    ws.Add(tw);
                }
                break;
            case "ai":
                Find("quotas")?.Set("providers", new[] { "claude", "codex" }.Where(x => On(x)).ToArray());
                break;
            case "work":
                if (!On("sessions")) { ws.RemoveAll(w => w.Type == "sessions"); if (Find("github") is { } g) { g.X = 0; g.W = 12; } }
                else if (!On("github")) { ws.RemoveAll(w => w.Type == "github"); if (Find("sessions") is { } s) s.W = 12; }
                break;
            case "procs":
                Find("processes")?.Set("allowKill", On("allowKill"));
                break;
            case "deck":
                if (p.Options?.TryGetValue("columns", out var cols) == true && cols.TryGetInt32(out var n)) Find("shortcuts")?.Set("columns", n);
                break;
        }
    }

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Makes sure the generated Wi-Fi token is written to disk so it stays stable across restarts.</summary>
    public void PersistGenerated()
    {
        var c = Get();
        var version = _stamp.Ticks;
        try
        {
            var onDisk = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ReadOpts);
            var damagedIcons = (onDisk?.Pages ?? []).Any(p => p is not null && IconText.Repair(p.Icon) != p.Icon)
                || (onDisk?.Shortcuts ?? []).Any(s => s is not null && IconText.Repair(s.Icon) != s.Icon);
            if (!damagedIcons && onDisk?.Lan?.Token == c.Lan.Token && onDisk?.Lan?.WebhookToken == c.Lan.WebhookToken) return;
        }
        catch { }
        Save(JsonSerializer.Serialize(c, WriteOpts), version);
    }

    // ToLowerInvariant keeps the Turkish capital İ (U+0130) as it is, so it is mapped by hand like the other letters
    private static string Slug(string s) => NonSlug().Replace(s.ToLowerInvariant()
        .Replace('İ', 'i').Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c'), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex HhMm();
}

public sealed class CodexBarConfig
{
    public string[] Providers { get; set; } = ["claude", "codex", "antigravity"];
    public bool Rotate { get; set; } = true;
    public int PageSize { get; set; } = 2;
    public int RotateInterval { get; set; } = 8;
}
