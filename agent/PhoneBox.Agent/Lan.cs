using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneBox.Agent;

/// <summary>
/// Wi-Fi / Tailscale access. The phone pairs over USB: it receives the PC's addresses, a shared token and
/// the SHA-256 fingerprint of a self-signed TLS certificate. Afterwards it talks HTTPS to the LAN port and
/// trusts only that exact certificate (pinning), so nobody on the network can read or impersonate the agent.
/// </summary>
public static class Lan
{
    private static readonly string CertPath = Path.Combine(AppPaths.ConfigDir, "lan-cert.pfx");

    /// <summary>Loads the agent's TLS certificate, creating it on first use.</summary>
    public static X509Certificate2 Certificate()
    {
        if (File.Exists(CertPath))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(CertPath, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
            catch { /* corrupt: recreate below */ }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=PhoneBox agent ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx));
        // Re-import so Windows TLS (SChannel) gets a persisted key.
        return X509CertificateLoader.LoadPkcs12FromFile(CertPath, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();

    /// <summary>Addresses the phone can try: private LAN IPv4 addresses first, then Tailscale (100.64.0.0/10).</summary>
    public static IReadOnlyList<string> Addresses()
    {
        var lan = new List<string>();
        var tailscale = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var desc = ni.Description + " " + ni.Name;
            var isTailscale = desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            if (!isTailscale && (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || desc.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 100 && b[1] is >= 64 and <= 127) tailscale.Add(ua.Address.ToString());
                else if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)) lan.Add(ua.Address.ToString());
            }
        }
        return lan.Concat(tailscale).Distinct().ToList();
    }

    public static bool Matches(string? header, string token)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var given = System.Text.Encoding.UTF8.GetBytes(header[7..]);
        var expected = System.Text.Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public static bool MatchesQuery(string? given, string token) =>
        !string.IsNullOrEmpty(given) && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>Endpoints the phone may use over Wi-Fi. Settings, tests and pairing stay local-only.</summary>
    public static bool AllowedOverLan(PathString path) =>
        path == "/api/controls/spotify/state" || path.StartsWithSegments("/api/controls/spotify/action")
        || path == "/api/controls/state" || path == "/api/controls/listen" || path.StartsWithSegments("/api/controls/command") || path.StartsWithSegments("/api/controls/obs")
        || path.StartsWithSegments("/api/state") || path.StartsWithSegments("/api/config") || path.StartsWithSegments("/api/art")
        || path.StartsWithSegments("/api/media") || path.StartsWithSegments("/api/volume") || path.StartsWithSegments("/api/quotas")
        || path.StartsWithSegments("/api/work") || path.StartsWithSegments("/api/procs") || path.StartsWithSegments("/api/run")
        || path.StartsWithSegments("/api/widget") || path.StartsWithSegments("/api/calendar") || path.StartsWithSegments("/api/docker") || path.StartsWithSegments("/api/obs") || path.StartsWithSegments("/api/batteries") || path.StartsWithSegments("/api/ha/states") || path.StartsWithSegments("/api/ha/toggle") || path.StartsWithSegments("/api/approvals") || path.StartsWithSegments("/api/rules/pause") || path.StartsWithSegments("/api/phone/battery") || path.StartsWithSegments("/api/stream") || path == "/api/photos/feed" || path.StartsWithSegments("/api/photos/image") || IsWebhook(path);

    /// <summary>The webhook API: other machines on the network (or Tailscale) may push data with the same token.</summary>
    public static bool IsWebhook(PathString path) =>
        path.StartsWithSegments("/api/push") || path.StartsWithSegments("/api/notify")
        || path.StartsWithSegments("/api/goto") || path.StartsWithSegments("/api/refresh");

    public static IPAddress Any => IPAddress.Any;
}
