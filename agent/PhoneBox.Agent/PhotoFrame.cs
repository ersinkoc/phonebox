using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>Settings › General › Photo frame. The folder stays on this computer (a local path names the user).</summary>
public sealed class PhotoFrameConfig
{
    public string Folder { get; set; } = "";
    public bool Subfolders { get; set; } = true;
}

/// <summary>
/// Photos for the Photo frame widget from one folder on this computer (a synced OneDrive, Dropbox or iCloud folder
/// works too). The phone gets ids and asks for one photo at a time; large photos are scaled down here first, so an old
/// phone never decodes a camera original. Nothing is scanned or served while no enabled page shows the widget, and
/// files that are only in the cloud are skipped, so a slideshow never downloads a whole synced library.
/// </summary>
public sealed class PhotoFrame(ConfigStore config, ILogger<PhotoFrame> log)
{
    internal const string WidgetType = "photos";
    internal const int MaxFiles = 5000, MaxEntries = 50_000, LongEdge = 1600;
    internal const long MaxSourceBytes = 60L * 1024 * 1024;
    /// <summary>What an old phone is sent as it is: at most this many pixels and 8192 px a side.</summary>
    internal const long MaxPhonePixels = 4096L * 4096, MaxDecodePixels = 100_000_000;
    private const long OriginalMaxBytes = 6L * 1024 * 1024;
    private const int HeadBytes = 256 * 1024;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    // ids the phone sees are keyed per run, so a paired phone can't confirm guessed file names
    private static readonly byte[] IdKey = RandomNumberGenerator.GetBytes(32);

    internal sealed record Photo(string Id, string Path, long Length, long Ticks);
    internal sealed record Library(string Root, bool Subfolders, IReadOnlyList<Photo> Photos, long ScannedAt, string? Error);

    private readonly Lock _gate = new();
    private Library? _library;
    private Task? _scan;
    private (string Root, bool Subfolders, int Generation)? _scanKey;
    // bumped when the folder setting is cleared or changed: a scan or conversion from before must not write back
    private int _generation;
    private readonly SemaphoreSlim _convert = new(1, 1);
    private readonly HashSet<string> _failed = []; // cache keys of photos that couldn't be converted

    /// <summary>Test seam: where scaled copies are kept.</summary>
    internal string CacheDir { get; init; } = System.IO.Path.Combine(AppPaths.LocalDir, "photos-cache");
    /// <summary>Test seam: the clock (Environment.TickCount64).</summary>
    internal Func<long> Now { get; init; } = () => Environment.TickCount64;
    /// <summary>Test seam: lists a folder (<see cref="Scan"/>).</summary>
    internal Func<string, bool, Library> Scanner { get; init; } = Scan;

    /// <summary>Test seam: scans now and waits for it.</summary>
    internal void ScanNow() { lock (_gate) _library = null; Current(config.Get().Photos, wait: true); }

    public bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    /// <summary>The photo ids, or an error the phone can show (never a local path).</summary>
    public (IReadOnlyList<string> Ids, string? Error, bool Scanning) List()
    {
        var cfg = config.Get().Photos;
        if (string.IsNullOrWhiteSpace(cfg.Folder)) { ForgetCache(); return ([], null, false); }
        if (!Shown()) return ([], "Add a Photo frame widget to a page to show photos", false);
        var lib = Current(cfg, wait: false);
        if (lib is null) return ([], null, true);
        return (lib.Photos.Select(p => p.Id).ToList(), lib.Error, false);
    }

    /// <summary>The library for these settings; scans in the background when it is missing or older than five minutes.</summary>
    private Library? Current(PhotoFrameConfig cfg, bool wait)
    {
        Task? scan;
        lock (_gate)
        {
            var root = cfg.Folder.Trim();
            var subfolders = cfg.Subfolders;
            var lib = _library;
            var fresh = lib is not null && lib.Root == root && lib.Subfolders == subfolders;
            if (fresh && Now() - lib!.ScannedAt < ScanEvery.TotalMilliseconds) return lib;
            var gen = _generation;
            var key = (root, subfolders, gen);
            if (_scan is null || _scanKey != key)
            {
                _scanKey = key;
                Task? launched = null;
                launched = _scan = Task.Run(() =>
                {
                    Library next;
                    try { next = Scanner(root, subfolders); }
                    catch (Exception ex)
                    {
                        log.LogDebug("photo scan: {Msg}", ex.Message);
                        next = new(root, subfolders, [], 0, "The photo folder can't be read");
                    }
                    // always cleared for the active scan; stale scans from a cleared/switched folder are dropped, and
                    // so is an older scan for the same selection that no longer owns the slot. The cache is cleaned in
                    // the same lock, so a copy cached for a folder chosen since can't be deleted by this scan.
                    lock (_gate)
                    {
                        if (_scan == launched && _generation == gen && _scanKey == key)
                        {
                            _library = next with { ScannedAt = Now() };
                            try { CleanCacheLocked(next); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                        if (_scan == launched) { _scan = null; _scanKey = null; }
                    }
                });
            }
            scan = _scan!;
            if (fresh && !wait) return lib; // the old list until the new scan is done
        }
        if (!wait) return null;
        scan.Wait(TimeSpan.FromSeconds(30));
        lock (_gate) return _library;
    }

    /// <summary>
    /// Lists the images in the folder: bounded, no hidden or system files, no links (symlinks or junctions, files or
    /// folders), nothing that is only in the cloud. Cloud-synced files themselves are reparse points too, so they are
    /// told apart from links by their link target, not by the attribute.
    /// </summary>
    internal static Library Scan(string root, bool subfolders)
    {
        if (RefuseRoot(root) is { } refused) return new(root, subfolders, [], 0, refused);
        var full = System.IO.Path.GetFullPath(root);
        var photos = new List<Photo>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = subfolders, MaxRecursionDepth = 6, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new FileSystemEnumerable<FileInfo>(full, (ref FileSystemEntry e) => (FileInfo)e.ToFileSystemInfo(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            // a junction or symlinked folder is never entered; a cloud-synced folder (reparse point, no target) is
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 || e.ToFileSystemInfo().LinkTarget is null,
        };
        var seen = 0;
        try
        {
            foreach (var f in files)
            {
                if (++seen > MaxEntries) break;
                try
                {
                    if (!Extensions.Contains(f.Extension.ToLowerInvariant()) || CloudOnly(f.Attributes) || IsLink(f.Attributes, () => f.LinkTarget)) continue;
                    if (f.Length is <= 0 or > MaxSourceBytes) continue;
                    photos.Add(new Photo(IdOf(full, System.IO.Path.GetRelativePath(full, f.FullName)), f.FullName, f.Length, f.LastWriteTimeUtc.Ticks));
                    if (photos.Count >= MaxFiles) break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // gone or locked while syncing: the rest still counts
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (photos.Count == 0) return new(root, subfolders, [], 0, "The photo folder can't be read");
        }
        photos.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new(root, subfolders, photos, 0, photos.Count == 0 ? "No photos in the folder" : null);
    }

    /// <summary>A symlink or junction (a reparse point with a target), not a cloud-synced file or folder (no target).</summary>
    internal static bool IsLink(FileAttributes attributes, Func<string?> linkTarget) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && linkTarget() is not null;

    // Windows cloud placeholders (OneDrive Files On-Demand and others): reading them downloads the file.
    internal const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    internal const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    internal static bool CloudOnly(FileAttributes a) => (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    internal static string IdOf(string root, string relativePath)
    {
        var data = Encoding.UTF8.GetBytes(System.IO.Path.TrimEndingDirectorySeparator(root) + "\n" + relativePath.Replace('\\', '/'));
        return Convert.ToHexStringLower(HMACSHA256.HashData(IdKey, data))[..16];
    }

    /// <summary>The name of a photo's scaled copy: its full path, size and time, never sent anywhere.</summary>
    private static string CacheKey(Photo p) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(p.Path)))[..20] + $"-{p.Ticks:x}-{p.Length:x}";

    /// <summary>Folders too broad to show as a photo frame: a drive or file-system root, the home folder itself, system folders.</summary>
    internal static string? RefuseRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root)) return "Choose a full folder path in Settings › General";
        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Choose a full folder path in Settings › General"; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)) return "Network paths aren't supported; use a folder on this computer";
        if (OperatingSystem.IsWindows())
            try { if (new DriveInfo(full).DriveType == DriveType.Network) return "Network drives aren't supported; use a folder on this computer"; }
            catch (ArgumentException) { }
        var home = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Same(string? other) => !string.IsNullOrEmpty(other) && string.Equals(full, System.IO.Path.TrimEndingDirectorySeparator(other), cmp);
        bool Under(string? other) => !string.IsNullOrEmpty(other)
            && (Same(other) || full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(other) + System.IO.Path.DirectorySeparatorChar, cmp));
        if (System.IO.Path.GetPathRoot(full) is { } r && Same(r)) return "Choose a photo folder, not a whole drive";
        if (Same(home) || Same(System.IO.Path.GetDirectoryName(home))) return "Choose a photo folder, not your whole home folder";
        if (Under(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) || Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) || Under(AppPaths.ConfigDir)
            || (!OperatingSystem.IsWindows() && (Under("/etc") || Under("/proc") || Under("/sys") || Under("/dev") || Under("/System") || Under("/Library") || Under("/usr"))))
            return "Choose a photo folder, not a system folder";
        if (!Directory.Exists(full)) return "The photo folder doesn't exist";
        return null;
    }

    /// <summary>After the settings are saved: a cleared folder takes its scaled copies with it, widget or not.</summary>
    public void SettingsSaved()
    {
        var photos = config.Get().Photos;
        var folder = photos.Folder.Trim();
        if (string.IsNullOrWhiteSpace(folder)) { ForgetCache(); return; }
        // Subfolders counts as much as the folder: a recursive scan in flight must not publish after it was turned off
        lock (_gate)
        {
            if (_library is { } lib && (lib.Root != folder || lib.Subfolders != photos.Subfolders)) { _library = null; _generation++; }
            else if (_scanKey is { } sk && (sk.Root != folder || sk.Subfolders != photos.Subfolders)) _generation++;
        }
    }

    /// <summary>One photo, scaled down when it is large; null with a reason when it can't be served.</summary>
    public async Task<(byte[]? Bytes, string? Type, string? Error)> Get(string? id, CancellationToken ct)
    {
        if (!Shown()) return (null, null, "Add a Photo frame widget to a page to show photos");
        var cfg = config.Get().Photos;
        if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(cfg.Folder)) return (null, null, "Unknown photo");
        var lib = Current(cfg, wait: false);
        if (lib?.Photos.FirstOrDefault(p => p.Id == id) is not { } photo) return (null, null, "Unknown photo");
        try
        {
            // checked again now: the file, or a folder on its way, may have become a link since the scan
            var fi = new FileInfo(photo.Path);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(lib.Root));
            if (!fi.Exists || !fi.FullName.StartsWith(root + System.IO.Path.DirectorySeparatorChar, cmp)
                || IsLink(fi.Attributes, () => fi.LinkTarget) || CloudOnly(fi.Attributes) || fi.Length is <= 0 or > MaxSourceBytes)
                return (null, null, "That photo is no longer available");
            for (var d = fi.Directory; d is not null && d.FullName.Length > root.Length; d = d.Parent)
                if (IsLink(d.Attributes, () => d.LinkTarget)) return (null, null, "That photo is no longer available");
            return await Prepare(photo with { Length = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            log.LogDebug("photo {Id}: {Msg}", id, ex.Message);
            return (null, null, "That photo can't be shown");
        }
    }

    private async Task<(byte[]? Bytes, string? Type, string? Error)> Prepare(Photo photo, CancellationToken ct)
    {
        int generation;
        lock (_gate) generation = _generation;
        var head = new byte[(int)Math.Min(HeadBytes, photo.Length)];
        int got;
        await using (var s = new FileStream(photo.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            got = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (MimeOf(head.AsSpan(0, got)) is not { } mime) return (null, null, "That photo can't be shown");
        var size = SizeOf(head.AsSpan(0, got), mime);
        if (size is { } invalid && (invalid.W <= 0 || invalid.H <= 0)) return (null, null, "That photo can't be shown");
        // only a known, moderate size goes to the phone as it is: a small file can still be 20000 x 20000 pixels
        var phoneSafe = size is { } d && d.W <= 8192 && d.H <= 8192 && (long)d.W * d.H <= MaxPhonePixels && photo.Length <= OriginalMaxBytes;

        var key = CacheKey(photo);
        var cached = System.IO.Path.Combine(CacheDir, key + ".jpg");
        if (File.Exists(cached)) return (await File.ReadAllBytesAsync(cached, ct), "image/jpeg", null);
        if ((mime is "image/gif" or "image/webp" || photo.Length <= 400_000) && phoneSafe)
            return (await File.ReadAllBytesAsync(photo.Path, ct), mime, null);

        bool failed;
        lock (_gate) failed = _failed.Contains(key);
        var decodable = mime is "image/jpeg" or "image/png" && (size is null ? photo.Length <= 30L * 1024 * 1024 : (long)size.Value.W * size.Value.H <= MaxDecodePixels);
        if (decodable && !failed)
        {
            await _convert.WaitAsync(ct);
            try
            {
                if (File.Exists(cached)) return (await File.ReadAllBytesAsync(cached, ct), "image/jpeg", null);
                byte[]? jpeg = null;
                try { jpeg = Scale(photo.Path, ct); }
                catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException or System.Runtime.InteropServices.ExternalException)
                {
                    // GDI+ answers a damaged or half-synced file with OutOfMemoryException: remember it, don't decode it every round
                    log.LogDebug("photo {Id} can't be converted: {Msg}", photo.Id, ex.Message);
                    lock (_gate) { if (_failed.Count > 1000) _failed.Clear(); _failed.Add(key); }
                }
                if (jpeg is not null)
                {
                    WriteCache(cached, jpeg, generation);
                    return (jpeg, "image/jpeg", null);
                }
            }
            finally { _convert.Release(); }
        }
        return phoneSafe ? (await File.ReadAllBytesAsync(photo.Path, ct), mime, null) : (null, null, "That photo is too large to show");
    }

    /// <summary>Writes a scaled copy to a temporary owner-only file first, so a reader never sees half of one.</summary>
    private void WriteCache(string path, byte[] jpeg, int generation)
    {
        // under the same lock as ForgetCache: a copy is either written before the folder was cleared (and deleted with
        // the rest) or not at all
        lock (_gate)
        {
            if (generation != _generation) return;
            Directory.CreateDirectory(CacheDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            using (var f = AppPaths.CreatePrivate(tmp)) f.Write(jpeg);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>
    /// After a scan, under <see cref="_gate"/> (the lock WriteCache publishes with, so a copy being written is never
    /// pulled away mid-move): scaled copies of photos that are gone, changed or in another folder are deleted.
    /// </summary>
    private void CleanCacheLocked(Library lib)
    {
        var keep = lib.Photos.Select(p => CacheKey(p) + ".jpg").ToHashSet(StringComparer.Ordinal);
        if (!Directory.Exists(CacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(CacheDir))
            if (!keep.Contains(System.IO.Path.GetFileName(f))) try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>No folder any more: no scaled copies of its photos either.</summary>
    private void ForgetCache()
    {
        lock (_gate)
        {
            _library = null;
            _scan = null;
            _scanKey = null;
            _generation++;
            try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The image type from its first bytes; a file named .jpg that isn't one is refused.</summary>
    internal static string? MimeOf(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    /// <summary>Width and height from the image's own header (PNG IHDR, GIF screen, WebP VP8/VP8L/VP8X, JPEG SOFn); null when unknown.</summary>
    internal static (int W, int H)? SizeOf(ReadOnlySpan<byte> b, string mime)
    {
        switch (mime)
        {
            case "image/png" when b.Length >= 24:
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
                return ((int)Math.Min(w, int.MaxValue), (int)Math.Min(h, int.MaxValue));
            }
            case "image/gif" when b.Length >= 10:
                return (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
            case "image/webp" when b.Length >= 30:
                if (b[12..16].SequenceEqual("VP8 "u8)) return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
                if (b[12..16].SequenceEqual("VP8L"u8) && b.Length >= 25)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
                if (b[12..16].SequenceEqual("VP8X"u8))
                    return ((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
                return null;
            case "image/jpeg":
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF) { i += marker == 0xFF ? 1 : 2; continue; }
                    // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
                    if (marker == 0xDA) return null; // image data begins before any frame header: give up
                    i += 2 + BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>A JPEG whose long edge is at most <see cref="LongEdge"/>, turned upright; null where no converter is available.</summary>
    private static byte[]? Scale(string path, CancellationToken ct)
    {
#if WINDOWS
        using var src = System.Drawing.Image.FromFile(path);
        var orientation = src.PropertyIdList.Contains(0x0112) && src.GetPropertyItem(0x0112)?.Value is { Length: > 0 } v ? v[0] : 1;
        var scale = Math.Min(1.0, (double)LongEdge / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Black);
            g.DrawImage(src, 0, 0, w, h);
        }
        // the EXIF orientation is lost when re-encoding, so it is applied to the (already small) pixels
        bmp.RotateFlip(orientation switch
        {
            2 => System.Drawing.RotateFlipType.RotateNoneFlipX, 3 => System.Drawing.RotateFlipType.Rotate180FlipNone,
            4 => System.Drawing.RotateFlipType.Rotate180FlipX, 5 => System.Drawing.RotateFlipType.Rotate90FlipX,
            6 => System.Drawing.RotateFlipType.Rotate90FlipNone, 7 => System.Drawing.RotateFlipType.Rotate270FlipX,
            8 => System.Drawing.RotateFlipType.Rotate270FlipNone, _ => System.Drawing.RotateFlipType.RotateNoneFlipNone,
        });
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var ps = new System.Drawing.Imaging.EncoderParameters(1);
        ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, ps);
        return ms.ToArray();
#else
        if (!OperatingSystem.IsMacOS()) return null;
        // sips keeps the EXIF orientation, which the WebView honours; its output isn't read, so it isn't redirected
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phonebox-photo-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sips") { UseShellExecute = false };
            foreach (var a in new[] { "-Z", LongEdge.ToString(System.Globalization.CultureInfo.InvariantCulture), "-s", "format", "jpeg", "-s", "formatOptions", "85", path, "--out", tmp })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            // killed when the request goes away or after 20 s, so an abandoned conversion doesn't hold the queue
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(20_000);
            try { p.WaitForExitAsync(limit.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
            return p.ExitCode == 0 && File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
        }
        finally { try { File.Delete(tmp); } catch { } }
#endif
    }
}
