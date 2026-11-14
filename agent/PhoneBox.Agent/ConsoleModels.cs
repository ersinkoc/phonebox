using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Application-aware Console configuration: profiles, key sub-pages, and rotary/roller axes.</summary>
public sealed class AppConsoleConfig
{
    public bool AutoFollow { get; set; } = true;
    public bool FocusBeforeKeys { get; set; } = true;
    public List<ConsoleProfile>? Profiles { get; set; }
}

public sealed class ConsoleProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🎛️";
    public string Color { get; set; } = "#38bdf8";
    public string? AppMatch { get; set; }
    public string? TitleMatch { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 2;
    public List<ConsoleSubPage>? SubPages { get; set; }
    public List<ConsoleAxis>? Axes { get; set; }
}

public sealed class ConsoleSubPage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Main";
    public List<ConsoleSlot>? Slots { get; set; }
}

public sealed class ConsoleSlot
{
    public string Kind { get; set; } = "keys";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string? Hint { get; set; }
    public string? Color { get; set; }
    public bool Toggle { get; set; }
    public string? StateKey { get; set; }
    public string? Keys { get; set; }
    public List<string>? Steps { get; set; }
    public string? Text { get; set; }
    public string? Target { get; set; }
}

public sealed class ConsoleAxis
{
    public int Index { get; set; } = 1;
    public string Name { get; set; } = "Axis";
    public string Icon { get; set; } = "🎚️";
    public string Kind { get; set; } = "keys";
    public string? PrevKeys { get; set; }
    public string? NextKeys { get; set; }
    public string? TapAction { get; set; }
    public string? AppTarget { get; set; }
    public string Sensitivity { get; set; } = "normal";
}

public sealed record ConsoleWindowInfo(string Process, string Title);

public sealed record ConsoleSnapshot(
    bool AutoFollow,
    string? ActiveProcess,
    string? ActiveTitle,
    string? MatchedProfileId,
    string? PinnedProfileId,
    string? EffectiveProfileId,
    Dictionary<string, bool> SlotStates,
    long UpdatedAt);

public sealed record ConsoleActRequest(
    string Kind,
    string? ProfileId = null,
    string? SubPageId = null,
    int? Slot = null,
    int? Axis = null,
    int? Delta = null,
    string? Pinned = null);

public static partial class AppConsole
{
    private const string DefaultColor = "#38bdf8";

    public static readonly HashSet<string> SlotKinds = ["empty", "keys", "macro", "text", "shortcut", "deck"];
    public static readonly HashSet<string> AxisKinds = ["keys", "system-volume", "app-volume", "mic-gain", "media-seek", "brightness"];
    public static readonly HashSet<string> Sensitivities = ["fine", "normal", "coarse"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorRe();

    public static string? ValidColor(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && HexColorRe().IsMatch(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    private static string? Clean(string? value, int maxLen = 256)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > maxLen ? s[..maxLen] : s;
    }

    private static string CleanIcon(string? value, string fallback)
    {
        var repaired = IconText.Repair(value)?.Trim();
        return string.IsNullOrEmpty(repaired) ? fallback : (repaired.Length > 16 ? repaired[..16] : repaired);
    }

    private static string UniqueSlug(string? raw, string prefix, HashSet<string> used)
    {
        var id = ConfigStore.Slug(string.IsNullOrWhiteSpace(raw) ? "" : raw);
        if (id == "") id = prefix;
        var unique = id;
        for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
        return unique;
    }

    public static AppConsoleConfig Normalize(AppConsoleConfig? cfg)
    {
        cfg ??= new();
        var list = cfg.Profiles ?? DefaultProfiles();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var defaultSeen = false;
        var normalized = new List<ConsoleProfile>();

        foreach (var p in list.Where(x => x is not null).Take(48))
        {
            p.Name = Clean(p.Name, 48) ?? "Profile";
            p.Id = UniqueSlug(string.IsNullOrWhiteSpace(p.Id) ? p.Name : p.Id, "profile", usedIds);
            p.Icon = CleanIcon(p.Icon, p.IsDefault ? "🖥️" : "🎛️");
            p.Color = ValidColor(p.Color) ?? DefaultColor;
            p.AppMatch = Clean(p.AppMatch, 256);
            p.TitleMatch = Clean(p.TitleMatch, 256);
            p.Columns = Math.Clamp(p.Columns == 0 ? 4 : p.Columns, 2, 6);
            p.Rows = Math.Clamp(p.Rows == 0 ? 2 : p.Rows, 1, 4);

            if (p.IsDefault)
            {
                if (defaultSeen) p.IsDefault = false;
                else defaultSeen = true;
            }

            p.SubPages = NormalizeSubPages(p.SubPages);
            p.Axes = NormalizeAxes(p.Axes);
            normalized.Add(p);
        }

        cfg.Profiles = normalized;
        return cfg;
    }

    private static List<ConsoleSubPage> NormalizeSubPages(List<ConsoleSubPage>? pages)
    {
        var src = pages is { Count: > 0 } ? pages : [new ConsoleSubPage { Id = "main", Name = "Main", Slots = [] }];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ConsoleSubPage>();

        foreach (var sp in src.Where(x => x is not null).Take(16))
        {
            sp.Name = Clean(sp.Name, 32) ?? "Main";
            sp.Id = UniqueSlug(string.IsNullOrWhiteSpace(sp.Id) ? sp.Name : sp.Id, "page", used);
            sp.Slots = NormalizeSlots(sp.Slots);
            result.Add(sp);
        }

        if (result.Count == 0) result.Add(new ConsoleSubPage { Id = "main", Name = "Main", Slots = [] });
        return result;
    }

    private static List<ConsoleSlot> NormalizeSlots(List<ConsoleSlot>? slots)
    {
        var result = new List<ConsoleSlot>();
        foreach (var s in (slots ?? []).Take(24))
        {
            if (s is null) { result.Add(new ConsoleSlot { Kind = "empty" }); continue; }
            s.Kind = SlotKinds.Contains(s.Kind) ? s.Kind : "keys";
            s.Label = Clean(s.Label, 36) ?? (s.Kind == "empty" ? "" : "Key");
            s.Icon = CleanIcon(s.Icon, s.Kind == "empty" ? "" : "🔘");
            s.Hint = Clean(s.Hint, 20);
            s.Color = ValidColor(s.Color);
            s.StateKey = Clean(s.StateKey, 64);
            s.Keys = Clean(s.Keys, 128);
            s.Text = s.Text is { Length: > 0 } ? (s.Text.Length > 2000 ? s.Text[..2000] : s.Text) : null;
            s.Target = Clean(s.Target, 256);
            s.Steps = s.Steps?.Select(x => Clean(x, 200)).Where(x => x is not null).Select(x => x!).Take(32).ToList();
            if (s.Steps is { Count: 0 }) s.Steps = null;
            if (s.Kind == "empty")
            {
                s.Toggle = false;
                s.Keys = s.Text = s.Target = s.Hint = s.StateKey = null;
                s.Steps = null;
            }
            result.Add(s);
        }
        return result;
    }

    private static List<ConsoleAxis> NormalizeAxes(List<ConsoleAxis>? axes)
    {
        var seen = new HashSet<int>();
        var result = new List<ConsoleAxis>();
        foreach (var a in (axes ?? []).Where(x => x is not null).Take(16))
        {
            var idx = Math.Clamp(a.Index == 0 ? 1 : a.Index, 1, 8);
            if (!seen.Add(idx)) continue;
            a.Index = idx;
            a.Name = Clean(a.Name, 32) ?? $"Axis {idx}";
            a.Icon = CleanIcon(a.Icon, "🎚️");
            a.Kind = AxisKinds.Contains(a.Kind) ? a.Kind : "keys";
            a.Sensitivity = Sensitivities.Contains(a.Sensitivity) ? a.Sensitivity : "normal";
            a.PrevKeys = Clean(a.PrevKeys, 128);
            a.NextKeys = Clean(a.NextKeys, 128);
            a.TapAction = Clean(a.TapAction, 128);
            a.AppTarget = Clean(a.AppTarget, 128);
            result.Add(a);
        }
        return result.OrderBy(a => a.Index).ToList();
    }

    public static bool HasDamagedIcons(AppConsoleConfig? cfg) =>
        (cfg?.Profiles ?? []).Any(p =>
            p is not null && (IconText.Repair(p.Icon) != p.Icon
                || (p.Axes ?? []).Any(a => a is not null && IconText.Repair(a.Icon) != a.Icon)
                || (p.SubPages ?? []).Any(sp => sp is not null && (sp.Slots ?? []).Any(s => s is not null && IconText.Repair(s.Icon) != s.Icon))));

    /// <summary>Phone-safe projection: never exposes keystrokes, typed text, macros, targets, or match rules.</summary>
    public static object ToPublic(AppConsoleConfig? cfg)
    {
        var c = cfg ?? new();
        return new
        {
            autoFollow = c.AutoFollow,
            profiles = (c.Profiles ?? []).Select(p => new
            {
                p.Id,
                p.Name,
                p.Icon,
                p.Color,
                p.IsDefault,
                p.Enabled,
                p.Columns,
                p.Rows,
                subPages = (p.SubPages ?? []).Select(sp => new
                {
                    sp.Id,
                    sp.Name,
                    slots = (sp.Slots ?? []).Select(s => new
                    {
                        s.Kind,
                        s.Label,
                        s.Icon,
                        hint = s.Kind == "keys" && !string.IsNullOrWhiteSpace(s.Hint)
                            ? (s.Hint.Length > 20 ? s.Hint[..20] : s.Hint)
                            : null,
                        s.Color,
                        s.Toggle,
                        stateKey = s.Toggle ? s.StateKey : null,
                    }),
                }),
                axes = (p.Axes ?? []).Select(a => new
                {
                    a.Index,
                    a.Name,
                    a.Icon,
                    a.Kind,
                    a.Sensitivity,
                    hasTap = !string.IsNullOrWhiteSpace(a.TapAction),
                }),
            }),
        };
    }

    public static List<ConsoleProfile> DefaultProfiles()
    {
        var mod = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        var switchApp = OperatingSystem.IsMacOS() ? "Cmd+Tab" : "Alt+Tab";
        return
        [
            new ConsoleProfile
            {
                Id = "desktop",
                Name = "Desktop",
                Icon = "🖥️",
                Color = "#38bdf8",
                IsDefault = true,
                Enabled = true,
                Columns = 4,
                Rows = 2,
                SubPages =
                [
                    new ConsoleSubPage
                    {
                        Id = "media",
                        Name = "Media & Essentials",
                        Slots =
                        [
                            new() { Kind = "deck", Label = "Prev", Icon = "⏮️", Target = "media:prev" },
                            new() { Kind = "deck", Label = "Play / Pause", Icon = "⏯️", Target = "media:playpause", Toggle = true, StateKey = "media:playing" },
                            new() { Kind = "deck", Label = "Next", Icon = "⏭️", Target = "media:next" },
                            new() { Kind = "deck", Label = "Mute", Icon = "🔇", Target = "audio:mute", Toggle = true, StateKey = "audio:muted" },
                            new() { Kind = "keys", Label = "Copy", Icon = "📋", Hint = $"{mod}+C", Keys = $"{mod}+C" },
                            new() { Kind = "keys", Label = "Paste", Icon = "📥", Hint = $"{mod}+V", Keys = $"{mod}+V" },
                            new() { Kind = "keys", Label = "Undo", Icon = "↩️", Hint = $"{mod}+Z", Keys = $"{mod}+Z" },
                            new() { Kind = "keys", Label = "Switch App", Icon = "🪟", Hint = switchApp, Keys = switchApp },
                        ],
                    },
                ],
                Axes =
                [
                    new() { Index = 1, Name = "Volume", Icon = "🔊", Kind = "system-volume", TapAction = "deck:audio:mute", Sensitivity = "normal" },
                    new() { Index = 2, Name = "Seek", Icon = "⏩", Kind = "media-seek", TapAction = "deck:media:playpause", Sensitivity = "normal" },
                ],
            },
            new ConsoleProfile
            {
                Id = "vscode",
                Name = "VS Code",
                Icon = "💻",
                Color = "#3b82f6",
                AppMatch = "code|code - insiders|cursor|windsurf",
                Enabled = true,
                Columns = 4,
                Rows = 2,
                SubPages =
                [
                    new ConsoleSubPage
                    {
                        Id = "edit",
                        Name = "Code & Edit",
                        Slots =
                        [
                            new() { Kind = "keys", Label = "Palette", Icon = "⌘", Hint = $"{mod}+Shift+P", Keys = $"{mod}+Shift+P" },
                            new() { Kind = "keys", Label = "Quick Open", Icon = "🔍", Hint = $"{mod}+P", Keys = $"{mod}+P" },
                            new() { Kind = "keys", Label = "Find", Icon = "🔎", Hint = $"{mod}+F", Keys = $"{mod}+F" },
                            new() { Kind = "keys", Label = "Sidebar", Icon = "◧", Hint = $"{mod}+B", Keys = $"{mod}+B" },
                            new() { Kind = "keys", Label = "Comment", Icon = "💬", Hint = $"{mod}+/", Keys = $"{mod}+/" },
                            new() { Kind = "keys", Label = "Terminal", Icon = "⬛", Hint = $"{mod}+`", Keys = $"{mod}+`" },
                            new() { Kind = "keys", Label = "Format", Icon = "✨", Hint = OperatingSystem.IsMacOS() ? "Shift+Alt+F" : "Shift+Alt+F", Keys = "Shift+Alt+F" },
                            new() { Kind = "keys", Label = "Save", Icon = "💾", Hint = $"{mod}+S", Keys = $"{mod}+S" },
                        ],
                    },
                    new ConsoleSubPage
                    {
                        Id = "debug",
                        Name = "Run & Debug",
                        Slots =
                        [
                            new() { Kind = "keys", Label = "Run / Cont", Icon = "▶️", Hint = "F5", Keys = "F5" },
                            new() { Kind = "keys", Label = "Step Over", Icon = "⤵️", Hint = "F10", Keys = "F10" },
                            new() { Kind = "keys", Label = "Step Into", Icon = "⬇️", Hint = "F11", Keys = "F11" },
                            new() { Kind = "keys", Label = "Stop", Icon = "⏹️", Hint = "Shift+F5", Keys = "Shift+F5" },
                            new() { Kind = "keys", Label = "Breakpoint", Icon = "🔴", Hint = "F9", Keys = "F9" },
                            new() { Kind = "keys", Label = "Go Def", Icon = "🎯", Hint = "F12", Keys = "F12" },
                            new() { Kind = "keys", Label = "Rename", Icon = "✏️", Hint = "F2", Keys = "F2" },
                            new() { Kind = "keys", Label = "Close Tab", Icon = "✖️", Hint = $"{mod}+W", Keys = $"{mod}+W" },
                        ],
                    },
                ],
                Axes =
                [
                    new() { Index = 1, Name = "Undo / Redo", Icon = "↩️", Kind = "keys", PrevKeys = $"{mod}+Z", NextKeys = OperatingSystem.IsMacOS() ? "Cmd+Shift+Z" : "Ctrl+Y", TapAction = $"{mod}+S", Sensitivity = "normal" },
                    new() { Index = 2, Name = "Zoom", Icon = "🔎", Kind = "keys", PrevKeys = $"{mod}+-", NextKeys = $"{mod}+=", TapAction = $"{mod}+0", Sensitivity = "normal" },
                ],
            },
            new ConsoleProfile
            {
                Id = "browser",
                Name = "Browser",
                Icon = "🌐",
                Color = "#f59e0b",
                AppMatch = "chrome|msedge|firefox|safari|brave|arc|opera|vivaldi",
                Enabled = true,
                Columns = 4,
                Rows = 2,
                SubPages =
                [
                    new ConsoleSubPage
                    {
                        Id = "tabs",
                        Name = "Browse",
                        Slots =
                        [
                            new() { Kind = "keys", Label = "Back", Icon = "⬅️", Hint = OperatingSystem.IsMacOS() ? "Cmd+Left" : "Alt+Left", Keys = OperatingSystem.IsMacOS() ? "Cmd+Left" : "Alt+Left" },
                            new() { Kind = "keys", Label = "Forward", Icon = "➡️", Hint = OperatingSystem.IsMacOS() ? "Cmd+Right" : "Alt+Right", Keys = OperatingSystem.IsMacOS() ? "Cmd+Right" : "Alt+Right" },
                            new() { Kind = "keys", Label = "Reload", Icon = "🔄", Hint = $"{mod}+R", Keys = $"{mod}+R" },
                            new() { Kind = "keys", Label = "New Tab", Icon = "➕", Hint = $"{mod}+T", Keys = $"{mod}+T" },
                            new() { Kind = "keys", Label = "Reopen Tab", Icon = "♻️", Hint = $"{mod}+Shift+T", Keys = $"{mod}+Shift+T" },
                            new() { Kind = "keys", Label = "Find", Icon = "🔎", Hint = $"{mod}+F", Keys = $"{mod}+F" },
                            new() { Kind = "keys", Label = "Bookmark", Icon = "⭐", Hint = $"{mod}+D", Keys = $"{mod}+D" },
                            new() { Kind = "keys", Label = "Close Tab", Icon = "✖️", Hint = $"{mod}+W", Keys = $"{mod}+W" },
                        ],
                    },
                ],
                Axes =
                [
                    new() { Index = 1, Name = "Scroll", Icon = "📜", Kind = "keys", PrevKeys = "Up", NextKeys = "Down", TapAction = "Space", Sensitivity = "normal" },
                    new() { Index = 2, Name = "Tabs", Icon = "🗂️", Kind = "keys", PrevKeys = "Ctrl+Shift+Tab", NextKeys = "Ctrl+Tab", TapAction = $"{mod}+T", Sensitivity = "normal" },
                    new() { Index = 3, Name = "Volume", Icon = "🔊", Kind = "system-volume", TapAction = "deck:audio:mute", Sensitivity = "normal" },
                ],
            },
        ];
    }
}
