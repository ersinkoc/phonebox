using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// The phone's USB connection. "adb reverse" makes 127.0.0.1:8765 on the phone reach the computer, but that address on
/// the phone is open to every app there, not only PhoneBox. So the tunnel ends on a port of its own (not the local port
/// the settings page, the tray and the hooks use), and every request on it must carry the key the PhoneBox app made.
/// The agent reads that key over adb from a content provider that only the adb shell may read (it needs the DUMP
/// permission, which no installed app can hold), and writes the fingerprint of its certificate there the same way.
/// The app talks TLS to the tunnel and pins that certificate, so an app that took the phone's port 8765 before the
/// tunnel never sees the key. Before both, the agent checks that the provider belongs to the PhoneBox app: an app
/// that claimed the same provider name first must not be able to choose the key.
/// With the key, the phone reaches the same paths as over Wi-Fi, plus the two only USB has: the Wi-Fi pairing data and
/// the feed proxy.
/// </summary>
public sealed partial class UsbLink(int port, string pin)
{
    /// <summary>The computer-side port of the adb reverse tunnel (loopback only).</summary>
    public int Port { get; } = port;

    /// <summary>SHA-256 of the tunnel's TLS certificate (lower-case hex), which the app pins.</summary>
    public string Pin { get; } = pin;

    public const string Package = "net.oxog.phonebox.deskpanel";
    /// <summary>The content provider in the PhoneBox app that hands its USB key to the adb shell.</summary>
    public const string KeyUri = "content://net.oxog.phonebox.deskpanel.usb/key";
    /// <summary>Where the agent writes its certificate's fingerprint (insert, also DUMP only).</summary>
    public const string PinUri = "content://net.oxog.phonebox.deskpanel.usb/pin";

    private readonly ConcurrentDictionary<string, string> _keys = new(); // adb serial -> the key its app made
    private int _unknownKey;

    /// <summary>A free loopback port for the tunnel, picked at start.</summary>
    public static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public bool HasKey(string serial) => _keys.ContainsKey(serial);
    internal bool MatchesPhone(string? serial, string? authorization) =>
        serial is not null && _keys.TryGetValue(serial, out var key) && Lan.Matches(authorization, key);
    public void SetKey(string serial, string key) => _keys[serial] = key;
    public void Forget(string serial) => _keys.TryRemove(serial, out _);

    /// <summary>Forgets the keys of phones that are no longer attached.</summary>
    public void Retain(IReadOnlyCollection<string> serials)
    {
        foreach (var serial in _keys.Keys)
            if (!serials.Contains(serial)) _keys.TryRemove(serial, out _);
    }

    /// <summary>True once after a request came with a key the agent doesn't know (a reinstalled app, a new phone).</summary>
    public bool TakeUnknownKey() => Interlocked.Exchange(ref _unknownKey, 0) == 1;

    public enum Access { Allowed, Unauthorized, Forbidden }

    public Access Authorize(string method, PathString path, string origin, string? authorization)
    {
        // the phone's app makes these requests itself, never a web page
        if (origin != "") return Access.Forbidden;
        var known = false;
        foreach (var key in _keys.Values)
            known |= Lan.Matches(authorization, key); // every key is compared, so the time taken says nothing
        if (!known)
        {
            Interlocked.Exchange(ref _unknownKey, 1);
            return Access.Unauthorized;
        }
        return Allowed(method, path) ? Access.Allowed : Access.Forbidden;
    }

    /// <summary>What the phone may reach over USB: the Wi-Fi paths, its Wi-Fi pairing data, and the feed proxy.</summary>
    internal static bool Allowed(string method, PathString path) =>
        Lan.AllowedOverLan(path) || (HttpMethods.IsGet(method) && (path == "/api/pair" || path == "/api/proxy"));

    /// <summary>The key in "adb shell content query" output ("Row: 0 key=…"), or null.</summary>
    internal static string? ParseKey(string output) =>
        KeyRow().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// True when "dumpsys package net.oxog.phonebox.deskpanel" lists the key provider as the app's own. Another app
    /// could have taken the provider name while an older PhoneBox without it was installed; the name can belong to one
    /// app only, so the app's own listing settles it. Output in any other shape counts as not the app's.
    /// </summary>
    internal static bool OwnsKeyProvider(string dumpsys) => OwnedProvider().IsMatch(dumpsys);

    [GeneratedRegex(@"^\s*\[net\.oxog\.phonebox\.deskpanel\.usb\]:\s*\r?\n\s*Provider\{[0-9a-f]+ net\.oxog\.phonebox\.deskpanel/(?:\.|net\.oxog\.phonebox\.deskpanel\.)UsbKeyProvider\}", RegexOptions.Multiline)]
    private static partial Regex OwnedProvider();

    /// <summary>The app's versionName in "dumpsys package" output, or null.</summary>
    internal static string? ParseVersionName(string output) =>
        VersionName().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"versionName=([0-9][0-9A-Za-z.+-]{0,30})")]
    private static partial Regex VersionName();

    [GeneratedRegex(@"\bkey=([A-Za-z0-9_-]{32,128})\s*$", RegexOptions.Multiline)]
    private static partial Regex KeyRow();
}
