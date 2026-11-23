using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>
/// The panel's wallpaper: one picture the user uploads in the settings page (JPEG, PNG or WEBP), kept in the config
/// folder as wallpaper.&lt;ext&gt; and drawn behind every page. The panel asks for it with its revision in the URL, so
/// the phone may keep it as long as it likes and loads it again only when it changes.
/// </summary>
public sealed class WallpaperStore(string? dir = null)
{
    /// <summary>4 MB: a phone decodes the whole picture into memory, and the app fetches it again on each panel
    /// reload, so a smaller picture (a phone screen needs no more than about 2400 px) is kinder to both.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    private readonly string _dir = dir ?? AppPaths.ConfigDir;
    private readonly Lock _gate = new();
    // the content revision of the file it was computed from: a file replaced or deleted by hand gets a new one
    private (string Path, long Length, DateTime Written, long Revision)? _revision;

    private string PathFor(string ext) => Path.Combine(_dir, "wallpaper" + ext);

    /// <summary>A value that changes with the picture, or null when there is none. Hashed again only when the file
    /// changed (size or time); a file deleted by hand is noticed at once, and a failed read is not remembered.</summary>
    public long? Revision()
    {
        lock (_gate)
        {
            if (Current() is not { } file) { _revision = null; return null; }
            try
            {
                var info = new FileInfo(file);
                if (_revision is { } r && r.Path == file && r.Length == info.Length && r.Written == info.LastWriteTimeUtc)
                    return r.Revision;
                var rev = ContentRevision(File.ReadAllBytes(file));
                _revision = (file, info.Length, info.LastWriteTimeUtc, rev);
                return rev;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>The wallpaper file, in the order Read looks for it, or null.</summary>
    private string? Current() => ExtensionByContentType.Values.Select(PathFor).FirstOrDefault(File.Exists);

    internal static long ContentRevision(byte[] bytes) => BitConverter.ToInt64(SHA256.HashData(bytes));

    /// <summary>The picture and its content type, or null when none was uploaded.</summary>
    public (byte[] Bytes, string ContentType)? Read()
    {
        lock (_gate)
        {
            foreach (var (contentType, ext) in ExtensionByContentType)
            {
                var path = PathFor(ext);
                if (!File.Exists(path)) continue;
                try { return (File.ReadAllBytes(path), contentType); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }
            return null;
        }
    }

    /// <summary>Reads a request body up to <see cref="MaxBytes"/>; null means it is larger.</summary>
    public static async Task<byte[]?> ReadBounded(Stream body)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buf)) > 0)
        {
            if (ms.Length + read > MaxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    /// <returns>An error message, or null on success.</returns>
    /// <remarks>The type comes from the file's own bytes, not from what the browser reports: some send an empty type
    /// or application/octet-stream for a valid picture.</remarks>
    public string? Save(byte[]? bytes)
    {
        if (bytes is null) return $"The picture is larger than {MaxBytes / (1024 * 1024)} MB";
        if (bytes.Length == 0) return "The file is empty";
        if (ExtensionByContentType.Values.FirstOrDefault(e => LooksValid(e, bytes)) is not { } ext)
            return "Only JPEG, PNG or WEBP pictures are supported";

        Directory.CreateDirectory(_dir);
        var target = PathFor(ext);
        var tmp = target + ".tmp";
        lock (_gate)
        {
            // the new picture is in place before the other formats go, so a failed write keeps the old one
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            RemoveLocked(exceptExtension: ext);
            _revision = null; // hashed again: a same-sized picture can land on the same file time on Windows
            if (ExtensionByContentType.Values.Any(other => other != ext && File.Exists(PathFor(other))))
            {
                // Read looks in a fixed order: an old file that could not be deleted would still be served
                try { File.Delete(target); } catch { }
                return "The previous wallpaper is in use and could not be replaced. Try again in a moment.";
            }
        }
        return null;
    }

    /// <summary>Deletes the wallpaper, whichever format it was saved in.</summary>
    /// <returns>An error message when a file could not be deleted (it would still be served), or null.</returns>
    public string? Remove()
    {
        lock (_gate)
        {
            RemoveLocked();
            return Current() is null ? null : "The wallpaper is in use and could not be removed. Try again in a moment.";
        }
    }

    private void RemoveLocked(string? exceptExtension = null)
    {
        foreach (var ext in ExtensionByContentType.Values)
        {
            if (ext == exceptExtension) continue;
            try { File.Delete(PathFor(ext)); } catch { }
        }
    }

    internal static bool LooksValid(string ext, byte[] b) => ext switch
    {
        ".jpg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        ".png" => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        ".webp" => b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P',
        _ => false,
    };
}
