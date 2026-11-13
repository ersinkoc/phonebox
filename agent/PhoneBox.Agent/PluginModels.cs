using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? MinAgentVersion { get; set; }
    public List<string> Os { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
    public List<PluginOptionDef> Settings { get; set; } = [];
    public PluginBackendDef Backend { get; set; } = new();
    public List<PluginWidgetDef> Widgets { get; set; } = [];
}

public sealed class PluginOptionDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Secret { get; set; }
    public JsonElement? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string[][]? Choices { get; set; }
}

public sealed class PluginBackendDef
{
    public string Kind { get; set; } = "none";
    public string Runtime { get; set; } = "none";
    public string? Entry { get; set; }
    public int IntervalSec { get; set; } = 30;
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, PluginHttpActionDef>? Actions { get; set; }
}

public sealed class PluginHttpActionDef
{
    public string Method { get; set; } = "POST";
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
}

public sealed class PluginWidgetDef
{
    public string Type { get; set; } = "";
    public string Category { get; set; } = "Plugins";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🧩";
    public string Desc { get; set; } = "";
    public int W { get; set; } = 4;
    public int H { get; set; } = 3;
    public int MinW { get; set; } = 2;
    public int MinH { get; set; } = 2;
    public PluginUiDef Ui { get; set; } = new();
    public List<PluginOptionDef> Options { get; set; } = [];
}

public sealed class PluginUiDef
{
    public string Kind { get; set; } = "auto";
    public string? File { get; set; }
}

public sealed class MarketplaceIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string? UpdatedAt { get; set; }
    public List<MarketplaceEntry> Plugins { get; set; } = [];
}

public sealed class MarketplacePackage
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class MarketplaceEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Icon { get; set; }
    public string? Homepage { get; set; }
    public bool Verified { get; set; }
    public string? MinAgentVersion { get; set; }
    public List<string> Os { get; set; } = [];
    public string? Runtime { get; set; }
    public List<string> Permissions { get; set; } = [];
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public Dictionary<string, MarketplacePackage>? Packages { get; set; }

    public (string Url, string Sha256)? ResolvePackage(string rid, bool allowLoopback = false)
    {
        string? url = null, sha = null;
        if (Packages is not null && Packages.TryGetValue(rid, out var pkg) && !string.IsNullOrWhiteSpace(pkg.Url))
        {
            url = pkg.Url.Trim();
            sha = pkg.Sha256?.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(DownloadUrl))
        {
            url = DownloadUrl.Trim();
            sha = Sha256?.Trim();
        }
        if (url is null || !PluginManifestValidator.IsValidSha256(sha)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PluginManifestValidator.IsAllowedDownloadUri(uri, allowLoopback))
            return null;
        return (uri.ToString(), sha!.ToLowerInvariant());
    }
}

public sealed class PluginsStateFile
{
    public Dictionary<string, StoredPluginState> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PluginInstallRequest(string Id, List<string>? ApprovedPermissions);
public sealed record PluginToggleRequest(bool Enabled, List<string>? ApprovedPermissions);
public sealed record PluginSettingsRequest(Dictionary<string, JsonElement>? Settings, bool RegenerateBridgeToken = false);

public sealed class StoredPluginState
{
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";
    public string Source { get; set; } = "local";
    public string? Sha256 { get; set; }
    public List<string> ApprovedPermissions { get; set; } = [];
    public string? BridgeToken { get; set; }
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.Ordinal);
}

public static partial class PluginManifestValidator
{
    public const string DefaultRepo = "ersinkoc/phonebox-plugins";

    private static readonly HashSet<string> AllowedOs = ["windows", "linux", "macos"];
    private static readonly HashSet<string> AllowedPermissions = ["http", "exec", "bridge"];
    private static readonly HashSet<string> AllowedOptionTypes = ["text", "password", "number", "bool", "select", "lines", "color", "textarea"];
    private static readonly HashSet<string> AllowedBackendKinds = ["none", "http", "command", "sidecar"];
    private static readonly HashSet<string> AllowedRuntimes = ["none", "shell", "python3", "node", "pwsh", "binary"];
    private static readonly HashSet<string> AllowedUiKinds = ["auto", "stat", "gauge", "list", "buttons", "html"];
    private static readonly HashSet<string> AllowedHttpMethods = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    public static string NormalizeRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return DefaultRepo;
        var trimmed = repo.Trim().Trim('/');
        return RepoRegex().IsMatch(trimmed) ? trimmed : DefaultRepo;
    }

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdRegex().IsMatch(id);

    public static bool IsValidSha256(string? sha) => !string.IsNullOrEmpty(sha) && Sha256Regex().IsMatch(sha.Trim());

    public static bool IsValidVersion(string? ver, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(ver)) return false;
        var clean = ver.Trim().TrimStart('v', 'V').Split('-')[0];
        return Version.TryParse(clean, out var v) && v is not null && (parsed = v) is not null;
    }

    public static bool IsCompatibleAgentVersion(string? minAgentVersion, string? currentAgentVersion = null)
    {
        if (string.IsNullOrWhiteSpace(minAgentVersion)) return true;
        if (!IsValidVersion(minAgentVersion, out var minV)) return false;
        if (!IsValidVersion(currentAgentVersion ?? AppPaths.Version, out var curV)) return true;
        return curV >= minV;
    }

    public static bool IsAllowedDownloadUri(Uri uri, bool allowLoopback = false)
    {
        if (allowLoopback && uri.IsLoopback && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com" or "raw.githubusercontent.com"
            || host.EndsWith(".github.io", StringComparison.Ordinal);
    }

    public static string? ResolveSafeChildPath(string pluginDir, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var rel = relativePath.Trim();
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.StartsWith('\\')) return null;
        var baseFull = Path.GetFullPath(pluginDir);
        var baseWithSep = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(baseFull, rel));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(baseWithSep, cmp)) return null;
        if (File.Exists(target) && new FileInfo(target).LinkTarget is not null) return null;
        return target;
    }

    public static string? Validate(PluginManifest? m, string? pluginDir)
    {
        if (m is null) return "plugin.json is empty";
        if (!IsValidId(m.Id)) return "plugin id must match ^[a-z0-9][a-z0-9-]{0,47}$";
        if (string.IsNullOrWhiteSpace(m.Name)) return "plugin name is required";
        if (!IsValidVersion(m.Version, out _)) return "plugin version must be a valid version (e.g. 1.0.0)";
        if (!string.IsNullOrWhiteSpace(m.MinAgentVersion) && !IsValidVersion(m.MinAgentVersion, out _))
            return "minAgentVersion must be a valid version";
        if (m.Os is not null && m.Os.Any(o => !AllowedOs.Contains(o)))
            return "unsupported os entry in plugin.json";
        if (m.Permissions is null) m.Permissions = [];
        if (m.Permissions.Any(p => !AllowedPermissions.Contains(p)))
            return "unknown permission requested in plugin.json";
        m.Permissions = m.Permissions.Distinct(StringComparer.Ordinal).ToList();

        m.Settings ??= [];
        if (m.Settings.Any(s => s is null || string.IsNullOrWhiteSpace(s.Key) || !AllowedOptionTypes.Contains(s.Type)))
            return "invalid plugin setting definition";
        if (m.Settings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != m.Settings.Count)
            return "duplicate plugin setting key";

        m.Backend ??= new();
        if (!AllowedBackendKinds.Contains(m.Backend.Kind)) return "unsupported backend kind";
        if (!AllowedRuntimes.Contains(m.Backend.Runtime)) return "unsupported backend runtime";
        m.Backend.IntervalSec = Math.Clamp(m.Backend.IntervalSec <= 0 ? 30 : m.Backend.IntervalSec, 5, 86400);

        if (m.Backend.Kind is "command" or "sidecar")
        {
            if (!m.Permissions.Contains("exec")) return $"backend kind '{m.Backend.Kind}' requires 'exec' permission";
            if (m.Backend.Runtime == "none") return $"backend kind '{m.Backend.Kind}' requires a valid runtime";
            if (string.IsNullOrWhiteSpace(m.Backend.Entry)) return $"backend kind '{m.Backend.Kind}' requires entry";
            if (pluginDir is not null)
            {
                var entryPath = ResolveSafeChildPath(pluginDir, m.Backend.Entry);
                if (entryPath is null || !File.Exists(entryPath)) return "backend entry path is missing or unsafe";
            }
            else if (Path.IsPathRooted(m.Backend.Entry) || m.Backend.Entry.Contains(".."))
            {
                return "backend entry path must stay inside the plugin folder";
            }
        }
        else if (m.Backend.Kind == "http")
        {
            if (!m.Permissions.Contains("http")) return "backend kind 'http' requires 'http' permission";
            if (!IsAllowedBackendUrlTemplate(m.Backend.Url))
                return "http backend requires an http://, https://, or {settings.*} url";
            if (m.Backend.Actions is not null)
            {
                foreach (var (op, act) in m.Backend.Actions)
                {
                    if (string.IsNullOrWhiteSpace(op) || act is null || string.IsNullOrWhiteSpace(act.Url))
                        return "http backend action requires an operation name and url";
                    if (!AllowedHttpMethods.Contains((act.Method ?? "POST").ToUpperInvariant()))
                        return $"unsupported http action method '{act.Method}'";
                    if (!IsAllowedBackendUrlTemplate(act.Url))
                        return "http action url must start with http://, https://, or {settings.*}";
                }
            }
        }

        if (m.Widgets is null || m.Widgets.Count == 0) return "plugin must declare at least one widget";
        if (m.Widgets.Count > 24) return "plugin cannot declare more than 24 widgets";

        var widgetTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in m.Widgets)
        {
            if (w is null) return "null widget entry";
            if (!Catalog.IsPluginWidgetType(w.Type) || Catalog.PluginIdOf(w.Type) != m.Id)
                return $"widget type '{w.Type}' must be namespaced as plugin:{m.Id} or plugin:{m.Id}.<name>";
            if (!widgetTypes.Add(w.Type)) return $"duplicate widget type '{w.Type}'";
            if (string.IsNullOrWhiteSpace(w.Name)) return $"widget '{w.Type}' requires a name";
            w.Category = string.IsNullOrWhiteSpace(w.Category) ? "Plugins" : w.Category.Trim();
            w.Icon = string.IsNullOrWhiteSpace(w.Icon) ? "🧩" : IconText.Repair(w.Icon);
            w.MinW = Math.Clamp(w.MinW <= 0 ? 1 : w.MinW, 1, Catalog.Cols);
            w.MinH = Math.Clamp(w.MinH <= 0 ? 1 : w.MinH, 1, Catalog.Rows);
            w.W = Math.Clamp(w.W <= 0 ? Math.Max(4, w.MinW) : w.W, w.MinW, Catalog.Cols);
            w.H = Math.Clamp(w.H <= 0 ? Math.Max(3, w.MinH) : w.H, w.MinH, Catalog.Rows);
            w.Ui ??= new();
            if (!AllowedUiKinds.Contains(w.Ui.Kind)) return $"unsupported ui kind '{w.Ui.Kind}'";
            if (w.Ui.Kind == "html")
            {
                if (string.IsNullOrWhiteSpace(w.Ui.File)) return $"widget '{w.Type}' html ui requires file";
                if (pluginDir is not null)
                {
                    var htmlPath = ResolveSafeChildPath(pluginDir, w.Ui.File);
                    if (htmlPath is null || !File.Exists(htmlPath)) return $"widget '{w.Type}' html file is missing or unsafe";
                }
                else if (Path.IsPathRooted(w.Ui.File) || w.Ui.File.Contains(".."))
                {
                    return $"widget '{w.Type}' html file path must stay inside the plugin folder";
                }
            }
            w.Options ??= [];
            if (w.Options.Any(o => o is null || string.IsNullOrWhiteSpace(o.Key) || !AllowedOptionTypes.Contains(o.Type)))
                return $"widget '{w.Type}' has an invalid option definition";
        }
        return null;
    }

    private static bool IsAllowedBackendUrlTemplate(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || BackendBaseTokenRegex().IsMatch(url));

    [GeneratedRegex(@"^\{(settings|options)\.[A-Za-z0-9_.-]+\}")]
    private static partial Regex BackendBaseTokenRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();
}
