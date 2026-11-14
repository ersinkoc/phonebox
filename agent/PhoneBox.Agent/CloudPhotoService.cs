using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

/// <summary>
/// Fetches, browses, validates, and caches photos from local/synced folders, iCloud Shared Albums,
/// OneDrive, Google Drive, Dropbox, and Nextcloud public shares on demand (zero background polling).
/// </summary>
public sealed partial class CloudPhotoService : IDisposable
{
    private const int CacheTtlMs = 15 * 60 * 1000;
    private const int MaxManifestItems = 250;
    private const int MaxItemRegistry = 5000;
    private const int MaxLocalScanEntries = 4000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    private static readonly HashSet<string> CloudPreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"
    };

    internal sealed record CachedManifest(
        PhotoFeedResponse Response,
        long FetchedAtMs,
        string Provider,
        long ProviderEpoch
    );

    private sealed record InFlightRefresh(
        Task<PhotoFeedResponse> Task,
        CancellationTokenSource CancelCts,
        string Provider,
        long ProviderEpoch
    );

    internal sealed record ResolvedPhoto(
        string Id,
        string Provider,
        string RemoteUrlOrId,
        string? LocalFilePath,
        string? LocalRootPath,
        string? ExtraHeaderArg,
        string? FallbackUrl,
        long? TakenAtMs,
        string? Caption,
        string? Album,
        string? SourceLabel,
        bool AllowCloudDownload = false,
        long ProviderEpoch = 0,
        string OwnerProvider = ""
    );

    private static string EffectiveOwnerProvider(ResolvedPhoto item) =>
        string.IsNullOrEmpty(item.OwnerProvider) ? item.Provider : item.OwnerProvider;

    private readonly ConfigStore _config;
    private readonly CloudPhotoAuth _auth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDir;
    private readonly IReadOnlyList<PhotoLocalRoot>? _customLocalRoots;
    private readonly Func<FileInfo, bool>? _isCloudOnlyOverride;
    private readonly Func<string, bool, byte[]>? _localFileReader;
    private readonly TimeSpan _bodyReadTimeout;
    private readonly SemaphoreSlim _materializationGate = new(2, 2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlightRefresh> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedPhoto> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _providerEpochs = new(StringComparer.Ordinal);
    private readonly Queue<string> _itemOrder = new();
    private int _cacheWriteCounter;

    public CloudPhotoService(ConfigStore config, CloudPhotoAuth auth)
        : this(config, auth, SafePhotoHttp.CreateClient(TimeSpan.FromSeconds(25)),
            Path.Combine(AppPaths.LocalDir, "photos-cache"), ownsHttp: true) { }

    internal CloudPhotoService(
        ConfigStore config,
        CloudPhotoAuth auth,
        HttpClient http,
        string cacheDir,
        IEnumerable<PhotoLocalRoot>? customLocalRoots = null,
        bool ownsHttp = false,
        Func<FileInfo, bool>? isCloudOnlyOverride = null,
        Func<string, bool, byte[]>? localFileReader = null,
        TimeSpan? bodyReadTimeout = null)
    {
        _config = config;
        _auth = auth;
        _http = http;
        _cacheDir = cacheDir;
        _customLocalRoots = customLocalRoots?.ToList();
        _ownsHttp = ownsHttp;
        _isCloudOnlyOverride = isCloudOnlyOverride;
        _localFileReader = localFileReader;
        _bodyReadTimeout = bodyReadTimeout ?? TimeSpan.FromSeconds(25);
        _auth.ProviderAuthChanged += InvalidateProvider;
    }

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public PhotoStatusResponse GetStatus()
    {
        var roots = DiscoverLocalRoots();
        var cp = _config.Get().CloudPhotos;
        var providers = CloudPhotoAuth.OAuthProviders.Select(p =>
        {
            var clientConfigured = !string.IsNullOrWhiteSpace(_auth.GetClientId(p));
            var hintMatch = p == "google" ? "google" : p;
            return new PhotoProviderStatus(
                Provider: p,
                ClientConfigured: clientConfigured,
                Connected: _auth.IsConnected(p),
                Account: _auth.GetAccount(p),
                RedirectUri: _auth.RedirectUri(p),
                LocalFolderDetected: roots.Any(r => r.ProviderHint == hintMatch)
            );
        }).ToList();

        _ = cp;
        return new PhotoStatusResponse(providers, roots);
    }

    public IReadOnlyList<PhotoLocalRoot> DiscoverLocalRoots()
    {
        if (_customLocalRoots is not null)
            return _customLocalRoots;

        var candidates = new List<(string Id, string Name, string Hint, string? Path)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);

        candidates.Add(("pictures", "Pictures", "local", pictures));

        // iCloud Photos
        if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("icloud-drive-photos", "iCloud Drive › Photos", "icloud", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs", "Photos")));
            candidates.Add(("icloud-pics", "Pictures › iCloud Photos", "icloud", Path.Combine(pictures, "iCloud Photos")));
        }
        else
        {
            candidates.Add(("icloud-photos", "iCloud Photos", "icloud", Path.Combine(pictures, "iCloud Photos", "Photos")));
            candidates.Add(("icloud-root", "iCloud Photos folder", "icloud", Path.Combine(pictures, "iCloud Photos")));
            candidates.Add(("icloud-drive", "iCloud Drive › Photos", "icloud", Path.Combine(home, "iCloudDrive", "Photos")));
        }

        // OneDrive
        foreach (var envName in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var envDir = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                candidates.Add(($"od-{envName}-pics", "OneDrive › Pictures", "onedrive", Path.Combine(envDir, "Pictures")));
                candidates.Add(($"od-{envName}", "OneDrive", "onedrive", envDir));
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            AddGlobCloudStorage(candidates, Path.Combine(home, "Library", "CloudStorage"), "OneDrive*", "onedrive", "OneDrive");
            AddGlobCloudStorage(candidates, Path.Combine(home, "Library", "CloudStorage"), "GoogleDrive*", "google", "Google Drive");
        }
        candidates.Add(("od-home-pics", "OneDrive › Pictures", "onedrive", Path.Combine(home, "OneDrive", "Pictures")));
        candidates.Add(("od-home", "OneDrive", "onedrive", Path.Combine(home, "OneDrive")));

        // Google Drive
        candidates.Add(("gd-home-my", "Google Drive › My Drive", "google", Path.Combine(home, "Google Drive", "My Drive")));
        candidates.Add(("gd-home", "Google Drive", "google", Path.Combine(home, "Google Drive")));

        // Dropbox (~/.dropbox/info.json first, then common paths)
        TryAddDropboxJsonRoots(candidates, home);
        candidates.Add(("db-cam", "Dropbox › Camera Uploads", "dropbox", Path.Combine(home, "Dropbox", "Camera Uploads")));
        candidates.Add(("db-photos", "Dropbox › Photos", "dropbox", Path.Combine(home, "Dropbox", "Photos")));
        candidates.Add(("db-root", "Dropbox", "dropbox", Path.Combine(home, "Dropbox")));
        if (OperatingSystem.IsMacOS())
            candidates.Add(("db-cs", "Dropbox (CloudStorage)", "dropbox", Path.Combine(home, "Library", "CloudStorage", "Dropbox")));

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<PhotoLocalRoot>();
        foreach (var (id, name, hint, rawPath) in candidates)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            var actual = TryResolveSafeDirectory(rawPath);
            if (actual is null) continue;
            if (seen.Add(actual))
                result.Add(new PhotoLocalRoot(id, name, hint, actual));

            // For CloudStorage/GoogleDrive-*, also add My Drive subfolder if present
            if (hint == "google" && !actual.EndsWith("My Drive", StringComparison.OrdinalIgnoreCase))
            {
                var myDrive = TryResolveSafeDirectory(Path.Combine(actual, "My Drive"));
                if (myDrive is not null && seen.Add(myDrive))
                    result.Add(new PhotoLocalRoot(id + "-mydrive", name + " › My Drive", hint, myDrive));
            }
        }
        return result;
    }

    public async Task<PhotoFeedResponse> GetWidgetFeedAsync(string widgetId, bool refresh, CancellationToken ct)
    {
        var settings = FindWidgetSettings(widgetId);
        if (settings is null)
            return new PhotoFeedResponse(false, "local", "", null, NowMs, false, "Photo frame widget was not found", []);
        return await GetFeedAsync(settings, refresh, ct);
    }

    public Task<PhotoFeedResponse> GetPreviewFeedAsync(PhotoPreviewRequest req, CancellationToken ct)
    {
        var settings = PhotoWidgetOptions.Parse(req.Options);
        return GetFeedAsync(settings, refresh: true, ct);
    }

    public async Task<PhotoTestResponse> TestSourceAsync(PhotoTestRequest req, CancellationToken ct)
    {
        var settings = PhotoWidgetOptions.Parse(req.Options);
        if (!IsSourceConfigured(settings))
            return new PhotoTestResponse(false, MissingSourceMessage(settings.Provider), 0, "");

        var remoteStream = !UsesLocalDirectory(settings);
        var feed = await GetFeedAsync(settings, refresh: true, ct);
        if (!settings.DownloadCloudFiles && feed.Photos.Count == 0 && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                false,
                $"Found {feed.CloudOnlyCount} cloud-only photo{(feed.CloudOnlyCount == 1 ? "" : "s")} in {feed.SourceLabel}. Enable \"Download cloud-only files on demand\" below — viewing them will download photos from your cloud storage onto this computer.",
                0,
                feed.SourceLabel,
                feed.CloudOnlyCount,
                remoteStream);
        }
        if (!string.IsNullOrEmpty(feed.Error))
            return new PhotoTestResponse(false, feed.Error, feed.Photos.Count, feed.SourceLabel, feed.CloudOnlyCount, remoteStream);
        if (feed.Photos.Count == 0)
            return new PhotoTestResponse(false, $"No supported photos found in {feed.SourceLabel}", 0, feed.SourceLabel, RemoteStream: remoteStream);
        if (!settings.DownloadCloudFiles && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                true,
                $"Found {feed.Photos.Count} local photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel} ({feed.CloudOnlyCount} cloud-only skipped — enable on-demand download to include them)",
                feed.Photos.Count,
                feed.SourceLabel,
                feed.CloudOnlyCount,
                remoteStream);
        }
        if (settings.DownloadCloudFiles && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                true,
                $"Found {feed.Photos.Count} photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel} ({feed.CloudOnlyCount} in cloud — photos will be downloaded onto this computer as they are shown)",
                feed.Photos.Count,
                feed.SourceLabel,
                feed.CloudOnlyCount,
                remoteStream);
        }

        return new PhotoTestResponse(true, $"Found {feed.Photos.Count} photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel}", feed.Photos.Count, feed.SourceLabel, RemoteStream: remoteStream);
    }

    public async Task<PhotoBrowseResponse> BrowseAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        var provider = PhotoWidgetOptions.Providers.Contains(req.Provider) ? req.Provider : "local";
        try
        {
            return provider switch
            {
                "local" => BrowseLocal(req.Path),
                "onedrive" => await BrowseOneDriveAsync(req, ct),
                "google" => await BrowseGoogleDriveAsync(req, ct),
                "dropbox" => await BrowseDropboxAsync(req, ct),
                "nextcloud" => await BrowseNextcloudAsync(req, ct),
                "icloud" => new PhotoBrowseResponse(true, null, "icloud", null, null,
                    DiscoverLocalRoots().Where(r => r.ProviderHint == "icloud")
                        .Select(r => new PhotoBrowseEntry(r.Path, r.Name, "folder", null, r.Path)).ToList()),
                _ => new PhotoBrowseResponse(false, "Browsing is not supported for this provider", provider, null, null, []),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or XmlException or UnauthorizedAccessException or TaskCanceledException)
        {
            return new PhotoBrowseResponse(false, ex.Message, provider, req.Path, null, []);
        }
    }

    private long GetProviderEpochLocked(string provider) =>
        _providerEpochs.TryGetValue(provider, out var ep) ? ep : 0;

    private long GetProviderEpoch(string provider)
    {
        lock (_gate) return GetProviderEpochLocked(provider);
    }

    /// <summary>
    /// Clears cached manifests, cancels/invalidates in-flight refreshes, and removes registered/cached
    /// items for <paramref name="provider"/> when its credentials are disconnected or replaced.
    /// </summary>
    public void InvalidateProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;

        List<CancellationTokenSource> toCancel = [];
        List<string> removedIds = [];
        lock (_gate)
        {
            _providerEpochs[provider] = GetProviderEpochLocked(provider) + 1;

            var manifestKeys = _manifests
                .Where(kv => string.Equals(kv.Value.Provider, provider, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var mk in manifestKeys) _manifests.Remove(mk);

            var flightKeys = _inFlight
                .Where(kv => string.Equals(kv.Value.Provider, provider, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var fk in flightKeys)
            {
                if (_inFlight.TryGetValue(fk, out var inf))
                    toCancel.Add(inf.CancelCts);
                _inFlight.Remove(fk);
            }

            foreach (var kv in _items)
            {
                if (string.Equals(EffectiveOwnerProvider(kv.Value), provider, StringComparison.Ordinal)
                    || string.Equals(kv.Value.Provider, provider, StringComparison.Ordinal))
                    removedIds.Add(kv.Key);
            }
            foreach (var id in removedIds)
                _items.Remove(id);
        }

        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        foreach (var id in removedIds)
        {
            try
            {
                var p = Path.Combine(_cacheDir, id + ".bin");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }
    }

    public async Task<(byte[] Bytes, string Mime)?> GetPhotoBytesAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80) return null;
        ResolvedPhoto? item;
        string ownerProvider;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out item))
                return null;
            ownerProvider = EffectiveOwnerProvider(item);
            if (item.ProviderEpoch != GetProviderEpochLocked(ownerProvider))
                return null;
        }

        if (item.LocalFilePath is not null && item.LocalRootPath is not null)
            return await ReadSafeLocalImageAsync(item.LocalRootPath, item.LocalFilePath, item.AllowCloudDownload, ct);

        var cachedPath = Path.Combine(_cacheDir, id + ".bin");
        if (File.Exists(cachedPath))
        {
            try
            {
                var info = new FileInfo(cachedPath);
                if (info.Length is > 0 and <= SafePhotoHttp.MaxImageBytes)
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachedPath, ct);
                    var cachedMime = SafePhotoHttp.DetectRasterMime(cachedBytes);
                    if (cachedMime is not null)
                    {
                        if (item.ProviderEpoch != GetProviderEpoch(ownerProvider)) return null;
                        try { File.SetLastWriteTimeUtc(cachedPath, DateTime.UtcNow); } catch { }
                        return (cachedBytes, cachedMime);
                    }
                }
            }
            catch { /* fall through to remote fetch */ }
        }

        var fetched = await FetchRemotePhotoBytesAsync(item, ct);
        if (fetched is null) return null;
        if (item.ProviderEpoch != GetProviderEpoch(ownerProvider)) return null;

        await WriteCacheFileAsync(cachedPath, fetched.Value.Bytes, ct);
        return fetched;
    }

    private async Task<PhotoFeedResponse> GetFeedAsync(PhotoWidgetSettings settings, bool refresh, CancellationToken ct)
    {
        if (!IsSourceConfigured(settings))
        {
            return new PhotoFeedResponse(
                Configured: false,
                Provider: settings.Provider,
                SourceLabel: CloudPhotoAuth.ProviderDisplayName(settings.Provider),
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: MissingSourceMessage(settings.Provider),
                Photos: []);
        }

        var key = SourceKey(settings);
        CachedManifest? staleFallback = null;
        InFlightRefresh? existing = null;

        lock (_gate)
        {
            var epoch = GetProviderEpochLocked(settings.Provider);
            if (_manifests.TryGetValue(key, out var cached) && cached.ProviderEpoch == epoch)
            {
                staleFallback = cached;
                if (!refresh && NowMs - cached.FetchedAtMs < CacheTtlMs)
                    return ApplyPresentationOrder(cached.Response, settings);
            }

            if (!_inFlight.TryGetValue(key, out existing) || existing.Task.IsCompleted || existing.ProviderEpoch != epoch)
            {
                var cancelCts = new CancellationTokenSource();
                var task = RefreshManifestCoreAsync(key, settings, staleFallback, epoch, cancelCts);
                existing = new InFlightRefresh(task, cancelCts, settings.Provider, epoch);
                if (!task.IsCompleted)
                    _inFlight[key] = existing;
            }
        }

        var fresh = await existing.Task.WaitAsync(ct);
        return ApplyPresentationOrder(fresh, settings);
    }

    private async Task<PhotoFeedResponse> RefreshManifestCoreAsync(
        string key,
        PhotoWidgetSettings settings,
        CachedManifest? staleFallback,
        long epochAtStart,
        CancellationTokenSource cancelCts)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancelCts.Token);
        var ct = linkedCts.Token;
        try
        {
            var (sourceLabel, resolved, cloudOnlyCount) = await FetchProviderItemsAsync(settings, ct);
            var feedItems = new List<PhotoFeedItem>(resolved.Count);

            lock (_gate)
            {
                if (cancelCts.IsCancellationRequested || GetProviderEpochLocked(settings.Provider) != epochAtStart)
                    throw new OperationCanceledException(cancelCts.Token);

                foreach (var rawItem in resolved.Take(MaxManifestItems))
                {
                    var item = rawItem with { OwnerProvider = settings.Provider, ProviderEpoch = epochAtStart };
                    RegisterItemLocked(item);
                    feedItems.Add(new PhotoFeedItem(
                        Id: item.Id,
                        Url: "/api/photos/image/" + Uri.EscapeDataString(item.Id),
                        Caption: item.Caption,
                        TakenAt: item.TakenAtMs,
                        Album: item.Album,
                        Source: sourceLabel,
                        Width: null,
                        Height: null));
                }

                var errorMsg = !settings.DownloadCloudFiles && feedItems.Count == 0 && cloudOnlyCount > 0
                    ? $"{cloudOnlyCount} cloud-only photo{(cloudOnlyCount == 1 ? " was" : "s were")} not downloaded. Enable \"Download cloud-only files on demand\" in PhoneBox Settings to allow cloud downloads."
                    : null;

                var response = new PhotoFeedResponse(
                    Configured: true,
                    Provider: settings.Provider,
                    SourceLabel: sourceLabel,
                    Title: NullIfEmpty(settings.Title),
                    UpdatedAt: NowMs,
                    Stale: false,
                    Error: errorMsg,
                    Photos: feedItems,
                    CloudOnlyCount: cloudOnlyCount);

                _manifests[key] = new CachedManifest(response, response.UpdatedAt, settings.Provider, epochAtStart);
                return response;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or XmlException or UnauthorizedAccessException or TaskCanceledException or OperationCanceledException)
        {
            var invalidated = cancelCts.IsCancellationRequested || GetProviderEpoch(settings.Provider) != epochAtStart;
            if (!invalidated && staleFallback is not null && staleFallback.Response.Photos.Count > 0)
            {
                return staleFallback.Response with
                {
                    Stale = true,
                    Error = ex.Message,
                };
            }
            var errText = invalidated && CloudPhotoAuth.IsOAuthProvider(settings.Provider) && !_auth.IsConnected(settings.Provider)
                ? $"Connect {CloudPhotoAuth.ProviderDisplayName(settings.Provider)} in Settings › General"
                : (invalidated ? "Photo source credentials changed" : ex.Message);
            return new PhotoFeedResponse(
                Configured: true,
                Provider: settings.Provider,
                SourceLabel: CloudPhotoAuth.ProviderDisplayName(settings.Provider),
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: errText,
                Photos: []);
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(key, out var cur) && ReferenceEquals(cur.CancelCts, cancelCts))
                    _inFlight.Remove(key);
            }
            cancelCts.Dispose();
        }
    }

    private static PhotoFeedResponse ApplyPresentationOrder(PhotoFeedResponse resp, PhotoWidgetSettings settings)
    {
        if (resp.Photos.Count <= 1 && string.IsNullOrEmpty(settings.Title))
            return resp;

        IEnumerable<PhotoFeedItem> ordered = settings.Order switch
        {
            "newest" => resp.Photos.OrderByDescending(p => p.TakenAt ?? 0L).ThenBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            "oldest" => resp.Photos.OrderBy(p => p.TakenAt ?? long.MaxValue).ThenBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            "name" => resp.Photos.OrderBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            _ => resp.Photos,
        };
        return resp with
        {
            Title = NullIfEmpty(settings.Title),
            Photos = ordered.ToList(),
        };
    }

    private bool UsesLocalDirectory(PhotoWidgetSettings s)
    {
        if (s.Provider == "local") return true;
        if (s.Provider == "icloud" && string.IsNullOrWhiteSpace(s.ShareUrl) && IsExistingLocalDirectory(s.Folder))
            return true;
        if (s.Provider is "onedrive" or "google" or "dropbox"
            && string.IsNullOrWhiteSpace(s.AlbumId)
            && s.Folder.Trim() != "/"
            && IsExistingLocalDirectory(s.Folder)
            && (!_auth.IsConnected(s.Provider) || LooksLikeLocalFilesystemPath(s.Folder)))
        {
            return true;
        }
        return false;
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items, int CloudOnlyCount)> FetchProviderItemsAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        // Synced local folder fallback works seamlessly for local or when a local synced directory was chosen under OneDrive/iCloud/Google/Dropbox
        if (UsesLocalDirectory(s))
        {
            var label = s.Provider switch
            {
                "local" => "Computer folder",
                "icloud" => "iCloud Photos (synced)",
                _ => $"{CloudPhotoAuth.ProviderDisplayName(s.Provider)} (synced)",
            };
            return FetchFromLocalDirectory(s, s.Folder, label);
        }

        var (sourceLabel, items) = s.Provider switch
        {
            "icloud" => await FetchICloudSharedAlbumAsync(s, ct),
            "onedrive" => await FetchOneDriveAsync(s, ct),
            "google" => await FetchGoogleDriveAsync(s, ct),
            "dropbox" => await FetchDropboxAsync(s, ct),
            "nextcloud" => await FetchNextcloudShareAsync(s, ct),
            _ => throw new IOException($"Unsupported photo provider '{s.Provider}'"),
        };
        return (sourceLabel, items, 0);
    }

    // ---------------------------------------------------------------- local & synced folder

    private (string SourceLabel, List<ResolvedPhoto> Items, int CloudOnlyCount) FetchFromLocalDirectory(
        PhotoWidgetSettings s,
        string rawFolder,
        string fallbackLabel)
    {
        var expanded = ExpandUserPath(rawFolder);
        if (string.IsNullOrWhiteSpace(expanded))
            throw new IOException("Choose a folder on this computer");

        var rootFull = TryResolveSafeDirectory(expanded)
            ?? throw new IOException($"Folder '{rawFolder}' was not found or is a symlink");

        var rootPrefix = EnsureTrailingSep(rootFull);
        var label = Path.GetFileName(rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(label)) label = fallbackLabel;

        var items = new List<ResolvedPhoto>();
        var dirs = new Queue<string>();
        dirs.Enqueue(rootFull);
        var inspected = 0;
        var cloudOnlySkipped = 0;
        var cloudOnlyIncluded = 0;

        while (dirs.Count > 0 && items.Count < MaxManifestItems && inspected < MaxLocalScanEntries)
        {
            var dir = dirs.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++inspected > MaxLocalScanEntries || items.Count >= MaxManifestItems) break;

                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                FileSystemInfo fsi;
                try
                {
                    fsi = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                }
                catch { continue; }

                if (fsi.LinkTarget is not null)
                    continue;

                var fullEntry = Path.GetFullPath(entry);
                if (!IsPathInsideRoot(fullEntry, rootFull, rootPrefix))
                    continue;

                if (fsi is DirectoryInfo)
                {
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (s.Subfolders && !name.EndsWith(".photoslibrary", StringComparison.OrdinalIgnoreCase))
                        dirs.Enqueue(fullEntry);
                    continue;
                }

                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0
                    && !(OperatingSystem.IsWindows() && IsWindowsCloudPlaceholder(fsi.Attributes)))
                {
                    continue;
                }

                var ext = Path.GetExtension(name);
                if (!ImageExtensions.Contains(ext)) continue;

                var fileInfo = (FileInfo)fsi;
                if (fileInfo.Length is <= 0 or > SafePhotoHttp.MaxImageBytes) continue;

                var isCloudOnly = IsCloudOnlyPlaceholder(fileInfo);
                if (isCloudOnly && !s.DownloadCloudFiles)
                {
                    cloudOnlySkipped++;
                    continue;
                }
                if (isCloudOnly) cloudOnlyIncluded++;

                var takenAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var albumName = PathsEqual(Path.GetDirectoryName(fullEntry), rootFull)
                    ? label
                    : Path.GetFileName(Path.GetDirectoryName(fullEntry) ?? label);
                var caption = Path.GetFileNameWithoutExtension(name);
                var id = ComputeId("local", fullEntry, $"{takenAt.ToString(CultureInfo.InvariantCulture)}:{(s.DownloadCloudFiles ? "dl1" : "dl0")}");
                items.Add(new ResolvedPhoto(
                    Id: id,
                    Provider: "local",
                    RemoteUrlOrId: fullEntry,
                    LocalFilePath: fullEntry,
                    LocalRootPath: rootFull,
                    ExtraHeaderArg: null,
                    FallbackUrl: null,
                    TakenAtMs: takenAt,
                    Caption: caption,
                    Album: albumName,
                    SourceLabel: label,
                    AllowCloudDownload: s.DownloadCloudFiles));
            }
        }

        return (label, items, s.DownloadCloudFiles ? cloudOnlyIncluded : cloudOnlySkipped);
    }

    private PhotoBrowseResponse BrowseLocal(string? rawPath)
    {
        var roots = DiscoverLocalRoots();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new PhotoBrowseResponse(
                Ok: true,
                Error: null,
                Provider: "local",
                CurrentPath: null,
                ParentPath: null,
                Entries: roots.Select(r => new PhotoBrowseEntry(r.Path, r.Name, "folder", null, r.Path)).ToList());
        }

        var expanded = ExpandUserPath(rawPath);
        var full = TryResolveSafeDirectory(expanded)
            ?? throw new IOException($"Folder '{rawPath}' does not exist or cannot be browsed");

        var entries = new List<PhotoBrowseEntry>();
        foreach (var dir in Directory.EnumerateDirectories(full).Take(200))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            var di = new DirectoryInfo(dir);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null) continue;
            entries.Add(new PhotoBrowseEntry(di.FullName, name, "folder", null, di.FullName));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        var parent = Directory.GetParent(full)?.FullName;
        return new PhotoBrowseResponse(true, null, "local", full, parent, entries);
    }

    private async Task<(byte[] Bytes, string Mime)?> ReadSafeLocalImageAsync(
        string rootPath,
        string filePath,
        bool allowCloudDownload,
        CancellationToken ct)
    {
        var rootFull = TryResolveSafeDirectory(rootPath);
        if (rootFull is null) return null;

        var fullFile = Path.GetFullPath(filePath);
        if (!IsPathInsideRoot(fullFile, rootFull, EnsureTrailingSep(rootFull)))
            return null;
        if (!HasNoIntermediateReparsePoints(rootFull, fullFile))
            return null;

        var fi = new FileInfo(fullFile);
        if (!fi.Exists || fi.LinkTarget is not null)
            return null;
        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0
            && !(OperatingSystem.IsWindows() && IsWindowsCloudPlaceholder(fi.Attributes)))
            return null;
        if (fi.Length is <= 0 or > SafePhotoHttp.MaxImageBytes)
            return null;
        if (IsCloudOnlyPlaceholder(fi) && !allowCloudDownload)
            return null;

        byte[]? maybeBytes;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_bodyReadTimeout);
        try
        {
            if (_localFileReader is not null)
            {
                maybeBytes = _localFileReader(fullFile, allowCloudDownload);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (allowCloudDownload)
                {
                    await _materializationGate.WaitAsync(readCts.Token);
                    var releasedByWorker = false;
                    Task<byte[]?> readTask;
                    try
                    {
                        readTask = Task.Run(() =>
                        {
                            try
                            {
                                return ReadMacFileWithPolicy(rootFull, fullFile, IOPOL_MATERIALIZE_DATALESS_FILES_ON);
                            }
                            finally
                            {
                                ReleaseGateQuietly(_materializationGate);
                            }
                        });
                        releasedByWorker = true;
                    }
                    finally
                    {
                        if (!releasedByWorker)
                            ReleaseGateQuietly(_materializationGate);
                    }
                    _ = readTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    maybeBytes = await readTask.WaitAsync(readCts.Token);
                }
                else
                {
                    maybeBytes = await Task.Run(
                        () => ReadMacFileWithPolicy(rootFull, fullFile, IOPOL_MATERIALIZE_DATALESS_FILES_OFF),
                        readCts.Token).WaitAsync(readCts.Token);
                }
            }
            else
            {
                await using var fs = new FileStream(fullFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16384, useAsync: true);
                if (!IsOpenHandleInsideRoot(fs.SafeFileHandle, rootFull))
                    return null;
                maybeBytes = await ReadBoundedFileStreamAsync(fs, readCts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }

        if (maybeBytes is null) return null;
        var mime = SafePhotoHttp.DetectRasterMime(maybeBytes);
        return mime is null ? null : (maybeBytes, mime);
    }

    private static void ReleaseGateQuietly(SemaphoreSlim gate)
    {
        try { gate.Release(); } catch (ObjectDisposedException) { }
    }

    private static bool TryGetRelativeSegments(string rootFull, string fullFile, out string[] segments)
    {
        segments = [];
        var normRoot = NormalizeMacPath(rootFull).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normFile = NormalizeMacPath(fullFile);
        if (PathsEqual(normFile, normRoot) || !IsPathInsideRoot(normFile, normRoot, EnsureTrailingSep(normRoot)))
            return false;

        var rawRootSep = EnsureTrailingSep(rootFull);
        var rel = fullFile.StartsWith(rawRootSep, PathComparison)
            ? fullFile[rawRootSep.Length..]
            : Path.GetRelativePath(normRoot, normFile);

        if (string.IsNullOrWhiteSpace(rel) || rel == "." || Path.IsPathRooted(rel))
            return false;

        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (part is "." or "..") return false;
        }
        segments = parts;
        return true;
    }

    private static bool HasNoIntermediateReparsePoints(string rootFull, string fullFile)
    {
        if (!TryGetRelativeSegments(rootFull, fullFile, out var parts))
            return false;

        var cur = rootFull;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            cur = Path.Combine(cur, parts[i]);
            try
            {
                var di = new DirectoryInfo(cur);
                if (!di.Exists || di.LinkTarget is not null || (di.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsOpenHandleInsideRoot(SafeFileHandle fileHandle, string rootFull)
    {
        if (fileHandle.IsInvalid) return false;

        if (OperatingSystem.IsMacOS())
        {
            var rootFd = DarwinOpen(rootFull, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
            if (rootFd < 0) return false;
            try
            {
                if (!TryGetMacFdPath(rootFd, out var rootReal)) return false;
                var added = false;
                fileHandle.DangerousAddRef(ref added);
                try
                {
                    if (!TryGetMacFdPath((int)fileHandle.DangerousGetHandle(), out var fileReal)) return false;
                    return IsPathInsideRoot(fileReal, rootReal, EnsureTrailingSep(rootReal));
                }
                finally
                {
                    if (added) fileHandle.DangerousRelease();
                }
            }
            finally
            {
                DarwinClose(rootFd);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using var rootHandle = WinCreateFile(rootFull, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (!TryGetWinHandleFinalPath(rootHandle, out var rootFinal)) return false;
            if (!TryGetWinHandleFinalPath(fileHandle, out var fileFinal)) return false;
            return IsPathInsideRoot(fileFinal, rootFinal, EnsureTrailingSep(rootFinal));
        }

        try
        {
            var added = false;
            fileHandle.DangerousAddRef(ref added);
            try
            {
                var fdLink = $"/proc/self/fd/{fileHandle.DangerousGetHandle()}";
                var fileTarget = File.ResolveLinkTarget(fdLink, returnFinalTarget: true)?.FullName;
                if (string.IsNullOrEmpty(fileTarget)) return false;
                var rootCanonical = CanonicalizeUnixDirectory(rootFull);
                return IsPathInsideRoot(fileTarget, rootCanonical, EnsureTrailingSep(rootCanonical));
            }
            finally
            {
                if (added) fileHandle.DangerousRelease();
            }
        }
        catch
        {
            return false;
        }
    }

    private static string CanonicalizeUnixDirectory(string fullPath)
    {
        var cur = Path.GetPathRoot(fullPath) ?? "/";
        var rest = fullPath[cur.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in rest)
        {
            cur = Path.Combine(cur, part);
            var target = Directory.ResolveLinkTarget(cur, returnFinalTarget: true);
            if (target is not null)
                cur = Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(cur);
    }

    private static byte[]? ReadBoundedFileStream(FileStream fs)
    {
        if (fs.CanSeek && fs.Length is <= 0 or > SafePhotoHttp.MaxImageBytes)
            return null;

        using var ms = new MemoryStream(fs.CanSeek && fs.Length is > 0 and <= 1024 * 1024 ? (int)fs.Length : 16384);
        var buffer = new byte[16384];
        long total = 0;
        while (true)
        {
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > SafePhotoHttp.MaxImageBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return total == 0 ? null : ms.ToArray();
    }

    private static async Task<byte[]?> ReadBoundedFileStreamAsync(FileStream fs, CancellationToken ct)
    {
        if (fs.CanSeek && fs.Length is <= 0 or > SafePhotoHttp.MaxImageBytes)
            return null;

        using var ms = new MemoryStream(fs.CanSeek && fs.Length is > 0 and <= 1024 * 1024 ? (int)fs.Length : 16384);
        var buffer = new byte[16384];
        long total = 0;
        while (true)
        {
            var read = await fs.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > SafePhotoHttp.MaxImageBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return total == 0 ? null : ms.ToArray();
    }

    private bool IsCloudOnlyPlaceholder(FileInfo fi)
    {
        if (_isCloudOnlyOverride is not null)
            return _isCloudOnlyOverride(fi);
        if (OperatingSystem.IsMacOS())
            return IsMacDatalessFile(fi.FullName);
        if (OperatingSystem.IsWindows())
            return IsWindowsCloudPlaceholder(fi.Attributes);
        return false;
    }

    private static bool IsWindowsCloudPlaceholder(FileAttributes attr) =>
        ((int)attr & (0x00400000 | 0x00040000 | (int)FileAttributes.Offline)) != 0;

    private static bool IsMacDatalessFile(string fullPath)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var al = new DarwinAttrList { bitmapcount = ATTR_BIT_MAP_COUNT, commonattr = ATTR_CMN_FLAGS };
            var rc = DarwinGetAttrList(fullPath, ref al, out var buf, (nuint)Marshal.SizeOf<DarwinFlagsAttrBuf>(), FSOPT_NOFOLLOW);
            return rc == 0 && (buf.flags & SF_DATALESS) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadMacFileWithPolicy(string rootFull, string fullFile, int policy)
    {
        var prev = DarwinGetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD);
        DarwinSetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, policy);
        try
        {
            return OpenAndReadMacFileNoFollow(rootFull, fullFile);
        }
        finally
        {
            DarwinSetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD,
                prev >= 0 ? prev : IOPOL_MATERIALIZE_DATALESS_FILES_DEFAULT);
        }
    }

    private static byte[]? OpenAndReadMacFileNoFollow(string rootFull, string fullFile)
    {
        if (!TryGetRelativeSegments(rootFull, fullFile, out var segments))
            return null;

        var curFd = DarwinOpen(rootFull, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
        if (curFd < 0) return null;

        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var nextFd = DarwinOpenAt(curFd, segments[i], O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
                DarwinClose(curFd);
                curFd = nextFd;
                if (curFd < 0) return null;
            }

            var fileFd = DarwinOpenAt(curFd, segments[^1], O_RDONLY | O_NOFOLLOW);
            DarwinClose(curFd);
            curFd = -1;
            if (fileFd < 0) return null;

            using var handle = new SafeFileHandle((IntPtr)fileFd, ownsHandle: true);
            if (!IsOpenHandleInsideRoot(handle, rootFull))
                return null;

            using var fs = new FileStream(handle, FileAccess.Read);
            return ReadBoundedFileStream(fs);
        }
        finally
        {
            if (curFd >= 0) DarwinClose(curFd);
        }
    }

    private static bool TryGetMacFdPath(int fd, out string path)
    {
        path = "";
        if (fd < 0) return false;
        var al = new DarwinAttrList { bitmapcount = ATTR_BIT_MAP_COUNT, commonattr = ATTR_CMN_FULLPATH };
        var buf = new byte[4096];
        if (DarwinFGetAttrList(fd, ref al, buf, (nuint)buf.Length, 0) != 0)
            return false;
        var offset = BitConverter.ToInt32(buf, 4);
        var len = BitConverter.ToInt32(buf, 8);
        var start = 4 + offset;
        if (offset < 8 || len <= 0 || start < 0 || start + len > buf.Length)
            return false;
        path = Encoding.UTF8.GetString(buf, start, len).TrimEnd('\0');
        return path.Length > 0;
    }

    private static bool TryGetWinHandleFinalPath(SafeFileHandle handle, out string path)
    {
        path = "";
        if (handle.IsInvalid) return false;
        var buf = new char[1024];
        var len = WinGetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
        if (len == 0) return false;
        if (len > buf.Length)
        {
            buf = new char[len];
            len = WinGetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
            if (len == 0 || len > buf.Length) return false;
        }
        path = new string(buf, 0, (int)len);
        return path.Length > 0;
    }

    private const ushort ATTR_BIT_MAP_COUNT = 5;
    private const uint ATTR_CMN_FLAGS = 0x00040000u;
    private const uint ATTR_CMN_FULLPATH = 0x08000000u;
    private const uint FSOPT_NOFOLLOW = 0x00000001u;
    private const uint SF_DATALESS = 0x40000000u;
    private const int O_RDONLY = 0x0000;
    private const int O_NOFOLLOW = 0x0100;
    private const int O_DIRECTORY = 0x00100000;
    private const int IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES = 3;
    private const int IOPOL_SCOPE_THREAD = 1;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_DEFAULT = 0;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_OFF = 1;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_ON = 2;
    private const uint FILE_SHARE_ALL = 0x00000007u;
    private const uint OPEN_EXISTING = 3u;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000u;

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAttrList
    {
        public ushort bitmapcount;
        public ushort reserved;
        public uint commonattr;
        public uint volattr;
        public uint dirattr;
        public uint fileattr;
        public uint forkattr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinFlagsAttrBuf
    {
        public uint length;
        public uint flags;
    }

    [DllImport("libSystem.dylib", EntryPoint = "getattrlist")]
    private static extern int DarwinGetAttrList(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref DarwinAttrList attrList,
        out DarwinFlagsAttrBuf attrBuf,
        nuint attrBufSize,
        uint options);

    [DllImport("libSystem.dylib", EntryPoint = "fgetattrlist")]
    private static extern int DarwinFGetAttrList(
        int fd,
        ref DarwinAttrList attrList,
        [Out] byte[] attrBuf,
        nuint attrBufSize,
        uint options);

    [DllImport("libSystem.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int DarwinOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag);

    [DllImport("libSystem.dylib", EntryPoint = "openat", SetLastError = true)]
    private static extern int DarwinOpenAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag);

    [DllImport("libSystem.dylib", EntryPoint = "close")]
    private static extern int DarwinClose(int fd);

    [DllImport("libSystem.dylib", EntryPoint = "getiopolicy_np")]
    private static extern int DarwinGetIoPolicy(int iotype, int scope);

    [DllImport("libSystem.dylib", EntryPoint = "setiopolicy_np")]
    private static extern int DarwinSetIoPolicy(int iotype, int scope, int policy);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle WinCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WinGetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    // ---------------------------------------------------------------- iCloud Shared Album

    internal static bool TryParseICloudShareToken(string raw, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[(hash + 1)..];
        var semi = s.LastIndexOf(';');
        if (semi >= 0) s = s[(semi + 1)..];
        s = s.Trim().TrimEnd('/');
        if (!ICloudTokenRegex().IsMatch(s)) return false;
        token = s;
        return true;
    }

    internal static string ICloudInitialHost(string token)
    {
        const string base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        int? partition = null;
        if (token.Length >= 3)
        {
            var chars = token[0] == 'A' ? token.AsSpan(1, 1) : token.AsSpan(1, 2);
            var sum = 0;
            var valid = true;
            foreach (var ch in chars)
            {
                var idx = base62.IndexOf(ch);
                if (idx < 0) { valid = false; break; }
                sum = sum * 62 + idx;
            }
            if (valid && sum is >= 1 and <= 255) partition = sum;
        }
        var p = partition ?? 1;
        return $"p{p:D2}-sharedstreams.icloud.com";
    }

    internal static bool IsValidICloudStreamHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && ICloudStreamHostRegex().IsMatch(host.Trim());

    internal static bool IsValidICloudAssetHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().ToLowerInvariant();
        return h.EndsWith(".icloud-content.com", StringComparison.Ordinal)
            || h.EndsWith(".icloud.com", StringComparison.Ordinal)
            || h.EndsWith(".mzstatic.com", StringComparison.Ordinal);
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items)> FetchICloudSharedAlbumAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        if (!TryParseICloudShareToken(s.ShareUrl, out var token))
            throw new IOException("Paste an iCloud Shared Album link (https://www.icloud.com/sharedalbum/#...)");

        var host = ICloudInitialHost(token);
        JsonDocument? streamDoc = null;

        for (var hop = 0; hop < 3; hop++)
        {
            var url = $"https://{host}/{Uri.EscapeDataString(token)}/sharedstreams/webstream";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("""{"streamCtag":null}""", Encoding.UTF8, "application/json"),
            };
            using var resp = await SafePhotoHttp.SendFollowingRedirectsAsync(_http, req, ct,
                extraHostValidator: u => IsValidICloudStreamHost(u.Host));

            if ((int)resp.StatusCode == 330)
            {
                if (!resp.Headers.TryGetValues("X-Apple-MMe-Host", out var hosts))
                    throw new IOException("iCloud redirect header missing");
                var redirectedHost = hosts.FirstOrDefault()?.Trim() ?? "";
                if (!IsValidICloudStreamHost(redirectedHost))
                    throw new IOException("Blocked invalid iCloud shard host");
                host = redirectedHost;
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new IOException($"iCloud Shared Album returned HTTP {(int)resp.StatusCode}; check that the public link is active");

            var body = await ReadResponseStringBoundedAsync(resp.Content, SafePhotoHttp.MaxJsonBytes, ct);
            streamDoc = JsonDocument.Parse(body);
            break;
        }

        if (streamDoc is null)
            throw new IOException("Too many iCloud shard redirects");

        using (streamDoc)
        {
            var root = streamDoc.RootElement;
            var albumName = root.TryGetProperty("streamName", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString() ?? "iCloud Album"
                : "iCloud Album";

            if (!root.TryGetProperty("photos", out var photosEl) || photosEl.ValueKind != JsonValueKind.Array)
                return (albumName, []);

            var selected = new List<(string Guid, string Checksum, string? Caption, long? TakenAt)>();
            foreach (var ph in photosEl.EnumerateArray())
            {
                if (selected.Count >= MaxManifestItems) break;
                if (ph.TryGetProperty("mediaAssetType", out var mat) && mat.ValueKind == JsonValueKind.String
                    && string.Equals(mat.GetString(), "video", StringComparison.OrdinalIgnoreCase))
                    continue;

                var guid = ph.TryGetProperty("photoGuid", out var pg) ? pg.GetString() : null;
                if (string.IsNullOrWhiteSpace(guid)) continue;

                var bestChecksum = SelectBestICloudDerivativeChecksum(ph);
                if (bestChecksum is null) continue;

                var caption = ph.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.String
                    ? cap.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(caption)) caption = null;

                var dateStr = ph.TryGetProperty("dateCreated", out var dc) && dc.ValueKind == JsonValueKind.String
                    ? dc.GetString()
                    : ph.TryGetProperty("batchDateCreated", out var bdc) && bdc.ValueKind == JsonValueKind.String
                        ? bdc.GetString()
                        : null;
                long? takenAt = DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                    ? dto.ToUnixTimeMilliseconds()
                    : null;

                selected.Add((guid, bestChecksum, caption, takenAt));
            }

            if (selected.Count == 0)
                return (albumName, []);

            // Resolve signed CDN URLs via webasseturls in chunks of 40
            var assetUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chunk in selected.Chunk(40))
            {
                var guidsJson = JsonSerializer.Serialize(new { photoGuids = chunk.Select(c => c.Guid).ToArray() });
                var assetEndpoint = $"https://{host}/{Uri.EscapeDataString(token)}/sharedstreams/webasseturls";
                using var req = new HttpRequestMessage(HttpMethod.Post, assetEndpoint)
                {
                    Content = new StringContent(guidsJson, Encoding.UTF8, "application/json"),
                };
                using var resp = await SafePhotoHttp.SendFollowingRedirectsAsync(_http, req, ct,
                    extraHostValidator: u => IsValidICloudStreamHost(u.Host));
                if (!resp.IsSuccessStatusCode) continue;

                var json = await ReadResponseStringBoundedAsync(resp.Content, SafePhotoHttp.MaxJsonBytes, ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in itemsEl.EnumerateObject())
                {
                    var loc = prop.Value.TryGetProperty("url_location", out var ul) ? ul.GetString() : null;
                    var path = prop.Value.TryGetProperty("url_path", out var up) ? up.GetString() : null;
                    if (!IsValidICloudAssetHost(loc) || string.IsNullOrWhiteSpace(path)) continue;
                    var built = "https://" + loc!.Trim() + (path!.StartsWith('/') ? path : "/" + path);
                    if (SafePhotoHttp.TryValidateHttpsUri(built, out var validated, out _))
                        assetUrls[prop.Name] = validated!.AbsoluteUri;
                }
            }

            var resolved = new List<ResolvedPhoto>();
            foreach (var item in selected)
            {
                if (!assetUrls.TryGetValue(item.Checksum, out var assetUrl)) continue;
                var id = ComputeId("icloud", token, item.Guid);
                resolved.Add(new ResolvedPhoto(
                    Id: id,
                    Provider: "icloud",
                    RemoteUrlOrId: assetUrl,
                    LocalFilePath: null,
                    LocalRootPath: null,
                    ExtraHeaderArg: null,
                    FallbackUrl: null,
                    TakenAtMs: item.TakenAt,
                    Caption: item.Caption,
                    Album: albumName,
                    SourceLabel: albumName));
            }
            return (albumName, resolved);
        }
    }

    private static string? SelectBestICloudDerivativeChecksum(JsonElement photo)
    {
        if (!photo.TryGetProperty("derivatives", out var derivs) || derivs.ValueKind != JsonValueKind.Object)
            return null;

        string? bestChecksum = null;
        long bestArea = -1;
        foreach (var prop in derivs.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("checksum", out var cs) || cs.ValueKind != JsonValueKind.String) continue;
            var checksum = cs.GetString();
            if (string.IsNullOrWhiteSpace(checksum)) continue;

            var w = TryReadLong(prop.Value, "width") ?? 0;
            var h = TryReadLong(prop.Value, "height") ?? 0;
            var area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestChecksum = checksum;
            }
        }
        return bestChecksum;
    }

    // ---------------------------------------------------------------- OneDrive (Microsoft Graph v1.0)

    private async Task<PhotoBrowseResponse> BrowseOneDriveAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        var roots = DiscoverLocalRoots().Where(r => r.ProviderHint == "onedrive").ToList();
        if (!_auth.IsConnected("onedrive"))
        {
            if (roots.Count > 0 && string.IsNullOrWhiteSpace(req.ParentId))
            {
                return new PhotoBrowseResponse(true, null, "onedrive", null, null,
                    roots.Select(r => new PhotoBrowseEntry("", $"{r.Name} (Local Sync)", "local-folder", null, r.Path)).ToList());
            }
            throw new IOException("Connect OneDrive in Settings › General, or pick a synced local OneDrive folder");
        }

        if (string.IsNullOrWhiteSpace(req.ParentId))
        {
            var entries = new List<PhotoBrowseEntry>
            {
                new("special:photos", "Pictures / Photos", "folder", null, "Pictures"),
                new("albums", "Photo Albums", "collection", null, "Albums"),
            };
            var url = "https://graph.microsoft.com/v1.0/me/drive/root/children?$filter=folder ne null&$select=id,name,folder&$top=100";
            using var doc = await SendAuthedJsonAsync("onedrive", HttpMethod.Get, url, null, ct);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    int? count = item.TryGetProperty("folder", out var fl) && fl.TryGetProperty("childCount", out var cc) && cc.TryGetInt32(out var c) ? c : null;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        entries.Add(new PhotoBrowseEntry(id, name, "folder", count, "/" + name));
                }
            }
            return new PhotoBrowseResponse(true, null, "onedrive", "/", null, entries);
        }

        if (req.ParentId == "albums")
        {
            const string bundlesUrl = "https://graph.microsoft.com/v1.0/me/drive/bundles?$select=id,name,bundle&$top=100";
            using var doc = await SendAuthedJsonAsync("onedrive", HttpMethod.Get, bundlesUrl, null, ct);
            var entries = new List<PhotoBrowseEntry>();
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (!item.TryGetProperty("bundle", out var b) || !b.TryGetProperty("album", out _)) continue;
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    int? count = b.TryGetProperty("childCount", out var cc) && cc.TryGetInt32(out var c) ? c : null;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        entries.Add(new PhotoBrowseEntry(id, name, "album", count, name));
                }
            }
            return new PhotoBrowseResponse(true, null, "onedrive", "Albums", "", entries);
        }

        var childUrl = req.ParentId == "special:photos"
            ? "https://graph.microsoft.com/v1.0/me/drive/special/photos/children?$filter=folder ne null&$select=id,name,folder&$top=100"
            : $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(req.ParentId)}/children?$filter=folder ne null&$select=id,name,folder&$top=100";

        using var subDoc = await SendAuthedJsonAsync("onedrive", HttpMethod.Get, childUrl, null, ct);
        var subEntries = new List<PhotoBrowseEntry>();
        if (subDoc.RootElement.TryGetProperty("value", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sArr.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                int? count = item.TryGetProperty("folder", out var fl) && fl.TryGetProperty("childCount", out var cc) && cc.TryGetInt32(out var c) ? c : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    subEntries.Add(new PhotoBrowseEntry(id, name, "folder", count, string.IsNullOrEmpty(req.Path) ? "/" + name : req.Path.TrimEnd('/') + "/" + name));
            }
        }
        return new PhotoBrowseResponse(true, null, "onedrive", req.Path ?? req.ParentId, "", subEntries);
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items)> FetchOneDriveAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        if (!_auth.IsConnected("onedrive"))
            throw new IOException("Connect OneDrive in Settings › General, or pick a synced local OneDrive folder");

        var sessionStamp = _auth.GetSessionStamp("onedrive");
        var sourceLabel = !string.IsNullOrWhiteSpace(s.Folder) ? s.Folder : (!string.IsNullOrWhiteSpace(s.AlbumId) ? "OneDrive Album" : "OneDrive Photos");
        var initialUrl = BuildOneDriveInitialUrl(s);

        var items = new List<ResolvedPhoto>();
        var folderQueue = new Queue<(string Url, string Label)>();
        folderQueue.Enqueue((initialUrl, sourceLabel));
        var pagesFetched = 0;

        while (folderQueue.Count > 0 && items.Count < MaxManifestItems && pagesFetched < 10)
        {
            var (startUrl, albumLabel) = folderQueue.Dequeue();
            string? pageUrl = startUrl;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);

            while (pageUrl is not null && seenPages.Add(pageUrl) && items.Count < MaxManifestItems && pagesFetched < 10)
            {
                pagesFetched++;
                using var doc = await SendAuthedJsonAsync("onedrive", HttpMethod.Get, pageUrl, null, ct);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in arr.EnumerateArray())
                    {
                        if (items.Count >= MaxManifestItems) break;
                        var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        var name = entry.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                        if (entry.TryGetProperty("folder", out _))
                        {
                            if (s.Subfolders && folderQueue.Count < 8)
                            {
                                var child = $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(id)}/children?$select=id,name,file,folder,image,photo,createdDateTime,lastModifiedDateTime&$expand=thumbnails($select=large)&$top=100";
                                folderQueue.Enqueue((child, name));
                            }
                            continue;
                        }

                        if (!entry.TryGetProperty("file", out var fileEl)) continue;
                        var mime = fileEl.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";
                        var isImage = entry.TryGetProperty("image", out _) || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                        if (!isImage) continue;

                        var ext = Path.GetExtension(name);
                        var preferThumbnail = !ImageExtensions.Contains(ext) && CloudPreviewImageExtensions.Contains(ext);
                        string? thumbUrl = null;
                        if (entry.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0)
                        {
                            var t0 = thumbs[0];
                            if (t0.TryGetProperty("large", out var large) && large.TryGetProperty("url", out var uEl))
                                thumbUrl = uEl.GetString();
                        }

                        var contentUrl = $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(id)}/content";
                        var fetchUrl = preferThumbnail && !string.IsNullOrWhiteSpace(thumbUrl) ? thumbUrl! : contentUrl;
                        var fallback = preferThumbnail ? null : thumbUrl;

                        long? takenAt = null;
                        if (entry.TryGetProperty("photo", out var phEl) && phEl.TryGetProperty("takenDateTime", out var td)
                            && DateTimeOffset.TryParse(td.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                            takenAt = dto.ToUnixTimeMilliseconds();
                        else if (entry.TryGetProperty("createdDateTime", out var cd)
                            && DateTimeOffset.TryParse(cd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cdto))
                            takenAt = cdto.ToUnixTimeMilliseconds();

                        var itemId = ComputeId("onedrive", $"{sessionStamp}\0{id}", takenAt?.ToString(CultureInfo.InvariantCulture) ?? "");
                        items.Add(new ResolvedPhoto(
                            Id: itemId,
                            Provider: "onedrive",
                            RemoteUrlOrId: fetchUrl,
                            LocalFilePath: null,
                            LocalRootPath: null,
                            ExtraHeaderArg: null,
                            FallbackUrl: fallback,
                            TakenAtMs: takenAt,
                            Caption: Path.GetFileNameWithoutExtension(name),
                            Album: albumLabel,
                            SourceLabel: sourceLabel));
                    }
                }

                pageUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextEl)
                    && nextEl.ValueKind == JsonValueKind.String
                    && TryValidateOneDriveContinuationUrl(nextEl.GetString(), out var validatedNext)
                    ? validatedNext
                    : null;
            }
        }

        return (sourceLabel, items);
    }

    private static bool TryValidateOneDriveContinuationUrl(string? raw, out string validated)
    {
        validated = "";
        if (!SafePhotoHttp.TryValidateHttpsUri(raw, out var uri, out _) || uri is null)
            return false;
        if (!string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return false;
        validated = uri.AbsoluteUri;
        return true;
    }

    private static string BuildOneDriveInitialUrl(PhotoWidgetSettings s)
    {
        const string select = "$select=id,name,file,folder,image,photo,createdDateTime,lastModifiedDateTime&$expand=thumbnails($select=large)&$top=100";
        if (string.Equals(s.AlbumId, "root", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://graph.microsoft.com/v1.0/me/drive/root/children?{select}";
        }
        if (!string.IsNullOrWhiteSpace(s.AlbumId) && s.AlbumId != "special:photos" && s.AlbumId != "albums")
        {
            return $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(s.AlbumId)}/children?{select}";
        }
        var trimmed = s.Folder.Trim('/');
        if (string.IsNullOrEmpty(trimmed) && s.Folder.Trim() == "/")
        {
            return $"https://graph.microsoft.com/v1.0/me/drive/root/children?{select}";
        }
        if (!string.IsNullOrEmpty(trimmed) && !string.Equals(trimmed, "Pictures", StringComparison.OrdinalIgnoreCase))
        {
            var encodedPath = string.Join('/', trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            return $"https://graph.microsoft.com/v1.0/me/drive/root:/{encodedPath}:/children?{select}";
        }
        return $"https://graph.microsoft.com/v1.0/me/drive/special/photos/children?{select}";
    }

    // ---------------------------------------------------------------- Google Drive API v3

    private async Task<PhotoBrowseResponse> BrowseGoogleDriveAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        var roots = DiscoverLocalRoots().Where(r => r.ProviderHint == "google").ToList();
        if (!_auth.IsConnected("google"))
        {
            if (roots.Count > 0 && string.IsNullOrWhiteSpace(req.ParentId))
            {
                return new PhotoBrowseResponse(true, null, "google", null, null,
                    roots.Select(r => new PhotoBrowseEntry("", $"{r.Name} (Local Sync)", "local-folder", null, r.Path)).ToList());
            }
            throw new IOException("Connect Google Drive in Settings › General, or pick a synced local Google Drive folder");
        }

        var parentId = string.IsNullOrWhiteSpace(req.ParentId) ? "root" : SanitizeDriveId(req.ParentId);
        if (string.IsNullOrEmpty(parentId))
            throw new IOException("Invalid Google Drive folder ID");

        var q = $"'{parentId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id,name)&pageSize=100&orderBy=name";
        using var doc = await SendAuthedJsonAsync("google", HttpMethod.Get, url, null, ct);
        var entries = new List<PhotoBrowseEntry>();
        if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    entries.Add(new PhotoBrowseEntry(id, name, "folder", null, string.IsNullOrEmpty(req.Path) ? name : req.Path + "/" + name));
            }
        }
        return new PhotoBrowseResponse(true, null, "google", req.Path ?? (parentId == "root" ? "My Drive" : parentId), "", entries);
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items)> FetchGoogleDriveAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        if (!_auth.IsConnected("google"))
            throw new IOException("Connect Google Drive in Settings › General, or pick a synced local Google Drive folder");

        var sessionStamp = _auth.GetSessionStamp("google");
        var targetFolderId = SanitizeDriveId(s.AlbumId);
        var sourceLabel = !string.IsNullOrWhiteSpace(s.Folder) ? s.Folder : "Google Drive";
        if (string.IsNullOrEmpty(targetFolderId) && !string.IsNullOrWhiteSpace(s.Folder))
            targetFolderId = await ResolveGoogleDriveFolderPathAsync(s.Folder, ct);

        var items = new List<ResolvedPhoto>();
        var queue = new Queue<(string? FolderId, string Album)>();
        queue.Enqueue((targetFolderId, sourceLabel));
        var ops = 0;

        while (queue.Count > 0 && items.Count < MaxManifestItems && ops < 10)
        {
            var (folderId, albumName) = queue.Dequeue();
            var q = folderId is not null
                ? $"'{folderId}' in parents and trashed = false"
                : "mimeType contains 'image/' and trashed = false";
            string? pageToken = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);

            do
            {
                ops++;
                var pageParam = pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken);
                var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=nextPageToken,files(id,name,mimeType,thumbnailLink,createdTime,modifiedTime,description,imageMediaMetadata(time))&pageSize=100{pageParam}";

                using var doc = await SendAuthedJsonAsync("google", HttpMethod.Get, url, null, ct);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        if (items.Count >= MaxManifestItems) break;
                        var id = f.TryGetProperty("id", out var i) ? i.GetString() : null;
                        var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var mime = f.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                        if (mime == "application/vnd.google-apps.folder")
                        {
                            if (s.Subfolders && folderId is not null && queue.Count < 8 && SanitizeDriveId(id) is { Length: > 0 } safeChild)
                                queue.Enqueue((safeChild, name));
                            continue;
                        }

                        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;

                        var thumb = f.TryGetProperty("thumbnailLink", out var tl) ? tl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(thumb))
                            thumb = Regex.Replace(thumb, @"=s\d+$", "=s1600");

                        var directUrl = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media";
                        var isHeic = mime.Contains("heic", StringComparison.OrdinalIgnoreCase) || mime.Contains("heif", StringComparison.OrdinalIgnoreCase);
                        var primary = isHeic && !string.IsNullOrWhiteSpace(thumb) ? thumb! : directUrl;
                        var fallback = isHeic ? null : thumb;

                        var caption = f.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                            ? desc.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(caption)) caption = Path.GetFileNameWithoutExtension(name);

                        long? takenAt = null;
                        if (f.TryGetProperty("imageMediaMetadata", out var meta)
                            && meta.TryGetProperty("time", out var metaTime)
                            && metaTime.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParseExact(metaTime.GetString(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mDto))
                            takenAt = mDto.ToUnixTimeMilliseconds();
                        else if (f.TryGetProperty("createdTime", out var ctEl)
                            && DateTimeOffset.TryParse(ctEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cDto))
                            takenAt = cDto.ToUnixTimeMilliseconds();

                        var itemId = ComputeId("google", $"{sessionStamp}\0{id}", takenAt?.ToString(CultureInfo.InvariantCulture) ?? "");
                        items.Add(new ResolvedPhoto(
                            Id: itemId,
                            Provider: "google",
                            RemoteUrlOrId: primary,
                            LocalFilePath: null,
                            LocalRootPath: null,
                            ExtraHeaderArg: null,
                            FallbackUrl: fallback,
                            TakenAtMs: takenAt,
                            Caption: caption,
                            Album: albumName,
                            SourceLabel: sourceLabel));
                    }
                }

                var nextTok = doc.RootElement.TryGetProperty("nextPageToken", out var npt) && npt.ValueKind == JsonValueKind.String
                    ? npt.GetString()
                    : null;
                pageToken = !string.IsNullOrWhiteSpace(nextTok) && seenTokens.Add(nextTok) ? nextTok : null;
            } while (pageToken is not null && items.Count < MaxManifestItems && ops < 10);
        }

        return (sourceLabel, items);
    }

    private async Task<string?> ResolveGoogleDriveFolderPathAsync(string folderPath, CancellationToken ct)
    {
        var current = "root";
        foreach (var seg in folderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var escaped = seg.Replace("\\", "\\\\").Replace("'", "\\'");
            var q = $"'{current}' in parents and name = '{escaped}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)&pageSize=5";
            using var doc = await SendAuthedJsonAsync("google", HttpMethod.Get, url, null, ct);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
                throw new IOException($"Google Drive folder '{folderPath}' was not found");
            var id = files[0].TryGetProperty("id", out var i) ? SanitizeDriveId(i.GetString()) : null;
            if (string.IsNullOrEmpty(id))
                throw new IOException($"Google Drive folder '{folderPath}' could not be resolved");
            current = id;
        }
        return current;
    }

    private static string? SanitizeDriveId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return DriveIdRegex().IsMatch(trimmed) ? trimmed : null;
    }

    // ---------------------------------------------------------------- Dropbox API v2

    private async Task<PhotoBrowseResponse> BrowseDropboxAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        var roots = DiscoverLocalRoots().Where(r => r.ProviderHint == "dropbox").ToList();
        if (!_auth.IsConnected("dropbox"))
        {
            if (roots.Count > 0 && string.IsNullOrWhiteSpace(req.Path))
            {
                return new PhotoBrowseResponse(true, null, "dropbox", null, null,
                    roots.Select(r => new PhotoBrowseEntry("", $"{r.Name} (Local Sync)", "local-folder", null, r.Path)).ToList());
            }
            throw new IOException("Connect Dropbox in Settings › General, or pick a synced local Dropbox folder");
        }

        var path = NormalizeDropboxPath(req.Path);
        var payload = JsonSerializer.Serialize(new { path, recursive = false, include_media_info = false, limit = 200 });
        using var doc = await SendAuthedJsonAsync("dropbox", HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder", payload, ct);
        var entries = new List<PhotoBrowseEntry>();
        if (doc.RootElement.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var tag = e.TryGetProperty(".tag", out var t) ? t.GetString() : null;
                if (tag != "folder") continue;
                var p = e.TryGetProperty("path_display", out var pd) ? pd.GetString() : null;
                var name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(name))
                    entries.Add(new PhotoBrowseEntry(p, name, "folder", null, p));
            }
        }
        return new PhotoBrowseResponse(true, null, "dropbox", string.IsNullOrEmpty(path) ? "/" : path, "", entries);
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items)> FetchDropboxAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        if (!_auth.IsConnected("dropbox"))
            throw new IOException("Connect Dropbox in Settings › General, or pick a synced local Dropbox folder");

        var sessionStamp = _auth.GetSessionStamp("dropbox");
        var path = NormalizeDropboxPath(string.IsNullOrEmpty(s.AlbumId) ? s.Folder : s.AlbumId);
        var label = string.IsNullOrEmpty(path) ? "Dropbox" : Path.GetFileName(path.Trim('/'));
        if (string.IsNullOrEmpty(label)) label = "Dropbox";

        var items = new List<ResolvedPhoto>();
        var payload = JsonSerializer.Serialize(new { path, recursive = s.Subfolders, include_media_info = true, limit = 200 });
        string? cursor = null;

        for (var page = 0; page < 3 && items.Count < MaxManifestItems; page++)
        {
            using var doc = cursor is null
                ? await SendAuthedJsonAsync("dropbox", HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder", payload, ct)
                : await SendAuthedJsonAsync("dropbox", HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder/continue", JsonSerializer.Serialize(new { cursor }), ct);

            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (items.Count >= MaxManifestItems) break;
                    if (entry.TryGetProperty(".tag", out var t) && t.GetString() != "file") continue;

                    var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var pathLower = entry.TryGetProperty("path_lower", out var pl) ? pl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(pathLower)) continue;
                    var ext = Path.GetExtension(name);
                    if (!ImageExtensions.Contains(ext)) continue;

                    long? takenAt = null;
                    if (entry.TryGetProperty("media_info", out var mi)
                        && mi.TryGetProperty("metadata", out var md)
                        && md.TryGetProperty("time_taken", out var tt)
                        && DateTimeOffset.TryParse(tt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                        takenAt = dto.ToUnixTimeMilliseconds();
                    else if (entry.TryGetProperty("client_modified", out var cm)
                        && DateTimeOffset.TryParse(cm.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cDto))
                        takenAt = cDto.ToUnixTimeMilliseconds();

                    var rev = entry.TryGetProperty("rev", out var r) ? r.GetString() ?? "" : "";
                    var itemId = ComputeId("dropbox", $"{sessionStamp}\0{pathLower}", rev);
                    var apiArg = JsonSerializer.Serialize(new { path = pathLower });
                    var parentDir = Path.GetFileName(Path.GetDirectoryName(pathLower.Replace('/', Path.DirectorySeparatorChar)) ?? "") ?? label;

                    items.Add(new ResolvedPhoto(
                        Id: itemId,
                        Provider: "dropbox",
                        RemoteUrlOrId: "https://content.dropboxapi.com/2/files/download",
                        LocalFilePath: null,
                        LocalRootPath: null,
                        ExtraHeaderArg: apiArg,
                        FallbackUrl: null,
                        TakenAtMs: takenAt,
                        Caption: Path.GetFileNameWithoutExtension(name),
                        Album: string.IsNullOrEmpty(parentDir) ? label : parentDir,
                        SourceLabel: label));
                }
            }

            if (root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True
                && root.TryGetProperty("cursor", out var cur) && cur.GetString() is { Length: > 0 } c)
                cursor = c;
            else
                break;
        }

        return (label, items);
    }

    private static string NormalizeDropboxPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw.Trim().Replace('\\', '/');
        if (t == "/") return "";
        return t.StartsWith('/') ? t : "/" + t;
    }

    // ---------------------------------------------------------------- Nextcloud Public Share WebDAV

    internal static bool TryParseNextcloudShare(string raw, out string webdavBase, out string fallbackWebdavBase, out string token)
    {
        webdavBase = "";
        fallbackWebdavBase = "";
        token = "";
        if (!SafePhotoHttp.TryValidateHttpsUri(raw, out var uri, out _) || uri is null)
            return false;

        var path = uri.AbsolutePath;
        var idx = path.IndexOf("/s/", StringComparison.Ordinal);
        if (idx < 0) return false;

        var after = path[(idx + 3)..];
        var slash = after.IndexOf('/');
        var candidateToken = (slash >= 0 ? after[..slash] : after).Trim();
        if (!NextcloudTokenRegex().IsMatch(candidateToken))
            return false;

        var prefix = path[..idx];
        if (prefix.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase))
            prefix = prefix[..^"/index.php".Length];

        var rootUrl = $"{uri.Scheme}://{uri.Authority}{prefix}";
        webdavBase = $"{rootUrl}/public.php/dav/files/{Uri.EscapeDataString(candidateToken)}";
        fallbackWebdavBase = $"{rootUrl}/public.php/webdav";
        token = candidateToken;
        return true;
    }

    private async Task<PhotoBrowseResponse> BrowseNextcloudAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        if (!TryParseNextcloudShare(req.ShareUrl ?? "", out var davBase, out var fallbackBase, out var token))
            throw new IOException("Paste a valid HTTPS Nextcloud public share link (.../s/<token>)");

        var subPath = NormalizeNextcloudSubpath(req.Path);
        var propfindUrl = BuildNextcloudPath(davBase, subPath);
        var authHeader = BuildBasicAuth(token, req.SharePassword ?? "");

        var parsed = await SendPropfindAsync(propfindUrl, fallbackUrl: BuildNextcloudPath(fallbackBase, subPath), authHeader, ct);
        var entries = parsed
            .Where(x => x.IsDirectory)
            .Select(x =>
            {
                var rel = string.IsNullOrEmpty(subPath) ? x.Name : subPath.TrimEnd('/') + "/" + x.Name;
                return new PhotoBrowseEntry(rel, x.Name, "folder", null, rel);
            })
            .ToList();

        return new PhotoBrowseResponse(true, null, "nextcloud", string.IsNullOrEmpty(subPath) ? "/" : "/" + subPath, "", entries);
    }

    private async Task<(string SourceLabel, List<ResolvedPhoto> Items)> FetchNextcloudShareAsync(
        PhotoWidgetSettings s,
        CancellationToken ct)
    {
        if (!TryParseNextcloudShare(s.ShareUrl, out var davBase, out var fallbackBase, out var token))
            throw new IOException("Paste a valid HTTPS Nextcloud public share link (.../s/<token>)");

        var startSubpath = NormalizeNextcloudSubpath(s.Folder);
        var label = string.IsNullOrEmpty(startSubpath) ? "Nextcloud Share" : Path.GetFileName(startSubpath.TrimEnd('/'));
        var authHeader = BuildBasicAuth(token, s.SharePassword);

        var items = new List<ResolvedPhoto>();
        var queue = new Queue<(string Subpath, string Album)>();
        queue.Enqueue((startSubpath, label));
        var ops = 0;

        while (queue.Count > 0 && items.Count < MaxManifestItems && ops < 10)
        {
            var (sub, albumName) = queue.Dequeue();
            ops++;

            var davUrl = BuildNextcloudPath(davBase, sub);
            var fbUrl = BuildNextcloudPath(fallbackBase, sub);
            var entries = await SendPropfindAsync(davUrl, fbUrl, authHeader, ct);

            foreach (var entry in entries)
            {
                if (items.Count >= MaxManifestItems) break;
                if (entry.IsDirectory)
                {
                    if (s.Subfolders && queue.Count < 8)
                    {
                        var nextSub = string.IsNullOrEmpty(sub) ? entry.Name : sub.TrimEnd('/') + "/" + entry.Name;
                        queue.Enqueue((nextSub, entry.Name));
                    }
                    continue;
                }

                var ext = Path.GetExtension(entry.Name);
                var isImage = (entry.ContentType is not null && entry.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !entry.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                    || ImageExtensions.Contains(ext);
                if (!isImage) continue;

                var fileSub = string.IsNullOrEmpty(sub) ? entry.Name : sub.TrimEnd('/') + "/" + entry.Name;
                var fileUrl = BuildNextcloudPath(entry.UsedFallback ? fallbackBase : davBase, fileSub, trailingSlash: false);
                var itemId = ComputeId("nextcloud", fileUrl, entry.LastModifiedMs?.ToString(CultureInfo.InvariantCulture) ?? "");

                items.Add(new ResolvedPhoto(
                    Id: itemId,
                    Provider: "nextcloud",
                    RemoteUrlOrId: fileUrl,
                    LocalFilePath: null,
                    LocalRootPath: null,
                    ExtraHeaderArg: authHeader.Parameter,
                    FallbackUrl: null,
                    TakenAtMs: entry.LastModifiedMs,
                    Caption: Path.GetFileNameWithoutExtension(entry.Name),
                    Album: albumName,
                    SourceLabel: label));
            }
        }

        return (label, items);
    }

    internal sealed record WebDavEntry(string Name, bool IsDirectory, string? ContentType, long? LastModifiedMs, bool UsedFallback);

    private async Task<List<WebDavEntry>> SendPropfindAsync(
        string primaryUrl,
        string fallbackUrl,
        AuthenticationHeaderValue authHeader,
        CancellationToken ct)
    {
        var usedFallback = false;
        HttpResponseMessage? resp = null;
        string requestUrl = primaryUrl;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUrl);
            req.Headers.Authorization = authHeader;
            req.Headers.Add("Depth", "1");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Content = new StringContent(
                """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/><d:getcontenttype/><d:getlastmodified/></d:prop></d:propfind>""",
                Encoding.UTF8,
                "application/xml");

            resp = await SafePhotoHttp.SendFollowingRedirectsAsync(_http, req, ct);
            if ((int)resp.StatusCode is 404 or 405 && attempt == 0 && !string.Equals(primaryUrl, fallbackUrl, StringComparison.Ordinal))
            {
                resp.Dispose();
                requestUrl = fallbackUrl;
                usedFallback = true;
                continue;
            }
            break;
        }

        if (resp is null)
            throw new IOException("Nextcloud request failed");

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new IOException("Nextcloud share requires a password or the link expired");
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 207)
                throw new IOException($"Nextcloud WebDAV returned HTTP {(int)resp.StatusCode}");

            var xml = await ReadResponseStringBoundedAsync(resp.Content, SafePhotoHttp.MaxJsonBytes, ct);
            return ParseWebDavResponses(xml, new Uri(requestUrl).AbsolutePath, usedFallback);
        }
    }

    internal static List<WebDavEntry> ParseWebDavResponses(string xml, string requestPath, bool usedFallback)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersFromEntities = 1024,
        };
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(reader);
        XNamespace dav = "DAV:";
        var normalizedReq = requestPath.TrimEnd('/');
        var list = new List<WebDavEntry>();

        foreach (var respEl in doc.Descendants(dav + "response"))
        {
            var hrefRaw = respEl.Element(dav + "href")?.Value?.Trim();
            if (string.IsNullOrEmpty(hrefRaw)) continue;
            var hrefPath = Uri.TryCreate(hrefRaw, UriKind.Absolute, out var abs) ? abs.AbsolutePath : hrefRaw;
            var trimmedHref = hrefPath.TrimEnd('/');
            if (string.Equals(trimmedHref, normalizedReq, StringComparison.OrdinalIgnoreCase))
                continue; // skip the directory itself

            var decodedName = Uri.UnescapeDataString(Path.GetFileName(trimmedHref.TrimEnd('/')));
            if (string.IsNullOrWhiteSpace(decodedName) || decodedName.StartsWith('.'))
                continue;

            var prop = respEl.Descendants(dav + "prop").FirstOrDefault();
            if (prop is null) continue;

            var isDir = prop.Element(dav + "resourcetype")?.Element(dav + "collection") is not null;
            var contentType = prop.Element(dav + "getcontenttype")?.Value?.Trim();
            var lastModRaw = prop.Element(dav + "getlastmodified")?.Value?.Trim();
            long? lastModMs = DateTimeOffset.TryParse(lastModRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : null;

            list.Add(new WebDavEntry(decodedName, isDir, contentType, lastModMs, usedFallback));
        }

        return list;
    }

    // ---------------------------------------------------------------- authenticated JSON & image fetching

    private async Task<JsonDocument> SendAuthedJsonAsync(
        string provider,
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(provider, forceRefresh: attempt > 0, ct)
                ?? throw new IOException($"Connect {CloudPhotoAuth.ProviderDisplayName(provider)} in Settings › General");

            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await SafePhotoHttp.SendFollowingRedirectsAsync(_http, req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                continue;
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"{CloudPhotoAuth.ProviderDisplayName(provider)} returned HTTP {(int)resp.StatusCode}");

            var body = await ReadResponseStringBoundedAsync(resp.Content, SafePhotoHttp.MaxJsonBytes, ct);
            return JsonDocument.Parse(body);
        }

        throw new IOException($"Reconnect {CloudPhotoAuth.ProviderDisplayName(provider)} in Settings › General");
    }

    private async Task<string> ReadResponseStringBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(_bodyReadTimeout);
        try
        {
            return await SafePhotoHttp.ReadBoundedStringAsync(content, maxBytes, bodyCts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Provider response timed out", ex);
        }
    }

    private async Task<(byte[] Bytes, string Mime)?> FetchRemotePhotoBytesAsync(ResolvedPhoto item, CancellationToken ct)
    {
        var urlsToTry = new List<string> { item.RemoteUrlOrId };
        if (!string.IsNullOrWhiteSpace(item.FallbackUrl))
            urlsToTry.Add(item.FallbackUrl);

        foreach (var targetUrl in urlsToTry)
        {
            var isPrimary = string.Equals(targetUrl, item.RemoteUrlOrId, StringComparison.Ordinal);
            var maxAttempts = CloudPhotoAuth.IsOAuthProvider(item.Provider) ? 2 : 1;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                using var req = new HttpRequestMessage(
                    item.Provider == "dropbox" && isPrimary ? HttpMethod.Post : HttpMethod.Get,
                    targetUrl);

                if (CloudPhotoAuth.IsOAuthProvider(item.Provider))
                {
                    var access = await _auth.GetAccessTokenAsync(item.Provider, forceRefresh: attempt > 0, ct);
                    if (string.IsNullOrEmpty(access)) return null;
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                }
                else if (item.Provider == "nextcloud" && !string.IsNullOrEmpty(item.ExtraHeaderArg))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", item.ExtraHeaderArg);
                    req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                }

                if (item.Provider == "dropbox" && !string.IsNullOrEmpty(item.ExtraHeaderArg))
                    req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", item.ExtraHeaderArg);

                try
                {
                    using var resp = await SafePhotoHttp.SendFollowingRedirectsAsync(_http, req, ct);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && maxAttempts > 1)
                        continue;
                    if (!resp.IsSuccessStatusCode) break;

                    using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bodyCts.CancelAfter(_bodyReadTimeout);
                    var bytes = await SafePhotoHttp.ReadBoundedBytesAsync(resp.Content, SafePhotoHttp.MaxImageBytes, bodyCts.Token);
                    var mime = SafePhotoHttp.DetectRasterMime(bytes);
                    if (mime is not null)
                        return (bytes, mime);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    break;
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- disk cache & widget lookup

    private async Task WriteCacheFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            tmp = null;

            if (Interlocked.Increment(ref _cacheWriteCounter) % 8 == 0)
                PruneCacheDirectory();
        }
        catch { /* disk cache failures are non-fatal */ }
        finally
        {
            try
            {
                if (tmp is not null && File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch { }
        }
    }

    private void PruneCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            var maxBytes = Math.Clamp(_config.Get().CloudPhotos.CacheMaxMb, 20, 2048) * 1024L * 1024L;
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*.bin")
                .Select(f => new { f.FullName, f.Length, f.LastWriteTimeUtc })
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            var target = (long)(maxBytes * 0.85);
            foreach (var f in files)
            {
                if (total <= target) break;
                try
                {
                    File.Delete(f.FullName);
                    total -= f.Length;
                }
                catch { }
            }
        }
        catch { }
    }

    private void RegisterItemLocked(ResolvedPhoto item)
    {
        if (!_items.ContainsKey(item.Id))
        {
            while ((_items.Count >= MaxItemRegistry || _itemOrder.Count >= MaxItemRegistry * 2) && _itemOrder.Count > 0)
            {
                var victim = _itemOrder.Dequeue();
                _items.Remove(victim);
            }
            _itemOrder.Enqueue(item.Id);
        }
        _items[item.Id] = item;
    }

    private PhotoWidgetSettings? FindWidgetSettings(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId)) return null;
        var cfg = _config.Get();

        foreach (var page in cfg.Pages)
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "photos" && string.Equals(w.Id, widgetId, StringComparison.Ordinal))
                    return PhotoWidgetOptions.Parse(w.Options);
            }
        }

        var colon = widgetId.IndexOf(':');
        if (colon > 0)
        {
            var stackId = widgetId[..colon];
            var childId = widgetId[(colon + 1)..];
            foreach (var page in cfg.Pages)
            {
                foreach (var w in page.Widgets ?? [])
                {
                    if (w.Type == "stack" && string.Equals(w.Id, stackId, StringComparison.Ordinal))
                    {
                        var child = StackWidgets.Children(w)
                            .FirstOrDefault(c => c.Type == "photos" && string.Equals(c.Id, childId, StringComparison.Ordinal));
                        if (child is not null)
                            return PhotoWidgetOptions.Parse(child.Options);
                    }
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- utilities

    private static bool IsSourceConfigured(PhotoWidgetSettings s) => s.Provider switch
    {
        "local" => !string.IsNullOrWhiteSpace(s.Folder),
        "icloud" => !string.IsNullOrWhiteSpace(s.ShareUrl) || !string.IsNullOrWhiteSpace(s.Folder),
        "nextcloud" => !string.IsNullOrWhiteSpace(s.ShareUrl),
        "onedrive" or "google" or "dropbox" => true,
        _ => false,
    };

    private static string MissingSourceMessage(string provider) => provider switch
    {
        "icloud" => "Paste an iCloud Shared Album link or pick a synced folder in Settings",
        "nextcloud" => "Paste a Nextcloud public share link in Settings",
        _ => "Choose a photo album or folder in PhoneBox Settings",
    };

    private string SourceKey(PhotoWidgetSettings s)
    {
        var pwdHash = string.IsNullOrEmpty(s.SharePassword)
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.SharePassword))[..8]);
        var authStamp = CloudPhotoAuth.IsOAuthProvider(s.Provider) ? _auth.GetSessionStamp(s.Provider) : "";
        var epoch = GetProviderEpoch(s.Provider);
        return $"{s.Provider}|{authStamp}|{epoch}|{s.ShareUrl}|{pwdHash}|{s.AlbumId}|{s.Folder}|{(s.Subfolders ? '1' : '0')}|{(s.DownloadCloudFiles ? '1' : '0')}";
    }

    private static string ComputeId(string provider, string key, string stamp)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\0{key}\0{stamp}"));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? TryReadLong(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static AuthenticationHeaderValue BuildBasicAuth(string user, string password)
    {
        var raw = Encoding.UTF8.GetBytes($"{user}:{password}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private static string NormalizeNextcloudSubpath(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim().Trim('/').Replace('\\', '/');

    private static string BuildNextcloudPath(string baseUrl, string subPath, bool trailingSlash = true)
    {
        var suffix = trailingSlash ? "/" : "";
        if (string.IsNullOrEmpty(subPath)) return baseUrl + suffix;
        var encoded = string.Join('/', subPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"{baseUrl}/{encoded}{suffix}";
    }

    private static string ExpandUserPath(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            return Path.Combine(home, trimmed[2..]);
        }
        return trimmed;
    }

    private static bool LooksLikeLocalFilesystemPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim();
        if (t == "~" || t.StartsWith("~/", StringComparison.Ordinal) || t.StartsWith("~\\", StringComparison.Ordinal))
            return true;
        if (Path.IsPathFullyQualified(t) && OperatingSystem.IsWindows())
            return true;
        return t.StartsWith("/Users/", StringComparison.Ordinal)
            || t.StartsWith("/home/", StringComparison.Ordinal)
            || t.StartsWith("/Volumes/", StringComparison.Ordinal)
            || t.StartsWith("/mnt/", StringComparison.Ordinal)
            || t.StartsWith("/media/", StringComparison.Ordinal)
            || t.StartsWith("/var/", StringComparison.Ordinal)
            || t.StartsWith("/private/", StringComparison.Ordinal)
            || t.StartsWith("/tmp/", StringComparison.Ordinal);
    }

    private static bool IsExistingLocalDirectory(string raw) =>
        !string.IsNullOrWhiteSpace(raw) && TryResolveSafeDirectory(ExpandUserPath(raw)) is not null;

    private static string? TryResolveSafeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;
            var di = new DirectoryInfo(full);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null)
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string EnsureTrailingSep(string fullPath) =>
        fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;

    private static string NormalizeMacPath(string path) =>
        OperatingSystem.IsMacOS() ? path.Normalize(NormalizationForm.FormC) : path;

    internal static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(NormalizeMacPath(a), NormalizeMacPath(b), PathComparison);

    internal static bool IsPathInsideRoot(string fullCandidate, string rootFull, string rootWithSep)
    {
        var cand = NormalizeMacPath(fullCandidate);
        var root = NormalizeMacPath(rootFull);
        var pref = NormalizeMacPath(rootWithSep);
        return string.Equals(cand, root, PathComparison) || cand.StartsWith(pref, PathComparison);
    }

    private static void AddGlobCloudStorage(
        List<(string Id, string Name, string Hint, string? Path)> candidates,
        string parentDir,
        string pattern,
        string hint,
        string label)
    {
        try
        {
            if (!Directory.Exists(parentDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(parentDir, pattern).Take(4))
            {
                var leaf = Path.GetFileName(dir);
                candidates.Add(($"cs-{hint}-{leaf}", $"{label} ({leaf})", hint, dir));
            }
        }
        catch { }
    }

    private static void TryAddDropboxJsonRoots(
        List<(string Id, string Name, string Hint, string? Path)> candidates,
        string home)
    {
        try
        {
            var infoPaths = new[]
            {
                Path.Combine(home, ".dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
            };
            foreach (var infoPath in infoPaths)
            {
                if (!File.Exists(infoPath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                foreach (var kind in new[] { "personal", "business" })
                {
                    if (doc.RootElement.TryGetProperty(kind, out var section)
                        && section.TryGetProperty("path", out var pEl)
                        && pEl.GetString() is { Length: > 0 } p)
                    {
                        candidates.Add(($"db-json-{kind}", $"Dropbox ({kind})", "dropbox", p));
                    }
                }
            }
        }
        catch { }
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{6,40}$")]
    private static partial Regex ICloudTokenRegex();

    [GeneratedRegex(@"^[a-z0-9-]+-sharedstreams\.icloud\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex ICloudStreamHostRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{4,64}$")]
    private static partial Regex NextcloudTokenRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex DriveIdRegex();

    public void Dispose()
    {
        _auth.ProviderAuthChanged -= InvalidateProvider;
        _materializationGate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
