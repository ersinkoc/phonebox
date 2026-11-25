using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Reads the Grok CLI's saved sign-in and the credit-window JSON from
/// <c>cli-chat-proxy.grok.com</c>. The token is only parsed here. Nothing in this type refreshes it,
/// logs it, or puts it on a <see cref="QuotaProvider"/>.
/// </summary>
internal static class GrokQuota
{
    internal const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    internal const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";
    /// <summary>A token this close to <c>expires_at</c> is not sent. The Grok CLI refreshes it.</summary>
    internal const long TokenMarginMs = 60_000;

    internal enum AuthKind { None, Expired, Ready }

    internal readonly record struct Auth(AuthKind Kind, SignIn? SignIn);

    /// <summary>A usable bearer. <paramref name="PlanFallback"/> is only the OIDC label, not the billed tier.</summary>
    internal sealed record SignIn(string Token, string PlanFallback);

    internal readonly record struct Reading(double? Percent, long? ResetsAt, long? WindowSec, string Label,
        string? Tier, string? Extra, bool HasPrepaid);

    /// <summary><c>GROK_HOME/auth.json</c> when that variable is set, otherwise <c>~/.grok/auth.json</c>.</summary>
    internal static string DefaultAuthPath()
    {
        var root = Environment.GetEnvironmentVariable("GROK_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        return Path.Combine(root, "auth.json");
    }

    /// <summary>
    /// Picks the SuperGrok entry (<c>https://auth.x.ai::…</c>), then the legacy
    /// <c>https://accounts.x.ai/sign-in</c> entry. An expired entry is not returned.
    /// A management key (<c>xai-</c>) or a cookie header is ignored.
    /// </summary>
    internal static Auth ReadAuth(string json, long nowMs)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new Auth(AuthKind.None, null); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new Auth(AuthKind.None, null);
            SignIn? preferred = null, legacy = null;
            var expired = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var where = prop.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal) ? 1
                    : prop.Name == "https://accounts.x.ai/sign-in" ? 2 : 0;
                if (where == 0) continue;
                switch (Classify(prop.Value, nowMs))
                {
                    case EntryKind.Ready when where == 1 && preferred is null:
                        preferred = ToSignIn(prop.Value);
                        break;
                    case EntryKind.Ready when where == 2 && legacy is null:
                        legacy = ToSignIn(prop.Value);
                        break;
                    case EntryKind.Expired:
                        expired = true;
                        break;
                    case EntryKind.Skip:
                    case EntryKind.Ready:
                        break;
                }
            }
            if ((preferred ?? legacy) is { } chosen) return new Auth(AuthKind.Ready, chosen);
            return new Auth(expired ? AuthKind.Expired : AuthKind.None, null);
        }
    }

    /// <summary>
    /// The credit window from <c>/v1/billing?format=credits</c>. Null when the body has no percent,
    /// no reset, and no prepaid balance. A percent is clamped to 0–100. Product shares are kept only
    /// when they add up to the raw <c>creditUsagePercent</c> (within one point). Included credits are
    /// never turned into dollars. <paramref name="nowMs"/> is only used to reject a period that has not started.
    /// </summary>
    internal static Reading? ParseCredits(string json, long nowMs)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (Prop(doc.RootElement, "config") is not { ValueKind: JsonValueKind.Object } config) return null;

            double? raw = Finite(Prop(config, "creditUsagePercent"));
            double? percent = raw;
            if (percent is null && Finite(Prop(Prop(config, "onDemandCap"), "val")) is { } cap && cap > 0
                && Finite(Prop(Prop(config, "onDemandUsed"), "val")) is { } used)
                percent = used / cap * 100;

            var (resetsAt, windowSec) = Period(config, nowMs);
            var prepaid = PrepaidUsd(Prop(config, "prepaidBalance"));
            if (percent is null && resetsAt is null && prepaid is null) return null;

            var tier = PlanName(Str(config, "subscriptionTier") ?? Str(config, "subscription_tier")
                ?? Str(doc.RootElement, "subscriptionTier") ?? Str(doc.RootElement, "subscription_tier"));
            var products = raw is { } reported ? Products(Prop(config, "productUsage"), reported) : [];
            return new Reading(percent is { } p ? Math.Clamp(p, 0, 100) : null, resetsAt, windowSec,
                WindowLabel(windowSec), tier, Describe(products, prepaid), prepaid is not null);
        }
    }

    /// <summary>The billed plan from <c>/v1/settings</c> (<c>subscription_tier_display</c>).</summary>
    internal static string? ParseSettingsPlan(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return PlanName(Str(doc.RootElement, "subscription_tier_display") ?? Str(doc.RootElement, "subscriptionTierDisplay"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>SuperGrok and SuperGrok Heavy, whatever spacing the payload uses. Anything else is kept as written.</summary>
    internal static string? PlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var compact = new string(raw.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        return compact switch
        {
            "supergrokheavy" or "heavy" => "SuperGrok Heavy",
            "supergrok" => "SuperGrok",
            _ => raw.Trim(),
        };
    }

    /// <summary>Weekly or monthly when the measured period is that long. Time left until reset is not a duration.</summary>
    internal static string WindowLabel(long? windowSec)
    {
        if (windowSec is not { } sec || sec <= 0) return "Credits";
        var days = sec / 86400d;
        if (days is >= 6 and <= 8) return "Weekly";
        if (days is >= 27 and <= 32) return "Monthly";
        return "Credits";
    }

    private enum EntryKind { Skip, Expired, Ready }

    private static EntryKind Classify(JsonElement entry, long nowMs)
    {
        var token = Str(entry, "key");
        if (string.IsNullOrEmpty(token) || Rejected(token)) return EntryKind.Skip;
        if (IsoMs(Str(entry, "expires_at") ?? Str(entry, "expiresAt")) is not { } exp) return EntryKind.Skip;
        return exp - nowMs < TokenMarginMs ? EntryKind.Expired : EntryKind.Ready;
    }

    private static SignIn? ToSignIn(JsonElement entry)
    {
        var token = Str(entry, "key");
        if (string.IsNullOrEmpty(token)) return null;
        var mode = Str(entry, "auth_mode") ?? Str(entry, "authMode");
        var plan = string.Equals(mode, "oidc", StringComparison.OrdinalIgnoreCase) ? "SuperGrok"
            : string.IsNullOrWhiteSpace(mode) ? "Grok" : mode.Trim();
        return new SignIn(token, plan);
    }

    /// <summary>xAI management keys and cookie headers are a different account surface. They are not this sign-in.</summary>
    private static bool Rejected(string token) =>
        token.StartsWith("xai-", StringComparison.Ordinal)
        || token.Contains("Cookie:", StringComparison.OrdinalIgnoreCase)
        || token.Contains(';');

    private static (long? ResetsAt, long? WindowSec) Period(JsonElement config, long nowMs)
    {
        var current = Prop(config, "currentPeriod");
        var currentEnd = IsoMs(Str(current, "end"));
        var end = currentEnd ?? IsoMs(Str(config, "billingPeriodEnd"));
        // Match the start to the end that was used. Mixing the two periods would invent a duration.
        var start = currentEnd is null ? IsoMs(Str(config, "billingPeriodStart")) : IsoMs(Str(current, "start"));
        long? window = null;
        if (start is { } s && end is { } e && e > s && s <= nowMs)
        {
            var minutes = (e - s) / 60_000;
            if (minutes > 0) window = minutes * 60;
        }
        return (end, window);
    }

    /// <summary>USD cents in <c>val</c>. An empty object is a confirmed zero. A missing or bad value stays unknown.</summary>
    private static double? PrepaidUsd(JsonElement balance)
    {
        if (balance.ValueKind != JsonValueKind.Object) return null;
        if (!balance.TryGetProperty("val", out var val))
            return balance.EnumerateObject().Any() ? null : 0;
        long? cents = val.ValueKind switch
        {
            JsonValueKind.Number when val.TryGetInt64(out var n) => n,
            JsonValueKind.Number when val.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && Math.Abs(d - Math.Round(d)) < 0.001 => (long)Math.Round(d),
            JsonValueKind.String when long.TryParse(val.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
        return cents is >= 0 ? cents.Value / 100d : null;
    }

    /// <summary>
    /// Shares of the same credit pool. One bad entry, or a sum that misses the raw percent by more than a point,
    /// drops the whole breakdown so a partial list cannot look complete.
    /// </summary>
    private static List<(string Name, double Percent)> Products(JsonElement array, double rawPercent)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        var all = new List<(string Name, double Percent)>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return [];
            var name = Str(item, "product")?.Trim();
            var pct = Finite(Prop(item, "usagePercent"));
            if (string.IsNullOrEmpty(name) || pct is not { } p || p < 0) return [];
            all.Add((ProductName(name), p));
        }
        if (Math.Abs(all.Sum(p => p.Percent) - rawPercent) > 1) return [];
        return all.Where(p => p.Percent > 0).OrderByDescending(p => p.Percent).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static string ProductName(string product) => product switch
    {
        "GrokBuild" => "Grok Build",
        "GrokChat" => "Grok Chat",
        "GrokImagine" => "Grok Imagine",
        "GrokAppBuilder" => "Grok App Builder",
        _ => product,
    };

    private static string? Describe(IReadOnlyList<(string Name, double Percent)> products, double? prepaidUsd)
    {
        var parts = new List<string>();
        if (products.Count > 0)
            parts.Add(string.Join(" · ", products.Select(p =>
                string.Create(CultureInfo.InvariantCulture, $"{p.Name} {p.Percent:0.#}%"))));
        if (prepaidUsd is { } usd)
            parts.Add("Prepaid $" + usd.ToString("0.00", CultureInfo.InvariantCulture));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static long? IsoMs(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUnixTimeMilliseconds() : null;

    private static double? Finite(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    private static JsonElement Prop(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;

    private static string? Str(JsonElement e, string key) =>
        Prop(e, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
