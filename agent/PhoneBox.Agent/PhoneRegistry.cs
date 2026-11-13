using System.Collections.Concurrent;

namespace PhoneBox.Agent;

public sealed record PhoneSeen(string Id, string Model, long LastSeen, string Via, string? WebView = null);

/// <summary>
/// The phones that talk to this agent (they send X-PhoneBox-Device / X-PhoneBox-Model), so the settings can give each
/// one its own pages. Remembered while the agent runs.
/// </summary>
public static class PhoneRegistry
{
    private static readonly ConcurrentDictionary<string, PhoneSeen> Seen = new();
    private static readonly ConcurrentDictionary<string, string?> Warned = new();

    public static string? Touch(string? id, string? model, string via, string? webView = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit)) return null;
        model = string.IsNullOrWhiteSpace(model) ? "Android phone" : new string(model.Where(c => c >= ' ' && c <= '~').Take(40).ToArray());
        var normalized = WebViewDoctor.Normalize(webView);
        Seen[id] = new PhoneSeen(id, model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), via, normalized);
        var problem = WebViewDoctor.Diagnose(normalized, model);
        var prev = Warned.TryGetValue(id, out var old) ? old : null;
        Warned[id] = problem;
        return problem != prev ? problem : null;
    }

    public static IReadOnlyCollection<PhoneSeen> All() => Seen.Values.ToList();
}
