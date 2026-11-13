using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Per-provider cost and token usage over <see cref="QuotaOverview.WindowDays"/>.</summary>
public sealed record QuotaSubscriptionCost(string Id, string Name, long Tokens, double Cost,
    string CurrencyCode = "USD", double SessionCost = 0, long SessionTokens = 0);

/// <summary>Per-model cost and token usage over <see cref="QuotaOverview.WindowDays"/>.</summary>
public sealed record QuotaTopModelCost(string ModelName, string ProviderId, string ProviderName,
    double Cost, long Tokens, string CurrencyCode = "USD");

/// <summary>Aggregated CodexBar cost overview over one stated rolling window (<see cref="WindowDays"/>).</summary>
public sealed record QuotaOverview(long TotalTokens, double TotalCost, string CurrencyCode, int WindowDays,
    int ProviderCount, long UpdatedAt, IReadOnlyList<QuotaSubscriptionCost> Subscriptions,
    IReadOnlyList<QuotaTopModelCost> TopModels);

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

    internal const long OverviewStaleMs = 3_600_000;

    /// <summary>Returns the cached cost overview only while enabled and within the 1-hour freshness window.</summary>
    internal static QuotaOverview? SelectOverview(QuotaOverview? overview, bool enabled, long now) =>
        enabled && overview is { UpdatedAt: var updated } && now - updated <= OverviewStaleMs ? overview : null;

    /// <summary>
    /// Runs `codexbar cost --days 30 --format json` and parses the cost overview.
    /// </summary>
    public static async Task<QuotaOverview?> ReadCostAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path);
        start.ArgumentList.Add("cost");
        start.ArgumentList.Add("--days");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add("json");
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(25), ct);
        return ParseCost(result.Output, result.ExitCode);
    }

    /// <summary>
    /// Parses CodexBar CLI cost JSON into an overview over one consistent window and primary currency.
    /// </summary>
    public static QuotaOverview? ParseCost(string json, int exitCode = 0, DateOnly? referenceDate = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a CodexBar cost array");

        var items = new List<(JsonElement Item, string RawId, string Id)>();
        int? minWindow = null;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var rawId = Str(item, "provider")?.Trim();
            if (string.IsNullOrEmpty(rawId) || string.Equals(rawId, "cli", StringComparison.OrdinalIgnoreCase)) continue;
            var id = rawId.ToLowerInvariant();
            items.Add((item, rawId, id));

            var totals = Prop(item, "totals");
            var daily = Prop(item, "daily");
            var hasData = (Num(item, "last30DaysCostUSD") ?? Num(totals, "totalCost") ?? 0) > 0
                || (Num(item, "last30DaysTokens") ?? Num(totals, "totalTokens") ?? 0) > 0
                || (daily.ValueKind == JsonValueKind.Array && daily.GetArrayLength() > 0);
            if (!hasData) continue;
            var itemWin = Num(item, "historyDays") is { } hd && hd >= 1 ? Math.Min((int)hd, 30) : 30;
            minWindow = minWindow is { } cur ? Math.Min(cur, itemWin) : itemWin;
        }

        var windowDays = minWindow ?? 30;
        var subs = new List<QuotaSubscriptionCost>();
        var models = new Dictionary<(string Id, string Model, string Currency), (string ModelName, string ProviderId, string ProviderName, double Cost, long Tokens, string CurrencyCode)>();

        foreach (var (item, rawId, id) in items)
        {
            var name = DisplayName(rawId);
            var currency = Str(item, "currencyCode")?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(currency)) currency = "USD";

            var totals = Prop(item, "totals");
            var dailyEl = Prop(item, "daily");
            var parsedDaily = new List<(DateOnly Date, double Cost, long Tokens, JsonElement Breakdowns)>();
            if (dailyEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dailyEl.EnumerateArray())
                {
                    if (Str(d, "date") is not { } ds || !DateOnly.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        continue;
                    parsedDaily.Add((date, Num(d, "totalCost") ?? 0, (long)(Num(d, "totalTokens") ?? 0), Prop(d, "modelBreakdowns")));
                }
            }

            var anchorDate = referenceDate
                ?? (Date(Str(item, "updatedAt")) is { } updMs ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(updMs).UtcDateTime) : (DateOnly?)null)
                ?? (parsedDaily.Count > 0 ? (DateOnly?)parsedDaily.Max(x => x.Date) : null)
                ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var windowStart = anchorDate.AddDays(-(windowDays - 1));
            var filteredDaily = parsedDaily.Where(d => d.Date >= windowStart && d.Date <= anchorDate).ToList();
            var hasFilteredDaily = filteredDaily.Count > 0;

            double dailyCost = 0;
            long dailyTokens = 0;
            var itemModels = new List<(string ModelName, double Cost, long Tokens)>();
            foreach (var d in filteredDaily)
            {
                dailyCost += d.Cost;
                dailyTokens += d.Tokens;
                if (d.Breakdowns.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in d.Breakdowns.EnumerateArray())
                {
                    var mName = Str(m, "modelName")?.Trim();
                    if (string.IsNullOrEmpty(mName)) continue;
                    itemModels.Add((mName, Math.Max(0, Num(m, "cost") ?? 0), Math.Max(0, (long)(Num(m, "totalTokens") ?? 0))));
                }
            }

            int? histDays = Num(item, "historyDays") is { } h && h >= 1 ? (int)h : null;
            double? rawCost;
            long? rawTok;
            if (histDays == windowDays || (histDays is null && windowDays == 30))
            {
                rawCost = Num(item, "last30DaysCostUSD") ?? Num(totals, "totalCost") ?? (hasFilteredDaily ? dailyCost : null);
                rawTok = (Num(item, "last30DaysTokens") ?? Num(totals, "totalTokens")) is { } t ? (long)t : (hasFilteredDaily ? dailyTokens : null);
            }
            else if (histDays > 30 && windowDays == 30)
            {
                rawCost = Num(item, "last30DaysCostUSD") ?? (hasFilteredDaily ? dailyCost : null);
                rawTok = Num(item, "last30DaysTokens") is { } t ? (long)t : (hasFilteredDaily ? dailyTokens : null);
            }
            else
            {
                if (!hasFilteredDaily) continue;
                rawCost = dailyCost;
                rawTok = dailyTokens;
            }

            if (rawCost is null && rawTok is null) continue;
            var cost = Math.Max(0, rawCost ?? 0);
            var tok = Math.Max(0, rawTok ?? 0);
            if (cost <= 0 && tok <= 0 && !hasFilteredDaily) continue;

            var sessCost = Math.Max(0, Num(item, "sessionCostUSD") ?? 0);
            var sessTok = Math.Max(0, (long)(Num(item, "sessionTokens") ?? 0));
            subs.Add(new QuotaSubscriptionCost(id, name, tok, cost, currency, sessCost, sessTok));

            foreach (var (mName, mCost, mTok) in itemModels)
            {
                var key = (id, mName.ToLowerInvariant(), currency);
                if (!models.TryGetValue(key, out var cur))
                    cur = (mName, id, name, 0, 0, currency);
                models[key] = (cur.ModelName, cur.ProviderId, cur.ProviderName, cur.Cost + mCost, cur.Tokens + mTok, currency);
            }
        }

        if (subs.Count == 0)
        {
            if (exitCode != 0) throw new IOException("CodexBar cost exited with code " + exitCode);
            return null;
        }

        var overviewCurrency = subs
            .GroupBy(s => s.CurrencyCode, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => string.Equals(g.Key, "USD", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => g.Sum(s => s.Cost))
            .Select(g => g.Key)
            .First();

        var totalCost = subs.Where(s => s.CurrencyCode == overviewCurrency).Sum(s => s.Cost);
        var totalTokens = subs.Where(s => s.CurrencyCode == overviewCurrency).Sum(s => s.Tokens);
        var sortedSubs = subs
            .OrderByDescending(s => s.CurrencyCode == overviewCurrency)
            .ThenByDescending(s => s.Cost)
            .ThenByDescending(s => s.Tokens)
            .ToList();
        var topModels = models.Values
            .Where(m => m.CurrencyCode == overviewCurrency)
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.Tokens)
            .Take(6)
            .Select(m => new QuotaTopModelCost(m.ModelName, m.ProviderId, m.ProviderName, m.Cost, m.Tokens, m.CurrencyCode))
            .ToList();

        return new QuotaOverview(totalTokens, totalCost, overviewCurrency, windowDays, sortedSubs.Count,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sortedSubs, topModels);
    }

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
