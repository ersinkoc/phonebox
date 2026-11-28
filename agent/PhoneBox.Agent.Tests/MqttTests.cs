using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The MQTT connection against a broker inside the test process (MQTTnet's server): nothing here reaches a network
/// outside the machine. The secrecy tests carry the rule from the issue: the password, host, user name and topics never
/// appear in what the phone gets, what Settings gets back, the status, the test reply or the log.
/// </summary>
public sealed class MqttTests : IDisposable
{
    private const string User = "alice-mqtt-user", Password = "p4ss-Secret-Mqtt", WillTopic = "home/secret/door/will";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-mqtt-{Guid.NewGuid():N}");

    public MqttTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MqttConfig Config(int port, bool tls = false, string? user = User, string? password = Password) => new()
    {
        Enabled = true, Host = "127.0.0.1", Port = port, Tls = tls, Username = user, Password = password,
        WillTopic = WillTopic, WillPayload = "gone", ClientId = "phonebox-test",
    };

    private ConfigStore Store(MqttConfig cfg)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { mqtt = cfg }, ConfigStore.WriteOpts));
        return new ConfigStore(path);
    }

    private static async Task<MqttStatus> Until(MqttConnection connection, Func<MqttStatus, bool> done, int seconds = 30)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (done(connection.Status)) return connection.Status;
            await Task.Delay(50);
        }
        throw new TimeoutException("the connection did not reach the state: " + JsonSerializer.Serialize(connection.Status));
    }

    private async Task<(MqttStatus Status, ListLogger<MqttConnection> Log)> RunOnceAsync(MqttConfig cfg, Func<MqttStatus, bool> done)
    {
        var log = new ListLogger<MqttConnection>();
        var connection = new MqttConnection(Store(cfg), log);
        await connection.StartAsync(CancellationToken.None);
        try { return (await Until(connection, done), log); }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    // ---------------------------------------------------------------- defaults and settings

    [Fact]
    public void ItIsOffByDefaultAndNothingRetainsUnlessAsked()
    {
        var c = new MqttConfig();
        Assert.False(c.Enabled);
        Assert.False(c.Configured);
        Assert.Null(c.Host);
        Assert.Null(c.Password);
        Assert.False(c.WillRetain);
        Assert.True(c.CleanSession);
        Assert.False(new AgentConfig().Mqtt.Enabled);
    }

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void TheDefaultSettingsFilesKeepItOff(string name)
    {
        using var stream = typeof(AgentConfig).Assembly.GetManifestResourceStream(name)!;
        var cfg = JsonSerializer.Deserialize<AgentConfig>(stream, ConfigStore.ReadOpts)!;
        Assert.False(cfg.Mqtt.Enabled);
        Assert.False(cfg.Mqtt.WillRetain);
        Assert.Null(cfg.Mqtt.Password);
    }

    [Fact]
    public void BadValuesAreCleanedUp()
    {
        var c = MqttConfig.Normalized(new MqttConfig
        {
            Host = " mqtt://broker.local/path ", Port = 70000, Transport = "carrier-pigeon", WebSocketPath = "x?y",
            KeepAliveSec = 1, WillTopic = "home/#", WillQos = 9, WillRetain = true, PinnedFingerprint = "nope",
        });
        Assert.Equal("mqtt://broker.local/path", c.Host); // kept as typed; connecting says it is no host name (below)
        Assert.Equal(1883, c.Port);
        Assert.Equal("tcp", c.Transport);
        Assert.Equal("/mqtt", c.WebSocketPath);
        Assert.Equal(5, c.KeepAliveSec);
        Assert.Equal(" a\nb ", MqttConfig.Normalized(new MqttConfig { WillTopic = "t", WillPayload = " a\nb " }).WillPayload); // sent as it is
        Assert.Equal(60, MqttConfig.Normalized(new MqttConfig { KeepAliveSec = 0 }).KeepAliveSec);
        Assert.Equal(600, MqttConfig.Normalized(new MqttConfig { KeepAliveSec = 9000 }).KeepAliveSec);
        Assert.Null(c.WillTopic); // a wildcard is no topic to publish to
        Assert.False(c.WillRetain);
        Assert.Equal(2, c.WillQos);
        Assert.Null(c.PinnedFingerprint);
    }

    [Theory]
    [InlineData("broker.local", "broker.local")]
    [InlineData(" 192.168.1.5 ", "192.168.1.5")]
    [InlineData("[::1]", "::1")]
    [InlineData("user@broker", null)]
    [InlineData("broker/mqtt", null)]
    [InlineData("", null)]
    public void OnlyAHostNameOrAddressIsAccepted(string given, string? expected) => Assert.Equal(expected, MqttConfig.CleanHost(given));

    [Fact]
    public void AFingerprintIsKeptAsLowerCaseHexWhateverItWasPastedWith()
    {
        var hex = new string('a', 32) + new string('B', 32);
        var colons = string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));
        Assert.Equal(hex.ToLowerInvariant(), MqttConfig.CleanFingerprint(colons));
        Assert.Null(MqttConfig.CleanFingerprint(hex + "00"));
    }

    [Fact]
    public void ThereIsNoSwitchThatAcceptsAnyCertificate()
    {
        var names = typeof(MqttConfig).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(names, n => n.Contains("ignore") || n.Contains("insecure") || n.Contains("untrusted") || n.Contains("skip") || n.Contains("verify"));
    }

    // ---------------------------------------------------------------- the password is write-only

    [Fact]
    public void SettingsGetsNoPasswordOnlyWhetherOneIsSet()
    {
        var cfg = new AgentConfig { Mqtt = Config(1883) };
        var shown = JsonSerializer.SerializeToNode(cfg, ConfigStore.WriteOpts)!.AsObject();
        MqttConfig.Redact(shown);
        var text = shown.ToJsonString();
        Assert.DoesNotContain(Password, text);
        Assert.True(shown["mqtt"]!["passwordSet"]!.GetValue<bool>());
        Assert.Null(shown["mqtt"]!["password"]);
        Assert.Equal("127.0.0.1", shown["mqtt"]!["host"]!.GetValue<string>()); // Settings needs the rest to show it
        var none = JsonSerializer.SerializeToNode(new AgentConfig(), ConfigStore.WriteOpts)!.AsObject();
        MqttConfig.Redact(none);
        Assert.False(none["mqtt"]!["passwordSet"]!.GetValue<bool>());
    }

    [Fact]
    public void ASaveWithoutAPasswordKeepsTheStoredOneAndAnEmptyOneRemovesIt()
    {
        var stored = Config(1883);
        var left = new AgentConfig { Mqtt = new() { Host = "127.0.0.1", Port = 1883, Username = User, Password = null } };
        Assert.True(MqttConfig.KeepPassword(left, stored));
        Assert.Equal(Password, left.Mqtt.Password);
        var replaced = new AgentConfig { Mqtt = new() { Host = "127.0.0.1", Port = 1883, Username = User, Password = "new-one" } };
        Assert.False(MqttConfig.KeepPassword(replaced, stored));
        Assert.Equal("new-one", replaced.Mqtt.Password);

        var store = Store(Config(1883));
        var cleared = JsonSerializer.Serialize(new { mqtt = new { enabled = true, host = "127.0.0.1", password = "" } });
        var proposed = JsonSerializer.Deserialize<AgentConfig>(cleared, ConfigStore.ReadOpts);
        Assert.False(MqttConfig.KeepPassword(proposed, Config(1883))); // "" is not "left out"
        Assert.Null(store.Save(cleared));
        Assert.Null(store.Get().Mqtt.Password);
    }

    [Fact]
    public void ThePasswordIsStillSavedInTheSettingsFile()
    {
        var store = Store(Config(1883));
        Assert.Equal(Password, store.Get().Mqtt.Password);
        Assert.Contains(Password, File.ReadAllText(store.Path)); // like the Home Assistant token: it must survive a restart
    }

    [Fact]
    public void TheTestUsesTheStoredPasswordOnlyForTheBrokerItWasSavedFor()
    {
        var stored = Config(1883);
        var same = MqttConfig.ForTest(new MqttConfig { Host = "127.0.0.1", Port = 1883, Username = User, Password = null }, stored);
        Assert.Equal(Password, same.Password);
        // another host, port or user: the page could be collecting the password with a broker of its own
        Assert.Null(MqttConfig.ForTest(new MqttConfig { Host = "evil.example", Port = 1883, Username = User }, stored).Password);
        Assert.Null(MqttConfig.ForTest(new MqttConfig { Host = "127.0.0.1", Port = 1884, Username = User }, stored).Password);
        Assert.Null(MqttConfig.ForTest(new MqttConfig { Host = "127.0.0.1", Port = 1883, Username = "mallory" }, stored).Password);
        Assert.Equal("typed", MqttConfig.ForTest(new MqttConfig { Host = "evil.example", Username = User, Password = "typed" }, stored).Password);
    }

    [Fact]
    public void NothingOfItGoesToThePhone()
    {
        var cfg = new AgentConfig { Mqtt = Config(1883) };
        cfg.Mqtt.PinnedFingerprint = new string('a', 64);
        cfg.Mqtt.CaFile = "/home/secret/ca.pem";
        var json = JsonSerializer.Serialize(cfg.ToPublic(), ConfigStore.WriteOpts);
        foreach (var secret in new[] { Password, User, "127.0.0.1", WillTopic, "ca.pem", cfg.Mqtt.PinnedFingerprint, "mqtt" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStatusAndTheWifiAllowListLeaveItOut()
    {
        Assert.False(Lan.AllowedOverLan("/api/mqtt/status"));
        Assert.False(Lan.AllowedOverLan("/api/mqtt/test"));
        Assert.False(Lan.AllowedOverLan("/api/mqtt"));
        Assert.False(Lan.IsWebhook("/api/mqtt/status"));
        var status = JsonSerializer.Serialize(new MqttStatus("error", 1700000000000, "auth"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"state":"error","lastMessageAt":1700000000000,"errorKind":"auth"}""", status);
        Assert.Equal(["ErrorKind", "LastMessageAt", "State"], typeof(MqttStatus).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["ErrorKind", "Ok"], typeof(MqttTestResult).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    // ---------------------------------------------------------------- connecting

    [Fact]
    public async Task ItConnectsWithTheLoginAndTheClientIdAndSaysSo()
    {
        using var broker = await MqttTestBroker.StartAsync(user: User, password: Password);
        var (status, log) = await RunOnceAsync(Config(broker.Port), s => s.State == MqttConnection.Connected);
        Assert.Null(status.ErrorKind);
        Assert.Contains((User, Password), broker.Logins);
        Assert.Contains("phonebox-test", broker.ClientIds);
        AssertNoSecrets(JsonSerializer.Serialize(status), log, broker.Port);
    }

    [Fact]
    public async Task ItStaysOffWhileDisabled()
    {
        var cfg = Config(1883);
        cfg.Enabled = false;
        var connection = new MqttConnection(Store(cfg), NullLogger<MqttConnection>.Instance);
        await connection.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(new MqttStatus("off", null, null), connection.Status);
        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AWrongPasswordIsAnAuthError()
    {
        using var broker = await MqttTestBroker.StartAsync(user: User, password: Password);
        var (status, log) = await RunOnceAsync(Config(broker.Port, password: "wrong-" + Password), s => s.State == MqttConnection.Error);
        Assert.Equal("auth", status.ErrorKind);
        AssertNoSecrets(JsonSerializer.Serialize(status), log, broker.Port, "wrong-" + Password);
    }

    [Fact]
    public async Task ABrokerThatIsNotThereIsRefused()
    {
        var port = MqttTestBroker.FreePort(); // nothing listens on it
        var (status, log) = await RunOnceAsync(Config(port), s => s.State == MqttConnection.Error);
        Assert.Equal("refused", status.ErrorKind);
        AssertNoSecrets(JsonSerializer.Serialize(status), log, port);
    }

    [Fact]
    public async Task TestConnectionAnswersWithAKindAndNeverWithText()
    {
        using var broker = await MqttTestBroker.StartAsync(user: User, password: Password);
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(Config(broker.Port), new MqttConfig()), CancellationToken.None));
        var wrong = await MqttConnection.TestAsync(MqttConfig.ForTest(Config(broker.Port, password: "nope"), new MqttConfig()), CancellationToken.None);
        Assert.Equal(new MqttTestResult(false, "auth"), wrong);
        var gone = await MqttConnection.TestAsync(MqttConfig.ForTest(Config(MqttTestBroker.FreePort()), new MqttConfig()), CancellationToken.None);
        Assert.Equal(new MqttTestResult(false, "refused"), gone);
        Assert.Equal(new MqttTestResult(false, "config"), await MqttConnection.TestAsync(new MqttConfig(), CancellationToken.None));
        var text = JsonSerializer.Serialize(new[] { wrong, gone });
        foreach (var secret in new[] { Password, User, "127.0.0.1", WillTopic }) Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task TheTestDoesNotTakeTheRealConnectionsClientId()
    {
        using var broker = await MqttTestBroker.StartAsync();
        await MqttConnection.TestAsync(MqttConfig.ForTest(Config(broker.Port), new MqttConfig()), CancellationToken.None);
        Assert.Equal(["phonebox-test-test"], broker.ClientIds); // a broker drops the older connection of an id it sees again
    }

    [Fact]
    public async Task ItTriesAgainWhenTheBrokerComesBack()
    {
        var port = MqttTestBroker.FreePort();
        var connection = new MqttConnection(Store(Config(port, user: null, password: null)), NullLogger<MqttConnection>.Instance);
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Until(connection, s => s.State == MqttConnection.Error);
            // the next try comes after a wait of a few seconds
            using var broker = await MqttTestBroker.StartAsync(at: port);
            var status = await Until(connection, s => s.State == MqttConnection.Connected, 20);
            Assert.Null(status.ErrorKind);
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    // ---------------------------------------------------------------- TLS

    [Fact]
    public async Task ASelfSignedBrokerIsRefusedByDefault()
    {
        using var cert = MqttTestBroker.Certificate();
        using var broker = await MqttTestBroker.StartAsync(cert);
        var (status, _) = await RunOnceAsync(Config(broker.Port, tls: true), s => s.State == MqttConnection.Error);
        Assert.Equal("tls", status.ErrorKind);
        Assert.Empty(broker.ClientIds);
    }

    [Fact]
    public async Task APinnedFingerprintTrustsThatCertificateAndNoOther()
    {
        using var cert = MqttTestBroker.Certificate();
        using var broker = await MqttTestBroker.StartAsync(cert);
        var good = Config(broker.Port, tls: true);
        good.PinnedFingerprint = string.Join(":", MqttTestBroker.Fingerprint(cert).Chunk(2).Select(c => new string(c))).ToUpperInvariant();
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(good, new MqttConfig()), CancellationToken.None));
        var wrong = Config(broker.Port, tls: true);
        wrong.PinnedFingerprint = new string('0', 64);
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(wrong, new MqttConfig()), CancellationToken.None));
    }

    [Fact]
    public async Task ACaFileTrustsACertificateItSignedForTheRightName()
    {
        using var ca = MqttTestBroker.Certificate("PhoneBox test CA", authority: true);
        using var leaf = MqttTestBroker.Certificate("localhost", ca);
        var caFile = Path.Combine(_dir, "ca.pem");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        using var broker = await MqttTestBroker.StartAsync(leaf);
        var cfg = Config(broker.Port, tls: true);
        cfg.CaFile = caFile;
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));

        // the same CA file does not excuse a certificate for another name
        using var wrongName = MqttTestBroker.Certificate("other.example", ca);
        using var other = await MqttTestBroker.StartAsync(wrongName);
        var cfg2 = Config(other.Port, tls: true);
        cfg2.CaFile = caFile;
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg2, new MqttConfig()), CancellationToken.None));

        // nor a certificate another CA signed
        using var otherCa = MqttTestBroker.Certificate("Another CA", authority: true);
        using var stranger = MqttTestBroker.Certificate("localhost", otherCa);
        using var third = await MqttTestBroker.StartAsync(stranger);
        var cfg3 = Config(third.Port, tls: true);
        cfg3.CaFile = caFile;
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg3, new MqttConfig()), CancellationToken.None));
    }

    [Fact]
    public async Task TheBrokersOwnSelfSignedCertificateWorksAsTheCaFile()
    {
        using var cert = MqttTestBroker.Certificate();
        var caFile = Path.Combine(_dir, "self.pem");
        File.WriteAllText(caFile, cert.ExportCertificatePem());
        using var broker = await MqttTestBroker.StartAsync(cert);
        var cfg = Config(broker.Port, tls: true);
        cfg.CaFile = caFile;
        Assert.Equal(new MqttTestResult(true, null), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));
        // another self-signed certificate on the same address is not it
        using var impostor = MqttTestBroker.Certificate();
        using var other = await MqttTestBroker.StartAsync(impostor);
        var cfg2 = Config(other.Port, tls: true);
        cfg2.CaFile = caFile;
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg2, new MqttConfig()), CancellationToken.None));
    }

    [Fact]
    public async Task ACaFileThatCannotBeReadIsAConfigurationError()
    {
        var cfg = Config(MqttTestBroker.FreePort(), tls: true);
        cfg.CaFile = Path.Combine(_dir, "missing.pem");
        Assert.Equal(new MqttTestResult(false, "config"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));
        File.WriteAllText(cfg.CaFile, "this is not a certificate");
        Assert.Equal(new MqttTestResult(false, "config"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));
    }

    [Theory]
    [InlineData("evil.example", 1883, User, true, "tcp")]  // another host
    [InlineData("127.0.0.1", 1884, User, true, "tcp")]     // another port
    [InlineData("127.0.0.1", 1883, "mallory", true, "tcp")] // another user
    [InlineData("127.0.0.1", 1883, User, true, "websocket")] // another transport
    [InlineData("127.0.0.1", 1883, User, false, "tcp")]    // the same, TLS switched on or off
    public void ASaveThatPointsAtAnotherBrokerDropsTheStoredPassword(string host, int port, string user, bool sameTls, string transport)
    {
        var stored = Config(1883);
        var proposed = new AgentConfig { Mqtt = new() { Host = host, Port = port, Username = user, Transport = transport, Tls = !sameTls, Password = null } };
        if (sameTls) proposed.Mqtt.Tls = stored.Tls;
        Assert.False(MqttConfig.KeepPassword(proposed, stored));
        Assert.Null(proposed.Mqtt.Password);
    }

    [Fact]
    public async Task ASettingsChangeIsNotAFailedConnection()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var log = new ListLogger<MqttConnection>();
        var store = Store(Config(broker.Port, user: null, password: null));
        var connection = new MqttConnection(store, log);
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Until(connection, s => s.State == MqttConnection.Connected);
            var off = Config(broker.Port, user: null, password: null);
            off.Enabled = false;
            Assert.Null(store.Save(JsonSerializer.Serialize(new { mqtt = off }, ConfigStore.WriteOpts)));
            // up to 15 s: the goodbye alone may take 2 s on a slow runner
            for (var i = 0; i < 300 && connection.Status.State != MqttConnection.Off; i++)
            {
                Assert.NotEqual(MqttConnection.Error, connection.Status.State);
                await Task.Delay(50);
            }
            Assert.Equal(MqttConnection.Off, connection.Status.State);
            Assert.DoesNotContain(log.Lines, l => l.Contains("failed"));
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ABrokerThatGoesAwayIsAnErrorAndIsTriedAgain()
    {
        var port = MqttTestBroker.FreePort();
        var broker = await MqttTestBroker.StartAsync(at: port);
        var connection = new MqttConnection(Store(Config(port, user: null, password: null)), NullLogger<MqttConnection>.Instance);
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Until(connection, s => s.State == MqttConnection.Connected);
            broker.Dispose();
            await Until(connection, s => s.State == MqttConnection.Error);
            using var again = await MqttTestBroker.StartAsync(at: port);
            await Until(connection, s => s.State == MqttConnection.Connected);
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ACorrectedSettingEndsTheWaitAtOnce()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var store = Store(Config(MqttTestBroker.FreePort(), user: null, password: null)); // nothing listens there
        // a first wait of 30 s, so the connection found well under that proves the wait was cut short
        var connection = new MqttConnection(store, NullLogger<MqttConnection>.Instance, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Until(connection, s => s.State == MqttConnection.Error);
            var started = DateTime.UtcNow;
            Assert.Null(store.Save(JsonSerializer.Serialize(new { mqtt = Config(broker.Port, user: null, password: null) }, ConfigStore.WriteOpts)));
            await Until(connection, s => s.State == MqttConnection.Connected);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the wait was sat out");
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ABrokerThatAcceptsAndThenDropsEveryConnectionKeepsBackingOff()
    {
        using var broker = await MqttTestBroker.StartAsync();
        broker.DropClients = true;
        var log = new ListLogger<MqttConnection>();
        // waits of 200 ms, 400 ms, 800 ms... (a connection counts as held only after a minute)
        var connection = new MqttConnection(Store(Config(broker.Port, user: null, password: null)), log, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(60));
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            int tries;
            lock (broker.Logins) tries = broker.Logins.Count;
            // 200 ms between tries all the time would be about 25 in six seconds; doubling gives about six
            Assert.InRange(tries, 3, 12);
            // one line for the first connection, one for the failure: not a pair every few seconds
            Assert.True(log.Lines.Count(l => l.Contains("failed")) <= 1, string.Join(" | ", log.Lines));
            Assert.True(log.Lines.Count(l => l.Contains("connected")) <= 1, string.Join(" | ", log.Lines));
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task NewSettingsAreLoggedEvenIfTheOldOnesFailedTheSameWay()
    {
        // two brokers that accept the login and drop the client: the same kind of failure, "other"
        using var first = await MqttTestBroker.StartAsync();
        using var second = await MqttTestBroker.StartAsync();
        first.DropClients = second.DropClients = true;
        var log = new ListLogger<MqttConnection>();
        var store = Store(Config(first.Port, user: null, password: null));
        var connection = new MqttConnection(store, log, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(60));
        await connection.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 100 && !log.Lines.Any(l => l.Contains("failed")); i++) await Task.Delay(100);
            Assert.Single(log.Lines, l => l.Contains("failed"));
            Assert.Null(store.Save(JsonSerializer.Serialize(new { mqtt = Config(second.Port, user: null, password: null) }, ConfigStore.WriteOpts)));
            // the new settings' own failure is not left out because the old ones failed the same way
            for (var i = 0; i < 100 && log.Lines.Count(l => l.Contains("failed")) < 2; i++) await Task.Delay(100);
            Assert.Equal(2, log.Lines.Count(l => l.Contains("failed")));
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AConnectionThatHeldStartsOverWithAShortWaitAndAFreshLog()
    {
        var port = MqttTestBroker.FreePort();
        var log = new ListLogger<MqttConnection>();
        var connection = new MqttConnection(Store(Config(port, user: null, password: null)), log, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));
        await connection.StartAsync(CancellationToken.None);
        try
        {
            await Until(connection, s => s.State == MqttConnection.Error); // nothing listens: "refused", noted once
            using var broker = await MqttTestBroker.StartAsync(at: port); // disposed on an early exit too
            await Until(connection, s => s.State == MqttConnection.Connected);
            // after a failure the "connected" line waits until the connection has held
            Assert.DoesNotContain(log.Lines, l => l.Contains("connected"));
            for (var i = 0; i < 100 && !log.Lines.Any(l => l.Contains("connected")); i++) await Task.Delay(100);
            Assert.Contains(log.Lines, l => l.Contains("connected"));
            // it held: a drop now is reported as a new failure, and retried after the short first wait
            broker.Dispose(); // the explicit call is what drops the connection
            await Until(connection, s => s.State == MqttConnection.Error);
            // the state is set before the line is written: wait for the line itself (bounded), then count
            for (var i = 0; i < 100 && log.Lines.Count(l => l.Contains("failed")) < 2; i++) await Task.Delay(100);
            Assert.Equal(2, log.Lines.Count(l => l.Contains("failed")));
            using var again = await MqttTestBroker.StartAsync(at: port);
            await Until(connection, s => s.State == MqttConnection.Connected, 5);
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AControlCharacterInTheAddressMakesItUnusableInsteadOfBeingStripped()
    {
        var typed = MqttConfig.Normalized(new MqttConfig { Host = "  bro\nker.local " });
        Assert.Equal("bro\nker.local", typed.Host); // trimmed, nothing taken out of the middle
        typed.Enabled = true;
        Assert.Equal(new MqttTestResult(false, "config"), await MqttConnection.TestAsync(MqttConfig.ForTest(typed, new MqttConfig()), CancellationToken.None));
        var (status, _) = await RunOnceAsync(typed, s => s.State == MqttConnection.Error);
        Assert.Equal("config", status.ErrorKind);
    }

    [Fact]
    public async Task AnAddressThatIsNotAHostIsAConfigErrorNotSilence()
    {
        var cfg = Config(1883);
        cfg.Host = "mqtt://broker.local";
        var (status, log) = await RunOnceAsync(cfg, s => s.State == MqttConnection.Error);
        Assert.Equal("config", status.ErrorKind);
        AssertNoSecrets(JsonSerializer.Serialize(status), log, 1883, "broker.local");
        Assert.Equal(new MqttTestResult(false, "config"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));
    }

    [Fact]
    public async Task ACertificateForClientsOnlyIsNotAServerCertificate()
    {
        using var ca = MqttTestBroker.Certificate("PhoneBox test CA", authority: true);
        using var clientOnly = MqttTestBroker.Certificate("localhost", ca, usage: "1.3.6.1.5.5.7.3.2"); // clientAuth
        var caFile = Path.Combine(_dir, "ca2.pem");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        using var broker = await MqttTestBroker.StartAsync(clientOnly);
        var cfg = Config(broker.Port, tls: true);
        cfg.CaFile = caFile;
        Assert.Equal(new MqttTestResult(false, "tls"), await MqttConnection.TestAsync(MqttConfig.ForTest(cfg, new MqttConfig()), CancellationToken.None));
    }

    // ---------------------------------------------------------------- the kinds of error

    [Fact]
    public void ErrorsAreSortedIntoFixedKindsByTheirTypeNotTheirText()
    {
        Assert.Equal("dns", MqttConnection.KindOf(new MQTTnet.Exceptions.MqttCommunicationException(new SocketException((int)SocketError.HostNotFound)), false));
        Assert.Equal("refused", MqttConnection.KindOf(new IOException("x", new SocketException((int)SocketError.ConnectionRefused)), false));
        // a WebSocket error wraps the socket error that explains it
        Assert.Equal("refused", MqttConnection.KindOf(new System.Net.WebSockets.WebSocketException("x", new HttpRequestException("y", new SocketException((int)SocketError.ConnectionRefused))), false));
        Assert.Equal("timeout", MqttConnection.KindOf(new TimeoutException(), false));
        Assert.Equal("tls", MqttConnection.KindOf(new IOException("x", new System.Security.Authentication.AuthenticationException()), false));
        Assert.Equal("tls", MqttConnection.KindOf(new InvalidOperationException("whatever"), certificateRefused: true));
        Assert.Equal("protocol", MqttConnection.KindOf(new System.Net.WebSockets.WebSocketException("not a websocket"), false));
        Assert.Equal("other", MqttConnection.KindOf(new InvalidOperationException("broker.secret.example said no"), false));
        Assert.Equal("auth", MqttConnection.KindOf(MQTTnet.MqttClientConnectResultCode.NotAuthorized));
        Assert.Equal("protocol", MqttConnection.KindOf(MQTTnet.MqttClientConnectResultCode.UnsupportedProtocolVersion));
    }

    private static void AssertNoSecrets(string status, ListLogger<MqttConnection> log, int port, params string[] more)
    {
        var all = status + "\n" + string.Join("\n", log.Lines);
        foreach (var secret in new[] { Password, User, "127.0.0.1", WillTopic, "phonebox-test" }.Concat(more))
            Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain(":" + port, all);
    }
}
