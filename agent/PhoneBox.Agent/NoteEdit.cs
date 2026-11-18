using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The phone's only write into the saved settings: the text of one Note widget.
/// Kept separate from the HTTP endpoint so the rules can be tested directly.
/// </summary>
public static class NoteEdit
{
    public const int MaxLength = 500;

    public sealed record Result(int Status, string? Error = null, string? Version = null);

    public static Result Apply(ConfigStore cfg, string pageId, string widgetId, string? text, long? expectedVersion = null)
    {
        if (text is null) return new(400, "text expected");
        if (text.Length > MaxLength) return new(400, "text too long");

        // Work on a copy: cfg.Get() is the shared, cached config, and a save may still refuse.
        var version = expectedVersion ?? cfg.Version;
        var json = JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts);
        var copy = JsonSerializer.Deserialize<AgentConfig>(json, ConfigStore.WriteOpts);
        var page = copy?.Pages?.FirstOrDefault(p => p.Id == pageId);
        var widget = page?.Widgets?.FirstOrDefault(w => w.Id == widgetId);
        if (widget is null) return new(404, "widget not found");
        if (widget.Type != "note") return new(400, "option not allowed");

        widget.Set("text", text);
        var err = cfg.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts), version, out var savedVersion);
        if (err == ConfigStore.Conflict) return new(409, err);
        return err is null ? new(200, null, savedVersion.ToString(CultureInfo.InvariantCulture)) : new(400, err);
    }
}
