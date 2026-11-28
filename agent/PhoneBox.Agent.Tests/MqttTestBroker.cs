using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Protocol;
using MQTTnet.Server;

/// <summary>An MQTT broker inside the test process, on a free loopback port: tests never reach a broker outside.</summary>
internal sealed class MqttTestBroker : IDisposable
{
    private readonly MqttServer _server;
    public int Port { get; }
    public List<string> ClientIds { get; } = [];
    public List<(string? User, string? Password)> Logins { get; } = [];

    private MqttTestBroker(MqttServer server, int port) { _server = server; Port = port; }

    public static async Task<MqttTestBroker> StartAsync(X509Certificate2? cert = null, string? user = null, string? password = null, int? at = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = at ?? FreePort();
            var factory = new MqttServerFactory();
            var builder = factory.CreateServerOptionsBuilder();
            // loopback IPv4 only: a broker on every interface would show up twice in what ListeningPortsTests counts for this process
            if (cert is null) builder.WithDefaultEndpoint().WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None);
            else builder.WithoutDefaultEndpoint().WithEncryptedEndpoint().WithEncryptedEndpointPort(port).WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback).WithEncryptedEndpointBoundIPV6Address(IPAddress.None).WithEncryptionCertificate(cert);
            var server = factory.CreateMqttServer(builder.Build());
            var broker = new MqttTestBroker(server, port);
            server.ValidatingConnectionAsync += e =>
            {
                lock (broker.Logins) { broker.ClientIds.Add(e.ClientId); broker.Logins.Add((e.UserName, e.Password)); }
                if (user is not null && (e.UserName != user || e.Password != password)) e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
            try { await server.StartAsync(); return broker; }
            catch (Exception) when (attempt < 10) { server.Dispose(); } // the port was taken in between
        }
    }

    /// <summary>A port no other fixture of this run has been given (the HTTP fixtures share the same list).</summary>
    public static int FreePort() => TestPorts.Reserve();

    public void Dispose() { try { _server.StopAsync().GetAwaiter().GetResult(); } catch { } _server.Dispose(); }

    /// <summary>A certificate for 127.0.0.1 / localhost (or another name), self-signed or signed by <paramref name="issuer"/>.</summary>
    public static X509Certificate2 Certificate(string name = "localhost", X509Certificate2? issuer = null, bool authority = false, string usage = "1.3.6.1.5.5.7.3.1")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        if (authority) request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        else
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            if (name == "localhost") san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
        }
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        // a certificate may not outlive the one that signed it (macOS refuses to create it)
        var to = issuer is null ? DateTimeOffset.UtcNow.AddDays(30) : new DateTimeOffset(issuer.NotAfter).AddHours(-1);
        using var made = issuer is null
            ? request.CreateSelfSigned(from, to)
            : request.Create(issuer, from, to, RandomNumberGenerator.GetBytes(16)).CopyWithPrivateKey(key);
        // exported and loaded again: Windows' TLS (SChannel) needs a key that was persisted, as the agent's own certificate does (Lan.Certificate)
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
