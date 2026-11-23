using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>
/// The panel's wallpaper: one picture the user uploads in the settings page (JPEG, PNG or WEBP), kept in the config
/// folder as wallpaper.&lt;ext&gt; and drawn behind every page. The panel asks for it with its revision in the URL, so
/// the phone may keep it as long as it likes and loads it again only when it changes.
/// </summary>
public sealed class WallpaperStore(string? dir = null)
{
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    private readonly string _dir = dir ?? AppPaths.ConfigDir;
    private readonly Lock _gate = new();
    private (bool Known, long? Value) _revision;

    private string PathFor(string ext) => Path.Combine(_dir, "wallpaper" + ext);

    /// <summary>A value that changes with the picture, or null when there is none.</summary>
    public long? Revision()
    {
        lock (_gate)
        {
            if (_revision.Known) return _revision.Value;
            var rev = Read() is { } found ? ContentRevision(found.Bytes) : (long?)null;
            _revision = (true, rev);
            return rev;
        }
    }

    private static long ContentRevision(byte[] bytes) => BitConverter.ToInt64(SHA256.HashData(bytes));

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
    public string? Save(string? contentType, byte[]? bytes)
    {
        if (bytes is null) return $"The picture is larger than {MaxBytes / (1024 * 1024)} MB";
        if (bytes.Length == 0) return "The file is empty";
        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (!ExtensionByContentType.TryGetValue(type, out var ext)) return "Only JPEG, PNG or WEBP pictures are supported";
        if (!LooksValid(ext, bytes)) return $"That does not look like a valid {ext[1..].ToUpperInvariant()} file";

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
            if (ExtensionByContentType.Values.Any(other => other != ext && File.Exists(PathFor(other))))
            {
                // Read looks in a fixed order: an old file that could not be deleted would still be served
                try { File.Delete(target); } catch { }
                _revision = default;
                return "The previous wallpaper is in use and could not be replaced. Try again in a moment.";
            }
            _revision = (true, ContentRevision(bytes));
        }
        return null;
    }

    /// <summary>Deletes the wallpaper, whichever format it was saved in.</summary>
    public void Remove()
    {
        lock (_gate)
        {
            RemoveLocked();
            _revision = default; // a file that could not be deleted is found again by the next Revision()
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
