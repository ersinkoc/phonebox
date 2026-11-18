using System.Security.Cryptography;
using System.Xml;

namespace PhoneBox.Agent;

/// <summary>
/// Custom icon images the user uploads for a shortcut in the settings page (PNG, WEBP or SVG), kept under the config
/// folder as icons/&lt;shortcut id&gt;.&lt;ext&gt;. A custom icon takes priority over the shortcut's auto-detected
/// program icon (<see cref="ShortcutIcons"/>) and, below that, the emoji.
/// </summary>
public sealed class ShortcutIconUploads(string? iconsDir = null)
{
    public const int MaxBytes = 512 * 1024;

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
    };

    private readonly string _dir = iconsDir ?? Path.Combine(AppPaths.ConfigDir, "icons");
    private readonly Lock _gate = new();

    // A file's own last-write time is not reliable for this: on Windows it can tick in steps of ~15ms, so two
    // uploads in close succession (replacing an icon right after uploading it) can land on the same value. Keyed
    // by id, filled from the bytes already in memory on Save (no extra read), evicted by Remove; Revision() fills
    // it lazily for an icon that was already on disk when this process started.
    private readonly Dictionary<string, long> _revisionCache = [];

    /// <summary>Whether a custom icon was uploaded for a shortcut, without reading it.</summary>
    public bool Exists(string id) => ExtensionByContentType.Values.Any(ext => File.Exists(PathFor(id, ext)));

    /// <summary>A value that changes when the uploaded icon is replaced or removed, or null when none was uploaded.</summary>
    public long? Revision(string id)
    {
        lock (_gate)
        {
            if (_revisionCache.TryGetValue(id, out var cached)) return cached;
            if (Read(id) is not { } found) return null;
            return _revisionCache[id] = ContentRevision(found.Bytes);
        }
    }

    private static long ContentRevision(byte[] bytes) => BitConverter.ToInt64(SHA256.HashData(bytes));

    /// <summary>The uploaded icon for a shortcut and its content type, or null when none was uploaded.</summary>
    public (byte[] Bytes, string ContentType)? Read(string id)
    {
        lock (_gate)
        {
            foreach (var (contentType, ext) in ExtensionByContentType)
            {
                var path = PathFor(id, ext);
                if (!File.Exists(path)) continue;
                try { return (File.ReadAllBytes(path), contentType); }
                catch { return null; }
            }
            return null;
        }
    }

    /// <summary>
    /// Reads a request body up to <see cref="MaxBytes"/> (plus one byte, to tell "too large" from "exactly the
    /// limit"); null means the upload is too large. Bounded so a huge body cannot be read fully into memory first.
    /// </summary>
    public static async Task<byte[]?> ReadBounded(Stream body)
    {
        var buf = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while ((read = await body.ReadAsync(buf.AsMemory(total, buf.Length - total))) > 0)
        {
            total += read;
            if (total > MaxBytes) return null;
        }
        return buf[..total];
    }

    /// <returns>An error message, or null on success.</returns>
    public string? Save(string id, string? contentType, byte[]? bytes)
    {
        if (bytes is null) return $"The icon is larger than {MaxBytes / 1024} KB";
        if (bytes.Length == 0) return "The file is empty";
        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (!ExtensionByContentType.TryGetValue(type, out var ext)) return "Only PNG, WEBP or SVG icons are supported";
        if (!LooksValid(ext, bytes)) return $"That does not look like a valid {ext[1..].ToUpperInvariant()} file";

        Directory.CreateDirectory(_dir);
        var target = PathFor(id, ext);
        var tmp = target + ".tmp";
        lock (_gate)
        {
            // Publish the replacement before removing other formats so a failed write or move preserves the old icon.
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            RemoveLocked(id, exceptExtension: ext);
            // Read looks for the formats in a fixed order, so an old file in another format that could not be deleted
            // (open elsewhere, on Windows) would still be served under the new revision: undo the change instead.
            if (ExtensionByContentType.Values.Any(other => other != ext && File.Exists(PathFor(id, other))))
            {
                try { File.Delete(target); } catch { }
                return "The previous icon is in use and could not be replaced. Try again in a moment.";
            }
            _revisionCache[id] = ContentRevision(bytes);
        }
        return null;
    }

    /// <summary>Deletes a shortcut's custom icon, in whichever format it was saved.</summary>
    public void Remove(string id) { lock (_gate) { RemoveLocked(id); _revisionCache.Remove(id); } }

    private void RemoveLocked(string id, string? exceptExtension = null)
    {
        foreach (var ext in ExtensionByContentType.Values)
        {
            if (ext == exceptExtension) continue;
            try { File.Delete(PathFor(id, ext)); } catch { }
        }
    }

    /// <summary>Deletes uploads for shortcuts that no longer exist (deleted, or never saved past "New shortcut").</summary>
    public void Cleanup(Func<IEnumerable<string>> liveIds)
    {
        if (!Directory.Exists(_dir)) return;
        lock (_gate)
        {
            // Read inside the lock, not before it: a shortcut added and its icon uploaded between an outdated
            // snapshot and this lock would otherwise look dead to a save that started before either of them.
            var keep = new HashSet<string>(liveIds(), StringComparer.Ordinal);
            try
            {
                foreach (var file in Directory.EnumerateFiles(_dir))
                {
                    // never an orphaned icon; it is either an upload in progress under Save's lock, or dead
                    // weight left by a process that crashed mid-write, harmless either way
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (keep.Contains(id)) continue;
                    try { File.Delete(file); } catch { }
                    _revisionCache.Remove(id);
                }
            }
            catch { }
        }
    }

    private string PathFor(string id, string ext) => Path.Combine(_dir, SafeName(id) + ext);

    // Shortcut ids are already slugs ([a-z0-9-]+, see ConfigStore's Normalize); this is a last defense against
    // writing outside the icons folder from an id that does not match any real shortcut (the endpoints check that
    // separately, but a filename is cheap insurance against ever turning ".." or "/" into a path component here).
    private static string SafeName(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static bool LooksValid(string ext, byte[] b) => ext switch
    {
        ".png" => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        ".webp" => b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P',
        ".svg" => IsSvg(b),
        _ => false,
    };

    private static bool IsSvg(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            if (xml.MoveToContent() != XmlNodeType.Element || xml.LocalName != "svg"
                || xml.NamespaceURI is not ("" or "http://www.w3.org/2000/svg")) return false;
            while (xml.Read()) { } // Check that the entire document is well formed.
            return true;
        }
        catch (XmlException) { return false; }
    }
}
