using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// WorkBuddy answers the same PermissionRequest hook Claude Code does, but keeps its sessions in ~/.workbuddy, so
/// <see cref="SessionMonitor"/> has no live session to take a name from and the approval card would show the folder
/// name ("2026-11-16-08-55-00"). WorkBuddy stores the name you gave each workspace in
/// .workbuddy/workspace-display-names.json; reading that keeps the card on "Quarterly report" instead.
/// </summary>
public static class WorkBuddyNames
{
    private static readonly object Gate = new();
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    private static string _stamp = "";
    private static string? _cachedFile;
    private static IReadOnlyDictionary<string, string> _names = Empty;

    /// <summary>The display name of a WorkBuddy workspace, or null when WorkBuddy is not installed or has no name for it.</summary>
    public static string? For(string? cwd, string? homeDir = null)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        return Load(homeDir).TryGetValue(Normalize(cwd), out var name) && name.Length > 0 ? name : null;
    }

    /// <summary>WorkBuddy keys its map with lowercase forward slashes, so every lookup and key goes through this.</summary>
    internal static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static IReadOnlyDictionary<string, string> Load(string? homeDir)
    {
        var home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return Empty;
        var file = Path.Combine(home, ".workbuddy", "workspace-display-names.json");
        // The hook runs on every permission request, so the file is only re-read when it actually changed. The path is
        // part of the stamp: two homes can hold files of the same age and size.
        var info = new FileInfo(file);
        var stamp = file + "|" + (info.Exists ? info.LastWriteTimeUtc.Ticks + "|" + info.Length : "-");
        lock (Gate)
        {
            if (stamp == _stamp) return _names;
            var names = Read(file, out var settled);
            // A read that failed is not a refresh: the old names stay and the next request tries again. Storing the
            // stamp here would keep the folder fallback until something happened to touch the file.
            if (!settled) return file == _cachedFile ? _names : Empty;
            _cachedFile = file;
            _stamp = stamp;
            _names = names;
            return names;
        }
    }

    private static IReadOnlyDictionary<string, string> Read(string file, out bool settled)
    {
        settled = true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("workspaces", out var workspaces)
                || workspaces.ValueKind != JsonValueKind.Object) return Empty;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var workspace in workspaces.EnumerateObject())
            {
                if (workspace.Value.ValueKind != JsonValueKind.Object) continue;
                var name = Str(workspace.Value, "displayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                names[Normalize(workspace.Name)] = name;
                // the entry carries the real path as well: use it in case the key is ever encoded differently
                if (Str(workspace.Value, "path") is { Length: > 0 } path) names[Normalize(path)] = name;
            }
            return names;
        }
        // No WorkBuddy, or no file yet: that is a definite answer, and the card falls back to the folder name.
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Empty; }
        catch { settled = false; return Empty; }   // locked or half-written: try again on the next request
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
