using System.Collections.Concurrent;

namespace PhoneBox.Agent;

public sealed record PhoneViewport(int Width, int Height);
public sealed record PhoneSeen(string Id, string Model, long LastSeen, string Via, string? WebView = null, PhoneViewport? Viewport = null);

/// <summary>
/// The phones that talk to this agent (they send X-PhoneBox-Device / X-PhoneBox-Model), so the settings can give each
/// one its own pages. Remembered while the agent runs.
/// </summary>
public static class PhoneRegistry
{
    private static readonly ConcurrentDictionary<string, PhoneSeen> Seen = new();
    private static readonly ConcurrentDictionary<string, string?> Warned = new();
    private static readonly Lock WarningGate = new();

    public static string? Touch(string? id, string? model, string via, string? webView = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit)) return null;
        model = string.IsNullOrWhiteSpace(model) ? "Android phone" : new string(model.Where(c => c >= ' ' && c <= '~').Take(40).ToArray());
        var normalized = WebViewDoctor.Normalize(webView);
        var current = new PhoneSeen(id, model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), via, normalized);
        Seen.AddOrUpdate(id, current, (_, previous) => current with { Viewport = previous.Viewport });
        var problem = WebViewDoctor.Diagnose(normalized, model);
        lock (WarningGate)
        {
            var prev = Warned.TryGetValue(id, out var old) ? old : null;
            Warned[id] = problem;
            return problem != prev ? problem : null;
        }
    }

    internal static void SetViewport(string id, int? width, int? height)
    {
        if (width is not (>= 100 and <= 8192) || height is not (>= 100 and <= 8192)
            || !Seen.TryGetValue(id, out var current)) return;
        var viewport = new PhoneViewport(width.Value, height.Value);
        Seen.AddOrUpdate(id, current, (_, previous) => previous with { Viewport = viewport });
    }

    public static IReadOnlyCollection<PhoneSeen> All() => Seen.Values.ToList();
}
