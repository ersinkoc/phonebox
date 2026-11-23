using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The read-only Discord chat: off by default, asks for messages.read only when on, keeps nothing while off.</summary>
public sealed class DiscordChatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-discord-chat-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private int _writes;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private ConfigStore Store(bool chat, bool control = true, bool pageEnabled = true, bool widget = true, bool? notifications = null,
        bool allowControl = false, string secret = "s")
    {
        var options = new Dictionary<string, object> { ["control"] = control };
        if (notifications is { } n) options["notifications"] = n;
        object[] widgets = widget ? [new { id = "d", type = "discord", x = 0, y = 0, w = 4, h = 5, options }] : [];
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = true, clientId = "1", clientSecret = secret, chat, allowControl },
            // at least one page must stay enabled, so a second page holds no widgets
            pages = new[] { new { id = "p", title = "P", enabled = pageEnabled, widgets }, new { id = "q", title = "Q", enabled = true, widgets = Array.Empty<object>() } },
        }));
        // a write time of its own, so a store already reading this file sees the change however coarse the clock is
        File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddMinutes(++_writes));
        return new ConfigStore(ConfigPath);
    }

    private DiscordMonitor Monitor(bool chat, bool control = true) => new(Store(chat, control), NullLogger<DiscordMonitor>.Instance);

    // ---- settings and permissions ----

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void The_chat_is_off_in_every_default_file(string name)
    {
        using var stream = typeof(DiscordMonitor).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("discord").GetProperty("chat").ValueKind);
        Assert.False(new AgentConfig().Discord.Chat);
    }

    [Fact]
    public void Messages_read_is_asked_only_with_the_chat()
    {
        static List<string> Scopes(DiscordConfig c) => [.. DiscordMonitor.AuthorizeArgs("1", DiscordMonitor.ExtraScopes(c))["scopes"]!.AsArray().Select(n => (string)n!)];
        var off = Scopes(new() { Chat = false });
        Assert.DoesNotContain("messages.read", off);
        Assert.Equal(off.Append("messages.read"), Scopes(new() { Chat = true }));
    }

    [Fact]
    public void Chat_and_screen_share_together_ask_for_both()
    {
        static List<string> Scopes(DiscordConfig c) => [.. DiscordMonitor.AuthorizeArgs("1", DiscordMonitor.ExtraScopes(c))["scopes"]!.AsArray().Select(n => (string)n!)];
        var neither = Scopes(new());
        Assert.Equal([.. neither, "rpc.screenshare.read", "rpc.screenshare.write", "messages.read"], Scopes(new() { ScreenShare = true, Chat = true }));
    }

    [Theory]
    [InlineData("rpc identify messages.read", true)]
    [InlineData("rpc identify", false)]
    [InlineData("rpc messages.readx", false)]
    [InlineData(null, false)]
    public void Saved_scopes_are_matched_whole(string? scopes, bool expected) =>
        Assert.Equal(expected, DiscordMonitor.HasScopes(scopes, DiscordMonitor.ChatScope));

    [Fact]
    public void The_chat_does_not_turn_notifications_on()
    {
        Assert.False(DiscordMonitor.NotificationsWanted(Store(chat: true, notifications: false).Get()));
        Assert.False(DiscordMonitor.NotificationsWanted(Store(chat: true, widget: false).Get()));
        Assert.False(DiscordMonitor.NotificationsWanted(Store(chat: true, pageEnabled: false).Get()));
        Assert.True(DiscordMonitor.NotificationsWanted(Store(chat: true).Get()));
    }

    [Fact]
    public void The_chat_needs_its_setting_and_a_Discord_widget_on_an_enabled_page()
    {
        Assert.True(DiscordMonitor.ChatWanted(Store(chat: true).Get()));
        Assert.False(DiscordMonitor.ChatWanted(Store(chat: false).Get()));
        Assert.False(DiscordMonitor.ChatWanted(Store(chat: true, widget: false).Get()));
        Assert.False(DiscordMonitor.ChatWanted(Store(chat: true, pageEnabled: false).Get()));
    }

    // ---- over a pipe that stands in for Discord ----

    private const string Base = "rpc rpc.voice.read rpc.voice.write rpc.notifications.read";

    private void SaveLogin(DiscordMonitor m, string? scope) => File.WriteAllText(m.TokenPath, JsonSerializer.Serialize(new
    {
        ClientId = "1", Access = "saved", Refresh = "r", Expires = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(), Scope = scope,
    }));

    private sealed class FakeDiscord(Stream s, CancellationToken ct)
    {
        public CancellationToken Ct => ct;

        public async Task<JsonObject> Request()
        {
            var head = new byte[8];
            await s.ReadExactlyAsync(head, ct);
            var body = new byte[BitConverter.ToInt32(head, 4)];
            await s.ReadExactlyAsync(body, ct);
            return JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();
        }

        public async Task<JsonObject> Request(string cmd)
        {
            var r = await Request();
            Assert.Equal(cmd, (string?)r["cmd"]);
            return r;
        }

        public Task Answer(JsonObject request, string data, string? evt = null) =>
            Send(new() { ["cmd"] = (string?)request["cmd"], ["evt"] = evt, ["nonce"] = (string?)request["nonce"], ["data"] = JsonNode.Parse(data) });

        public Task Message(string channel, string id, string text, string author = "7") =>
            Send(new() { ["cmd"] = "DISPATCH", ["evt"] = "MESSAGE_CREATE", ["data"] = new JsonObject { ["channel_id"] = channel, ["message"] = JsonNode.Parse(Msg(id, text, author)) } });

        public async Task Send(JsonObject o)
        {
            var body = Encoding.UTF8.GetBytes(o.ToJsonString());
            var buf = new byte[8 + body.Length];
            BitConverter.TryWriteBytes(buf.AsSpan(0, 4), 1);
            BitConverter.TryWriteBytes(buf.AsSpan(4, 4), body.Length);
            body.CopyTo(buf, 8);
            await s.WriteAsync(buf, ct);
        }

        public async Task Authenticated(string scopes, string token = "saved")
        {
            var r = await Request("AUTHENTICATE");
            Assert.Equal(token, (string?)r["args"]!["access_token"]);
            await Answer(r, new JsonObject { ["user"] = new JsonObject { ["id"] = "1", ["username"] = "me" }, ["scopes"] = new JsonArray([.. scopes.Split(' ').Select(x => (JsonNode)x)]) }.ToJsonString());
        }
    }

    private static string Msg(string id, string text, string author = "7") =>
        new JsonObject { ["id"] = id, ["content"] = text, ["author"] = new JsonObject { ["id"] = author, ["username"] = "ada", ["avatar"] = "av" } }.ToJsonString();

    private static string History(params (string Id, string Text)[] messages) =>
        "{\"messages\":[" + string.Join(",", messages.Select(m => Msg(m.Id, m.Text))) + "]}";

    private static async Task WithDiscord(DiscordMonitor monitor, Func<FakeDiscord, Task> test)
    {
        var name = "pbc-" + Guid.NewGuid().ToString("N")[..12]; // short: macOS puts the socket under $TMPDIR
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.WhenAll(server.WaitForConnectionAsync(cts.Token), client.ConnectAsync(cts.Token));
        monitor._io = client;
        var loop = monitor.ReadLoop(client, cts.Token);
        await test(new(server, cts.Token));
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    /// <summary>Logged in with messages.read, in a voice channel ("500").</summary>
    private Task WithChat(DiscordMonitor monitor, Func<FakeDiscord, Task> test) => WithDiscord(monitor, async discord =>
    {
        SaveLogin(monitor, Base + " messages.read");
        var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
        await discord.Authenticated(Base + " messages.read");
        await login;
        monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, "500", "Game night", "voice", [], [], []));
        await test(discord);
    });

    private static JsonObject Json(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();
    private static string[] Texts(JsonObject chat) => [.. chat["messages"]!.AsArray().Select(m => (string)m!["Text"]!)];

    private static async Task<JsonObject> Open(DiscordMonitor monitor, FakeDiscord discord, string history)
    {
        var chat = monitor.ChatAsync(null, picker: false, discord.Ct);
        var sub = await discord.Request("SUBSCRIBE");
        Assert.Equal(("MESSAGE_CREATE", "500"), ((string?)sub["evt"], (string?)sub["args"]!["channel_id"]));
        await discord.Answer(sub, "{}");
        await discord.Answer(await discord.Request("GET_CHANNEL"), history);
        return Json(await chat);
    }

    [Theory]
    [InlineData(Base)]
    [InlineData(null)] // saved by a version that didn't keep the scopes
    public Task A_declined_permission_only_keeps_the_chat_off(string? savedScope)
    {
        var monitor = Monitor(chat: true);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, savedScope);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("messages.read", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
            await discord.Answer(ask, """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);                        // goes on with the saved login
            Assert.Equal("me", await login);
            monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, "500", "Game night", "voice", [], [], []));
            Assert.True(monitor.Snapshot().Connected);
            var chat = Json(await monitor.ChatAsync(null, false, discord.Ct));
            Assert.True((bool?)chat["enabled"]);
            Assert.False((bool?)chat["ready"]);

            var again = monitor.Authenticate(cfg, discord.Ct);        // the next connection doesn't ask again
            await discord.Authenticated(Base);
            await again;
        });
    }

    [Fact]
    public Task A_permission_taken_back_in_Discord_does_not_ask_in_a_loop()
    {
        var monitor = Monitor(chat: true);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base + " messages.read");
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            for (var i = 0; i < 2; i++)
            {
                var login = monitor.Authenticate(cfg, discord.Ct);
                await discord.Authenticated(Base);
                Assert.Equal("me", await login);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_permission_taken_back_in_Discord_is_asked_for_again_after_a_reset(bool restart)
    {
        var monitor = Monitor(chat: true);
        DiscordConfig Cfg() => new ConfigStore(ConfigPath).Get().Discord;
        await WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base + " messages.read");
            var login = monitor.Authenticate(Cfg(), discord.Ct);
            await discord.Authenticated(Base);                         // Discord no longer grants it
            await login;
            Assert.False(monitor.NeedsLogin(Cfg()));                   // and is not asked in a loop
            if (restart) return;
            Store(chat: false);
            Assert.False(monitor.NeedsLogin(Cfg()));
            Store(chat: true);
            Assert.True(monitor.NeedsLogin(Cfg()));
            await Asked(monitor, discord);
        });
        if (!restart) return;
        monitor = Monitor(chat: true);                              // the saved login alone has to tell
        await WithDiscord(monitor, discord => Asked(monitor, discord));

        async Task Asked(DiscordMonitor m, FakeDiscord discord)
        {
            var login = m.Authenticate(Cfg(), discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("messages.read", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
            await discord.Answer(ask, """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);                         // declined: the base login goes on
            await login;
            Assert.False(m.NeedsLogin(Cfg()));
        }
    }

    [Fact]
    public Task A_declined_permission_is_asked_for_again_with_another_client_secret()
    {
        var monitor = Monitor(chat: true);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);
            await login;
            Assert.False(monitor.NeedsLogin(cfg));
            Assert.True(monitor.NeedsLogin(Store(chat: true, secret: "new").Get().Discord));
        });
    }

    [Fact]
    public Task A_permission_taken_back_while_its_setting_is_off_is_asked_for_when_it_is_turned_on()
    {
        var monitor = Monitor(chat: false);
        DiscordConfig Cfg() => new ConfigStore(ConfigPath).Get().Discord;
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base + " messages.read");
            var login = monitor.Authenticate(Cfg(), discord.Ct);
            await discord.Authenticated(Base);
            await login;
            Assert.False(DiscordMonitor.HasScopes(SavedScope(monitor), "messages.read"));
            Assert.True(DiscordMonitor.HasScopes(SavedScope(monitor), Base.Split(' ')));
            Store(chat: true);
            Assert.True(monitor.NeedsLogin(Cfg()));
            login = monitor.Authenticate(Cfg(), discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);
            await login;
        });
    }

    /// <summary>discord.com's token service, answering every request with the same new login.</summary>
    private sealed class FakeTokens : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage
        {
            Content = new StringContent(new JsonObject { ["access_token"] = "new", ["refresh_token"] = "r2", ["expires_in"] = 600000, ["scope"] = Base + " messages.read" }.ToJsonString()),
        });
    }

    private static JsonNode Saved(DiscordMonitor m) => JsonNode.Parse(File.ReadAllText(m.TokenPath))!;
    private static string? SavedScope(DiscordMonitor m) => (string?)Saved(m)["Scope"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_new_login_replaces_the_saved_one_only_once_Discord_took_it(bool taken)
    {
        var monitor = Monitor(chat: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"code":"c"}""");
            if (taken) await discord.Authenticated(Base + " messages.read", "new");
            else
            {
                await discord.Answer(await discord.Request("AUTHENTICATE"), """{"message":"invalid token"}""", "ERROR");
                await discord.Authenticated(Base);                     // goes on with the saved login
            }
            await login;
            Assert.Equal(taken ? "new" : "saved", (string?)Saved(monitor)["Access"]);
            Assert.Equal(taken, DiscordMonitor.HasScopes(SavedScope(monitor), "messages.read"));
            Assert.False(monitor.NeedsLogin(cfg));
        });
    }

    [Fact]
    public Task A_new_login_is_saved_with_the_permissions_Discord_granted()
    {
        var monitor = Monitor(chat: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"code":"c"}""");
            await discord.Authenticated(Base, "new");
            await login;
            Assert.Equal(("new", Base), ((string?)Saved(monitor)["Access"], SavedScope(monitor)));
        });
    }

    [Fact]
    public Task A_permission_declined_at_the_first_login_is_left_out_of_the_next_question()
    {
        var monitor = Monitor(chat: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("messages.read", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
            await discord.Answer(ask, """{"message":"declined"}""", "ERROR");
            ask = await discord.Request("AUTHORIZE");
            Assert.Equal(Base, string.Join(' ', ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n)));
            await discord.Answer(ask, """{"code":"c"}""");
            await discord.Authenticated(Base, "new");
            await login;
        });
    }

    [Fact]
    public Task A_renewed_login_is_saved_at_once()
    {
        var monitor = Monitor(chat: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            File.WriteAllText(monitor.TokenPath, JsonSerializer.Serialize(new { ClientId = "1", Access = "saved", Refresh = "r", Expires = 0L, Scope = Base + " messages.read" }));
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            var auth = await discord.Request("AUTHENTICATE");
            Assert.Equal("r2", (string?)Saved(monitor)["Refresh"]);    // the one it replaces may no longer work
            await discord.Answer(auth, """{"user":{"id":"1","username":"me"}}""");
            await login;
        });
    }

    [Fact]
    public Task Without_the_chat_the_saved_login_is_used_as_it_is()
    {
        var monitor = Monitor(chat: false);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, null);
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            await discord.Authenticated(Base);
            Assert.Equal("me", await login);
        });
    }

    [Fact]
    public async Task Nothing_is_read_while_the_chat_is_off()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            Assert.Equal(["hi"], Texts(await Open(monitor, discord, History(("10", "hi")))));
            Assert.Null(monitor.ChatChoose("voice"));
            Store(chat: false);                                        // the permission is still there: only the switch went off
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(await monitor.ChatAsync(null, false, discord.Ct)));
            Assert.NotNull(monitor.ChatChoose("voice"));
            Assert.Contains("chat is turned off", await monitor.ChatOpenAsync());
            monitor.OnChatMessage(JsonNode.Parse("{\"channel_id\":\"500\",\"message\":" + Msg("11", "secret") + "}"));
            Assert.False(monitor.ChatUnread());
        });
    }

    [Fact]
    public async Task Turning_the_chat_off_forgets_the_messages_and_unsubscribes()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            await Open(monitor, discord, History(("10", "hi")));
            Assert.True(monitor.IsChatAuthor("7", "av"));
            Store(chat: false);
            var stop = monitor.StopChat(discord.Ct);
            var unsub = await discord.Request("UNSUBSCRIBE");
            Assert.Equal(("MESSAGE_CREATE", "500"), ((string?)unsub["evt"], (string?)unsub["args"]!["channel_id"]));
            Assert.False(monitor.IsChatAuthor("7", "av"));
            await discord.Answer(unsub, """{"message":"busy"}""", "ERROR");
            await stop;
            stop = monitor.StopChat(discord.Ct);                       // failed: tried again on the next pass
            await discord.Answer(await discord.Request("UNSUBSCRIBE"), "{}");
            await stop;
            await monitor.StopChat(discord.Ct);                        // nothing left to unsubscribe from
            Store(chat: true);
            Assert.Equal(["later"], Texts(await Open(monitor, discord, History(("12", "later"))))); // subscribes anew
        });
    }

    [Fact]
    public async Task A_message_text_is_not_sent_when_the_chat_goes_off_while_Discord_answers()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            await discord.Answer(await discord.Request("SUBSCRIBE"), "{}");
            var read = await discord.Request("GET_CHANNEL");
            Store(chat: false);
            await discord.Answer(read, History(("10", "secret")));
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(await chat));
            Assert.False(monitor.IsChatAuthor("7", "av"));
        });
    }

    [Fact]
    public async Task The_messages_are_not_read_when_the_chat_goes_off_while_subscribing()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            var sub = await discord.Request("SUBSCRIBE");
            Store(chat: false);
            await discord.Answer(sub, "{}");
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(await chat.WaitAsync(TimeSpan.FromSeconds(2), discord.Ct))); // no GET_CHANNEL
        });
    }

    [Fact]
    public async Task A_message_that_arrives_during_the_history_read_stays()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            await discord.Answer(await discord.Request("SUBSCRIBE"), "{}");
            var read = await discord.Request("GET_CHANNEL");
            await discord.Message("500", "11", "new");
            await discord.Answer(read, History(("10", "old")));
            Assert.Equal(["old", "new"], Texts(Json(await chat)));
        });
    }

    [Fact]
    public async Task A_failed_subscription_is_asked_for_again()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            await discord.Answer(await discord.Request("SUBSCRIBE"), """{"message":"no"}""", "ERROR");
            await discord.Answer(await discord.Request("GET_CHANNEL"), History(("10", "hi")));
            Assert.Equal(["hi"], Texts(Json(await chat)));
            Assert.Equal(["hi"], Texts(await Open(monitor, discord, History(("10", "hi")))));
        });
    }

    [Fact]
    public async Task A_subscription_answered_after_its_connection_ended_does_not_count_on_the_next_one()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            var sub = await discord.Request("SUBSCRIBE");
            var io = monitor._io;
            monitor._io = null;                                        // the connection ends...
            monitor.ClearChat();
            await discord.Answer(sub, "{}");                           // ...and its pending request is completed after that
            await chat;
            monitor._io = io;                                          // the next connection
            Assert.Equal(["hi"], Texts(await Open(monitor, discord, History(("10", "hi"))))); // subscribes again
        });
    }

    [Fact]
    public async Task No_more_channels_are_followed_than_the_limit_even_when_unsubscribing_fails()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            void Join(string id) => monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, id, "Room", "voice", [], [], []));
            for (var i = 0; i < DiscordMonitor.ChatFollowed; i++)
            {
                Join((600 + i).ToString());
                var opened = monitor.ChatAsync(null, false, discord.Ct);
                await discord.Answer(await discord.Request("SUBSCRIBE"), "{}");
                await discord.Answer(await discord.Request("GET_CHANNEL"), History());
                await opened;
            }
            Join("700");
            var chat = monitor.ChatAsync(null, false, discord.Ct);
            var unsub = await discord.Request("UNSUBSCRIBE");
            Assert.Equal("600", (string?)unsub["args"]!["channel_id"]);
            await discord.Answer(unsub, """{"message":"busy"}""", "ERROR");
            await discord.Answer(await discord.Request("GET_CHANNEL"), History(("10", "hi"))); // no SUBSCRIBE in between
            Assert.Equal(["hi"], Texts(Json(await chat)));

            chat = monitor.ChatAsync(null, false, discord.Ct);         // the next poll tries again
            await discord.Answer(await discord.Request("UNSUBSCRIBE"), "{}");
            var sub = await discord.Request("SUBSCRIBE");
            Assert.Equal("700", (string?)sub["args"]!["channel_id"]);
            await discord.Answer(sub, "{}");
            await discord.Answer(await discord.Request("GET_CHANNEL"), History(("10", "hi")));
            await chat;
        });
    }

    [Fact]
    public async Task The_channel_picker_reads_no_messages_and_marks_none_as_seen()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            await Open(monitor, discord, History(("10", "hi")));
            await discord.Message("500", "11", "new");
            for (var i = 0; i < 200 && !monitor.ChatUnread(); i++) await Task.Delay(25);
            Assert.True(monitor.ChatUnread());
            var picker = Json(await monitor.ChatAsync("10", picker: true, discord.Ct)); // answered without asking Discord anything
            Assert.Empty(picker["messages"]!.AsArray());
            Assert.True(monitor.ChatUnread());
            Assert.Equal(["hi", "new"], Texts(Json(await monitor.ChatAsync(null, picker: false, discord.Ct)))); // from memory
            Assert.False(monitor.ChatUnread());
        });
    }

    [Fact]
    public async Task Your_own_messages_make_no_dot()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            await Open(monitor, discord, History(("10", "hi")));
            await discord.Message("500", "11", "mine", author: "1");
            // received, without showing the chat (which would mark it as seen)
            for (var i = 0; i < 200 && !monitor.IsChatAuthor("1", "av"); i++) await Task.Delay(25);
            Assert.True(monitor.IsChatAuthor("1", "av"));
            Assert.False(monitor.ChatUnread());
        });
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]   // the global switch is for the voice actions
    [InlineData(true, false, true)]   // Control on, but on a page that is off
    public async Task Open_in_Discord_needs_a_widget_with_control_on_an_enabled_page(bool control, bool pageEnabled, bool allowControl)
    {
        var monitor = new DiscordMonitor(Store(chat: true, control, pageEnabled, allowControl: allowControl), NullLogger<DiscordMonitor>.Instance);
        Assert.Contains("controlling Discord is turned off", await monitor.ChatOpenAsync());
    }

    [Fact]
    public async Task Open_in_Discord_shows_the_channel_with_the_widgets_control()
    {
        var monitor = Monitor(chat: true);
        await WithChat(monitor, async discord =>
        {
            var open = monitor.ChatOpenAsync();
            var select = await discord.Request("SELECT_TEXT_CHANNEL");
            Assert.Equal("500", (string?)select["args"]!["channel_id"]);
            await discord.Answer(select, "{}");
            Assert.Null(await open);
        });
    }

    // ---- messages ----

    [Fact]
    public void Ids_order_by_number()
    {
        Assert.True(DiscordMonitor.Newer("1000", "999"));
        Assert.False(DiscordMonitor.Newer("999", "1000"));
        Assert.True(DiscordMonitor.Newer("5", null));
        Assert.False(DiscordMonitor.Newer(null, "5"));
    }

    [Fact]
    public void Messages_are_plain_text()
    {
        var m = JsonNode.Parse("""
            {"id":"10","content":"hi <@42> see <#7> <:wave:123> <@&5> <t:0:f>","timestamp":"2026-01-01T00:00:00Z",
             "author":{"id":"42","username":"ada","global_name":"Ada","bot":false},
             "mentions":[{"id":"42","username":"ada","global_name":"Ada"}],
             "attachments":[{},{}],"embeds":[{"title":"A link"}]}
            """)!.AsObject();
        var msg = DiscordMonitor.ParseMessage(m, new Dictionary<string, string> { ["7"] = "general" })!;
        Assert.Equal("hi @Ada see #general :wave: @role 1970-01-01 00:00 UTC", msg.Text);
        Assert.Equal("Ada", msg.Author);
        Assert.Equal(2, msg.Attachments);
        Assert.Equal("A link", msg.Embed);
        Assert.False(msg.Bot);
        Assert.Null(DiscordMonitor.ParseMessage(JsonNode.Parse("""{"content":"no id"}""")!.AsObject(), null));
    }

    [Theory]
    [InlineData("<t:253402300799>", "9999-12-31 23:59 UTC")]
    [InlineData("<t:253402300800>", "<t:253402300800>")]             // past what .NET can show: kept as written
    [InlineData("<t:99999999999999999999999>", "<t:99999999999999999999999>")]
    public void A_timestamp_out_of_range_is_kept_as_written(string content, string shown) =>
        Assert.Equal(shown, DiscordMonitor.ParseMessage(JsonNode.Parse(Msg("1", content))!.AsObject(), null)!.Text);
}
