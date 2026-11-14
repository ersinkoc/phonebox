using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Agent-wide OAuth client IDs and cache limit for cloud photo providers. Tokens never live in config.json.</summary>
public sealed class CloudPhotosConfig
{
    public string? OneDriveClientId { get; set; }
    public string? GoogleClientId { get; set; }
    public string? GoogleClientSecret { get; set; }
    public string? DropboxClientId { get; set; }
    public int CacheMaxMb { get; set; } = 250;

    public void Normalize()
    {
        OneDriveClientId = TrimOrNull(OneDriveClientId, 160);
        GoogleClientId = TrimOrNull(GoogleClientId, 200);
        GoogleClientSecret = TrimOrNull(GoogleClientSecret, 200);
        DropboxClientId = TrimOrNull(DropboxClientId, 160);
        CacheMaxMb = Math.Clamp(CacheMaxMb, 20, 2048);
    }

    private static string? TrimOrNull(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}

public sealed record PhotoWidgetSettings(
    string Provider,
    string ShareUrl,
    string SharePassword,
    string AlbumId,
    string Folder,
    bool Subfolders,
    int IntervalSec,
    string Order,
    string Fit,
    string Transition,
    string Info,
    bool ClockOverlay,
    string TapAction,
    string Title,
    bool DownloadCloudFiles = false
);

/// <summary>Validation and public-option scrubbing for Photo frame widgets (standalone or inside Smart Stack).</summary>
public static class PhotoWidgetOptions
{
    public static readonly IReadOnlySet<string> Providers =
        new HashSet<string>(StringComparer.Ordinal) { "local", "icloud", "onedrive", "google", "dropbox", "nextcloud" };

    public static readonly IReadOnlySet<string> Orders =
        new HashSet<string>(StringComparer.Ordinal) { "shuffle", "newest", "oldest", "name" };

    public static readonly IReadOnlySet<string> Fits =
        new HashSet<string>(StringComparer.Ordinal) { "cover", "contain-blur", "contain" };

    public static readonly IReadOnlySet<string> Transitions =
        new HashSet<string>(StringComparer.Ordinal) { "fade", "kenburns", "slide", "none" };

    public static readonly IReadOnlySet<string> Infos =
        new HashSet<string>(StringComparer.Ordinal) { "none", "caption", "caption-date", "full" };

    public static readonly IReadOnlySet<string> TapActions =
        new HashSet<string>(StringComparer.Ordinal) { "next", "pause", "info", "none" };

    /// <summary>Share links, passwords, OAuth album IDs and local directory paths stay on the computer.</summary>
    public static readonly IReadOnlySet<string> PrivateKeys =
        new HashSet<string>(StringComparer.Ordinal) { "shareUrl", "sharePassword", "folder", "albumId" };

    public static PhotoWidgetSettings Parse(Dictionary<string, JsonElement>? options)
    {
        var provider = Str(options, "provider");
        if (provider is null || !Providers.Contains(provider)) provider = "local";

        var shareUrl = (Str(options, "shareUrl") ?? "").Trim();
        var sharePassword = Str(options, "sharePassword") ?? "";
        var albumId = (Str(options, "albumId") ?? "").Trim();
        var folder = (Str(options, "folder") ?? "").Trim();
        var subfolders = Bool(options, "subfolders") != false;

        var intervalRaw = Int(options, "interval") ?? 60;
        var interval = Math.Clamp(intervalRaw, 5, 86400);

        var order = Str(options, "order");
        if (order is null || !Orders.Contains(order)) order = "shuffle";

        var fit = Str(options, "fit");
        if (fit is null || !Fits.Contains(fit)) fit = "cover";

        var transition = Str(options, "transition");
        if (transition is null || !Transitions.Contains(transition)) transition = "fade";

        var info = Str(options, "info");
        if (info is null || !Infos.Contains(info)) info = "caption-date";

        var clockOverlay = Bool(options, "clockOverlay") == true;

        var tapAction = Str(options, "tapAction");
        if (tapAction is null || !TapActions.Contains(tapAction)) tapAction = "next";

        var title = (Str(options, "title") ?? "").Trim();
        if (title.Length > 80) title = title[..80];

        var downloadCloudFiles = Bool(options, "downloadCloudFiles") == true;

        return new PhotoWidgetSettings(
            provider, shareUrl, sharePassword, albumId, folder,
            subfolders, interval, order, fit, transition,
            info, clockOverlay, tapAction, title, downloadCloudFiles);
    }

    public static void NormalizeWidget(WidgetConfig w)
    {
        if (w.Type == "photos")
        {
            NormalizeOptions(w.Options);
            return;
        }
        if (w.Type != "stack" || w.Options is null || !w.Options.TryGetValue("widgets", out var raw)) return;
        try
        {
            var arrayEl = raw.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(raw.GetString() ?? "[]")
                : raw;
            if (arrayEl.ValueKind != JsonValueKind.Array) return;

            var changed = false;
            var normalized = new List<object?>();
            foreach (var item in arrayEl.EnumerateArray().Take(12))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type == "photos" && item.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optsEl);
                    NormalizeOptions(dict);
                    changed = true;
                    normalized.Add(new
                    {
                        id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null,
                        type,
                        enabled = (!item.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False)
                            && (!item.TryGetProperty("active", out var act) || act.ValueKind != JsonValueKind.False),
                        options = dict,
                    });
                }
                else
                {
                    normalized.Add(item);
                }
            }
            if (changed) w.Options["widgets"] = JsonSerializer.SerializeToElement(normalized);
        }
        catch (JsonException) { }
    }

    public static void NormalizeOptions(Dictionary<string, JsonElement>? options)
    {
        if (options is null) return;
        if (options.ContainsKey("provider"))
        {
            var v = Str(options, "provider");
            options["provider"] = JsonSerializer.SerializeToElement(v is not null && Providers.Contains(v) ? v : "local");
        }
        if (options.ContainsKey("shareUrl"))
            options["shareUrl"] = JsonSerializer.SerializeToElement((Str(options, "shareUrl") ?? "").Trim());
        if (options.ContainsKey("sharePassword"))
            options["sharePassword"] = JsonSerializer.SerializeToElement(Str(options, "sharePassword") ?? "");
        if (options.ContainsKey("albumId"))
            options["albumId"] = JsonSerializer.SerializeToElement((Str(options, "albumId") ?? "").Trim());
        if (options.ContainsKey("folder"))
            options["folder"] = JsonSerializer.SerializeToElement((Str(options, "folder") ?? "").Trim());
        if (options.ContainsKey("subfolders"))
            options["subfolders"] = JsonSerializer.SerializeToElement(Bool(options, "subfolders") != false);
        if (options.ContainsKey("downloadCloudFiles"))
            options["downloadCloudFiles"] = JsonSerializer.SerializeToElement(Bool(options, "downloadCloudFiles") == true);
        if (options.ContainsKey("interval"))
            options["interval"] = JsonSerializer.SerializeToElement(Math.Clamp(Int(options, "interval") ?? 60, 5, 86400));
        if (options.ContainsKey("order"))
        {
            var v = Str(options, "order");
            options["order"] = JsonSerializer.SerializeToElement(v is not null && Orders.Contains(v) ? v : "shuffle");
        }
        if (options.ContainsKey("fit"))
        {
            var v = Str(options, "fit");
            options["fit"] = JsonSerializer.SerializeToElement(v is not null && Fits.Contains(v) ? v : "cover");
        }
        if (options.ContainsKey("transition"))
        {
            var v = Str(options, "transition");
            options["transition"] = JsonSerializer.SerializeToElement(v is not null && Transitions.Contains(v) ? v : "fade");
        }
        if (options.ContainsKey("info"))
        {
            var v = Str(options, "info");
            options["info"] = JsonSerializer.SerializeToElement(v is not null && Infos.Contains(v) ? v : "caption-date");
        }
        if (options.ContainsKey("clockOverlay"))
            options["clockOverlay"] = JsonSerializer.SerializeToElement(Bool(options, "clockOverlay") == true);
        if (options.ContainsKey("tapAction"))
        {
            var v = Str(options, "tapAction");
            options["tapAction"] = JsonSerializer.SerializeToElement(v is not null && TapActions.Contains(v) ? v : "next");
        }
        if (options.ContainsKey("title"))
        {
            var t = (Str(options, "title") ?? "").Trim();
            if (t.Length > 80) t = t[..80];
            options["title"] = JsonSerializer.SerializeToElement(t);
        }
    }

    public static Dictionary<string, JsonElement>? ScrubPublic(Dictionary<string, JsonElement>? options)
    {
        if (options is null) return null;
        return options.Where(kv => !PrivateKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
    }

    private static string? Str(Dictionary<string, JsonElement>? o, string k) =>
        o is not null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(Dictionary<string, JsonElement>? o, string k) =>
        o is not null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)d : null;

    private static bool? Bool(Dictionary<string, JsonElement>? o, string k) =>
        o is not null && o.TryGetValue(k, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;
}

public sealed record PhotoFeedItem(
    string Id,
    string Url,
    string? Caption,
    long? TakenAt,
    string? Album,
    string? Source,
    int? Width,
    int? Height
);

public sealed record PhotoFeedResponse(
    bool Configured,
    string Provider,
    string SourceLabel,
    string? Title,
    long UpdatedAt,
    bool Stale,
    string? Error,
    IReadOnlyList<PhotoFeedItem> Photos,
    int CloudOnlyCount = 0
);

public sealed record PhotoBrowseEntry(
    string Id,
    string Name,
    string Kind, // "album" or "folder"
    int? ItemCount,
    string? Path
);

public sealed record PhotoBrowseResponse(
    bool Ok,
    string? Error,
    string Provider,
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<PhotoBrowseEntry> Entries
);

public sealed record PhotoLocalRoot(
    string Id,
    string Name,
    string ProviderHint,
    string Path
);

public sealed record PhotoTestResponse(
    bool Ok,
    string Message,
    int Count,
    string SourceLabel,
    int CloudOnlyCount = 0,
    bool RemoteStream = false
);

public sealed record PhotoProviderStatus(
    string Provider,
    bool ClientConfigured,
    bool Connected,
    string? Account,
    string? RedirectUri,
    bool LocalFolderDetected
);

public sealed record PhotoStatusResponse(
    IReadOnlyList<PhotoProviderStatus> Providers,
    IReadOnlyList<PhotoLocalRoot> LocalRoots
);

public sealed record PhotoPreviewRequest(Dictionary<string, JsonElement>? Options);
public sealed record PhotoBrowseRequest(string Provider, string? ParentId, string? Path, string? ShareUrl, string? SharePassword);
public sealed record PhotoTestRequest(Dictionary<string, JsonElement>? Options);
