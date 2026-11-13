using System.Text.Json;

namespace PhoneBox.Agent;

internal static class StackWidgets
{
    internal sealed record Child(string Id, string Type, bool Enabled, Dictionary<string, JsonElement>? Options);
    public static IReadOnlyList<Child> Children(WidgetConfig parent)
    {
        if (parent.Type != "stack" || parent.Options is null || !parent.Options.TryGetValue("widgets", out var raw)) return [];
        try
        {
            if (raw.ValueKind == JsonValueKind.String) raw = JsonSerializer.Deserialize<JsonElement>(raw.GetString() ?? "[]");
            if (raw.ValueKind != JsonValueKind.Array) return [];
            return raw.EnumerateArray().Take(12).Where(x => x.ValueKind == JsonValueKind.Object
                && x.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is not ("stack" or "command") && Catalog.IsWidget(type.GetString()!))
                .Select((x, index) => new Child(x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()! : "card-" + index, x.GetProperty("type").GetString()!,
                    (!x.TryGetProperty("enabled", out var on) || on.ValueKind != JsonValueKind.False)
                        && (!x.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False),
                    x.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options) : null
                )).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static Dictionary<string, JsonElement>? PublicOptions(WidgetConfig w)
    {
        if (w.Type == "command") return w.Options?.Where(o => o.Key is "title" or "interval").ToDictionary();
        if (Catalog.IsPluginWidgetType(w.Type)) return PluginService.FilterPublicWidgetOptions(w.Type, w.Options);
        if (w.Type != "stack" || w.Options is null) return w.Options;
        var options = w.Options.ToDictionary();
        if (options.ContainsKey("widgets")) options["widgets"] = JsonSerializer.SerializeToElement(Children(w)
            .Select(child => new
            {
                id = child.Id,
                type = child.Type,
                enabled = child.Enabled,
                options = Catalog.IsPluginWidgetType(child.Type)
                    ? PluginService.FilterPublicWidgetOptions(child.Type, child.Options)
                    : child.Options
            }));
        return options;
    }
}
