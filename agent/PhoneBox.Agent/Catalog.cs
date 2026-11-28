using System.Text.Json;

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

/// <summary>The widget types and page templates shipped with the agent (widgets.json, templates.json).</summary>
public static class Catalog
{
    public const int Cols = 12;
    /// <summary>Rows on a phone. A tablet shows more (see the panel's grid.js); <see cref="MaxRows"/> is the limit.</summary>
    public const int Rows = 6;
    /// <summary>Tallest page that can be saved. Widgets below row <see cref="Rows"/> show on a tablet and stay off a phone.</summary>
    public const int MaxRows = 18;
    /// <summary>The grid of a page arranged for upright in Settings › Pages (the panel's grid.js UPRIGHT_COLS / UPRIGHT_ROWS).</summary>
    public const int UprightCols = 6;
    public const int UprightRows = 12;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static readonly string WidgetsJson = Read("widgets.json");
    public static readonly string TemplatesJson = Read("templates.json");

    public static readonly IReadOnlyList<TemplateDef> Templates =
        JsonSerializer.Deserialize<List<TemplateDef>>(TemplatesJson, Opts) ?? [];

    private static readonly Dictionary<string, (int MinW, int MinH)> Sizes = LoadSizes();

    public static bool IsWidget(string type) => Sizes.ContainsKey(type);

    public static (int MinW, int MinH) MinSize(string type) => Sizes.TryGetValue(type, out var s) ? s : (1, 1);

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
}
