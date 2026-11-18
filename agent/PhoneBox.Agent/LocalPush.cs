namespace PhoneBox.Agent;

/// <summary>
/// A browser extension on this computer that may push values to the panel. The local port refuses every request that
/// carries an Origin other than the settings page's own, which keeps web sites out; an extension's requests carry
/// its own origin (chrome-extension://&lt;id&gt;), so one that is to feed a widget is named in the config (push.origins).
/// The exception is narrow on purpose: POST or PUT to /api/push only. It can set a pushed value and nothing else:
/// no shortcuts, no approvals, no settings, and it cannot read anything back that another origin could not.
/// </summary>
public static class LocalPush
{
    public const int MaxOrigins = 8;
    private static readonly string[] Schemes = ["chrome-extension://", "moz-extension://"];

    /// <summary>An extension origin: the scheme and the extension's id, with no path, port or wildcard.</summary>
    public static bool ValidOrigin(string? origin)
    {
        if (origin is null) return false;
        foreach (var scheme in Schemes)
        {
            if (!origin.StartsWith(scheme, StringComparison.Ordinal)) continue;
            var id = origin.AsSpan(scheme.Length);
            if (id.Length is < 8 or > 64) return false;
            foreach (var ch in id)
                if (!(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
            return true;
        }
        return false;
    }

    /// <summary>True when the request comes from a listed extension and only sets a pushed value.</summary>
    public static bool Allows(LocalPushConfig? config, string origin, string method, PathString path)
    {
        if (config?.Origins is not { Count: > 0 } origins || !ValidOrigin(origin)) return false;
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method)) return false;
        if (!path.StartsWithSegments("/api/push", out var key) || !key.HasValue || key.Value!.Length < 2) return false;
        foreach (var allowed in origins)
            if (string.Equals(allowed, origin, StringComparison.Ordinal)) return true;
        return false;
    }
}
