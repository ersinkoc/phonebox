using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>A page template: a ready-made widget layout the settings UI (and migration) can start from.</summary>
public sealed class TemplateDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Desc { get; set; } = "";
    public List<WidgetConfig> Widgets { get; set; } = [];
}

/// <summary>The widget types and page templates shipped with the agent (widgets.json, templates.json) plus active plugins.</summary>
public static partial class Catalog
{
    public const int Cols = 12;
    public const int Rows = 6;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static readonly string WidgetsJson = Read("widgets.json");
    public static readonly string TemplatesJson = Read("templates.json");

    public static readonly IReadOnlyList<TemplateDef> Templates =
        JsonSerializer.Deserialize<List<TemplateDef>>(TemplatesJson, Opts) ?? [];

    private static readonly Dictionary<string, (int MinW, int MinH)> Sizes = LoadSizes();

    public static bool IsPluginWidgetType(string? type) =>
        !string.IsNullOrEmpty(type) && PluginWidgetRegex().IsMatch(type);

    public static string? PluginIdOf(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        var m = PluginWidgetRegex().Match(type);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsWidget(string type) => Sizes.ContainsKey(type) || IsPluginWidgetType(type);

    public static (int MinW, int MinH) MinSize(string type)
    {
        if (Sizes.TryGetValue(type, out var s)) return s;
        if (IsPluginWidgetType(type)) return PluginService.GetRegisteredMinSize(type);
        return (1, 1);
    }

    public static string CombinedJson(PluginService plugins)
    {
        var extra = plugins.ActiveCatalogWidgetsJson();
        var widgets = string.IsNullOrEmpty(extra)
            ? WidgetsJson
            : WidgetsJson.TrimEnd().TrimEnd(']') + "," + extra + "]";
        return $"{{\"cols\":{Cols},\"rows\":{Rows},\"widgets\":{widgets},\"templates\":{TemplatesJson}}}";
    }

    public static TemplateDef? Template(string? id) => id is null ? null : Templates.FirstOrDefault(t => t.Id == id);

    private static Dictionary<string, (int, int)> LoadSizes()
    {
        using var doc = JsonDocument.Parse(WidgetsJson);
        return doc.RootElement.EnumerateArray().ToDictionary(
            w => w.GetProperty("type").GetString()!,
            w => (w.GetProperty("minW").GetInt32(), w.GetProperty("minH").GetInt32()));
    }

    private static string Read(string name)
    {
        using var s = AppPaths.Resource(name) ?? throw new InvalidOperationException($"missing resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [GeneratedRegex(@"^plugin:([a-z0-9][a-z0-9-]{0,47})(?:\.([a-z0-9][a-z0-9-]{0,47}))?$")]
    private static partial Regex PluginWidgetRegex();
}
