using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>The pieces of "PhoneBox.app", the small program that shows a phone's notification with the PhoneBox icon: what goes in it and how it is asked.</summary>
internal static class NotifierBundle
{
    public const string BundleId = "net.oxog.phonebox.notifier";

    /// <summary>What the build does besides compiling the source: part of the version, so changing it rebuilds an old build.</summary>
    public const string BuildRecipe = "xcrun --sdk macosx swiftc -O; sips iconset 7 sizes; iconutil icns; codesign --force --deep -s -";

    /// <summary>Changes whenever the program, the icon, the Info.plist or the way it is built changes, so an old build is replaced.</summary>
    public static string Version(string swiftSource, byte[] iconPng)
    {
        var hash = SHA256.HashData([.. Encoding.UTF8.GetBytes(swiftSource + "\0" + InfoPlist("") + "\0" + BuildRecipe), 0, .. iconPng]);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string InfoPlist(string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>CFBundleExecutable</key><string>PhoneBoxNotifier</string>
        <key>CFBundleIdentifier</key><string>{BundleId}</string>
        <key>CFBundleName</key><string>PhoneBox</string>
        <key>CFBundleDisplayName</key><string>PhoneBox</string>
        <key>CFBundleIconFile</key><string>PhoneBox</string>
        <key>CFBundlePackageType</key><string>APPL</string>
        <key>CFBundleVersion</key><string>{version}</string>
        <key>CFBundleShortVersionString</key><string>1.0</string>
        <key>LSUIElement</key><true/>
        <key>LSMinimumSystemVersion</key><string>11.0</string>
        </dict></plist>
        """;

    /// <summary>
    /// The notifier folder holds a program the agent runs and feeds with phone text: it must not be writable by anyone but its owner,
    /// or another local user could put their own program there.
    /// </summary>
    public static bool DirectoryIsSafe(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch { return false; }
    }

    /// <summary>The icon sizes macOS wants in an .iconset, as file name and pixel size; all are made from the 256 pixel picture.</summary>
    public static readonly IReadOnlyList<(string File, int Pixels)> IconsetSizes =
    [
        ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32), ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256), ("icon_256x256.png", 256),
    ];

    /// <summary>The Swift compiler Xcode or the command line tools put in place, without ever starting the system's installer (a bare "swiftc" would offer to install them).</summary>
    public static string? FindSwiftc(Func<string?> developerDir, Func<string, bool> exists)
    {
        if (developerDir() is not { Length: > 0 } dir) return null;
        return new[] { Path.Combine(dir, "usr/bin/swiftc"), Path.Combine(dir, "Toolchains/XcodeDefault.xctoolchain/usr/bin/swiftc") }.FirstOrDefault(exists);
    }

    /// <summary>The largest PNG picture inside a Windows .ico file (the agent's own icon), or null when there is none.</summary>
    public static byte[]? LargestPng(byte[] ico)
    {
        if (ico.Length < 6 || ico[2] != 1 || ico[3] != 0) return null;
        var count = ico[4] | (ico[5] << 8);
        byte[]? best = null;
        var bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > ico.Length) break;
            var size = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            if (size <= 0 || offset < 0 || offset + size > ico.Length || size < 8) continue;
            if (ico[offset] != 0x89 || ico[offset + 1] != 0x50) continue; // a bitmap, not a PNG
            var width = (ico[offset + 16] << 24) | (ico[offset + 17] << 16) | (ico[offset + 18] << 8) | ico[offset + 19];
            if (width > bestSize) { bestSize = width; best = ico.AsSpan(offset, size).ToArray(); }
        }
        return best;
    }

    /// <summary>The one request the program reads on its standard input. The text is data in a JSON document, never part of a command.</summary>
    public static string RequestJson(string title, string subtitle, string body, byte[]? iconPng, string? replaceId = null)
    {
        var request = new Dictionary<string, string> { ["title"] = title, ["subtitle"] = subtitle, ["body"] = body };
        if (iconPng is { Length: > 0 }) request["image"] = Convert.ToBase64String(iconPng);
        if (!string.IsNullOrEmpty(replaceId)) request["id"] = replaceId;
        return JsonSerializer.Serialize(request);
    }
}
