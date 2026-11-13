using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

internal static class CodexBar
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "Claude",
        ["codex"] = "Codex",
        ["antigravity"] = "Antigravity",
        ["cursor"] = "Cursor",
        ["copilot"] = "Copilot",
        ["gemini"] = "Gemini",
        ["windsurf"] = "Windsurf",
        ["zai"] = "z.ai",
        ["openrouter"] = "OpenRouter",
        ["alibaba"] = "Alibaba Coding Plan",
        ["alibabatokenplan"] = "Alibaba Token Plan",
        ["deepseek"] = "DeepSeek",
        ["perplexity"] = "Perplexity",
        ["warp"] = "Warp",
        ["kimi"] = "Kimi",
        ["longcat"] = "LongCat",
        ["minimax"] = "MiniMax",
        ["moonshot"] = "Moonshot",
        ["amp"] = "Amp",
        ["augment"] = "Augment",
        ["factory"] = "Factory",
        ["jetbrains"] = "JetBrains AI",
        ["kilo"] = "Kilo",
        ["kiro"] = "Kiro",
        ["ollama"] = "Ollama",
        ["opencode"] = "OpenCode",
        ["opencodego"] = "OpenCode Go",
        ["opencodex"] = "OpenCodex",
        ["synthetic"] = "Synthetic",
        ["vertexai"] = "Vertex AI",
        ["abacus"] = "Abacus",
    };

    /// <summary>Maps a CodexBar provider identifier to its display name.</summary>
    internal static string DisplayName(string rawId) => Names.TryGetValue(rawId, out var known) ? known : FormatName(rawId);

    /// <summary>
    /// Runs `codexbar usage --json` and parses its provider quota snapshot.
    /// </summary>
    public static async Task<IReadOnlyList<QuotaProvider>> ReadAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path);
        start.ArgumentList.Add("usage"); start.ArgumentList.Add("--json");
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(25), ct);
        return Parse(result.Output, result.ExitCode);
    }

    /// <summary>
    /// Parses CodexBar CLI JSON output into normalized <see cref="QuotaProvider"/> records.
    /// </summary>
    public static IReadOnlyList<QuotaProvider> Parse(string json, int exitCode = 0)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a CodexBar provider array");
        var providers = new List<QuotaProvider>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var rawId = Str(item, "provider")?.Trim();
            if (string.IsNullOrEmpty(rawId) || string.Equals(rawId, "cli", StringComparison.OrdinalIgnoreCase)) continue;
            var id = rawId.ToLowerInvariant();
            var name = DisplayName(rawId);
            var usage = Prop(item, "usage");
            var itemPace = Prop(item, "pace");
            var error = Prop(item, "error");
            var message = error.ValueKind == JsonValueKind.String ? error.GetString() : Str(error, "message");
            if (error.ValueKind == JsonValueKind.Object && message is null) message = "CodexBar provider unavailable";
            var limits = new List<QuotaLimit>();
            var labels = Prop(item, "rateWindowLabels");
            void Add(JsonElement window, string label, JsonElement providerPace = default)
            {
                if (Prop(window, "isSyntheticPlaceholder").ValueKind == JsonValueKind.True || Num(window, "usedPercent") is not { } percent) return;
                var minutes = Num(window, "windowMinutes");
                var title = Str(labels, label) ?? (label is "primary" or "secondary" or "tertiary"
                    ? minutes switch { 300 => "5-hour session", 10080 => "Weekly", > 0 => string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:0.#}-hour"), _ => label switch { "primary" => "Primary limit", "secondary" => "Secondary limit", _ => "Additional limit" } } : label);
                percent = Math.Max(0, percent);
                var resetDesc = Str(window, "resetDescription")?.Trim();
                if (string.IsNullOrEmpty(resetDesc)) resetDesc = null;
                var deltaPct = Num(providerPace, "deltaPercent");
                var expectedPct = Num(providerPace, "expectedUsedPercent");
                var onTrack = Bool(providerPace, "willLastToReset");
                limits.Add(new QuotaLimit(title, percent, Date(Str(window, "resetsAt")),
                    minutes is > 0 and <= 525600 ? (long)(minutes.Value * 60) : null,
                    percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal",
                    resetDesc, deltaPct, expectedPct, onTrack));
            }
            Add(Prop(usage, "primary"), "primary", Prop(itemPace, "primary"));
            Add(Prop(usage, "secondary"), "secondary", Prop(itemPace, "secondary"));
            Add(Prop(usage, "tertiary"), "tertiary", Prop(itemPace, "tertiary"));
            var extra = Prop(usage, "extraRateWindows");
            if (extra.ValueKind == JsonValueKind.Array)
                foreach (var w in extra.EnumerateArray()) Add(Prop(w, "window"), Str(w, "title") ?? "Additional limit");
            var plan = Str(usage, "loginMethod") ?? Str(Prop(usage, "planObservation"), "rawPlan") ?? name;
            var credits = Num(Prop(item, "credits"), "remaining");
            providers.Add(new QuotaProvider(id, name, plan, limits, credits is { } c ? string.Create(CultureInfo.InvariantCulture, $"Credits: {c:0.##}") : null,
                Date(Str(usage, "updatedAt")) ?? Date(Str(item, "updatedAt")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                message ?? (limits.Count == 0 ? "CodexBar returned no measured limits" : null), Source: "codexbar"));
        }
        if (exitCode != 0 && providers.Count == 0) throw new IOException("CodexBar exited with code " + exitCode);
        return providers.GroupBy(p => p.Id).Select(g => g.First()).ToList();
    }

    // Cached limits are usable only while the provider's latest result is healthy and recent.
    /// <summary>Chooses between local direct quota readings and CodexBar output for a provider.</summary>
    internal static QuotaProvider? Select(QuotaProvider? local, QuotaProvider? external, long now)
    {
        if (external is { Error: null, Limits.Count: > 0, UpdatedAt: { } updated } && now - updated <= 600_000)
            return external with { Codex = local?.Codex };
        if (local is { Error: null, Limits.Count: > 0 }) return local;
        if (external is not null) return external with { Error = external.Error ?? "CodexBar data is stale", Codex = local?.Codex };
        return local;
    }

    /// <summary>Formats an unknown provider identifier as a capitalized label.</summary>
    private static string FormatName(string id) => char.ToUpperInvariant(id[0]) + id[1..];
    /// <summary>Returns the object property <paramref name="key"/> or a default element.</summary>
    private static JsonElement Prop(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    /// <summary>Returns a string property value when present.</summary>
    private static string? Str(JsonElement e, string key) => Prop(e, key) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>Returns a finite numeric property value when present.</summary>
    private static double? Num(JsonElement e, string key) => Prop(e, key) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    /// <summary>Returns a boolean property value when present.</summary>
    private static bool? Bool(JsonElement e, string key) => Prop(e, key) is var value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    /// <summary>Parses an ISO-8601 timestamp string into Unix milliseconds.</summary>
    private static long? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUnixTimeMilliseconds() : null;
}
