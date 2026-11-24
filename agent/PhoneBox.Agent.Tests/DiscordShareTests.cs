using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Discord screen share from the phone: off by default, its scopes only when on, needs control, never sent blindly.</summary>
public sealed class DiscordShareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-discord-share-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private int _writes;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private ConfigStore Store(bool share, bool control = true, bool pageEnabled = true, bool allowControl = false, string secret = "s", string type = "discord")
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = true, clientId = "1", clientSecret = secret, screenShare = share, allowControl },
            // at least one page must stay enabled, so a second page holds no widgets
            pages = new[] { new { id = "p", title = "P", enabled = pageEnabled, widgets = new object[] { new { id = "d", type, x = 0, y = 0, w = 4, h = 5, options = new { control } } } }, new { id = "q", title = "Q", enabled = true, widgets = Array.Empty<object>() } },
        }));
        // a write time of its own, so a store already reading this file sees the change however coarse the clock is
        File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddMinutes(++_writes));
        return new ConfigStore(ConfigPath);
    }

    private DiscordMonitor Monitor(bool share, bool control = true) => new(Store(share, control), NullLogger<DiscordMonitor>.Instance)
    {
        ListApps = () => [new(42, "Space Racers"), new(77, "Firefox")],
        ShareStartWait = TimeSpan.FromMilliseconds(300),
    };

    private static string? Code(DiscordShareError? e) => e?.Code;

    // ---- the current-channel bar: whether you share, and stop; never the apps ----

    [Fact]
    public async Task A_bar_alone_learns_that_you_share_but_never_the_apps_or_the_name()
    {
        // the phone can ask /api/discord/share?apps=true itself: with only the bar's Control the agent still says no
        var monitor = new DiscordMonitor(Store(share: true, type: "discord-bar"), NullLogger<DiscordMonitor>.Instance)
        {
            ListApps = () => [new(42, "Space Racers"), new(77, "Firefox")],
        };
        await WithShare(monitor, Active, async discord =>
        {
            await Until(() => monitor.Sharing == true);
            var answer = Json(monitor.Share(true));
            Assert.True((bool?)answer["active"]);
            Assert.Null(answer["apps"]);
            Assert.Null(answer["name"]);
        });
    }

    // ---- settings and permissions ----

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void Screen_share_is_off_in_every_default_file(string name)
    {
        using var stream = typeof(DiscordMonitor).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("discord").GetProperty("screenShare").ValueKind);
        Assert.False(new AgentConfig().Discord.ScreenShare);
    }

    [Fact]
    public void Screen_share_scopes_are_asked_only_when_on()
    {
        static List<string> Scopes(DiscordConfig c) => [.. DiscordMonitor.AuthorizeArgs("1", DiscordMonitor.ExtraScopes(c))["scopes"]!.AsArray().Select(n => (string)n!)];
        var off = Scopes(new() { ScreenShare = false });
        Assert.DoesNotContain(off, s => s.StartsWith("rpc.screenshare", StringComparison.Ordinal));
        Assert.Equal([.. off, "rpc.screenshare.read", "rpc.screenshare.write"], Scopes(new() { ScreenShare = true }));
    }

    [Theory]
    [InlineData("rpc rpc.screenshare.read rpc.screenshare.write", true)]
    [InlineData("rpc rpc.screenshare.read", false)]
    [InlineData("rpc.screenshare.readx rpc.screenshare.write", false)]
    [InlineData(null, false)]
    public void Saved_scopes_must_hold_both(string? scopes, bool expected) =>
        Assert.Equal(expected, DiscordMonitor.HasScopes(scopes, DiscordMonitor.ShareScopes));

    [Fact]
    public void Running_apps_have_names_and_no_duplicates()
    {
        var apps = DiscordMonitor.RunningApps();
        Assert.True(apps.Count <= DiscordMonitor.ShareAppsMax);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(apps.Count, apps.Select(a => a.Pid).Distinct().Count());
        Assert.DoesNotContain(apps, a => a.Pid == Environment.ProcessId);
    }

    // ---- over a pipe that stands in for Discord ----

    private const string Base = "rpc rpc.voice.read rpc.voice.write rpc.notifications.read";
    private const string Granted = Base + " rpc.screenshare.read rpc.screenshare.write";

    private sealed class FakeDiscord(Stream s, CancellationToken ct)
    {
        public CancellationToken Ct => ct;

        public async Task<JsonObject> Request(string cmd)
        {
            var head = new byte[8];
            await s.ReadExactlyAsync(head, ct);
            var body = new byte[BitConverter.ToInt32(head, 4)];
            await s.ReadExactlyAsync(body, ct);
            var r = JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();
            Assert.Equal(cmd, (string?)r["cmd"]);
            return r;
        }

        public Task Answer(JsonObject request, string data = "{}", string? evt = null) =>
            Send(new() { ["cmd"] = (string?)request["cmd"], ["evt"] = evt, ["nonce"] = (string?)request["nonce"], ["data"] = JsonNode.Parse(data) });

        public Task State(string data) => Send(new() { ["cmd"] = "DISPATCH", ["evt"] = "SCREENSHARE_STATE_UPDATE", ["data"] = JsonNode.Parse(data) });

        private async Task Send(JsonObject o)
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

    private const string Idle = """{"active":false,"pid":null,"application":null}""";
    private const string Active = """{"active":true,"pid":42,"application":{}}"""; // as Discord sends it for a share by process

    private void SaveLogin(DiscordMonitor m, string? scope) => File.WriteAllText(m.TokenPath, JsonSerializer.Serialize(new
    {
        ClientId = "1", Access = "saved", Refresh = "r", Expires = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(), Scope = scope,
    }));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static async Task WithDiscord(DiscordMonitor monitor, Func<FakeDiscord, Task> test)
    {
        var name = "pbs-" + Guid.NewGuid().ToString("N")[..12]; // short: macOS puts the socket under $TMPDIR
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

    /// <summary>Logged in with the screen-share scopes, in a call, and (with <paramref name="state"/>) told the share state.</summary>
    private Task WithShare(DiscordMonitor monitor, string? state, Func<FakeDiscord, Task> test, string? channel = "500") => WithDiscord(monitor, async discord =>
    {
        SaveLogin(monitor, Granted);
        var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
        await discord.Authenticated(Granted);
        await login;
        monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, channel, channel is null ? null : "Game night", "voice", [], [], []));
        var follow = monitor.FollowShare(discord.Ct);
        var sub = await discord.Request("SUBSCRIBE");
        Assert.Equal("SCREENSHARE_STATE_UPDATE", (string?)sub["evt"]);
        await discord.Answer(sub);
        await follow;
        if (state is not null)
        {
            await discord.State(state);
            await Until(() => monitor.Sharing is not null);
        }
        await test(discord);
    });

    private static JsonObject Json(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();

    [Theory]
    [InlineData(Base)]
    [InlineData(null)] // saved by a version that didn't keep the scopes
    public Task A_declined_permission_only_keeps_screen_share_off(string? savedScope)
    {
        var monitor = Monitor(share: true);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, savedScope);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("rpc.screenshare.write", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
            await discord.Answer(ask, """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);                        // goes on with the saved login
            Assert.Equal("me", await login);
            monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, "500", "Game night", "voice", [], [], []));
            Assert.True(monitor.Snapshot().Connected);
            await monitor.FollowShare(discord.Ct);                    // asks Discord nothing
            Assert.False((bool?)Json(monitor.Share(true))["ready"]);
            Assert.Equal("notAllowed", Code(await monitor.ShareActAsync("pick", null)));

            var again = monitor.Authenticate(cfg, discord.Ct);        // the next connection doesn't ask again
            await discord.Authenticated(Base);
            await again;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_permission_taken_back_in_Discord_is_asked_for_again_after_a_reset(bool restart)
    {
        var monitor = Monitor(share: true);
        DiscordConfig Cfg() => new ConfigStore(ConfigPath).Get().Discord;
        await WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Granted);
            var login = monitor.Authenticate(Cfg(), discord.Ct);
            await discord.Authenticated(Base);                         // Discord no longer grants it
            await login;
            Assert.False(monitor.NeedsLogin(Cfg()));                   // and is not asked in a loop
            if (restart) return;
            Store(share: false);
            Assert.False(monitor.NeedsLogin(Cfg()));
            Store(share: true);
            Assert.True(monitor.NeedsLogin(Cfg()));
            await Asked(monitor, discord);
        });
        if (!restart) return;
        monitor = Monitor(share: true);                              // the saved login alone has to tell
        await WithDiscord(monitor, discord => Asked(monitor, discord));

        async Task Asked(DiscordMonitor m, FakeDiscord discord)
        {
            var login = m.Authenticate(Cfg(), discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("rpc.screenshare.write", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
            await discord.Answer(ask, """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);                         // declined: the base login goes on
            await login;
            Assert.False(m.NeedsLogin(Cfg()));
        }
    }

    [Fact]
    public Task A_declined_permission_is_asked_for_again_with_another_client_secret()
    {
        var monitor = Monitor(share: true);
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"message":"declined"}""", "ERROR");
            await discord.Authenticated(Base);
            await login;
            Assert.False(monitor.NeedsLogin(cfg));
            Assert.True(monitor.NeedsLogin(Store(share: true, secret: "new").Get().Discord));
        });
    }

    [Fact]
    public Task A_permission_taken_back_while_its_setting_is_off_is_asked_for_when_it_is_turned_on()
    {
        var monitor = Monitor(share: false);
        DiscordConfig Cfg() => new ConfigStore(ConfigPath).Get().Discord;
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base + " rpc.screenshare.read rpc.screenshare.write");
            var login = monitor.Authenticate(Cfg(), discord.Ct);
            await discord.Authenticated(Base);
            await login;
            Assert.False(DiscordMonitor.HasScopes(SavedScope(monitor), "rpc.screenshare.read"));
            Assert.True(DiscordMonitor.HasScopes(SavedScope(monitor), Base.Split(' ')));
            Store(share: true);
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
            Content = new StringContent(new JsonObject { ["access_token"] = "new", ["refresh_token"] = "r2", ["expires_in"] = 600000, ["scope"] = Base + " rpc.screenshare.read rpc.screenshare.write" }.ToJsonString()),
        });
    }

    private static JsonNode Saved(DiscordMonitor m) => JsonNode.Parse(File.ReadAllText(m.TokenPath))!;
    private static string? SavedScope(DiscordMonitor m) => (string?)Saved(m)["Scope"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_new_login_replaces_the_saved_one_only_once_Discord_took_it(bool taken)
    {
        var monitor = Monitor(share: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Base);
            var cfg = new ConfigStore(ConfigPath).Get().Discord;
            var login = monitor.Authenticate(cfg, discord.Ct);
            await discord.Answer(await discord.Request("AUTHORIZE"), """{"code":"c"}""");
            if (taken) await discord.Authenticated(Base + " rpc.screenshare.read rpc.screenshare.write", "new");
            else
            {
                await discord.Answer(await discord.Request("AUTHENTICATE"), """{"message":"invalid token"}""", "ERROR");
                await discord.Authenticated(Base);                     // goes on with the saved login
            }
            await login;
            Assert.Equal(taken ? "new" : "saved", (string?)Saved(monitor)["Access"]);
            Assert.Equal(taken, DiscordMonitor.HasScopes(SavedScope(monitor), "rpc.screenshare.read"));
            Assert.False(monitor.NeedsLogin(cfg));
        });
    }

    [Fact]
    public Task A_new_login_is_saved_with_the_permissions_Discord_granted()
    {
        var monitor = Monitor(share: true);
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
        var monitor = Monitor(share: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            var ask = await discord.Request("AUTHORIZE");
            Assert.Contains("rpc.screenshare.write", ask["args"]!["scopes"]!.AsArray().Select(n => (string?)n));
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
        var monitor = Monitor(share: true);
        monitor.TokenHttp = () => new HttpClient(new FakeTokens());
        return WithDiscord(monitor, async discord =>
        {
            File.WriteAllText(monitor.TokenPath, JsonSerializer.Serialize(new { ClientId = "1", Access = "saved", Refresh = "r", Expires = 0L, Scope = Base + " rpc.screenshare.read rpc.screenshare.write" }));
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            var auth = await discord.Request("AUTHENTICATE");
            Assert.Equal("r2", (string?)Saved(monitor)["Refresh"]);    // the one it replaces may no longer work
            await discord.Answer(auth, """{"user":{"id":"1","username":"me"}}""");
            await login;
        });
    }

    [Fact]
    public async Task Discord_refusing_the_subscription_only_keeps_screen_share_off()
    {
        var monitor = Monitor(share: true);
        await WithDiscord(monitor, async discord =>
        {
            SaveLogin(monitor, Granted);
            var login = monitor.Authenticate(new ConfigStore(ConfigPath).Get().Discord, discord.Ct);
            await discord.Authenticated(Granted);
            await login;
            monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, "500", "Game night", "voice", [], [], []));
            var follow = monitor.FollowShare(discord.Ct);
            await discord.Answer(await discord.Request("SUBSCRIBE"), """{"message":"Invalid event"}""", "ERROR");
            await follow;                                              // no exception: the connection goes on
            await monitor.FollowShare(discord.Ct);                     // and is not asked again every pass
            Assert.True(monitor.Snapshot().Connected);
            var view = Json(monitor.Share(true));
            Assert.False((bool?)view["ready"]);
            Assert.True((bool?)view["refused"]);
            Assert.Equal("notAllowed", Code(await monitor.ShareActAsync("pick", null)));
        });
    }

    [Theory]
    [InlineData("pick")]
    [InlineData("stop")]
    [InlineData("app")]
    public async Task Nothing_is_sent_before_Discord_has_said_whether_you_share(string action)
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, null, async discord =>
        {
            var view = Json(monitor.Share(true));
            Assert.True((bool?)view["ready"]);
            Assert.Null(view["active"]);
            Assert.Equal("unknown", Code(await monitor.ShareActAsync(action, 42)));
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    [Fact]
    public async Task A_listed_app_is_shared_and_stopped()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            Assert.Equal("gone", Code(await monitor.ShareActAsync("app", 42)));       // not listed for the phone yet
            Assert.Equal(2, Json(monitor.Share(true))["apps"]!.AsArray().Count);
            Assert.Equal("gone", Code(await monitor.ShareActAsync("app", 43)));
            var share = monitor.ShareActAsync("app", 42);
            var toggle = await discord.Request("TOGGLE_SCREENSHARE");
            Assert.Equal(42, (int?)toggle["args"]!["pid"]);
            await discord.Answer(toggle);
            await discord.State(Active);
            Assert.Null(await share);
            var view = Json(monitor.Share(true));
            Assert.Equal((true, "Space Racers"), ((bool?)view["active"], (string?)view["name"]));

            Assert.Equal("sharing", Code(await monitor.ShareActAsync("pick", null))); // would stop it
            var stop = monitor.ShareActAsync("stop", null);
            toggle = await discord.Request("TOGGLE_SCREENSHARE");
            Assert.Empty(toggle["args"]!.AsObject());
            await discord.Answer(toggle);
            Assert.Null(await stop);
            Assert.Null(monitor.Sharing);                                             // until Discord says it ended...
            Assert.Null(Json(monitor.Share(true))["active"]);
            foreach (var action in new[] { "stop", "pick", "app" })
                Assert.Equal("unknown", Code(await monitor.ShareActAsync(action, 42))); // ...a second stop would start it again
            await discord.State(Idle);
            await Until(() => monitor.Sharing == false);
            Assert.Null(await monitor.ShareActAsync("stop", null));                   // nothing to stop: nothing sent (it would start one)
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    [Fact]
    public async Task A_stop_whose_state_event_comes_before_its_answer_is_known_at_once()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Active, async discord =>
        {
            var stop = monitor.ShareActAsync("stop", null);
            var toggle = await discord.Request("TOGGLE_SCREENSHARE");
            await discord.State(Idle);
            await discord.Answer(toggle);
            Assert.Null(await stop);
            Assert.False(monitor.Sharing);                             // no other event would come
        });
    }

    // A share ended on the computer just before Stop: Discord's "ended" event comes a moment later, and the toggle
    // would then start a share. Stop waits that moment and sends nothing.
    [Fact]
    public async Task A_stop_right_after_the_share_ended_on_the_computer_sends_nothing()
    {
        var monitor = Monitor(share: true);
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ShareStopWait = () => settle.Task;           // Stop waits here until the late event is in
        await WithShare(monitor, Active, async discord =>
        {
            var stop = monitor.ShareActAsync("stop", null); // still active as far as the agent knows
            await discord.State(Idle);                      // the late event of the share ended on the computer
            for (var i = 0; i < 500 && monitor.Sharing != false; i++) await Task.Delay(10);
            Assert.False(monitor.Sharing);
            settle.SetResult();
            Assert.Null(await stop);                        // it has ended, as asked
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    // A share stopped on the computer while the phone's Stop waited for the pipe: the toggle would start it again.
    [Fact]
    public async Task A_stop_that_waited_for_the_pipe_sends_nothing_once_the_share_has_ended()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Active, async discord =>
        {
            await monitor._send.WaitAsync();          // another request is being written
            var stop = monitor.ShareActAsync("stop", null); // has seen the share and waits for the pipe
            await discord.State(Idle);                // stopped on the computer meanwhile
            await Until(() => monitor.Sharing == false);
            monitor._send.Release();
            Assert.Null(await stop);                  // it has ended, as asked
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    [Fact]
    public async Task A_pick_that_waited_for_the_pipe_sends_nothing_once_control_is_off()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            await monitor._send.WaitAsync();
            var pick = monitor.ShareActAsync("pick", null);
            Store(share: true, control: false);       // the widget's Control turned off meanwhile
            monitor._send.Release();
            Assert.Equal("control", Code(await pick));
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    [Fact]
    public async Task The_picker_leaves_the_state_as_it_was()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            for (var i = 0; i < 2; i++)                                // cancelled: Discord sends no event, and it can be opened again
            {
                var pick = monitor.ShareActAsync("pick", null);
                await discord.Answer(await discord.Request("TOGGLE_SCREENSHARE"));
                Assert.Null(await pick);
                Assert.False(monitor.Sharing);
            }
        });
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"42\"")]
    public async Task The_shared_app_is_named_by_its_pid_and_another_one_does_not_keep_its_name(string pid)
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            monitor.Share(true);
            await discord.State($$$"""{"active":true,"pid":{{{pid}}},"application":{}}""");
            await Until(() => monitor.Sharing == true);
            Assert.Equal("Space Racers", (string?)Json(monitor.Share(true))["name"]);
            // the name goes with the share view only (PRIVACY.md): a state-only answer says just that you share
            Assert.Null(Json(monitor.Share(false))["name"]);
            Assert.True((bool?)Json(monitor.Share(false))["active"]);
            await discord.State("""{"active":true,"pid":999,"application":{}}"""); // changed to a process that isn't listed
            await Until(() => Json(monitor.Share(true))["name"] is null);
        });
    }

    [Theory]
    [InlineData("Space Racers", true)]
    [InlineData("Other", false)]   // the pid belongs to another program by now
    [InlineData(null, false)]      // ended
    public async Task An_old_list_is_read_again_before_an_app_is_shared(string? now, bool shared)
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            monitor.Share(true);
            monitor.ListApps = () => now is null ? [] : [new(42, now)];
            monitor.ShareAppsCacheMs = -1;
            var share = monitor.ShareActAsync("app", 42);
            if (shared)
            {
                await discord.Answer(await discord.Request("TOGGLE_SCREENSHARE"));
                await discord.State(Active);
            }
            Assert.Equal(shared ? null : "gone", Code(await share.WaitAsync(TimeSpan.FromSeconds(2), discord.Ct)));
        });
    }

    [Fact]
    public async Task An_app_is_not_shared_when_control_goes_off_while_the_list_is_read_again()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async discord =>
        {
            monitor.Share(true);
            monitor.ListApps = () => { Store(share: true, control: false); return [new(42, "Space Racers")]; };
            monitor.ShareAppsCacheMs = -1;
            Assert.Equal("control", Code(await monitor.ShareActAsync("app", 42).WaitAsync(TimeSpan.FromSeconds(2), discord.Ct)));
        });
    }

    [Fact]
    public async Task A_share_started_elsewhere_is_named_when_its_app_is_listed_and_not_after_it_ended()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Active, async discord =>
        {
            Assert.Null(Json(monitor.Share(false))["name"]);                          // nothing listed yet
            Assert.Equal("Space Racers", (string?)Json(monitor.Share(true))["name"]);
            await discord.State(Idle);
            await Until(() => monitor.Sharing == false);
            Assert.Null(Json(monitor.Share(true))["name"]);
            await discord.State("""{"active":true,"pid":999,"application":null}"""); // a process that isn't listed
            await Until(() => monitor.Sharing == true);
            Assert.Null(Json(monitor.Share(true))["name"]);
            await discord.State("""{"active":true,"pid":999,"application":{"name":"Other"}}"""); // a name of Discord's own counts first
            await Until(() => (string?)Json(monitor.Share(true))["name"] == "Other");
        });
    }

    [Fact]
    public async Task A_share_already_running_when_the_agent_connects_is_not_stopped_by_choosing()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Active, async discord =>
        {
            Assert.Equal("sharing", Code(await monitor.ShareActAsync("pick", null)));
            var sent = discord.Request("TOGGLE_SCREENSHARE");
            await Task.Delay(150);
            Assert.False(sent.IsCompleted);
        });
    }

    [Fact]
    public async Task An_app_Discord_does_not_share_is_reported_and_holds_up_no_other_button()
    {
        var monitor = Monitor(share: true);
        monitor.ShareStartWait = TimeSpan.FromSeconds(2);
        await WithShare(monitor, Idle, async discord =>
        {
            monitor.Share(true);
            var share = monitor.ShareActAsync("app", 77);
            await discord.Answer(await discord.Request("TOGGLE_SCREENSHARE"));
            Assert.Equal("busy", Code(await monitor.ShareActAsync("pick", null))); // another share action doesn't queue behind it...
            var mute = monitor.ActAsync("mute", null);                              // ...and mute doesn't wait for it
            await discord.Answer(await discord.Request("SET_VOICE_SETTINGS"), """{"mute":true}""");
            Assert.Null(await mute);
            Assert.False(share.IsCompleted);
            Assert.Equal("notDirect", Code(await share));
        });
    }

    [Fact]
    public async Task Starting_needs_a_call()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Idle, async _ => Assert.Equal("noCall", Code(await monitor.ShareActAsync("pick", null))), channel: null);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]   // the global switch is for the voice actions
    [InlineData(true, false, true)]   // Control on, but on a page that is off
    public async Task Without_a_widget_with_control_on_an_enabled_page_nothing_is_listed_or_done(bool control, bool pageEnabled, bool allowControl)
    {
        var monitor = Monitor(share: true);
        var listed = 0;
        monitor.ListApps = () => { listed++; return []; };
        await WithShare(monitor, Idle, async _ =>
        {
            Store(share: true, control, pageEnabled, allowControl);
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(monitor.Share(true)));
            Assert.Equal("control", Code(await monitor.ShareActAsync("pick", null)));
            Assert.Equal(0, listed);
        });
    }

    [Fact]
    public async Task With_the_setting_off_nothing_is_listed_or_done_and_the_state_is_dropped()
    {
        var monitor = Monitor(share: true);
        await WithShare(monitor, Active, async discord =>
        {
            Store(share: false);                                       // the permission is still there: only the switch went off
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(monitor.Share(true)));
            Assert.Equal("off", Code(await monitor.ShareActAsync("stop", null)));
            var follow = monitor.FollowShare(discord.Ct);
            var unsub = await discord.Request("UNSUBSCRIBE");
            Assert.Equal("SCREENSHARE_STATE_UPDATE", (string?)unsub["evt"]);
            await discord.Answer(unsub);
            await follow;
            Assert.Null(monitor.Sharing);
            monitor.OnShareState(JsonNode.Parse(Active));              // one still on its way
            Assert.Null(monitor.Sharing);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Apps_read_while_sharing_or_control_is_turned_off_are_not_sent(bool control, bool share)
    {
        var monitor = Monitor(share: true);
        monitor.ListApps = () => { Store(share, control); return [new(42, "Space Racers")]; };
        await WithShare(monitor, Idle, async _ =>
        {
            Assert.Equal("""{"enabled":false}""", JsonSerializer.Serialize(monitor.Share(true)));
            monitor.ListApps = () => [];
            Store(share: true);
            Assert.Equal("gone", Code(await monitor.ShareActAsync("app", 42))); // nor kept
        });
    }

    [Fact]
    public async Task The_running_apps_are_read_only_when_asked_and_not_at_every_poll()
    {
        var monitor = Monitor(share: true);
        var listed = 0;
        monitor.ListApps = () => { listed++; return [new(42, "Space Racers")]; };
        await WithShare(monitor, Idle, _ =>
        {
            Assert.Null(Json(monitor.Share(false))["apps"]);
            Assert.Equal(0, listed);
            for (var i = 0; i < 5; i++) Assert.Single(Json(monitor.Share(true))["apps"]!.AsArray());
            Assert.Equal(1, listed);
            return Task.CompletedTask;
        });
    }
}
