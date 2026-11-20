using System.Globalization;
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
    public PingConfig Ping { get; set; } = new();
    public NightConfig Night { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public LanConfig Lan { get; set; } = new();
    public UpdateConfig Updates { get; set; } = new();
    public ClaudeConfig Claude { get; set; } = new();
    /// <summary>Cursor agent-transcript sessions on the AI sessions widget (off by default).</summary>
    public CursorConfig Cursor { get; set; } = new();
    public List<PageConfig> Pages { get; set; } = [];
    public List<Shortcut> Shortcuts { get; set; } = [];
    public List<RuleConfig> Rules { get; set; } = [];
    public ChargeConfig Charge { get; set; } = new();
    /// <summary>Per-phone settings when several phones use this agent: a name and which pages each one shows.</summary>
    public List<PhoneConfig> Phones { get; set; } = [];
    /// <summary>iCalendar feeds for the Agenda widget; the URLs are secrets and never go to the phone.</summary>
    public List<CalendarSource> Calendars { get; set; } = [];
    /// <summary>RSS and Atom feeds for the Feeds widget; like the calendars, the addresses never go to the phone.</summary>
    public List<FeedSource> Feeds { get; set; } = [];
    /// <summary>The Photo frame widget's folder; like the feeds, it never goes to the phone.</summary>
    public PhotoFrameConfig Photos { get; set; } = new();
    /// <summary>IP cameras for the Camera widget; their addresses (with the passwords in them) never go to the phone.</summary>
    public CameraConfig Cameras { get; set; } = new();
    /// <summary>Home Assistant address and long-lived access token (the token never goes to the phone).</summary>
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    /// <summary>OBS Studio's WebSocket server (obs-websocket 5).</summary>
    public ObsConfig Obs { get; set; } = new();
    /// <summary>Discord desktop app (local IPC): the developer application's client id and secret (the secret never goes to the phone).</summary>
    public DiscordConfig Discord { get; set; } = new();
    /// <summary>App console widget: key and dial bindings per app (the phone gets labels and ids, not the keys to press).</summary>
    public ConsoleConfig Console { get; set; } = new();
    /// <summary>Steam friends presence (the Web API key stays on this computer and is never sent to the phone).</summary>
    public SteamConfig Steam { get; set; } = new();

    /// <summary>The pages a phone shows: its own selection (Settings › Phones), or all of them.</summary>
    public IEnumerable<PageConfig> PagesFor(string? deviceId)
    {
        var ids = Phones.FirstOrDefault(p => p.Id == deviceId)?.Pages;
        if (ids is not { Count: > 0 }) return Pages;
        var chosen = Pages.Where(p => ids.Contains(p.Id)).ToList();
        return chosen.Count > 0 ? chosen : Pages;
    }

    /// <summary>The part sent to the phone: commands (shortcut cmd/args/cwd, command widget scripts) are never included.</summary>
    /// <param name="shortcutIcon">A shortcut's picture: its revision (null when it has none), and whether the user uploaded
    /// it rather than it being the program's own icon; only an upload may fill a Big button (a program icon is small, or on
    /// Linux a theme logo). One lookup gives both, so they always describe the same picture.</param>
    public object ToPublic(string? deviceId = null, Func<Shortcut, (long? Rev, bool Upload)>? shortcutIcon = null) => new
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
        // cameras: only how often the Camera widget may ask for a picture; the addresses and names stay on this computer
        cameras = Cameras.Sources.Select(c => new { c.Id, c.Fps }),
        shortcuts = Shortcuts.Select(s =>
        {
            var pic = shortcutIcon?.Invoke(s);
            return new { s.Id, s.Label, s.Icon, s.Color, toggle = s.Toggle || s.Cmd == "builtin:mute", hold = s.LongPress is not null, cycle = s.Cmd == "builtin:cycle" && s.Cycle is { Count: > 0 }, iconRev = pic?.Rev, iconUpload = pic?.Upload == true };
        }),
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

    /// <summary>True when an enabled page (including enabled Smart Stack children) shows the quota cost overview card.</summary>
    public bool ShowQuotaOverview() => Widgets(enabledPagesOnly: true)
        .Any(w => w.Type is "codexbar" or "quotas" && w.Bool("showOverview") == true);

    /// <summary>True when an enabled page (including enabled Smart Stack children) shows a quota or CodexBar widget that includes Codex.</summary>
    public bool ShowCodexQuota() => Widgets(enabledPagesOnly: true)
        .Any(w => w.Type is "codexbar" or "quotas" && WidgetIncludesProvider(w, "codex"));

    private bool WidgetIncludesProvider(WidgetConfig w, string id)
    {
        if (w.Opt("providers") is { ValueKind: JsonValueKind.Array } arr)
            // case-sensitive, as the panel compares them: "CODEX" shows no Codex card, so it starts no polling either
            return arr.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == id);
        return CodexBar.Providers.Any(p => p == id);
    }
}

public sealed class WeatherConfig
{
    public string City { get; set; } = "Tallinn";
    public double Lat { get; set; } = 59.437;
    public double Lon { get; set; } = 24.745;
    /// <summary>
    /// "celsius" or "fahrenheit": how the panel shows temperatures. The forecast is always fetched in °C and converted
    /// on the phone, so a change applies to the cached forecast too and Custom HTML widgets keep Open-Meteo's values.
    /// </summary>
    [JsonConverter(typeof(WeatherUnitJsonConverter))]
    public string Unit { get; set; } = "celsius";
    /// <summary>
    /// The Open-Meteo forecast model the phone asks for ("gfs_seamless", "ecmwf_ifs025"…), or "" for Open-Meteo's own
    /// best match. Only the models in <see cref="WeatherModelJsonConverter.Models"/> reach the phone.
    /// </summary>
    [JsonConverter(typeof(WeatherModelJsonConverter))]
    public string Model { get; set; } = "";
}

public sealed class PingConfig
{
    /// <summary>Host or IP address pinged every 5 s for the ping metric and the "ping high" rule; empty = off.</summary>
    public string? Host { get; set; } = PingMonitor.DefaultHost;
    /// <summary>
    /// Network adapters (their names, as Windows or the OS shows them) whose traffic the network meters add up. Empty =
    /// automatic: the connected Ethernet and Wi-Fi adapters, without virtual ones. With a VPN that tunnels the same
    /// traffic, or a driver that reports it twice, picking the one adapter gives the real rate (#190, #169).
    /// </summary>
    public List<string> Adapters { get; set; } = [];
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
    /// <summary>UI language code ("auto", "en", "tr", "de", "fr", "es", "it", "pt", "pt-BR", "nl", "pl", "ru", "el", "vi", "zh").</summary>
    public string Language { get; set; } = "auto";
    /// <summary>The codes the panel and the settings page are translated into (i18n.js). Casing matters for pt-BR:
    /// it is a distinct entry from pt, not a variant of it, so it must survive a save in this exact form.</summary>
    internal static readonly string[] Languages = ["auto", "en", "tr", "de", "fr", "es", "it", "pt", "pt-BR", "nl", "pl", "ru", "el", "vi", "zh"];
    public bool ShowSeconds { get; set; } = true;
    /// <summary>false ("14:05", default), true ("2:05 PM") or "auto" (follow the phone).</summary>
    [JsonConverter(typeof(Hour12JsonConverter))]
    public string Hour12 { get; set; } = "false";
    /// <summary>Return to the first page after this many idle seconds; 0 = never.</summary>
    public int IdleReturnSec { get; set; } = 90;
    /// <summary>Vibrate when a session finishes or needs input.</summary>
    public bool Vibrate { get; set; } = true;
    /// <summary>Show a message when a session finishes. The "needs input" message always shows.</summary>
    public bool FinishToast { get; set; } = true;
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
    /// <summary>OLED burn-in protection: every 2 minutes the panel moves by a few pixels and the night clock drifts.</summary>
    public bool PixelShift { get; set; }
    /// <summary>The page dots and the permission prompt's Allow / Deny buttons at the top, for a panel whose bottom edge is hard to reach.</summary>
    public bool ControlsTop { get; set; }
    /// <summary>OLED burn-in protection: dim the panel after this many minutes without a touch; 0 = never.</summary>
    public int IdleDimMin { get; set; }
    /// <summary>Screen pinning: Home and Recents stay on the panel.</summary>
    public bool Pin { get; set; }
    /// <summary>The panel is the phone's home app: it starts after a reboot and Home returns to it.</summary>
    public bool HomeScreen { get; set; }

    /// <summary>Over USB, turn off Android's screen saver (it starts on the dock / charger) and keep the phone awake while it is plugged in.</summary>
    public bool KeepAwake { get; set; } = true;
    /// <summary>Over USB, wake the phone and open PhoneBox when it is plugged in (or adb reconnects). Off: the panel opens only
    /// when you start it on the phone, from the tray or the settings page.</summary>
    public bool OpenOnConnect { get; set; } = true;
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

public sealed class ClaudeConfig
{
    /// <summary>Show AI coding-agent permission prompts on the phone (needs an agent hook, see Settings › General).</summary>
    public bool PhoneApprovals { get; set; } = true;
    /// <summary>How long the phone may take before the coding agent asks in its normal UI instead.</summary>
    public int TimeoutSec { get; set; } = 90;
    /// <summary>Let the phone open a Claude Code session's recent messages and steps, read from its chat file on this
    /// computer. They are your prompts and Claude's replies, so this stays off until you turn it on.</summary>
    public bool ShareConversation { get; set; }
    /// <summary>Only use the phone when the PC has been idle for a minute (you are away from the keyboard).</summary>
    public bool OnlyWhenAway { get; set; }
    /// <summary>Show each Claude Code chat's title on the phone. The title is the name you gave the chat or the one
    /// Claude Code wrote, which it often draws from your first prompt, so it stays off until you turn it on.</summary>
    public bool ShareSessionTitles { get; set; }
}

public sealed class CursorConfig
{
    /// <summary>Show recent Cursor agent chats on the AI sessions widget (off by default).</summary>
    public bool Sessions { get; set; }
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
    /// <summary>session-waiting, session-busy, media-playing, process-running, app-in-front, mic-in-use, camera-in-use, obs-live, calendar-event, cpu-above, idle, time, push.</summary>
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
    /// <summary>
    /// Push trigger: compare the value instead of "is on" (&gt;, &gt;=, &lt;, &lt;=, =, !=) with <see cref="Arg"/>, or
    /// "stale": the value has not been updated for <see cref="Arg"/> seconds (or is not there at all).
    /// </summary>
    public string? Op { get; set; }
    public string? Arg { get; set; }

    public static readonly string[] Triggers = ["session-waiting", "session-busy", "media-playing", "process-running", "app-in-front", "mic-in-use", "camera-in-use", "obs-live", "calendar-event",
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

public sealed class DiscordConfig
{
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public sealed class SteamConfig
{
    public bool Enabled { get; set; }
    public string? SteamId { get; set; }
    public string? ApiKey { get; set; }
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

public sealed class FeedSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
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
    /// <summary>Id of another shortcut that a long press on this key runs instead (the phone only says "held"; the id is looked up here).</summary>
    public string? LongPress { get; set; }
    /// <summary>builtin:cycle: ids of the shortcuts to run one after another, a tap each; the key shows the one that ran last.</summary>
    public List<string>? Cycle { get; set; }
    public const int MaxCycle = 20;
}

/// <summary>Reads/writes config.json; external edits are picked up on the next request.</summary>
public sealed partial class ConfigStore(string path)
{
    /// <summary>Pages a fresh install (or an empty page list) starts with.</summary>
    public static readonly string[] DefaultPages = ["home", "ai", "work", "system", "procs", "deck"];

    public static readonly JsonSerializerOptions ReadOpts = new()
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

    /// <summary>Retain a secret redacted from settings GETs when a client sends it back as missing or null.</summary>
    internal static bool PreserveSteamApiKey(AgentConfig? proposed, string? storedKey)
    {
        if (proposed?.Steam is not { ApiKey: null } steam || storedKey is null) return false;
        steam.ApiKey = storedKey;
        return true;
    }

    private readonly Lock _lock = new();
    private AgentConfig _config = Normalize(new AgentConfig());
    private DateTime _stamp = DateTime.MinValue;
    private DateTime _seen = DateTime.MinValue; // write time of the file as last looked at, readable or not

    public string Path => path;

    /// <summary>Version stamp (file write time) so the phone can notice changes.</summary>
    public long Version { get { Get(); return _seen.Ticks; } }

    /// <summary>
    /// <see cref="Version"/> for a JSON reply. File-time ticks (~6.4e17) are beyond what a JavaScript number holds
    /// exactly (2^53), so as a JSON number the page gets it rounded, sends that back in X-Config-Version, and its next
    /// save is refused as a conflict. As text it comes back exactly.
    /// </summary>
    public string VersionText => Version.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>True when <paramref name="header"/> (X-Config-Version) names a version other than the current one.
    /// Missing or unparseable is not a conflict: scripts and other callers that never send it still go through.</summary>
    public bool VersionConflict(string? header) => long.TryParse(header, out var expected) && expected != Version;

    /// <summary>
    /// Advances <see cref="Version"/> without touching the saved settings: for a write that bypasses <see cref="Save"/>
    /// (a shortcut icon upload or removal), so another tab's own optimistic-concurrency check still notices it.
    /// </summary>
    public void Touch() { lock (_lock) { try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { } } }

    /// <summary>
    /// Validates and saves the settings. Returns an error message, or null on success. With
    /// <paramref name="expectedVersion"/> (the <see cref="Version"/> the caller loaded), a save over a newer file is
    /// refused with <see cref="Conflict"/> instead of silently undoing the newer change.
    /// </summary>
    public string? Save(string json, long? expectedVersion = null) => Save(json, expectedVersion, out _);

    /// <summary>Same as <see cref="Save(string, long?)"/>, and reports the version it wrote while it still holds the lock.</summary>
    public string? Save(string json, long? expectedVersion, out long savedVersion)
    {
        savedVersion = 0;
        AgentConfig cfg;
        try { cfg = JsonSerializer.Deserialize<AgentConfig>(json, ReadOpts) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { return "Invalid JSON: " + ex.Message; }

        var staleIds = Get().Shortcuts.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        cfg = Normalize(cfg, staleIds);
        foreach (var s in cfg.Shortcuts)
        {
            if (string.IsNullOrWhiteSpace(s.Cmd)) return $"Shortcut '{s.Label}' has no command";
            if (s.Cmd == "builtin:wake" && WakeOnLan.ParseArgs(s.Args, out _, out _) is { } wakeError) return $"Shortcut '{s.Label}': {wakeError}";
            // a toggle's off action runs through the same path while the toggle is on
            if (s.OffCmd == "builtin:wake" && WakeOnLan.ParseArgs(s.OffArgs, out _, out _) is { } offWakeError) return $"Shortcut '{s.Label}' (off action): {offWakeError}";
        }
        if (cfg.Console.Profiles.Count > AppConsole.MaxProfiles) return $"The app console has more than {AppConsole.MaxProfiles} apps";
        foreach (var p in cfg.Console.Profiles)
            if (AppConsole.Validate(p) is { } consoleError) return consoleError;
        if (cfg.Cameras.Sources.Count > CameraFeeds.MaxCameras) return $"There are more than {CameraFeeds.MaxCameras} cameras";
        foreach (var cam in cfg.Cameras.Sources)
            if (!CameraFeeds.ValidUrl(cam.Url))
                return $"Camera '{(cam.Name.Length > 0 ? cam.Name : "(no name)")}': the address must start with rtsp:// or rtsps://";
        if (cfg.Weather.Lat is < -90 or > 90 || cfg.Weather.Lon is < -180 or > 180) return "Invalid weather location";
        if (!HhMm().IsMatch(cfg.Night.Start ?? "") || !HhMm().IsMatch(cfg.Night.End ?? "")) return "Night mode times must be in HH:MM format";

        lock (_lock)
        {
            if (expectedVersion is { } expected && File.GetLastWriteTimeUtc(path).Ticks != expected) return Conflict;
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var tmp = path + ".tmp";
            // owner-only on macOS/Linux: the file holds the Wi-Fi, webhook and Home Assistant keys
            AppPaths.WriteAllTextPrivate(tmp, JsonSerializer.Serialize(cfg, WriteOpts));
            if (File.Exists(path)) AppPaths.CopyPrivate(path, path + ".bak");
            File.Move(tmp, path, overwrite: true);
            _config = cfg;
            _stamp = _seen = File.GetLastWriteTimeUtc(path);
            savedVersion = _seen.Ticks;
        }
        return null;
    }

    /// <summary>Fills in defaults: builds pages from templates, migrates old pages, generates unique ids.</summary>
    private static AgentConfig Normalize(AgentConfig c, IReadOnlySet<string>? staleIds = null)
    {
        c.Weather ??= new();
        c.Weather.Unit = WeatherUnitJsonConverter.Normalize(c.Weather.Unit);
        c.Weather.Model = WeatherModelJsonConverter.Normalize(c.Weather.Model);
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
        c.Display.Hour12 = Hour12JsonConverter.Normalize(c.Display.Hour12);
        c.Display.IdleReturnSec = Math.Clamp(c.Display.IdleReturnSec, 0, 3600);
        c.Display.IdleDimMin = Math.Clamp(c.Display.IdleDimMin, 0, 1440);
        // a language the panel does not have falls back to following the phone / browser. Matched case-insensitively
        // so "PT-br" still resolves, but stored in its canonical casing: i18n.js looks pt-BR up by exact code.
        var lang = (c.Display.Language ?? "").Trim();
        c.Display.Language = DisplayConfig.Languages.FirstOrDefault(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase)) ?? "auto";
        c.Display.SwitchCountdownSec = Math.Clamp(c.Display.SwitchCountdownSec, 0, 30);
        c.Display.TouchGraceSec = Math.Clamp(c.Display.TouchGraceSec, 0, 600);
        c.Display.MinDwellSec = Math.Clamp(c.Display.MinDwellSec, 0, 600);
        c.Display.HoldMaxMin = Math.Clamp(c.Display.HoldMaxMin, 0, 1440);
        c.Lan ??= new();
        c.Updates ??= new();
        c.Ping ??= new();
        c.Ping.Host = c.Ping.Host?.Trim() ?? "";
        if (c.Ping.Host.Length > 253 || c.Ping.Host.Any(ch => ch <= ' ' || ch > '~')) c.Ping.Host = PingMonitor.DefaultHost;
        c.Claude ??= new();
        c.Claude.TimeoutSec = Math.Clamp(c.Claude.TimeoutSec, 10, 3600);
        c.Cursor ??= new();
        if (string.IsNullOrWhiteSpace(c.Updates.Repo)) c.Updates.Repo = "ersinkoc/phonebox";
        c.Lan.Port = Math.Clamp(c.Lan.Port, 1024, 65535);
        if (string.IsNullOrWhiteSpace(c.Lan.Token)) c.Lan.Token = NewToken();
        if (string.IsNullOrWhiteSpace(c.Lan.WebhookToken)) c.Lan.WebhookToken = NewToken();
        c.Night.Brightness = Math.Clamp(c.Night.Brightness, 0.01, 1);

        c.Pages = NormalizePages(c.Pages, out var originalPageIds);
        // an exact match first; a reference with stray spaces around it still finds the page it names
        string PageId(string id) =>
            originalPageIds.TryGetValue(id, out var exact) ? exact
            : originalPageIds.TryGetValue(id.Trim(), out var trimmed) ? trimmed
            : id.Trim();
        c.Display.HomePage = string.IsNullOrWhiteSpace(c.Display.HomePage) ? null : PageId(c.Display.HomePage).Trim();

        var used = new HashSet<string>();
        c.Shortcuts = (c.Shortcuts ?? []).Where(s => s is not null).ToList(); // "shortcuts": [null] in a hand-written file

        // Pass 1: reserve every explicitly-provided id first. Otherwise a blank-id (brand-new) shortcut processed
        // earlier in the list than one that explicitly keeps an id could grab that id for itself, since at that
        // point nothing yet says it is taken — bumping the one that actually owns it to an unrelated id instead.
        var reserved = new Dictionary<Shortcut, string>();
        foreach (var s in c.Shortcuts.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            var id = Slug(s.Id);
            if (id == "") id = "shortcut";
            var unique = id;
            for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
            reserved[s] = unique;
        }

        foreach (var s in c.Shortcuts)
        {
            s.Label = s.Label?.Trim() ?? "";
            s.Icon = IconText.Repair(s.Icon);
            if (reserved.TryGetValue(s, out var keptId)) s.Id = keptId;
            else
            {
                var id = Slug(s.Label);
                if (id == "") id = "shortcut";
                var unique = id;
                for (var i = 2; staleIds?.Contains(unique) == true || !used.Add(unique); i++) unique = $"{id}-{i}";
                s.Id = unique;
            }
            if (string.IsNullOrWhiteSpace(s.Args)) s.Args = null;
            if (string.IsNullOrWhiteSpace(s.Cwd)) s.Cwd = null;
            s.Steps = s.Steps?.Where(x => x is not null).Select(x => x.Trim()).Where(x => x.Length > 0).Take(50).ToList();
            if (s.Steps is { Count: 0 }) s.Steps = null;
            if (string.IsNullOrWhiteSpace(s.StateKey)) s.StateKey = null;
            if (string.IsNullOrWhiteSpace(s.OffCmd)) s.OffCmd = null;
            if (string.IsNullOrWhiteSpace(s.OffArgs)) s.OffArgs = null;
        }
        // a long press runs another key: drop a blank, self-referencing or dangling target, so the phone never offers a hold that does nothing
        foreach (var s in c.Shortcuts)
        {
            s.LongPress = s.LongPress?.Trim();
            if (string.IsNullOrEmpty(s.LongPress) || s.LongPress == s.Id || !c.Shortcuts.Any(o => o.Id == s.LongPress)) s.LongPress = null;
        }
        // a cycle key: the keys it runs must exist (a deleted or renamed one drops out), and none of them is the cycle itself
        foreach (var s in c.Shortcuts)
        {
            s.Cycle = s.Cmd == "builtin:cycle"
                ? (s.Cycle ?? []).Select(x => x?.Trim() ?? "").Where(x => x != s.Id && c.Shortcuts.Any(o => o.Id == x)).Take(Shortcut.MaxCycle).ToList()
                : null;
            // a cycle key runs the next key, not a command of its own: an on / off setting would bypass the cycle
            if (s.Cmd == "builtin:cycle") { s.Toggle = false; s.StateKey = null; s.OffCmd = null; s.OffArgs = null; }
        }
        c.Phones = (c.Phones ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).Select(g => g.First()).Take(20).ToList();
        foreach (var ph in c.Phones)
        {
            ph.Name = ph.Name?.Trim() ?? "";
            ph.Pages = ph.Pages?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(PageId).Distinct().ToList();
            if (ph.Pages is { Count: 0 }) ph.Pages = null;
        }
        c.Ping ??= new();
        c.Ping.Adapters = (c.Ping.Adapters ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        c.Obs ??= new();
        c.Discord ??= new();
        c.Discord.ClientId = string.IsNullOrWhiteSpace(c.Discord.ClientId) ? null : c.Discord.ClientId.Trim();
        c.Discord.ClientSecret = string.IsNullOrWhiteSpace(c.Discord.ClientSecret) ? null : c.Discord.ClientSecret.Trim();
        c.Steam ??= new();
        c.Steam.SteamId = string.IsNullOrWhiteSpace(c.Steam.SteamId) ? null : c.Steam.SteamId.Trim();
        c.Steam.ApiKey = string.IsNullOrWhiteSpace(c.Steam.ApiKey) ? null : c.Steam.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(c.Obs.Url)) c.Obs.Url = "ws://127.0.0.1:4455";
        c.HomeAssistant ??= new();
        c.HomeAssistant.Url = string.IsNullOrWhiteSpace(c.HomeAssistant.Url) ? null : c.HomeAssistant.Url.Trim().TrimEnd('/');
        c.HomeAssistant.Token = string.IsNullOrWhiteSpace(c.HomeAssistant.Token) ? null : c.HomeAssistant.Token.Trim();
        c.Console ??= new();
        c.Console.Profiles = (c.Console.Profiles ?? []).Where(p => p is not null).Take(200).ToList(); // more than 30 is refused on save
        var consoleIds = new HashSet<string>();
        foreach (var p in c.Console.Profiles)
        {
            p.Label = Cap(p.Label?.Trim() ?? "", 40);
            p.Apps = p.Apps?.Trim() ?? ""; // too long is refused on save (AppConsole.Validate), not cut short
            var id = Slug(string.IsNullOrWhiteSpace(p.Id) ? p.Label : p.Id);
            if (id == "") id = "app";
            var unique = id;
            for (var i = 2; !consoleIds.Add(unique); i++) unique = $"{id}-{i}";
            p.Id = unique;
            p.Keys = (p.Keys ?? []).Where(k => k is not null && !string.IsNullOrWhiteSpace(k.Keys)).Take(100).ToList();
            // a key without a label shows its keys on the phone ("ctrl+z")
            foreach (var k in p.Keys) { k.Keys = k.Keys.Trim(); k.Label = Cap(k.Label?.Trim() is { Length: > 0 } l ? l : k.Keys, 24); }
            p.DialLeft = string.IsNullOrWhiteSpace(p.DialLeft) ? null : p.DialLeft.Trim();
            p.DialRight = string.IsNullOrWhiteSpace(p.DialRight) ? null : p.DialRight.Trim();
            p.DialPress = string.IsNullOrWhiteSpace(p.DialPress) ? null : p.DialPress.Trim();
        }
        c.Calendars = (c.Calendars ?? []).Where(x => x is not null).Take(20).ToList();
        foreach (var cal in c.Calendars) { cal.Name = cal.Name?.Trim() ?? ""; cal.Url = cal.Url?.Trim() ?? ""; }
        c.Feeds = (c.Feeds ?? []).Where(x => x is not null).Take(30).ToList();
        c.Photos ??= new();
        c.Photos.Folder = (c.Photos.Folder ?? "").Trim() is { Length: > 1024 } longFolder ? longFolder[..1024] : (c.Photos.Folder ?? "").Trim();
        c.Photos.Source = string.Equals(c.Photos.Source?.Trim(), PhotoFrameConfig.SourceImmich, StringComparison.OrdinalIgnoreCase) ? PhotoFrameConfig.SourceImmich : PhotoFrameConfig.SourceFolder;
        c.Photos.ImmichUrl = Cap((c.Photos.ImmichUrl ?? "").Trim(), 2048);
        // a control character (a stray newline from a paste) can't be part of a key, and would break the request header
        c.Photos.ImmichKey = Cap(new string((c.Photos.ImmichKey ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim(), 256);
        c.Photos.ImmichAlbum = Cap((c.Photos.ImmichAlbum ?? "").Trim(), 64);
        c.Photos.ImmichAlbumName = Cap((c.Photos.ImmichAlbumName ?? "").Trim(), 200);
        foreach (var f in c.Feeds) { var n = f.Name?.Trim() ?? ""; f.Name = n.Length > 60 ? n[..60] : n; f.Url = f.Url?.Trim() ?? ""; }
        c.Cameras ??= new();
        c.Cameras.Ffmpeg = (c.Cameras.Ffmpeg ?? "").Trim() is { Length: > 1024 } longFfmpeg ? longFfmpeg[..1024] : (c.Cameras.Ffmpeg ?? "").Trim();
        // a row left empty in the editor is dropped; ids are random and kept, so widgets keep pointing at the same camera
        c.Cameras.Sources = (c.Cameras.Sources ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Url)).Take(100).ToList();
        var cameraIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cam in c.Cameras.Sources)
        {
            var n = cam.Name?.Trim() ?? "";
            cam.Name = n.Length > 60 ? n[..60] : n;
            cam.Url = cam.Url.Trim() is { Length: > 2048 } longUrl ? longUrl[..2048] : cam.Url.Trim();
            cam.Transport = string.Equals(cam.Transport?.Trim(), "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
            cam.Fps = cam.Fps is >= 1 and <= CameraFeeds.MaxFps ? cam.Fps : CameraFeeds.DefaultFps;
            var id = (cam.Id ?? "").Trim().ToLowerInvariant();
            if (!CameraId().IsMatch(id) || !cameraIds.Add(id)) { id = Guid.NewGuid().ToString("N")[..12]; cameraIds.Add(id); }
            cam.Id = id;
        }

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
            r.Op = r.Op is ">" or ">=" or "<" or "<=" or "=" or "!=" or "stale" ? r.Op : null;
            // seconds without an update: 1 s to a day, a minute when the box was left empty or holds text ("Infinity" too)
            if (r.Op == "stale")
                r.Arg = (double.TryParse(r.Arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var staleSec) && double.IsFinite(staleSec) && staleSec > 0
                    ? Math.Clamp(Math.Round(staleSec), 1, 86400) : 60).ToString(CultureInfo.InvariantCulture);
            r.Shortcut = string.IsNullOrWhiteSpace(r.Shortcut) ? null : r.Shortcut.Trim();
            if (r.Page is { } page) r.Page = PageId(page);
        }
        return c;
    }

    /// <summary>Expands templates, migrates the old fixed pages, keeps widgets on the grid and makes ids unique.</summary>
    private static List<PageConfig> NormalizePages(List<PageConfig>? input, out Dictionary<string, string> originalIds)
    {
        originalIds = new(StringComparer.Ordinal);
        input = input?.Where(p => p is not null).ToList();
        var src = input is { Count: > 0 } ? input : DefaultPages.Select(t => new PageConfig { Template = t }).ToList();
        var pages = new List<PageConfig>();
        var pageIds = new HashSet<string>();
        var reservedPages = ReserveLayoutIds(src, p => p.Id, _ => "page", pageIds);
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
            var originalId = p.Id;
            p.Id = reservedPages.TryGetValue(p, out var reservedPage)
                ? reservedPage : UniqueLayoutId(LayoutId(tpl?.Id ?? p.Title, "page"), pageIds);
            if (!string.IsNullOrWhiteSpace(originalId)) originalIds.TryAdd(originalId, p.Id);
            p.Template = null;
            p.Type = null;
            p.Options = null;

            var widgetIds = new HashSet<string>();
            var widgets = new List<WidgetConfig>();
            var supportedWidgets = (p.Widgets ?? []).Where(w => w?.Type is not null && Catalog.IsWidget(w.Type)).ToList();
            var reservedWidgets = ReserveLayoutIds(supportedWidgets, w => w.Id, w => w.Type, widgetIds);
            foreach (var w in supportedWidgets)
            {
                w.Appearance?.Normalize(widget: true);
                var (minW, minH) = Catalog.MinSize(w.Type);
                w.W = Math.Clamp(w.W, Math.Min(minW, Catalog.Cols), Catalog.Cols);
                w.H = Math.Clamp(w.H, Math.Min(minH, Catalog.Rows), Catalog.Rows);
                w.X = Math.Clamp(w.X, 0, Catalog.Cols - w.W);
                w.Y = Math.Clamp(w.Y, 0, Catalog.Rows - w.H);
                w.Id = reservedWidgets.TryGetValue(w, out var reservedWidget)
                    ? reservedWidget : UniqueLayoutId(LayoutId(w.Type, w.Type), widgetIds);
                widgets.Add(w);
            }
            p.Widgets = widgets;
            pages.Add(p);
        }
        if (pages.Count > 0 && pages.All(p => !p.Enabled)) pages[0].Enabled = true;
        return pages;
    }

    private static Dictionary<T, string> ReserveLayoutIds<T>(IEnumerable<T> items, Func<T, string> getId,
        Func<T, string> fallback, HashSet<string> used) where T : class
    {
        var reserved = new Dictionary<T, string>();
        var collisions = new List<(T Item, string Id)>();
        var explicitIds = items.Select(item => (Item: item, Original: getId(item)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Original))
            .Select(entry => (entry.Item, entry.Original, Id: LayoutId(entry.Original, fallback(entry.Item))))
            .OrderByDescending(entry => entry.Id == entry.Original);
        // Reserve existing canonical IDs and each explicit base before allocating suffixes.
        foreach (var (item, _, id) in explicitIds)
        {
            if (used.Add(id)) reserved[item] = id;
            else collisions.Add((item, id));
        }
        foreach (var (item, id) in collisions) reserved[item] = UniqueLayoutId(id, used);
        return reserved;
    }

    private static string LayoutId(string text, string fallback) => Slug(text) is { Length: > 0 } id ? id : fallback;

    private static string UniqueLayoutId(string id, HashSet<string> used)
    {
        var unique = id;
        for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
        return unique;
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
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
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
                if (p.Options?.TryGetValue("columns", out var cols) == true && cols.ValueKind == JsonValueKind.Number && cols.TryGetInt32(out var n)) Find("shortcuts")?.Set("columns", n);
                break;
        }
    }

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Makes sure the generated Wi-Fi token is written to disk so it stays stable across restarts.</summary>
    public void PersistGenerated()
    {
        AgentConfig c;
        long version;
        lock (_lock)
        {
            c = Get();
            // The first save needs the missing-file stamp. For existing files, only overwrite a snapshot we read.
            version = (_stamp == DateTime.MinValue && !File.Exists(path) ? _seen : _stamp).Ticks;
        }
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

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;

    // ToLowerInvariant keeps the Turkish capital İ (U+0130) as it is, so it is mapped by hand like the other letters
    private static string Slug(string s) => NonSlug().Replace(s.ToLowerInvariant()
        .Replace('İ', 'i').Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c'), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex HhMm();

    [GeneratedRegex("^[a-z0-9]{6,32}$")]
    private static partial Regex CameraId();
}

public sealed class CodexBarConfig
{
    public string[] Providers { get; set; } = ["claude", "codex", "antigravity"];
    public bool Rotate { get; set; } = true;
    public int PageSize { get; set; } = 2;
    public int RotateInterval { get; set; } = 8;
    /// <summary>Opt-in: poll live Codex quotas through the local Codex CLI app-server (off by default).</summary>
    public bool CodexLiveQuota { get; set; }
}

/// <summary>
/// weather.unit: "fahrenheit" in any case or padding, anything else "celsius". A hand-edited number or boolean must not
/// make the whole settings file unreadable, so a non-string token is skipped instead of thrown on.
/// </summary>
internal sealed class WeatherUnitJsonConverter : JsonConverter<string>
{
    public static string Normalize(string? value) => string.Equals(value?.Trim(), "fahrenheit", StringComparison.OrdinalIgnoreCase) ? "fahrenheit" : "celsius";

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return Normalize(reader.GetString());
        reader.Skip();
        return "celsius";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Normalize(value));
}

/// <summary>
/// weather.model: one of the Open-Meteo models the settings offer, anything else "" (Open-Meteo's best match). The value
/// goes into the phone's forecast URL, so only known names pass; like the unit, a non-string token is skipped.
/// </summary>
internal sealed class WeatherModelJsonConverter : JsonConverter<string>
{
    public static readonly string[] Models =
        ["gfs_seamless", "ecmwf_ifs025", "icon_seamless", "ukmo_seamless", "gem_seamless", "jma_seamless", "meteofrance_seamless", "cma_grapes_global"];

    public static string Normalize(string? value) =>
        Models.FirstOrDefault(m => string.Equals(m, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return Normalize(reader.GetString());
        reader.Skip();
        return "";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Normalize(value));
}

/// <summary>Reads/writes display.hour12 as boolean false/true or "auto" (follow the phone) so boolean configs stay wire-compatible.</summary>
internal sealed class Hour12JsonConverter : JsonConverter<string>
{
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "12h" => "true",
        "auto" => "auto",
        _ => "false",
    };

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return "true";
        if (reader.TokenType == JsonTokenType.False) return "false";
        if (reader.TokenType == JsonTokenType.String) return Normalize(reader.GetString());
        reader.Skip();
        return "false";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value == "true") writer.WriteBooleanValue(true);
        else if (value == "auto") writer.WriteStringValue("auto");
        else writer.WriteBooleanValue(false);
    }
}
