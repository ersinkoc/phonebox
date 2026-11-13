using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Manages community plugins installed in AppPaths.PluginsDir and synchronized with the central
/// GitHub-hosted Marketplace index (ersinkoc/phonebox-plugins by default).
/// </summary>
public sealed partial class PluginService : BackgroundService
{
    public const string SecretPlaceholder = "__UNCHANGED__";
    private const long MaxZipBytes = 25 * 1024 * 1024;
    private const long MaxExtractedBytes = 64 * 1024 * 1024;
    private const int MaxZipEntries = 250;
    private const int MaxBridgeQueue = 64;
    private const int MaxCommandOutputBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions JsonLineOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ConfigStore _cfg;
    private readonly PushStore _push;
    private readonly ILogger<PluginService> _log;
    private readonly string _pluginsDir;
    private readonly string _statePath;
    private readonly HttpClient _http;
    private readonly HttpClient _marketHttp;
    private readonly bool _ownsHttp;
    private readonly Func<string, string[]> _indexUrls;
    private readonly bool _allowLoopbackDownloads;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly SemaphoreSlim _marketGate = new(1, 1);

    private PluginsStateFile _state = new();
    private Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SidecarRunner> _sidecars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedWidgetState> _widgetStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pollingWidgets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BridgeActionItem>> _bridgeQueues = new(StringComparer.OrdinalIgnoreCase);

    private MarketplaceIndex? _marketIndex;
    private string _marketStatus = "idle";
    private string? _marketError;
    private long? _marketUpdatedAt;

    private static readonly ConcurrentDictionary<string, (int MinW, int MinH)> GlobalWidgetMinSizes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, HashSet<string>> GlobalPublicOptionKeys = new(StringComparer.Ordinal);

    public PluginService(
        ConfigStore cfg,
        PushStore push,
        ILogger<PluginService> log,
        string? pluginsDir = null,
        string? statePath = null,
        HttpClient? http = null,
        Func<string, string[]>? indexUrls = null,
        bool allowLoopbackDownloads = false)
    {
        _cfg = cfg;
        _push = push;
        _log = log;
        _pluginsDir = pluginsDir ?? AppPaths.PluginsDir;
        _statePath = statePath ?? AppPaths.PluginsStatePath;
        _ownsHttp = http is null;
        _allowLoopbackDownloads = allowLoopbackDownloads;
        if (http is not null)
        {
            _http = http;
            _marketHttp = http;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }) { Timeout = TimeSpan.FromSeconds(25) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);

            var marketHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            if (!allowLoopbackDownloads)
                marketHandler.ConnectCallback = ConnectPublicOnlyAsync;
            _marketHttp = new HttpClient(marketHandler) { Timeout = TimeSpan.FromSeconds(25) };
            _marketHttp.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        }
        _indexUrls = indexUrls ?? DefaultIndexUrls;

        LoadState();
        ReloadPluginsFromDisk();
    }

    public static (int MinW, int MinH) GetRegisteredMinSize(string widgetType) =>
        GlobalWidgetMinSizes.TryGetValue(widgetType, out var size) ? size : (2, 2);

    public static Dictionary<string, JsonElement>? FilterPublicWidgetOptions(string widgetType, Dictionary<string, JsonElement>? options)
    {
        if (options is null || options.Count == 0) return null;
        if (!GlobalPublicOptionKeys.TryGetValue(widgetType, out var allowed))
            return [];
        var filtered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var kv in options)
            if (allowed.Contains(kv.Key)) filtered[kv.Key] = kv.Value;
        return filtered;
    }

    internal string PluginsDir => _pluginsDir;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        EnsureActiveSidecarsStarted();
        try { await Task.Delay(TimeSpan.FromSeconds(4), ct); }
        catch (OperationCanceledException) { return; }

        await RefreshMarketplaceAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), ct);
                await RefreshMarketplaceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug("marketplace background refresh failed: {Msg}", ex.Message); }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        foreach (var id in _sidecars.Keys.ToList())
            await StopSidecarAsync(id);
        await base.StopAsync(ct);
        if (_ownsHttp)
        {
            _http.Dispose();
            if (!ReferenceEquals(_marketHttp, _http)) _marketHttp.Dispose();
        }
    }

    public string ActiveCatalogWidgetsJson()
    {
        List<object> entries = [];
        lock (_lock)
        {
            foreach (var p in _loaded.Values.Where(x => x.Active).OrderBy(x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var w in p.Manifest.Widgets)
                {
                    var uiKind = w.Ui?.Kind ?? "auto";
                    var options = (w.Options ?? []).Select(o => new
                    {
                        key = o.Key,
                        label = o.Label,
                        type = o.Type,
                        @default = o.Default,
                        min = o.Min,
                        max = o.Max,
                        choices = o.Choices,
                    }).ToList();
                    entries.Add(new
                    {
                        type = w.Type,
                        category = string.IsNullOrWhiteSpace(w.Category) ? "Plugins" : w.Category,
                        name = w.Name,
                        icon = w.Icon,
                        desc = w.Desc,
                        w = w.W,
                        h = w.H,
                        minW = w.MinW,
                        minH = w.MinH,
                        plugin = p.Manifest.Id,
                        uiKind,
                        options,
                    });
                }
            }
        }
        if (entries.Count == 0) return "";
        var arr = JsonSerializer.Serialize(entries, JsonWriteOpts);
        return arr[1..^1];
    }

    public object GetOverview()
    {
        string repo;
        List<LoadedPlugin> loadedSnapshot;
        MarketplaceIndex? marketIndex;
        string marketStatus;
        string? marketError;
        long? marketUpdatedAt;
        lock (_lock)
        {
            repo = _cfg.Get().Plugins.Repo;
            loadedSnapshot = _loaded.Values.OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToList();
            marketIndex = _marketIndex;
            marketStatus = _marketStatus;
            marketError = _marketError;
            marketUpdatedAt = _marketUpdatedAt;
        }

        var loadedById = loadedSnapshot.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var installedList = loadedSnapshot.Select(p =>
        {
            var s = p.State;
            var osOk = p.OsOk;
            var versionOk = p.AgentVersionOk;
            var runtimeInfo = CheckRuntime(p.Manifest.Backend.Runtime);
            var market = marketIndex?.Plugins.FirstOrDefault(m => m.Id.Equals(p.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            var updateAvailable = market is not null
                && PluginManifestValidator.IsValidVersion(market.Version, out var mv)
                && PluginManifestValidator.IsValidVersion(p.Manifest.Version, out var cv)
                && mv > cv;
            var secretSet = p.Manifest.Settings
                .Where(opt => (opt.Secret || opt.Type == "password")
                    && s.Settings.TryGetValue(opt.Key, out var val)
                    && val.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(val.GetString()))
                .Select(opt => opt.Key)
                .ToArray();
            var publicSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var opt in p.Manifest.Settings)
            {
                if (opt.Secret || opt.Type == "password") continue;
                if (s.Settings.TryGetValue(opt.Key, out var val)) publicSettings[opt.Key] = val;
            }
            return new
            {
                id = p.Manifest.Id,
                name = p.Manifest.Name,
                version = p.Manifest.Version,
                author = p.Manifest.Author,
                description = p.Manifest.Description,
                homepage = p.Manifest.Homepage,
                source = s.Source,
                enabled = s.Enabled,
                active = p.Active,
                needsApproval = p.NeedsApproval,
                error = p.LoadError,
                osOk,
                agentVersionOk = versionOk,
                runtime = p.Manifest.Backend.Runtime,
                runtimeOk = runtimeInfo.Ok,
                runtimeHint = runtimeInfo.Hint,
                permissions = p.Manifest.Permissions,
                approvedPermissions = s.ApprovedPermissions,
                hasBridge = p.Manifest.Permissions.Contains("bridge"),
                bridgeToken = p.Manifest.Permissions.Contains("bridge") ? s.BridgeToken : null,
                settingsSchema = p.Manifest.Settings.Select(o => new
                {
                    key = o.Key,
                    label = o.Label,
                    type = o.Type,
                    secret = o.Secret || o.Type == "password",
                    @default = o.Default,
                    min = o.Min,
                    max = o.Max,
                    choices = o.Choices,
                }),
                settings = publicSettings,
                secretKeysSet = secretSet,
                widgets = p.Manifest.Widgets.Select(w => new { w.Type, w.Name, w.Icon, w.Category, ui = w.Ui.Kind }),
                updateAvailable,
                latestVersion = updateAvailable ? market!.Version : null,
            };
        }).ToList();

        var marketList = (marketIndex?.Plugins ?? []).Select(m =>
        {
            var osOk = m.Os.Count == 0 || m.Os.Contains(AppPaths.Os, StringComparer.OrdinalIgnoreCase);
            var agentVersionOk = PluginManifestValidator.IsCompatibleAgentVersion(m.MinAgentVersion);
            var rt = CheckRuntime(m.Runtime);
            var installed = loadedById.TryGetValue(m.Id, out var lp) ? lp : null;
            var updateAvailable = installed is not null
                && PluginManifestValidator.IsValidVersion(m.Version, out var mv)
                && PluginManifestValidator.IsValidVersion(installed.Manifest.Version, out var cv)
                && mv > cv;
            var hasPkg = m.ResolvePackage(AppPaths.Rid, _allowLoopbackDownloads) is not null;
            return new
            {
                id = m.Id,
                name = m.Name,
                version = m.Version,
                author = m.Author,
                description = m.Description,
                category = m.Category ?? "Plugins",
                icon = string.IsNullOrWhiteSpace(m.Icon) ? "🧩" : m.Icon,
                homepage = m.Homepage,
                verified = m.Verified,
                minAgentVersion = m.MinAgentVersion,
                os = m.Os,
                runtime = m.Runtime ?? "none",
                permissions = m.Permissions,
                osOk,
                agentVersionOk,
                runtimeOk = rt.Ok,
                runtimeHint = rt.Hint,
                packageAvailable = hasPkg,
                installed = installed is not null,
                installedVersion = installed?.Manifest.Version,
                updateAvailable,
            };
        }).ToList();

        return new
        {
            pluginsDir = _pluginsDir,
            marketplace = new
            {
                repo,
                status = marketStatus,
                error = marketError,
                updatedAt = marketUpdatedAt,
                plugins = marketList,
            },
            installed = installedList,
        };
    }

    public async Task RefreshMarketplaceAsync(CancellationToken ct = default)
    {
        if (!await _marketGate.WaitAsync(0, ct)) return;
        try
        {
            var repo = _cfg.Get().Plugins.Repo;
            var urls = _indexUrls(repo);
            string? json = null;
            var allNotFound = true;
            Exception? lastEx = null;

            foreach (var url in urls)
            {
                try
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var idxUri)) continue;
                    using var res = await SendSafeAsync(
                        _marketHttp,
                        (uri, _, _) => new HttpRequestMessage(HttpMethod.Get, uri),
                        idxUri,
                        externalMarketOnly: !_allowLoopbackDownloads,
                        HttpCompletionOption.ResponseContentRead,
                        ct);
                    if (res.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                    allNotFound = false;
                    res.EnsureSuccessStatusCode();
                    json = await res.Content.ReadAsStringAsync(ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    allNotFound = false;
                    lastEx = ex;
                }
            }

            if (json is not null)
            {
                var idx = JsonSerializer.Deserialize<MarketplaceIndex>(json, JsonReadOpts);
                if (idx?.Plugins is not null)
                {
                    idx.Plugins = idx.Plugins.Where(p => p is not null && PluginManifestValidator.IsValidId(p.Id)).ToList();
                    Directory.CreateDirectory(_pluginsDir);
                    await File.WriteAllTextAsync(Path.Combine(_pluginsDir, "marketplace-cache.json"), json, ct);
                    lock (_lock)
                    {
                        _marketIndex = idx;
                        _marketStatus = "ok";
                        _marketError = null;
                        _marketUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                    return;
                }
            }

            if (allNotFound)
            {
                lock (_lock)
                {
                    _marketIndex = new MarketplaceIndex();
                    _marketStatus = "not_published";
                    _marketError = null;
                    _marketUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                return;
            }

            var cachePath = Path.Combine(_pluginsDir, "marketplace-cache.json");
            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<MarketplaceIndex>(await File.ReadAllTextAsync(cachePath, ct), JsonReadOpts);
                    if (cached?.Plugins is not null)
                    {
                        lock (_lock)
                        {
                            _marketIndex = cached;
                            _marketStatus = "cached";
                            _marketError = lastEx?.Message ?? "Offline — showing cached marketplace index";
                        }
                        return;
                    }
                }
                catch { }
            }

            lock (_lock)
            {
                _marketStatus = "error";
                _marketError = lastEx?.Message ?? "Could not load marketplace index";
            }
        }
        finally
        {
            _marketGate.Release();
        }
    }

    public async Task<string?> InstallAsync(string id, IReadOnlyList<string>? approvedPermissions, CancellationToken ct = default)
    {
        if (!PluginManifestValidator.IsValidId(id)) return "invalid plugin id";
        await _installGate.WaitAsync(ct);
        try
        {
            MarketplaceEntry? entry;
            lock (_lock) entry = _marketIndex?.Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                await RefreshMarketplaceAsync(ct);
                lock (_lock) entry = _marketIndex?.Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            if (entry is null) return $"plugin '{id}' was not found in the marketplace";
            if (entry.Os.Count > 0 && !entry.Os.Contains(AppPaths.Os, StringComparer.OrdinalIgnoreCase))
                return $"plugin '{id}' does not support {AppPaths.Os}";
            if (!PluginManifestValidator.IsCompatibleAgentVersion(entry.MinAgentVersion))
                return $"plugin '{id}' requires PhoneBox Agent {entry.MinAgentVersion} or newer";

            var pkg = entry.ResolvePackage(AppPaths.Rid, _allowLoopbackDownloads);
            if (pkg is null) return $"plugin '{id}' has no valid package for {AppPaths.Rid}";

            var approved = (approvedPermissions ?? []).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var missing = entry.Permissions.Where(p => !approved.Contains(p)).ToList();
            if (missing.Count > 0) return "permissions must be approved before installing: " + string.Join(", ", missing);

            Directory.CreateDirectory(_pluginsDir);
            var stagingRoot = Path.Combine(_pluginsDir, ".staging");
            Directory.CreateDirectory(stagingRoot);
            var workDir = Path.Combine(stagingRoot, $"{id}-{Guid.NewGuid():N}");
            var zipPath = Path.Combine(stagingRoot, $"{id}-{Guid.NewGuid():N}.zip");
            Directory.CreateDirectory(workDir);

            try
            {
                if (!Uri.TryCreate(pkg.Value.Url, UriKind.Absolute, out var pkgUri))
                    return $"plugin '{id}' has an invalid package URL";
                using (var res = await SendSafeAsync(
                    _marketHttp,
                    (uri, _, _) => new HttpRequestMessage(HttpMethod.Get, uri),
                    pkgUri,
                    externalMarketOnly: !_allowLoopbackDownloads,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct))
                {
                    res.EnsureSuccessStatusCode();
                    if (res.Content.Headers.ContentLength is > MaxZipBytes)
                        return $"plugin package exceeds the {MaxZipBytes / (1024 * 1024)} MB limit";
                    await using var src = await res.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zipPath);
                    var buf = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        total += read;
                        if (total > MaxZipBytes) return $"plugin package exceeds the {MaxZipBytes / (1024 * 1024)} MB limit";
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    }
                }

                string actualSha;
                await using (var fs = File.OpenRead(zipPath))
                    actualSha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (!actualSha.Equals(pkg.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                    return "SHA-256 verification failed for downloaded plugin package";

                var extractErr = ExtractZipSafely(zipPath, workDir);
                if (extractErr is not null) return extractErr;

                var pluginSourceDir = UnwrapSingleRoot(workDir);
                var manifestPath = Path.Combine(pluginSourceDir, "plugin.json");
                if (!File.Exists(manifestPath)) return "plugin archive does not contain plugin.json";

                var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath, ct), JsonReadOpts);
                var valErr = PluginManifestValidator.Validate(manifest, pluginSourceDir);
                if (valErr is not null) return valErr;
                if (!string.Equals(manifest!.Id, entry.Id, StringComparison.Ordinal))
                    return $"archive plugin id '{manifest.Id}' does not match marketplace id '{entry.Id}'";
                if (!string.Equals(manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
                    return $"archive version '{manifest.Version}' does not match marketplace version '{entry.Version}'";

                var unapprovedManifestPerms = manifest.Permissions.Where(p => !approved.Contains(p)).ToList();
                if (unapprovedManifestPerms.Count > 0)
                    return "manifest requests unapproved permissions: " + string.Join(", ", unapprovedManifestPerms);

                if (!OperatingSystem.IsWindows() && manifest.Backend.Runtime == "binary"
                    && PluginManifestValidator.ResolveSafeChildPath(pluginSourceDir, manifest.Backend.Entry) is { } binPath)
                {
                    File.SetUnixFileMode(binPath, (UnixFileMode)0b111_101_101);
                }

                await StopSidecarAsync(id);
                var targetDir = Path.Combine(_pluginsDir, id);
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                MoveOrCopyDirectory(pluginSourceDir, targetDir);

                string stateJson;
                lock (_lock)
                {
                    var prev = _state.Plugins.TryGetValue(id, out var existing) ? existing : new StoredPluginState();
                    _state.Plugins[id] = new StoredPluginState
                    {
                        Enabled = true,
                        Version = manifest.Version,
                        Source = "marketplace",
                        Sha256 = actualSha,
                        ApprovedPermissions = [.. manifest.Permissions],
                        BridgeToken = manifest.Permissions.Contains("bridge")
                            ? (string.IsNullOrEmpty(prev.BridgeToken) ? NewToken() : prev.BridgeToken)
                            : null,
                        Settings = prev.Settings ?? new(StringComparer.Ordinal),
                    };
                    stateJson = SnapshotStateJsonLocked();
                }
                WriteStateFile(stateJson);
                ReloadPluginsFromDisk();

                EnsureActiveSidecarsStarted();
                _push.Emit("refresh");
                _log.LogInformation("installed plugin {Id} v{Version} from marketplace", id, manifest.Version);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ex.Message;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            }
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<string?> SetEnabledAsync(string id, bool enabled, IReadOnlyList<string>? approvedPermissions = null)
    {
        if (!PluginManifestValidator.IsValidId(id)) return "invalid plugin id";
        await _installGate.WaitAsync();
        try
        {
            string stateJson;
            lock (_lock)
            {
                if (!_loaded.TryGetValue(id, out var plugin)) return $"plugin '{id}' is not installed";
                if (!string.IsNullOrEmpty(plugin.LoadError)) return plugin.LoadError;
                if (enabled)
                {
                    if (!plugin.OsOk) return $"plugin '{id}' does not support {AppPaths.Os}";
                    if (!plugin.AgentVersionOk) return $"plugin '{id}' requires PhoneBox Agent {plugin.Manifest.MinAgentVersion}";
                    var approved = new HashSet<string>(plugin.State.ApprovedPermissions ?? [], StringComparer.Ordinal);
                    if (approvedPermissions is not null)
                        foreach (var p in approvedPermissions) approved.Add(p);
                    var missing = plugin.Manifest.Permissions.Where(p => !approved.Contains(p)).ToList();
                    if (missing.Count > 0) return "permissions must be approved before enabling: " + string.Join(", ", missing);
                    plugin.State.ApprovedPermissions = [.. approved];
                    if (plugin.Manifest.Permissions.Contains("bridge") && string.IsNullOrEmpty(plugin.State.BridgeToken))
                        plugin.State.BridgeToken = NewToken();
                }
                plugin.State.Enabled = enabled;
                plugin.State.Version = plugin.Manifest.Version;
                _state.Plugins[id] = plugin.State;
                stateJson = SnapshotStateJsonLocked();
            }
            WriteStateFile(stateJson);
            ReloadPluginsFromDisk();

            if (!enabled) await StopSidecarAsync(id);
            else EnsureActiveSidecarsStarted();

            _push.Emit("refresh");
            return null;
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<string?> SaveSettingsAsync(string id, Dictionary<string, JsonElement>? incoming, bool regenerateBridgeToken = false)
    {
        await _installGate.WaitAsync();
        try
        {
            string stateJson;
            lock (_lock)
            {
                if (!_loaded.TryGetValue(id, out var plugin)) return $"plugin '{id}' is not installed";
                var updated = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var opt in plugin.Manifest.Settings)
                {
                    var isSecret = opt.Secret || opt.Type == "password";
                    JsonElement newVal = default;
                    var hasIncoming = incoming is not null && incoming.TryGetValue(opt.Key, out newVal);
                    if (isSecret)
                    {
                        if (!hasIncoming || (newVal.ValueKind == JsonValueKind.String && newVal.GetString() == SecretPlaceholder))
                        {
                            if (plugin.State.Settings.TryGetValue(opt.Key, out var prevVal)) updated[opt.Key] = prevVal.Clone();
                        }
                        else if (newVal.ValueKind == JsonValueKind.Null || (newVal.ValueKind == JsonValueKind.String && newVal.GetString() == ""))
                        {
                            // cleared explicitly
                        }
                        else
                        {
                            updated[opt.Key] = newVal.Clone();
                        }
                    }
                    else if (hasIncoming && newVal.ValueKind != JsonValueKind.Undefined)
                    {
                        updated[opt.Key] = newVal.Clone();
                    }
                    else if (opt.Default is { } def)
                    {
                        updated[opt.Key] = def.Clone();
                    }
                }

                plugin.State.Settings = updated;
                if (regenerateBridgeToken && plugin.Manifest.Permissions.Contains("bridge"))
                    plugin.State.BridgeToken = NewToken();
                _state.Plugins[id] = plugin.State;
                stateJson = SnapshotStateJsonLocked();
            }
            WriteStateFile(stateJson);
            ReloadPluginsFromDisk();

            bool active;
            lock (_lock) active = _loaded.TryGetValue(id, out var refreshed) && refreshed.Active;

            await StopSidecarAsync(id);
            if (active) EnsureActiveSidecarsStarted();
            _push.Emit("refresh");
            return null;
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<string?> UninstallAsync(string id)
    {
        if (!PluginManifestValidator.IsValidId(id)) return "invalid plugin id";
        await _installGate.WaitAsync();
        try
        {
            await StopSidecarAsync(id);
            var dir = Path.Combine(_pluginsDir, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string stateJson;
            lock (_lock)
            {
                _state.Plugins.Remove(id);
                stateJson = SnapshotStateJsonLocked();
            }
            WriteStateFile(stateJson);
            ReloadPluginsFromDisk();
            foreach (var key in _widgetStates.Keys.Where(k => k.StartsWith(id + ":", StringComparison.Ordinal)).ToList())
                _widgetStates.TryRemove(key, out _);
            _bridgeQueues.TryRemove(id, out _);
            _push.Emit("refresh");
            return null;
        }
        finally
        {
            _installGate.Release();
        }
    }

    public bool AuthorizeBridgeRequest(PathString path, string? authHeader)
    {
        if (!path.StartsWithSegments("/api/plugins", out var rest)) return false;
        var parts = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not [var id, "bridge"]) return false;
        lock (_lock)
        {
            if (!_loaded.TryGetValue(id, out var p) || !p.Active) return false;
            if (!p.Manifest.Permissions.Contains("bridge") || string.IsNullOrEmpty(p.State.BridgeToken)) return false;
            return Lan.Matches(authHeader, p.State.BridgeToken);
        }
    }

    public async Task<(bool Ok, string? Error, object? Result)> HandleBridgePostAsync(string id, JsonElement payload, CancellationToken ct)
    {
        LoadedPlugin? plugin;
        lock (_lock) _loaded.TryGetValue(id, out plugin);
        if (plugin is null || !plugin.Active) return (false, "plugin is not active", null);
        if (!plugin.Manifest.Permissions.Contains("bridge")) return (false, "plugin does not declare bridge permission", null);

        var widget = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("widget", out var wEl) && wEl.ValueKind == JsonValueKind.String
            ? wEl.GetString()
            : plugin.Manifest.Widgets.FirstOrDefault()?.Type;

        if (plugin.Manifest.Backend.Kind == "sidecar" && _sidecars.TryGetValue(id, out var runner))
        {
            var reply = await runner.SendRequestAsync("bridge", new { widget, payload }, ct);
            if (reply is not null && reply.Value.TryGetProperty("data", out var dataEl))
                StoreWidgetState(id, widget, dataEl.Clone());
            else
                StoreWidgetState(id, widget, payload.Clone());
        }
        else
        {
            StoreWidgetState(id, widget, payload.Clone());
        }

        _push.Emit("plugin", JsonSerializer.SerializeToElement(new { plugin = id, widget }));
        return (true, null, new { ok = true });
    }

    public object DrainBridgeActions(string id)
    {
        if (!_bridgeQueues.TryGetValue(id, out var q)) return new { actions = Array.Empty<BridgeActionItem>() };
        var list = new List<BridgeActionItem>();
        while (q.TryDequeue(out var item)) list.Add(item);
        return new { actions = list };
    }

    public async Task<object> GetWidgetStateAsync(string pluginId, string pageId, string widgetId, bool force, CancellationToken ct)
    {
        LoadedPlugin? plugin;
        lock (_lock) _loaded.TryGetValue(pluginId, out plugin);
        if (plugin is null)
            return new { ok = false, status = "missing", error = $"Plugin '{pluginId}' is not installed" };
        if (!plugin.Active)
            return new { ok = false, status = "disabled", error = plugin.LoadError ?? $"Plugin '{plugin.Manifest.Name}' is disabled" };

        var resolved = ResolveConfiguredWidget(pageId, widgetId, plugin);
        if (resolved is null)
            return new { ok = false, status = "not_found", error = "Widget not found in settings" };

        var (widgetCfg, widgetDef) = resolved.Value;
        var controlAllowed = widgetCfg.Bool("control") != false;
        var uiMeta = BuildUiMeta(plugin, widgetDef);
        var stateKey = $"{pluginId}:{pageId}/{widgetId}";
        var widgetFallbackKey = $"{pluginId}:{widgetDef.Type}";
        var intervalMs = plugin.Manifest.Backend.IntervalSec * 1000L;

        if (plugin.Manifest.Backend.Kind == "none")
        {
            var cached = _widgetStates.GetValueOrDefault(stateKey) ?? _widgetStates.GetValueOrDefault(widgetFallbackKey);
            return new
            {
                ok = true,
                status = "ready",
                ui = uiMeta,
                controlAllowed,
                at = cached?.At ?? 0,
                data = cached?.Data,
                error = (string?)null,
            };
        }

        var current = _widgetStates.GetValueOrDefault(stateKey) ?? _widgetStates.GetValueOrDefault(widgetFallbackKey);
        var now = NowMs();
        var stale = current is null || force || (now - current.At >= intervalMs);
        if (stale && _pollingWidgets.TryAdd(stateKey, 0))
        {
            var pollTask = PollWidgetAsync(plugin, pageId, widgetId, widgetCfg, widgetDef, stateKey, ct);
            if (current is null || force)
                await Task.WhenAny(pollTask, Task.Delay(2500, ct));
        }

        var latest = _widgetStates.GetValueOrDefault(stateKey) ?? _widgetStates.GetValueOrDefault(widgetFallbackKey);
        return new
        {
            ok = latest?.Error is null,
            status = latest is null ? "loading" : (latest.Error is null ? "ready" : "error"),
            ui = uiMeta,
            controlAllowed,
            at = latest?.At ?? 0,
            data = latest?.Data,
            error = latest?.Error,
        };
    }

    public async Task<(int StatusCode, object Body)> ActAsync(string pluginId, string pageId, string widgetId, string op, string? arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(op)) return (400, new { ok = false, error = "action 'op' is required" });
        LoadedPlugin? plugin;
        lock (_lock) _loaded.TryGetValue(pluginId, out plugin);
        if (plugin is null || !plugin.Active) return (404, new { ok = false, error = "plugin is not active" });

        var resolved = ResolveConfiguredWidget(pageId, widgetId, plugin);
        if (resolved is null) return (404, new { ok = false, error = "widget not found" });
        var (widgetCfg, widgetDef) = resolved.Value;
        if (widgetCfg.Bool("control") == false)
            return (403, new { ok = false, error = "control from phone is disabled in widget settings" });

        var stateKey = $"{pluginId}:{pageId}/{widgetId}";
        var publicOpts = FilterPublicWidgetOptions(widgetDef.Type, widgetCfg.Options) ?? [];

        try
        {
            switch (plugin.Manifest.Backend.Kind)
            {
                case "none":
                    if (!plugin.Manifest.Permissions.Contains("bridge"))
                        return (400, new { ok = false, error = "plugin does not support actions" });
                    var q = _bridgeQueues.GetOrAdd(pluginId, _ => new ConcurrentQueue<BridgeActionItem>());
                    while (q.Count >= MaxBridgeQueue && q.TryDequeue(out _)) { }
                    q.Enqueue(new BridgeActionItem(op, arg, widgetDef.Type, pageId, widgetId, NowMs()));
                    return (200, new { ok = true, queued = true });

                case "http":
                {
                    if (plugin.Manifest.Backend.Actions is null || !plugin.Manifest.Backend.Actions.TryGetValue(op, out var actDef))
                        return (400, new { ok = false, error = $"unknown action '{op}'" });
                    var url = InterpolateUrl(actDef.Url, plugin.State.Settings, publicOpts, op, arg);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        return (400, new { ok = false, error = "invalid action url" });
                    var method = new HttpMethod(actDef.Method.ToUpperInvariant());
                    using var res = await SendSafeAsync(
                        _http,
                        (targetUri, sameOrigin, forceGet) =>
                        {
                            var req = new HttpRequestMessage(forceGet ? HttpMethod.Get : method, targetUri);
                            if (sameOrigin && actDef.Headers is not null)
                                foreach (var (hk, hv) in actDef.Headers)
                                    req.Headers.TryAddWithoutValidation(hk, Interpolate(hv, plugin.State.Settings, publicOpts, op, arg));
                            if (!forceGet && !string.IsNullOrEmpty(actDef.Body) && method != HttpMethod.Get)
                                req.Content = new StringContent(Interpolate(actDef.Body, plugin.State.Settings, publicOpts, op, arg), Encoding.UTF8, "application/json");
                            return req;
                        },
                        uri,
                        externalMarketOnly: false,
                        HttpCompletionOption.ResponseContentRead,
                        ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    if (!res.IsSuccessStatusCode)
                        return (502, new { ok = false, error = $"HTTP {(int)res.StatusCode}: {Truncate(body, 200)}" });
                    JsonElement? parsed = TryParseJson(body);
                    if (parsed is { } p) _widgetStates[stateKey] = new CachedWidgetState(p, NowMs(), null);
                    else await PollHttpAsync(plugin, widgetCfg, widgetDef, stateKey, ct);
                    _push.Emit("plugin", JsonSerializer.SerializeToElement(new { plugin = pluginId, widget = widgetDef.Type }));
                    return (200, new { ok = true, data = _widgetStates.GetValueOrDefault(stateKey)?.Data });
                }

                case "command":
                {
                    var payload = new
                    {
                        @event = "act",
                        pluginId,
                        pageId,
                        widgetId,
                        widgetType = widgetDef.Type,
                        op,
                        arg,
                        settings = plugin.State.Settings,
                        options = publicOpts,
                    };
                    var (data, err) = await RunCommandBackendAsync(plugin, payload, ct);
                    if (err is not null) return (502, new { ok = false, error = err });
                    if (data is { } d) _widgetStates[stateKey] = new CachedWidgetState(d, NowMs(), null);
                    _push.Emit("plugin", JsonSerializer.SerializeToElement(new { plugin = pluginId, widget = widgetDef.Type }));
                    return (200, new { ok = true, data });
                }

                case "sidecar":
                {
                    var runner = EnsureSidecarStarted(plugin);
                    if (runner is null) return (503, new { ok = false, error = "sidecar failed to start" });
                    var reply = await runner.SendRequestAsync("act", new
                    {
                        pageId,
                        widgetId,
                        widgetType = widgetDef.Type,
                        op,
                        arg,
                        options = publicOpts,
                    }, ct);
                    if (reply is null) return (504, new { ok = false, error = "sidecar timed out" });
                    if (reply.Value.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                    {
                        var errMsg = reply.Value.TryGetProperty("error", out var eEl) ? eEl.GetString() : "action failed";
                        return (400, new { ok = false, error = errMsg });
                    }
                    if (reply.Value.TryGetProperty("data", out var dataEl))
                    {
                        _widgetStates[stateKey] = new CachedWidgetState(dataEl.Clone(), NowMs(), null);
                        _push.Emit("plugin", JsonSerializer.SerializeToElement(new { plugin = pluginId, widget = widgetDef.Type }));
                    }
                    return (200, new { ok = true, data = _widgetStates.GetValueOrDefault(stateKey)?.Data });
                }

                default:
                    return (400, new { ok = false, error = "unsupported backend" });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (502, new { ok = false, error = ex.Message });
        }
    }

    private async Task PollWidgetAsync(LoadedPlugin plugin, string pageId, string widgetId, WidgetConfig widgetCfg, PluginWidgetDef widgetDef, string stateKey, CancellationToken ct)
    {
        try
        {
            switch (plugin.Manifest.Backend.Kind)
            {
                case "http":
                    await PollHttpAsync(plugin, widgetCfg, widgetDef, stateKey, ct);
                    break;
                case "command":
                {
                    var publicOpts = FilterPublicWidgetOptions(widgetDef.Type, widgetCfg.Options) ?? [];
                    var payload = new
                    {
                        @event = "poll",
                        pluginId = plugin.Manifest.Id,
                        pageId,
                        widgetId,
                        widgetType = widgetDef.Type,
                        settings = plugin.State.Settings,
                        options = publicOpts,
                    };
                    var (data, err) = await RunCommandBackendAsync(plugin, payload, ct);
                    _widgetStates[stateKey] = err is null
                        ? new CachedWidgetState(data, NowMs(), null)
                        : new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), err);
                    break;
                }
                case "sidecar":
                {
                    var runner = EnsureSidecarStarted(plugin);
                    if (runner is null)
                    {
                        _widgetStates[stateKey] = new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), "sidecar failed to start");
                        break;
                    }
                    var publicOpts = FilterPublicWidgetOptions(widgetDef.Type, widgetCfg.Options) ?? [];
                    var reply = await runner.SendRequestAsync("poll", new { pageId, widgetId, widgetType = widgetDef.Type, options = publicOpts }, ct);
                    if (reply is null)
                    {
                        _widgetStates[stateKey] = new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), "sidecar did not answer");
                    }
                    else if (reply.Value.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(errEl.GetString()))
                    {
                        _widgetStates[stateKey] = new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), errEl.GetString());
                    }
                    else if (reply.Value.TryGetProperty("data", out var dataEl))
                    {
                        _widgetStates[stateKey] = new CachedWidgetState(dataEl.Clone(), NowMs(), null);
                    }
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _widgetStates[stateKey] = new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), ex.Message);
        }
        finally
        {
            _pollingWidgets.TryRemove(stateKey, out _);
        }
    }

    private async Task PollHttpAsync(LoadedPlugin plugin, WidgetConfig widgetCfg, PluginWidgetDef widgetDef, string stateKey, CancellationToken ct)
    {
        var publicOpts = FilterPublicWidgetOptions(widgetDef.Type, widgetCfg.Options) ?? [];
        var url = InterpolateUrl(plugin.Manifest.Backend.Url ?? "", plugin.State.Settings, publicOpts, null, null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _widgetStates[stateKey] = new CachedWidgetState(null, NowMs(), "Invalid URL in plugin manifest or settings");
            return;
        }
        using var res = await SendSafeAsync(
            _http,
            (targetUri, sameOrigin, _) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
                if (sameOrigin && plugin.Manifest.Backend.Headers is not null)
                    foreach (var (hk, hv) in plugin.Manifest.Backend.Headers)
                        req.Headers.TryAddWithoutValidation(hk, Interpolate(hv, plugin.State.Settings, publicOpts, null, null));
                return req;
            },
            uri,
            externalMarketOnly: false,
            HttpCompletionOption.ResponseContentRead,
            ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _widgetStates[stateKey] = new CachedWidgetState(_widgetStates.GetValueOrDefault(stateKey)?.Data, NowMs(), $"HTTP {(int)res.StatusCode}");
            return;
        }
        var parsed = TryParseJson(body);
        _widgetStates[stateKey] = parsed is { } p
            ? new CachedWidgetState(p, NowMs(), null)
            : new CachedWidgetState(JsonSerializer.SerializeToElement(new { value = Truncate(body.Trim(), 2000) }), NowMs(), null);
    }

    private async Task<(JsonElement? Data, string? Error)> RunCommandBackendAsync(LoadedPlugin plugin, object payload, CancellationToken ct)
    {
        var psi = CreateBackendStartInfo(plugin);
        if (psi is null) return (null, $"Runtime '{plugin.Manifest.Backend.Runtime}' was not found");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var p = Process.Start(psi);
        if (p is null) return (null, "Could not start plugin process");
        JobObject.Track(p);

        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload, JsonLineOpts).AsMemory(), ct);
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var outTask = ReadLimitedAsync(p.StandardOutput, MaxCommandOutputBytes, cts.Token);
        var errTask = ReadLimitedAsync(p.StandardError, 8192, cts.Token);

        try
        {
            await p.WaitForExitAsync(cts.Token);
            var stdout = (await outTask).Trim();
            var stderr = (await errTask).Trim();
            if (p.ExitCode != 0)
                return (null, !string.IsNullOrEmpty(stderr) ? Truncate(stderr, 300) : $"Exited with code {p.ExitCode}");
            var parsed = TryParseJson(stdout);
            if (parsed is { } doc)
            {
                if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(e.GetString()))
                    return (null, e.GetString());
                return (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("data", out var d) ? d.Clone() : doc, null);
            }
            return (JsonSerializer.SerializeToElement(new { value = Truncate(stdout, 2000) }), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (null, "Plugin timed out after 20 s");
        }
    }

    private void StoreWidgetState(string pluginId, string? widgetType, JsonElement data)
    {
        var key = $"{pluginId}:{widgetType ?? pluginId}";
        _widgetStates[key] = new CachedWidgetState(data, NowMs(), null);
        // also update any page instances mapped to that widget type
        var cfg = _cfg.Get();
        foreach (var p in cfg.Pages)
        {
            foreach (var w in p.Widgets ?? [])
            {
                if (w.Type == widgetType || (widgetType is null && Catalog.PluginIdOf(w.Type) == pluginId))
                    _widgetStates[$"{pluginId}:{p.Id}/{w.Id}"] = new CachedWidgetState(data, NowMs(), null);
                foreach (var child in StackWidgets.Children(w))
                    if (child.Type == widgetType || (widgetType is null && Catalog.PluginIdOf(child.Type) == pluginId))
                        _widgetStates[$"{pluginId}:{p.Id}/{w.Id}:{child.Id}"] = new CachedWidgetState(data, NowMs(), null);
            }
        }
    }

    private static object BuildUiMeta(LoadedPlugin plugin, PluginWidgetDef def) =>
        new
        {
            kind = def.Ui.Kind,
            name = def.Name,
            icon = def.Icon,
            html = def.Ui.Kind == "html" ? plugin.HtmlFiles.GetValueOrDefault(def.Type) : null,
        };

    private (WidgetConfig Widget, PluginWidgetDef Def)? ResolveConfiguredWidget(string pageId, string widgetId, LoadedPlugin plugin)
    {
        var page = _cfg.Get().Pages.FirstOrDefault(p => p.Id == pageId);
        if (page?.Widgets is null) return null;

        var direct = page.Widgets.FirstOrDefault(w => w.Id == widgetId);
        if (direct is not null)
        {
            var def = plugin.Manifest.Widgets.FirstOrDefault(x => x.Type == direct.Type);
            return def is null ? null : (direct, def);
        }

        var colon = widgetId.IndexOf(':');
        if (colon > 0)
        {
            var stackId = widgetId[..colon];
            var childId = widgetId[(colon + 1)..];
            var stackWidget = page.Widgets.FirstOrDefault(w => w.Id == stackId && w.Type == "stack");
            if (stackWidget is not null)
            {
                var child = StackWidgets.Children(stackWidget).FirstOrDefault(c => c.Id == childId);
                if (child is not null)
                {
                    var def = plugin.Manifest.Widgets.FirstOrDefault(x => x.Type == child.Type);
                    if (def is not null)
                    {
                        var synthetic = new WidgetConfig { Id = widgetId, Type = child.Type, Options = child.Options };
                        return (synthetic, def);
                    }
                }
            }
        }
        return null;
    }

    private void EnsureActiveSidecarsStarted()
    {
        List<LoadedPlugin> sidecarPlugins;
        lock (_lock) sidecarPlugins = _loaded.Values.Where(p => p.Active && p.Manifest.Backend.Kind == "sidecar").ToList();
        foreach (var p in sidecarPlugins)
            EnsureSidecarStarted(p);
    }

    private SidecarRunner? EnsureSidecarStarted(LoadedPlugin plugin) =>
        _sidecars.GetOrAdd(plugin.Manifest.Id, _ =>
        {
            var runner = new SidecarRunner(this, plugin, _log);
            runner.Start();
            return runner;
        });

    private async Task StopSidecarAsync(string id)
    {
        if (_sidecars.TryRemove(id, out var runner))
            await runner.StopAsync();
    }

    private void LoadState()
    {
        PluginsStateFile loaded = new();
        if (File.Exists(_statePath))
        {
            try { loaded = JsonSerializer.Deserialize<PluginsStateFile>(File.ReadAllText(_statePath), JsonReadOpts) ?? new(); }
            catch { loaded = new(); }
        }
        loaded.Plugins ??= new(StringComparer.OrdinalIgnoreCase);
        lock (_lock) _state = loaded;
    }

    private string SnapshotStateJsonLocked() => JsonSerializer.Serialize(_state, JsonWriteOpts);

    private void WriteStateFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _statePath, overwrite: true);
    }

    private sealed record DiskPluginScan(
        string Id,
        string Dir,
        PluginManifest? Manifest,
        string? Error,
        IReadOnlyDictionary<string, string> HtmlFiles);

    internal void ReloadPluginsFromDisk()
    {
        var scanned = new List<DiskPluginScan>();
        if (Directory.Exists(_pluginsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(_pluginsDir))
            {
                var folderName = Path.GetFileName(dir);
                if (folderName.StartsWith('.')) continue;
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonReadOpts);
                    var err = PluginManifestValidator.Validate(manifest, dir);
                    if (err is not null || manifest is null)
                    {
                        var fallbackId = PluginManifestValidator.IsValidId(manifest?.Id) ? manifest!.Id : folderName;
                        scanned.Add(new DiskPluginScan(fallbackId, dir, manifest, err ?? "invalid plugin.json", new Dictionary<string, string>()));
                        continue;
                    }

                    var htmlFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var w in manifest.Widgets)
                    {
                        if (w.Ui.Kind == "html"
                            && PluginManifestValidator.ResolveSafeChildPath(dir, w.Ui.File) is { } htmlPath
                            && File.Exists(htmlPath))
                        {
                            try { htmlFiles[w.Type] = File.ReadAllText(htmlPath, Encoding.UTF8); } catch { }
                        }
                    }

                    scanned.Add(new DiskPluginScan(manifest.Id, dir, manifest, null, htmlFiles));
                }
                catch (Exception ex)
                {
                    if (PluginManifestValidator.IsValidId(folderName))
                        scanned.Add(new DiskPluginScan(folderName, dir, null, ex.Message, new Dictionary<string, string>()));
                }
            }
        }

        lock (_lock)
        {
            var next = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in scanned)
            {
                if (s.Error is not null || s.Manifest is null)
                {
                    var stErr = _state.Plugins.TryGetValue(s.Id, out var prevErr) ? prevErr : new StoredPluginState();
                    next[s.Id] = new LoadedPlugin(
                        s.Manifest ?? new PluginManifest { Id = s.Id, Name = s.Id },
                        s.Dir,
                        stErr,
                        s.HtmlFiles,
                        OsOk: false,
                        AgentVersionOk: false,
                        NeedsApproval: false,
                        Active: false,
                        LoadError: s.Error ?? "invalid plugin.json");
                    continue;
                }

                var st = _state.Plugins.TryGetValue(s.Manifest.Id, out var found)
                    ? found
                    : new StoredPluginState { Enabled = false, Version = s.Manifest.Version, Source = "local" };

                var osOk = s.Manifest.Os.Count == 0 || s.Manifest.Os.Contains(AppPaths.Os, StringComparer.OrdinalIgnoreCase);
                var versionOk = PluginManifestValidator.IsCompatibleAgentVersion(s.Manifest.MinAgentVersion);
                var approvedSet = new HashSet<string>(st.ApprovedPermissions ?? [], StringComparer.Ordinal);
                var needsApproval = s.Manifest.Permissions.Any(p => !approvedSet.Contains(p));
                var active = st.Enabled && osOk && versionOk && !needsApproval;

                next[s.Manifest.Id] = new LoadedPlugin(s.Manifest, s.Dir, st, s.HtmlFiles, osOk, versionOk, needsApproval, active, null);
            }

            _loaded = next;
            GlobalWidgetMinSizes.Clear();
            GlobalPublicOptionKeys.Clear();
            foreach (var p in next.Values.Where(x => x.Active))
            {
                foreach (var w in p.Manifest.Widgets)
                {
                    GlobalWidgetMinSizes[w.Type] = (w.MinW, w.MinH);
                    GlobalPublicOptionKeys[w.Type] = w.Options
                        .Where(o => !o.Secret && o.Type != "password")
                        .Select(o => o.Key)
                        .ToHashSet(StringComparer.Ordinal);
                }
            }
        }
    }

    private static string[] DefaultIndexUrls(string repo)
    {
        var normalized = PluginManifestValidator.NormalizeRepo(repo);
        var parts = normalized.Split('/', 2);
        return
        [
            $"https://{parts[0]}.github.io/{parts[1]}/index.json",
            $"https://raw.githubusercontent.com/{normalized}/main/index.json",
        ];
    }

    private static (bool Ok, string? Hint) CheckRuntime(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime) || runtime is "none" or "shell" or "binary")
            return (true, null);
        if (runtime == "python3")
        {
            var hit = ToolProcess.Find("python3") ?? ToolProcess.Find("python");
            return hit is not null ? (true, null) : (false, "Requires Python 3 in PATH");
        }
        if (runtime == "node")
        {
            var hit = ToolProcess.Find("node");
            return hit is not null ? (true, null) : (false, "Requires Node.js in PATH");
        }
        if (runtime == "pwsh")
        {
            var hit = ToolProcess.Find("pwsh") ?? (OperatingSystem.IsWindows() ? ToolProcess.Find("powershell") : null);
            return hit is not null ? (true, null) : (false, "Requires PowerShell in PATH");
        }
        return (false, $"Unknown runtime '{runtime}'");
    }

    private static ProcessStartInfo? CreateBackendStartInfo(LoadedPlugin plugin)
    {
        var entry = PluginManifestValidator.ResolveSafeChildPath(plugin.Dir, plugin.Manifest.Backend.Entry);
        if (entry is null || !File.Exists(entry)) return null;

        string? exe = null;
        string[] args = [];
        switch (plugin.Manifest.Backend.Runtime)
        {
            case "binary":
                exe = entry;
                break;
            case "python3":
                exe = ToolProcess.Find("python3") ?? ToolProcess.Find("python");
                args = ["-u", entry];
                break;
            case "node":
                exe = ToolProcess.Find("node");
                args = [entry];
                break;
            case "pwsh":
                exe = ToolProcess.Find("pwsh") ?? (OperatingSystem.IsWindows() ? ToolProcess.Find("powershell") : null);
                args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry];
                break;
            case "shell":
                if (OperatingSystem.IsWindows())
                {
                    exe = ToolProcess.Find("powershell") ?? "powershell.exe";
                    args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry];
                }
                else
                {
                    exe = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
                    args = [entry];
                }
                break;
        }
        if (exe is null) return null;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = plugin.Dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string? ExtractZipSafely(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar) ? destFull : destFull + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxZipEntries)
            return $"plugin archive exceeds the {MaxZipEntries} entry limit";

        long totalUncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                return "symbolic links are not permitted inside plugin archives";
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized)) continue;
            var target = Path.GetFullPath(Path.Combine(destFull, normalized));
            if (!target.StartsWith(destWithSep, cmp))
                return "unsafe path inside plugin archive";
            if (normalized.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = entry.Open();
            using var dst = File.Create(target);
            var buf = new byte[64 * 1024];
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                totalUncompressed += read;
                if (totalUncompressed > MaxExtractedBytes)
                    return $"extracted plugin exceeds the {MaxExtractedBytes / (1024 * 1024)} MB limit";
                dst.Write(buf, 0, read);
            }
        }
        return null;
    }

    private static string UnwrapSingleRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "plugin.json"))) return dir;
        var subDirs = Directory.GetDirectories(dir);
        var files = Directory.GetFiles(dir);
        if (files.Length == 0 && subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "plugin.json")))
            return subDirs[0];
        return dir;
    }

    private static void MoveOrCopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    internal static string InterpolateUrl(
        string template,
        Dictionary<string, JsonElement> settings,
        Dictionary<string, JsonElement> options,
        string? op,
        string? arg)
    {
        var queryIdx = template.IndexOf('?');
        return TemplateTokenRegex().Replace(template, m =>
        {
            var kind = m.Groups[1].Value;
            var key = m.Groups[2].Value;
            string raw = "";
            if (kind == "settings" && settings.TryGetValue(key, out var sv)) raw = ElementToString(sv);
            else if (kind == "options" && options.TryGetValue(key, out var ov)) raw = ElementToString(ov);
            else if (kind == "action" && key == "op") raw = op ?? "";
            else if (kind == "action" && key == "arg") raw = arg ?? "";

            if ((kind is "settings" or "options") && m.Index == 0)
            {
                var after = m.Index + m.Length;
                return template.Length > after && template[after] == '/' ? raw.TrimEnd('/') : raw;
            }
            if (kind == "action" || (queryIdx >= 0 && m.Index > queryIdx))
                return Uri.EscapeDataString(raw);

            var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => seg is "." or ".." ? "" : Uri.EscapeDataString(seg))
                .Where(seg => seg.Length > 0);
            return string.Join('/', segments);
        });
    }

    public static bool IsBlockedExternalIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;
            if (b[0] == 10) return true;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            if (b[0] >= 224) return true;
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6None))
                return true;
            var b = ip.GetAddressBytes();
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            if ((b[0] & 0xFE) == 0xFC) return true;
            if (b[0] == 0xFF) return true;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;
            return false;
        }
        return true;
    }

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var ips = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        if (ips.Length == 0 || ips.Any(IsBlockedExternalIp))
            throw new HttpRequestException($"Blocked connection to non-public address for '{ctx.DnsEndPoint.Host}'");
        SocketException? lastEx = null;
        foreach (var ip in ips)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(ip, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastEx = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw (Exception?)lastEx ?? new HttpRequestException($"Failed to connect to '{ctx.DnsEndPoint.Host}'");
    }

    private static async Task<HttpResponseMessage> SendSafeAsync(
        HttpClient client,
        Func<Uri, bool, bool, HttpRequestMessage> buildReq,
        Uri startUri,
        bool externalMarketOnly,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        ValidateUri(startUri, externalMarketOnly);
        var cur = startUri;
        var forceGet = false;
        for (var hop = 0; hop <= 5; hop++)
        {
            var sameOrigin = hop == 0 || (cur.Scheme == startUri.Scheme
                && cur.Host.Equals(startUri.Host, StringComparison.OrdinalIgnoreCase)
                && cur.Port == startUri.Port);
            using var req = buildReq(cur, sameOrigin, forceGet);
            var res = await client.SendAsync(req, completion, ct);
            var code = (int)res.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || res.Headers.Location is null)
                return res;

            var loc = res.Headers.Location;
            var next = loc.IsAbsoluteUri ? loc : new Uri(cur, loc);
            res.Dispose();

            if (cur.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("Blocked HTTPS to HTTP redirect downgrade");
            ValidateUri(next, externalMarketOnly);
            if (code is 301 or 302 or 303)
                forceGet = true;
            cur = next;
        }
        throw new HttpRequestException("Too many HTTP redirects");
    }

    private static void ValidateUri(Uri uri, bool externalMarketOnly)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException($"Unsupported URL scheme '{uri.Scheme}'");
        if (externalMarketOnly)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("Marketplace URLs must use HTTPS");
            if (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IsBlockedExternalIp(ip))
                throw new HttpRequestException("Blocked non-public marketplace IP address");
        }
    }

    private static string Interpolate(
        string template,
        Dictionary<string, JsonElement> settings,
        Dictionary<string, JsonElement> options,
        string? op,
        string? arg,
        bool urlEncode = false)
    {
        return TemplateTokenRegex().Replace(template, m =>
        {
            var kind = m.Groups[1].Value;
            var key = m.Groups[2].Value;
            string raw = "";
            if (kind == "settings" && settings.TryGetValue(key, out var sv)) raw = ElementToString(sv);
            else if (kind == "options" && options.TryGetValue(key, out var ov)) raw = ElementToString(ov);
            else if (kind == "action" && key == "op") raw = op ?? "";
            else if (kind == "action" && key == "arg") raw = arg ?? "";
            return urlEncode ? Uri.EscapeDataString(raw) : raw;
        });
    }

    private static string ElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static JsonElement? TryParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { using var doc = JsonDocument.Parse(raw); return doc.RootElement.Clone(); }
        catch { return null; }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buf, ct)) > 0)
        {
            var remaining = maxBytes - sb.Length;
            if (remaining > 0) sb.Append(buf, 0, Math.Min(read, remaining));
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex(@"\{(settings|options|action)\.([A-Za-z0-9_.-]+)\}")]
    private static partial Regex TemplateTokenRegex();

    internal sealed record LoadedPlugin(
        PluginManifest Manifest,
        string Dir,
        StoredPluginState State,
        IReadOnlyDictionary<string, string> HtmlFiles,
        bool OsOk,
        bool AgentVersionOk,
        bool NeedsApproval,
        bool Active,
        string? LoadError);

    internal sealed record CachedWidgetState(JsonElement? Data, long At, string? Error);
    public sealed record BridgeActionItem(string Op, string? Arg, string Widget, string PageId, string WidgetId, long At);

    private sealed class SidecarRunner(PluginService owner, LoadedPlugin plugin, ILogger log)
    {
        private readonly CancellationTokenSource _stopCts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process? _process;
        private Task? _loopTask;

        public void Start() => _loopTask = Task.Run(RunLoopAsync);

        public async Task StopAsync()
        {
            _stopCts.Cancel();
            _ready.TrySetResult();
            KillProcess();
            if (_loopTask is not null)
                try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
            _pending.Clear();
            _stopCts.Dispose();
            _writeLock.Dispose();
        }

        public async Task<JsonElement?> SendRequestAsync(string type, object payload, CancellationToken ct)
        {
            var reqId = Guid.NewGuid().ToString("N")[..12];
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[reqId] = tcs;
            try
            {
                if (!_ready.Task.IsCompleted)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
                    waitCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await _ready.Task.WaitAsync(waitCts.Token);
                }
                var line = JsonSerializer.Serialize(new { type, reqId, payload }, JsonLineOpts);
                await _writeLock.WaitAsync(ct);
                try
                {
                    if (_process is null || _process.HasExited) return null;
                    await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
                    await _process.StandardInput.FlushAsync(ct);
                }
                finally { _writeLock.Release(); }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                return await tcs.Task.WaitAsync(timeout.Token);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                _pending.TryRemove(reqId, out _);
            }
        }

        private async Task RunLoopAsync()
        {
            var backoffSec = new[] { 2, 5, 15, 60 };
            var attempt = 0;

            while (!_stopCts.IsCancellationRequested)
            {
                var psi = CreateBackendStartInfo(plugin);
                if (psi is null)
                {
                    log.LogWarning("plugin sidecar {Id} could not resolve runtime {Runtime}", plugin.Manifest.Id, plugin.Manifest.Backend.Runtime);
                    return;
                }
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                Process? p = null;
                try
                {
                    p = Process.Start(psi);
                    if (p is null) return;
                    JobObject.Track(p);
                    _process = p;

                    var initLine = JsonSerializer.Serialize(new
                    {
                        type = "init",
                        pluginId = plugin.Manifest.Id,
                        version = plugin.Manifest.Version,
                        settings = plugin.State.Settings,
                    }, JsonLineOpts);
                    await _writeLock.WaitAsync(_stopCts.Token);
                    try
                    {
                        await p.StandardInput.WriteLineAsync(initLine.AsMemory(), _stopCts.Token);
                        await p.StandardInput.FlushAsync(_stopCts.Token);
                    }
                    finally { _writeLock.Release(); }
                    _ready.TrySetResult();

                    var stderrTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (await p.StandardError.ReadLineAsync(_stopCts.Token) is { } errLine)
                                if (!string.IsNullOrWhiteSpace(errLine))
                                    log.LogDebug("plugin[{Id}] stderr: {Line}", plugin.Manifest.Id, Truncate(errLine, 300));
                        }
                        catch { }
                    });

                    while (!_stopCts.IsCancellationRequested)
                    {
                        var line = await p.StandardOutput.ReadLineAsync(_stopCts.Token);
                        if (line is null) break;
                        attempt = 0;
                        HandleStdoutLine(line);
                    }
                    await Task.WhenAny(stderrTask, Task.Delay(300));
                }
                catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogWarning("plugin sidecar {Id} error: {Msg}", plugin.Manifest.Id, ex.Message);
                }
                finally
                {
                    _process = null;
                    foreach (var kv in _pending.Keys.ToList())
                        if (_pending.TryRemove(kv, out var tcs)) tcs.TrySetResult(null);
                    try { p?.Dispose(); } catch { }
                }

                if (_stopCts.IsCancellationRequested) break;
                var delay = backoffSec[Math.Min(attempt, backoffSec.Length - 1)];
                attempt++;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), _stopCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void HandleStdoutLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "reply" && root.TryGetProperty("reqId", out var r) && r.GetString() is { } reqId)
                {
                    if (_pending.TryRemove(reqId, out var tcs))
                        tcs.TrySetResult(root.Clone());
                }
                else if (type == "state" && root.TryGetProperty("data", out var dataEl))
                {
                    var widget = root.TryGetProperty("widget", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                    owner.StoreWidgetState(plugin.Manifest.Id, widget, dataEl.Clone());
                    owner._push.Emit("plugin", JsonSerializer.SerializeToElement(new { plugin = plugin.Manifest.Id, widget }));
                }
            }
            catch { }
        }

        private void KillProcess()
        {
            try
            {
                var p = _process;
                if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }
}
