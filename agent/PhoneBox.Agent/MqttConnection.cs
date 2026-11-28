using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace PhoneBox.Agent;

/// <summary>
/// What the status endpoint says, and nothing more: the state, when a message last arrived (unix ms) and, when something
/// is wrong, the kind of error. Never the broker's address, the user name or a topic.
/// </summary>
public sealed record MqttStatus(string State, long? LastMessageAt, string? ErrorKind);

/// <summary>The answer to Settings › MQTT › Test connection: the same fixed kinds as the status, never the error's text.</summary>
public sealed record MqttTestResult(bool Ok, string? ErrorKind);

/// <summary>A failed connection. Only the kind is kept: the text of a socket or TLS error names the broker.</summary>
internal sealed class MqttFailure(string kind) : Exception(kind)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// Whether to trust the broker's certificate. By default the system's own check (chain and name). A broker with its own
/// certificate is trusted through a CA file (the chain must end in it and the name must still match) or through the
/// SHA-256 fingerprint of its certificate. There is deliberately no way to accept any certificate.
/// </summary>
internal sealed class MqttTrust
{
    private const long MaxCaFileBytes = 1024 * 1024;
    private readonly byte[]? _pin;
    private readonly X509Certificate2Collection? _roots;

    /// <summary>Set when a certificate was refused, so the failure is reported as "tls" whatever the exception says.</summary>
    public volatile bool Rejected;

    public MqttTrust(MqttConfig cfg)
    {
        if (!cfg.Tls) return;
        if (MqttConfig.CleanFingerprint(cfg.PinnedFingerprint) is { } pin) { _pin = Convert.FromHexString(pin); return; }
        if (cfg.CaFile is not { Length: > 0 } file) return;
        try
        {
            if (new FileInfo(file).Length > MaxCaFileBytes) throw new MqttFailure("config");
            var roots = new X509Certificate2Collection();
            try { roots.ImportFromPemFile(file); }
            catch (CryptographicException) { roots.Clear(); }
            if (roots.Count == 0) roots.Add(X509CertificateLoader.LoadCertificateFromFile(file));
            _roots = roots;
        }
        catch (MqttFailure) { throw; }
        catch (Exception) { throw new MqttFailure("config"); }
    }

    public bool Validate(MqttClientCertificateValidationEventArgs args)
    {
        var ok = Check(args.Certificate, args.Chain, args.SslPolicyErrors);
        if (!ok) Rejected = true;
        return ok;
    }

    internal bool Check(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (_pin is not null) return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), _pin);
        if (_roots is null) return errors == SslPolicyErrors.None;
        // the chain may fail only because the system does not know this CA; a wrong name or a missing certificate may not
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // the certificate must be one for a server (a client certificate from the same CA is not)
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (presented is not null)
            foreach (var element in presented.ChainElements.Skip(1)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }
}

/// <summary>
/// The connection to the MQTT broker (Settings › General › MQTT). The agent holds it while it is enabled and keeps
/// trying again, with a longer wait after each failure. This first version only connects and reports its state; what is
/// subscribed and published is added on top of it.
/// </summary>
public sealed class MqttConnection(ConfigStore config, ILogger<MqttConnection> log) : BackgroundService
{
    public const string Off = "off", Connecting = "connecting", Connected = "connected", Error = "error";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim TestGate = new(1, 1);

    private volatile MqttStatus _status = new(Off, null, null);
    private long _lastMessage;
    private readonly TimeSpan _firstWait = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _stableAfter = TimeSpan.FromSeconds(60);

    /// <summary>For tests: shorter waits (internal, so the container does not pick it).</summary>
    internal MqttConnection(ConfigStore config, ILogger<MqttConnection> log, TimeSpan firstWait, TimeSpan stableAfter) : this(config, log)
    {
        _firstWait = firstWait;
        _stableAfter = stableAfter;
    }

    public MqttStatus Status
    {
        get
        {
            var at = Interlocked.Read(ref _lastMessage);
            return _status with { LastMessageAt = at > 0 ? at : null };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var first = _firstWait;
        var wait = first;
        string? lastKind = null;
        string? previous = null; // the settings of the previous attempt
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get().Mqtt;
            if (!cfg.Configured) { _status = new(Off, null, null); wait = first; lastKind = null; previous = null; await Pause(TimeSpan.FromSeconds(2), null, ct); continue; }
            var signature = Signature(cfg);
            // new settings start over, however they were noticed (a change in the last half second of a pause is not): the
            // wait is the short one again and their first failure is logged even if the old settings failed the same way
            if (signature != previous) { wait = first; lastKind = null; previous = signature; }
            try
            {
                // returns when the settings changed or the agent stops: connect again at once, with what is saved now.
                // The wait and the last error kind reset only once a connection has held for a while: a broker that accepts
                // the login and then drops it (a duplicate client id, an access list) must keep backing off, and must not
                // fill the log with a "connected" and a "failed" line every few seconds.
                await RunAsync(cfg, signature, ct,
                    () => { if (lastKind is null) log.LogInformation("mqtt: connected"); },
                    () => { if (lastKind is not null) log.LogInformation("mqtt: connected"); wait = first; lastKind = null; });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                var kind = ex is MqttFailure f ? f.Kind : "other";
                _status = new(Error, null, kind);
                // only the kind is logged, once per change: the text of the error would name the broker
                if (kind != lastKind) log.LogInformation("mqtt: connection failed ({Kind})", kind);
                lastKind = kind;
                // a corrected setting ends the wait: no need to sit out a minute for it
                wait = await Pause(wait, signature, ct) ? first : TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 60));
            }
        }
        _status = new(Off, null, null);
    }

    /// <summary>Waits, or until the settings differ from <paramref name="signature"/>; true when they did.</summary>
    private async Task<bool> Pause(TimeSpan time, string? signature, CancellationToken ct)
    {
        try
        {
            for (var left = time; left > TimeSpan.Zero; left -= TimeSpan.FromMilliseconds(500))
            {
                if (signature is not null && Signature(config.Get().Mqtt) != signature) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    /// <summary>Connects and stays connected until the connection drops (an exception) or the settings change (a return).</summary>
    private async Task RunAsync(MqttConfig cfg, string signature, CancellationToken ct, Action connected, Action held)
    {
        Interlocked.Exchange(ref _lastMessage, 0);
        _status = new(Connecting, null, null);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = await OpenAsync(cfg, "", ct, c =>
        {
            c.DisconnectedAsync += _ => { lost.TrySetResult(); return Task.CompletedTask; };
            c.ApplicationMessageReceivedAsync += _ => { Interlocked.Exchange(ref _lastMessage, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); return Task.CompletedTask; };
        });
        _status = new(Connected, null, null);
        connected();
        var since = System.Diagnostics.Stopwatch.StartNew();
        var holding = false;
        var dropped = false;
        try
        {
            while (!ct.IsCancellationRequested && !lost.Task.IsCompleted && Signature(config.Get().Mqtt) == signature)
            {
                // held for a while: the next drop, after days, is retried in seconds
                if (!holding && since.Elapsed >= _stableAfter) { holding = true; held(); }
                await Task.WhenAny(lost.Task, Task.Delay(1000, ct));
            }
            // read before our own goodbye below: MQTTnet reports that one as a disconnect too
            dropped = lost.Task.IsCompleted;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (client.IsConnected)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await client.DisconnectAsync(new MqttClientDisconnectOptions(), closing.Token); } catch { /* the broker did not answer the goodbye in time */ }
            }
        }
        // dropped by the broker or the network, not by a settings change or the agent stopping
        if (dropped && !ct.IsCancellationRequested) throw new MqttFailure("other");
    }

    // the settings the connection was made with, to notice a change; compared in memory and never logged
    private static string Signature(MqttConfig c) => System.Text.Json.JsonSerializer.Serialize(c);

    /// <summary>Settings › Test connection: connects once with these settings, then disconnects. Its client id differs from the real connection's, so the broker does not drop that one.</summary>
    public static async Task<MqttTestResult> TestAsync(MqttConfig cfg, CancellationToken ct)
    {
        if (cfg.Host is null) return new(false, "config");
        await TestGate.WaitAsync(ct);
        try
        {
            using var client = await OpenAsync(cfg, "-test", ct);
            if (client.IsConnected)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await client.DisconnectAsync(new MqttClientDisconnectOptions(), closing.Token); } catch { /* connected is the answer */ }
            }
            return new(true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (MqttFailure f) { return new(false, f.Kind); }
        finally { TestGate.Release(); }
    }

    /// <summary>A connected client. Throws <see cref="MqttFailure"/> (a kind only) when the connection cannot be made.</summary>
    internal static async Task<IMqttClient> OpenAsync(MqttConfig cfg, string idSuffix, CancellationToken ct, Action<IMqttClient>? attach = null)
    {
        // the saved text may be anything (a pasted mqtt:// address): only a plain host name or IP address is connected to
        if (MqttConfig.CleanHost(cfg.Host) is null) throw new MqttFailure("config");
        var trust = new MqttTrust(cfg);
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            attach?.Invoke(client);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            var result = await client.ConnectAsync(Options(cfg, idSuffix, trust), timeout.Token);
            // a broker that refuses the login answers with a code instead of failing the call
            if (result.ResultCode != MqttClientConnectResultCode.Success) throw new MqttFailure(KindOf(result.ResultCode));
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new MqttFailure(KindOf(ex, trust.Rejected));
        }
    }

    internal static MqttClientOptions Options(MqttConfig cfg, string idSuffix, MqttTrust trust)
    {
        var id = cfg.ClientId ?? "phonebox-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..8].ToLowerInvariant();
        var b = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311) // what every broker speaks; MQTT 5 can come later
            .WithClientId(id + idSuffix)
            .WithCleanSession(cfg.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(cfg.KeepAliveSec))
            .WithTimeout(ConnectTimeout);
        // a password goes only with a user name
        if (cfg.Username is { } user) b.WithCredentials(user, cfg.Password ?? "");
        if (cfg.WillTopic is { } topic)
            b.WithWillTopic(topic).WithWillPayload(cfg.WillPayload ?? "").WithWillRetain(cfg.WillRetain)
                .WithWillQualityOfServiceLevel((MqttQualityOfServiceLevel)cfg.WillQos);
        var host = MqttConfig.CleanHost(cfg.Host)!;
        if (cfg.Transport == MqttConfig.WebSocket)
        {
            var name = host.Contains(':') ? $"[{host}]" : host;
            b.WithWebSocketServer(w => w.WithUri($"{(cfg.Tls ? "wss" : "ws")}://{name}:{cfg.Port}{cfg.WebSocketPath}"));
        }
        else b.WithTcpServer(host, cfg.Port);
        if (cfg.Tls) b.WithTlsOptions(t => t.UseTls().WithCertificateValidationHandler(trust.Validate));
        return b.Build();
    }

    /// <summary>The kind for a connection the broker refused.</summary>
    internal static string KindOf(MqttClientConnectResultCode code) => code switch
    {
        MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.Banned
            or MqttClientConnectResultCode.BadAuthenticationMethod => "auth",
        MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy or MqttClientConnectResultCode.UnspecifiedError => "other",
        _ => "protocol",
    };

    /// <summary>The kind of an error, from a fixed list. Only the type of each exception is looked at, never its text.</summary>
    internal static string KindOf(Exception ex, bool certificateRefused)
    {
        if (certificateRefused) return "tls";
        // a WebSocket error wraps the socket error that explains it, so the specific kinds are looked for first
        for (var e = ex; e is not null; e = e.InnerException)
            switch (e)
            {
                case MqttFailure f: return f.Kind;
                case AuthenticationException: return "tls";
                case SocketException s:
                    return s.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "dns",
                        SocketError.ConnectionRefused => "refused",
                        SocketError.TimedOut => "timeout",
                        _ => "other",
                    };
                case MqttCommunicationTimedOutException or TimeoutException or OperationCanceledException: return "timeout";
            }
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is MqttProtocolViolationException or System.Net.WebSockets.WebSocketException) return "protocol";
        return "other";
    }
}
