using System.Globalization;

namespace PhoneBox.Agent;

/// <summary>
/// Parsed Android WebView package and version (from the <c>X-PhoneBox-WebView</c> header, e.g.
/// <c>com.google.android.webview/134.0.6998.135</c>).
/// </summary>
public sealed record WebViewInfo(string Package, string Version, int Major);

/// <summary>
/// Flags phones running an older Android WebView that lacks CSS container queries (<c>cqw</c>/<c>cqh</c>,
/// Chromium 105+), which breaks widget scaling on the panel, and explains how to update it.
/// </summary>
public static class WebViewDoctor
{
    /// <summary>First Chromium milestone with CSS container query units used for widget scaling.</summary>
    public const int MinMajor = 105;

    /// <summary>A phone report older than this does not raise the active connection banner.</summary>
    public const long FreshMs = 2 * 60_000;

    /// <summary>Parses a validated <c>package/version</c> header string, or null when missing or malformed.</summary>
    public static WebViewInfo? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 80) return null;
        var s = raw.Trim();
        var slash = s.IndexOf('/');
        if (slash <= 0 || slash != s.LastIndexOf('/') || slash == s.Length - 1) return null;

        var pkg = s[..slash];
        var ver = s[(slash + 1)..];
        if (!IsValidPackage(pkg) || !IsValidVersion(ver)) return null;

        var digits = 0;
        while (digits < ver.Length && char.IsAsciiDigit(ver[digits])) digits++;
        if (digits == 0 || (digits < ver.Length && ver[digits] is not ('.' or '-' or '+'))) return null;
        if (!int.TryParse(ver[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || major is < 1 or > 999) return null;

        return new WebViewInfo(pkg, ver, major);
    }

    /// <summary>Returns a canonical <c>package/version</c> string, or null when malformed.</summary>
    public static string? Normalize(string? raw) => Parse(raw) is { } w ? $"{w.Package}/{w.Version}" : null;

    /// <summary>Actionable update message when the WebView is older than Chromium 105, or null otherwise.</summary>
    public static string? Diagnose(string? webView, string? model = null)
    {
        if (Parse(webView) is not { } w || w.Major >= MinMajor) return null;
        var who = string.IsNullOrWhiteSpace(model) ? "The phone" : model.Trim();
        var how = w.Package.StartsWith("com.android.chrome", StringComparison.OrdinalIgnoreCase)
            || w.Package.StartsWith("com.chrome.", StringComparison.OrdinalIgnoreCase)
            ? "Update Google Chrome from Google Play (it provides WebView on this device)."
            : w.Package.StartsWith("com.google.android.webview", StringComparison.OrdinalIgnoreCase)
                ? "Update Android System WebView from Google Play."
                : "Install or update Android System WebView (or Google Chrome), then select it in Developer options › WebView implementation.";
        return $"{who} has an outdated WebView ({w.Major}, {w.Package}); widget scaling needs Chromium {MinMajor}+. {how}";
    }

    /// <summary>Finds the most recently seen active phone with an outdated WebView, or null.</summary>
    public static string? DiagnoseRecent(IEnumerable<PhoneSeen> phones, long now)
    {
        foreach (var p in phones.OrderByDescending(x => x.LastSeen))
        {
            if (now - p.LastSeen is < 0 or > FreshMs) continue;
            if (Diagnose(p.WebView, p.Model) is { } problem) return problem;
        }
        return null;
    }

    private static bool IsValidPackage(string pkg)
    {
        if (pkg.Length is < 3 or > 60 || !char.IsAsciiLetter(pkg[0]) || !pkg.Contains('.')) return false;
        foreach (var c in pkg)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) return false;
        return true;
    }

    private static bool IsValidVersion(string ver)
    {
        if (ver.Length is < 1 or > 32) return false;
        foreach (var c in ver)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '+' or '-')) return false;
        return true;
    }
}
