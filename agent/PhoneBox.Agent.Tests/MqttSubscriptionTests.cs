using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// MQTT subscriptions → the push store, against a broker inside the test process. The rules from the issue: a subscription
/// writes one fixed key (a wildcard cannot fill the store), a chatty topic is throttled, and the topic of a message never
/// reaches the phone, the status or the log.
/// </summary>
public sealed class MqttSubscriptionTests : IDisposable
{
    private const string SecretTopic = "home/secret-door/state", SecretPayload = "SECRET-PAYLOAD-42", User = "alice-mqtt-user", Password = "p4ss-Secret-Mqtt";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-mqtt-subs-{Guid.NewGuid():N}");
    private readonly List<PushStore> _stores = [];

    public MqttSubscriptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private PushStore NewPush()
    {
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, $"{Guid.NewGuid():N}.json"));
        _stores.Add(push);
        return push;
    }

    private static MqttSubscription Sub(string topic, string key, string? field = null, string? unit = null) => new() { Topic = topic, Key = key, Field = field, Unit = unit };

    private static MqttConfig Config(int port, string clientId, params MqttSubscription[] subs) => new()
    {
        Enabled = true, Host = "127.0.0.1", Port = port, ClientId = clientId, Subscriptions = [.. subs],
    };

    private static MqttConfig WithLogin(MqttConfig cfg)
    {
        cfg.Username = User;
        cfg.Password = Password;
        return cfg;
    }

    private ConfigStore Store(MqttConfig cfg)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { mqtt = cfg }, ConfigStore.WriteOpts));
        return new ConfigStore(path);
    }

    private sealed class Running(MqttConnection connection, ListLogger<MqttConnection> log, ConfigStore store) : IAsyncDisposable
    {
        public MqttConnection Connection { get; } = connection;
        public ListLogger<MqttConnection> Log { get; } = log;
        public ConfigStore Store { get; } = store;
        public async ValueTask DisposeAsync() => await Connection.StopAsync(CancellationToken.None);
    }

    /// <summary>Starts the connection and returns once it is connected, which is after it has subscribed.</summary>
    private async Task<Running> Start(MqttConfig cfg, PushStore push)
    {
        var log = new ListLogger<MqttConnection>();
        var store = Store(cfg);
        var connection = new MqttConnection(store, log, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(60), push);
        await connection.StartAsync(CancellationToken.None);
        var running = new Running(connection, log, store);
        try { await Until(() => connection.Status.State == MqttConnection.Connected, "the connection to be up"); }
        catch { await running.DisposeAsync(); throw; }
        return running;
    }

    private static async Task Until(Func<bool> done, string what, int seconds = 15)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (done()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("timed out waiting for " + what);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // ---------------------------------------------------------------- settings

    [Fact]
    public void OnlyUsableSubscriptionsAreKept()
    {
        var c = MqttConfig.Normalized(new MqttConfig
        {
            Subscriptions =
            [
                Sub(" home/+/temp ", " temp ", " sensor.temp ", " °C "),
                Sub("home/#", "all"),
                Sub("#", "root"),
                Sub("a/#/b", "x1"),     // # is only the last level
                Sub("a/b+", "x2"),      // + is a whole level
                Sub("a/b#", "x3"),
                Sub("", "x4"),
                Sub("a/b\nc", "x5"),    // a control character is no part of a topic
                Sub("$share/group/a/b", "x6"), // a shared subscription is delivered without its prefix: it would never match
                Sub("ok/topic", "bad key"),
                Sub("ok/topic", ""),
                Sub("ok/topic", new string('k', 65)),
                Sub("again/topic", "temp"), // the key is taken
                null!,
            ],
        });
        Assert.Equal(["temp", "all", "root"], c.Subscriptions.Select(s => s.Key));
        Assert.Equal("home/+/temp", c.Subscriptions[0].Topic);
        Assert.Equal("sensor.temp", c.Subscriptions[0].Field);
        Assert.Equal("°C", c.Subscriptions[0].Unit);
        Assert.Null(c.Subscriptions[1].Field);
        Assert.Empty(MqttConfig.Normalized(new MqttConfig { Subscriptions = null! }).Subscriptions);
    }

    [Fact]
    public void ThereAreFewerSubscriptionsThanTheStoreHasRoomFor()
    {
        var many = Enumerable.Range(0, 100).Select(i => Sub($"t/{i}", $"key{i}")).ToList();
        var c = MqttConfig.Normalized(new MqttConfig { Subscriptions = many });
        Assert.Equal(MqttConfig.MaxSubscriptions, c.Subscriptions.Count);
        Assert.True(MqttConfig.MaxSubscriptions <= 50, "webhooks share the 200 keys of the store");
        Assert.Equal("key0", c.Subscriptions[0].Key);
    }

    [Fact]
    public void TheDefaultSettingsFilesSubscribeToNothing()
    {
        foreach (var name in new[] { "config.default.json", "config.default.linux.json", "config.default.macos.json" })
        {
            using var stream = typeof(AgentConfig).Assembly.GetManifestResourceStream(name)!;
            var cfg = JsonSerializer.Deserialize<AgentConfig>(stream, ConfigStore.ReadOpts)!;
            Assert.Empty(cfg.Mqtt.Subscriptions);
        }
    }

    // ---------------------------------------------------------------- from a message to a value

    [Theory]
    [InlineData("21.5", null, null, "21.5")]
    [InlineData(" 21.5\n", null, null, "21.5")]
    [InlineData("true", null, null, "true")]
    [InlineData("ON", null, null, "\"ON\"")]
    [InlineData("{\"value\":3,\"sub\":\"x\"}", null, null, "{\"value\":3,\"sub\":\"x\"}")]
    [InlineData("[1,2]", null, null, "[1,2]")]
    [InlineData("{\"t\":19.5,\"b\":88}", "t", null, "19.5")]
    [InlineData("{\"s\":{\"t\":\"x\"}}", "s.t", null, "\"x\"")]
    [InlineData("{\"l\":[5,6,7]}", "l.1", null, "6")]
    [InlineData("{\"s\":{\"a\":1}}", "s", null, "{\"a\":1}")]
    [InlineData("21.5", null, "°C", "{\"value\":21.5,\"unit\":\"°C\"}")]
    [InlineData("ON", null, "x", "{\"value\":\"ON\",\"unit\":\"x\"}")]
    [InlineData("{\"t\":19.5}", "t", "°C", "{\"value\":19.5,\"unit\":\"°C\"}")]
    [InlineData("{\"a\":1}", null, "°C", "{\"a\":1}")] // an object has its own shape
    [InlineData("true", null, "°C", "true")]            // a boolean has no unit
    [InlineData("{\"on\":false}", "on", "x", "false")]
    public void AMessageBecomesAValue(string payload, string? field, string? unit, string expected)
    {
        var v = MqttFeed.ValueOf(payload, Sub("t", "k", field, unit));
        Assert.NotNull(v);
        // the same JSON, whatever the escaping of "°C"
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(v!.Value.GetRawText())), v.Value.GetRawText());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("not json", "t")]
    [InlineData("{\"x\":1}", "t")]       // the field is missing
    [InlineData("{\"t\":null}", "t")]
    [InlineData("{\"l\":[1]}", "l.5")]
    [InlineData("{\"l\":[1]}", "l.-1")]
    [InlineData("21", "t")]              // a field needs an object
    public void AMessageThatHasNoValueWritesNothing(string payload, string? field) =>
        Assert.Null(MqttFeed.ValueOf(payload, Sub("t", "k", field)));

    // ---------------------------------------------------------------- the broker's messages

    [Fact]
    public async Task AMessageOfAnExactTopicSetsItsKey()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-1", Sub("home/room/temp", "room-temp")), push);
        await broker.Publish("home/room/temp", "21.5");
        await Until(() => push.Get("room-temp") is not null, "the value");
        Assert.Equal(JsonValueKind.Number, push.Get("room-temp")!.Data.ValueKind);
        Assert.Equal(21.5, push.Get("room-temp")!.Data.GetDouble());
        Assert.NotNull(run.Connection.Status.LastMessageAt);
    }

    [Fact]
    public async Task AJsonFieldAndAUnitMakeAValueTheRulesAndTheWidgetRead()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-2", Sub("zigbee/hall", "hall", "sensor.temp", "°C")), push);
        await broker.Publish("zigbee/hall", "{\"sensor\":{\"temp\":23.5},\"battery\":88}");
        await Until(() => push.Get("hall") is not null, "the value");
        var data = push.Get("hall")!.Data;
        Assert.Equal(23.5, data.GetProperty("value").GetDouble());
        Assert.Equal("°C", data.GetProperty("unit").GetString());
        Assert.True(RuleEngine.Compare(data, ">", "20"));
        Assert.False(RuleEngine.Compare(data, "<", "20"));
    }

    [Fact]
    public async Task ATopicWithAWildcardWritesOneKeyHoweverManyTopicsMatch()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-3", Sub("sensors/#", "latest"), Sub("rooms/+/temp", "temp")), push);
        for (var i = 0; i < 60; i++)
        {
            await broker.Publish($"sensors/board{i}/reading", i.ToString());
            await broker.Publish($"rooms/room{i}/temp", i.ToString());
        }
        // the last values arrive when their second has passed
        await Until(() => push.Get("latest")?.Data.GetDouble() == 59 && push.Get("temp")?.Data.GetDouble() == 59, "the last values");
        Assert.Equal(["latest", "temp"], push.Snapshot().Keys.Order());
    }

    [Fact]
    public async Task AChattyTopicIsOneUpdateASecondAndTheLastMessageWins()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-4", Sub("chatty", "chatty")), push);
        var changes = 0;
        push.Emitted += () => Interlocked.Increment(ref changes);
        for (var i = 1; i <= 50; i++) await broker.Publish("chatty", i.ToString());
        await Until(() => push.Get("chatty")?.Data.GetDouble() == 50, "the last value");
        // the first message at once, then about one write a second (a slow runner may spread the burst over a few seconds);
        // without the throttle this is 50
        Assert.InRange(Volatile.Read(ref changes), 2, 10);
    }

    [Fact]
    public async Task EveryMessageUpdatesTheTimeEvenWhenTheValueRepeats()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-5", Sub("beat", "beat")), push);
        await broker.Publish("beat", "1");
        await Until(() => push.Get("beat") is not null, "the first value");
        var first = push.Get("beat")!.At;
        await Task.Delay(1100); // the key may be written again
        await broker.Publish("beat", "1");
        // "no update for N seconds" is judged by this time: a sensor that keeps saying the same thing is alive
        await Until(() => push.Get("beat")!.At > first, "the second write");
    }

    [Fact]
    public async Task ARetainedMessageFillsTheKeyAndEveryNewConnectionDoesItAgain()
    {
        using var broker = await MqttTestBroker.StartAsync();
        await broker.Publish("state/lock", "locked", retain: true);
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-6", Sub("state/lock", "lock")), push);
        await Until(() => push.Get("lock") is not null, "the retained value");
        Assert.Equal("locked", push.Get("lock")!.Data.GetString());

        push.Remove("lock");
        // a changed setting connects again (a fresh subscription), and the broker sends the retained message once more
        Assert.Null(run.Store.Save(JsonSerializer.Serialize(new { mqtt = Config(broker.Port, "subs-6b", Sub("state/lock", "lock")) }, ConfigStore.WriteOpts)));
        await Until(() => push.Get("lock") is not null, "the value after the reconnect");
    }

    [Fact]
    public async Task ATooLargeOrBinaryMessageIsDroppedAndTheConnectionStaysUp()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-7", Sub("big", "big"), Sub("bin", "bin"), Sub("after", "after")), push);
        await broker.Publish("big", new string('a', PushStore.MaxBytes + 1));
        await broker.Publish("bin", [0xFF, 0xFE, 0x00, 0x80]);
        await broker.Publish("after", "fine");
        // the messages are handled in order: once the last one is stored, the other two have been dealt with
        await Until(() => push.Get("after") is not null, "the last message");
        Assert.Null(push.Get("big"));
        Assert.Null(push.Get("bin"));
        Assert.Equal(MqttConnection.Connected, run.Connection.Status.State);
    }

    [Fact]
    public async Task AFullStoreRefusesTheValueAndIsLoggedOnceWithoutTheTopic()
    {
        using var broker = await MqttTestBroker.StartAsync();
        var push = NewPush();
        for (var i = 0; i < 199; i++) Assert.Null(push.Set($"fill{i}", Json(i), null));
        Assert.Null(push.Set("second", Json("none"), null)); // the 200th key: updating it is allowed when the store is full, a new key is not
        await using var run = await Start(Config(broker.Port, "subs-8", Sub(SecretTopic, "door"), Sub("second", "second")), push);
        await broker.Publish(SecretTopic, "1");
        await Until(() => run.Log.Lines.Any(l => l.Contains("not stored")), "the log line");
        await Task.Delay(1100); // the key is written again for the next message
        await broker.Publish(SecretTopic, "2");
        await broker.Publish("second", "x"); // handled after the one above: once it is stored, that one has been dealt with
        await Until(() => push.Get("second")?.Data.GetString() == "x", "the barrier message");
        Assert.Null(push.Get("door"));
        Assert.Single(run.Log.Lines, l => l.Contains("not stored"));
        Assert.DoesNotContain(run.Log.Lines, l => l.Contains(SecretTopic) || l.Contains("door"));
        Assert.Equal(MqttConnection.Connected, run.Connection.Status.State);
    }

    [Fact]
    public async Task ARefusedSubscriptionIsLoggedWithoutItsTopicAndTheConnectionStaysUp()
    {
        using var broker = await MqttTestBroker.StartAsync();
        broker.DenySubscriptions = true;
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-9", Sub(SecretTopic, "door")), push);
        Assert.Contains(run.Log.Lines, l => l.Contains("refused 1 of 1"));
        Assert.DoesNotContain(run.Log.Lines, l => l.Contains(SecretTopic) || l.Contains("secret"));
        Assert.Equal(MqttConnection.Connected, run.Connection.Status.State);
    }

    [Fact]
    public async Task WithoutSubscriptionsNothingIsSubscribed()
    {
        using var broker = await MqttTestBroker.StartAsync();
        await broker.Publish("any/topic", "1", retain: true);
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-10"), push);
        await Task.Delay(500);
        Assert.Empty(push.Snapshot());
        Assert.Null(run.Connection.Status.LastMessageAt);
    }

    [Fact]
    public async Task ChangingARowConnectsAgainAndSubscribesWithTheNewList()
    {
        using var broker = await MqttTestBroker.StartAsync();
        await broker.Publish("new/topic", "first", retain: true);
        var push = NewPush();
        await using var run = await Start(Config(broker.Port, "subs-12", Sub("old/topic", "temp")), push);
        await broker.Publish("old/topic", "1");
        await Until(() => push.Get("temp")?.Data.GetDouble() == 1, "the first value");

        // "temp" becomes "temperature" on another topic: the retained message of the new topic shows it was subscribed
        Assert.Null(run.Store.Save(JsonSerializer.Serialize(new { mqtt = Config(broker.Port, "subs-12", Sub("new/topic", "temperature")) }, ConfigStore.WriteOpts)));
        await Until(() => push.Get("temperature")?.Data.GetString() == "first", "the new row");
        await broker.Publish("old/topic", "2");
        await broker.Publish("new/topic", "second");
        await Until(() => push.Get("temperature")?.Data.GetString() == "second", "the next value of the new row");
        Assert.Equal(1, push.Get("temp")!.Data.GetDouble()); // the old topic is not subscribed any more
    }

    [Fact]
    public async Task ATopicRemovedFromTheSettingsIsUnsubscribedWhenTheBrokerKeepsTheSession()
    {
        using var broker = await MqttTestBroker.StartAsync(persistentSessions: true);
        var push = NewPush();
        var before = Config(broker.Port, "subs-13", Sub("old/#", "old"));
        before.CleanSession = false; // the broker keeps this client's subscriptions between connections
        await using var run = await Start(before, push);
        var after = Config(broker.Port, "subs-13"); // no rows left
        after.CleanSession = false;
        Assert.Null(run.Store.Save(JsonSerializer.Serialize(new { mqtt = after }, ConfigStore.WriteOpts)));
        await Until(() => { lock (broker.Logins) return broker.Logins.Count >= 2; }, "the second connection");
        await Until(() => run.Connection.Status.State == MqttConnection.Connected, "the connection to be up again");
        await broker.Publish("old/anything", "1");
        await Task.Delay(500); // a message the broker still sent would have arrived by now
        Assert.Null(run.Connection.Status.LastMessageAt);
        lock (broker.Unsubscribed) Assert.Contains("old/#", broker.Unsubscribed);
    }

    [Fact]
    public async Task TheTopicsOfOneBrokerAreNotSentToAnother()
    {
        using var first = await MqttTestBroker.StartAsync(persistentSessions: true);
        using var second = await MqttTestBroker.StartAsync(persistentSessions: true);
        var push = NewPush();
        var before = Config(first.Port, "subs-14", Sub("private/door/#", "door"));
        before.CleanSession = false;
        await using var run = await Start(before, push);
        var after = Config(second.Port, "subs-14"); // another broker, no rows
        after.CleanSession = false;
        Assert.Null(run.Store.Save(JsonSerializer.Serialize(new { mqtt = after }, ConfigStore.WriteOpts)));
        await Until(() => { lock (second.Logins) return second.Logins.Count >= 1; }, "the connection to the second broker");
        await Until(() => run.Connection.Status.State == MqttConnection.Connected, "the connection to be up again");
        await Task.Delay(300); // an unsubscribe right after connecting would have arrived by now
        lock (second.Unsubscribed) Assert.Empty(second.Unsubscribed); // the first broker's topic names stay with it
    }

    [Fact]
    public void AFeedThatIsStoppedWritesNothingMore()
    {
        var push = NewPush();
        var feed = new MqttFeed([Sub("t", "k"), Sub("u", "other")], push, NullLogger.Instance);
        static System.Buffers.ReadOnlySequence<byte> Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));
        feed.Receive("t", Bytes("1"));
        feed.Receive("t", Bytes("2")); // inside the second: it waits
        Assert.Equal(1, push.Get("k")!.Data.GetDouble());
        feed.Stop(); // the connection is ending: the goodbye is on its way
        feed.Receive("u", Bytes("x"));
        feed.Flush(all: true);
        Assert.Equal(1, push.Get("k")!.Data.GetDouble());
        Assert.Null(push.Get("other"));
    }

    // ---------------------------------------------------------------- what stays on the computer

    [Fact]
    public async Task TheTopicsAndTheLoginAreNotInWhatThePhoneStatusOrLogGet()
    {
        using var broker = await MqttTestBroker.StartAsync(user: User, password: Password);
        var push = NewPush();
        var cfg = WithLogin(Config(broker.Port, "subs-11", Sub(SecretTopic, "door")));
        await using var run = await Start(cfg, push);
        await broker.Publish(SecretTopic, SecretPayload);
        await Until(() => push.Get("door") is not null, "the value");

        Assert.Contains(broker.Logins, l => l.User == User && l.Password == Password); // the login was used: its absence below means something
        // the phone's configuration: no MQTT section at all
        var phone = JsonSerializer.Serialize(new AgentConfig { Mqtt = cfg }.ToPublic(), ConfigStore.WriteOpts);
        // the status: three fields
        var status = JsonSerializer.Serialize(run.Connection.Status);
        // what the settings page gets back: the topics, but not the password
        var settings = JsonSerializer.SerializeToNode(new AgentConfig { Mqtt = cfg }, ConfigStore.WriteOpts)!.AsObject();
        MqttConfig.Redact(settings);
        var shown = settings.ToJsonString();
        Assert.Contains("secret-door", shown);
        Assert.DoesNotContain(Password, shown);
        // the pushed value is the message, under the key: the topic is not in the store or in what /api/push lists
        var listed = JsonSerializer.Serialize(push.Snapshot());
        Assert.Contains(SecretPayload, listed);
        foreach (var (what, text) in new[] { ("the phone's config", phone), ("the status", status), ("the pushed values", listed), ("the log", string.Join("\n", run.Log.Lines)) })
        {
            Assert.False(text.Contains("secret-door", StringComparison.Ordinal), "the topic is in " + what);
            Assert.False(text.Contains(Password, StringComparison.Ordinal), "the password is in " + what);
            Assert.False(text.Contains(User, StringComparison.Ordinal), "the user name is in " + what);
            if (what != "the pushed values") Assert.False(text.Contains(SecretPayload, StringComparison.Ordinal), "a message is in " + what);
        }
        Assert.DoesNotContain("subscriptions", phone, StringComparison.OrdinalIgnoreCase);
    }
}
