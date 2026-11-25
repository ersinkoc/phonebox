using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Discord widget's guards: control and notifications follow the widget's options, images only for what the agent reported, PING is answered.</summary>
public sealed class DiscordTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-discord-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_configPath); } catch { } }

    private DiscordMonitor Monitor(bool? control, bool pageEnabled = true, bool? notifications = null, bool discordEnabled = true, bool allowControl = false,
        string type = "discord", bool chat = false, bool screenShare = false)
    {
        var options = new Dictionary<string, object>();
        if (control is { } c) options["control"] = c;
        if (notifications is { } n) options["notifications"] = n;
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = discordEnabled, clientId = "1", clientSecret = "s", allowControl, chat, screenShare },
            // At least one page must stay enabled (the config repairs an all-disabled list), so a second page holds no widgets.
            pages = new[]
            {
                new { id = "p", title = "P", enabled = pageEnabled, widgets = new object[] { new { id = "d", type, x = 0, y = 0, w = 4, h = 5, options } } },
                new { id = "q", title = "Q", enabled = true, widgets = Array.Empty<object>() },
            },
        }));
        return new DiscordMonitor(new ConfigStore(_configPath), NullLogger<DiscordMonitor>.Instance);
    }

    private AgentConfig Config() => new ConfigStore(_configPath).Get();

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Actions_are_refused_unless_control_is_on(bool? control)
    {
        var error = await Monitor(control).ActAsync("mute", null);
        Assert.Contains("turned off", error);
    }

    [Fact]
    public async Task Actions_need_a_connection_even_with_control_on()
    {
        var error = await Monitor(true).ActAsync("leave", null);
        Assert.Equal("Discord is not connected", error);
    }

    [Fact]
    public async Task A_widget_on_a_disabled_page_does_not_allow_control()
    {
        var error = await Monitor(control: true, pageEnabled: false).ActAsync("mute", null);
        Assert.Contains("turned off", error);
        Assert.False(DiscordMonitor.ControlAllowed(Config()));
    }

    [Fact]
    public void Control_is_allowed_for_an_enabled_page_with_the_option_on()
    {
        Monitor(control: true);
        Assert.True(DiscordMonitor.ControlAllowed(Config()));
    }

    // ---- the current-channel bar (discord-bar): its Control allows only what it shows ----

    [Fact]
    public async Task The_bar_allows_mute_deafen_and_leave_and_nothing_else()
    {
        var monitor = Monitor(control: true, type: "discord-bar");
        foreach (var action in new[] { "mute", "deafen", "leave" }) Assert.True(DiscordMonitor.ControlAllowed(Config(), action), action);
        foreach (var action in new[] { "join", "inputmode" }) Assert.False(DiscordMonitor.ControlAllowed(Config(), action), action);
        Assert.Contains("turned off", await monitor.ActAsync("join", "c1"));
        Assert.Contains("turned off", await monitor.ActAsync("inputmode", null));
        // allowed, so the press gets as far as needing a connection
        Assert.Equal("Discord is not connected", await monitor.ActAsync("mute", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void The_bar_without_its_option_allows_nothing(bool? control)
    {
        Monitor(control, type: "discord-bar");
        Assert.False(DiscordMonitor.ControlAllowed(Config()));
        Assert.False(DiscordMonitor.ControlAllowed(Config(), "mute"));
    }

    [Fact]
    public void The_bar_on_a_disabled_page_allows_nothing()
    {
        Monitor(control: true, pageEnabled: false, type: "discord-bar");
        Assert.False(DiscordMonitor.ControlAllowed(Config(), "mute"));
        Assert.False(DiscordMonitor.BarControl(Config()));
    }

    [Fact]
    public void The_bar_does_not_open_screen_share_the_app_list_or_the_chat()
    {
        Monitor(control: true, type: "discord-bar", chat: true, screenShare: true);
        var cfg = Config();
        Assert.False(DiscordMonitor.WidgetControl(cfg));      // "Open in Discord" and starting a share
        Assert.False(DiscordMonitor.ShareWanted(cfg));        // the app list
        Assert.True(DiscordMonitor.ShareStateWanted(cfg));    // only whether you share, for Live and Stop
        Assert.False(DiscordMonitor.ChatWanted(cfg));         // no chat is read for a bar
    }

    [Fact]
    public async Task The_bar_may_stop_a_share_but_not_start_one()
    {
        var monitor = Monitor(control: true, type: "discord-bar", screenShare: true);
        Assert.Equal("control", (await monitor.ShareActAsync("pick", null))?.Code);
        Assert.Equal("control", (await monitor.ShareActAsync("app", 42))?.Code);
        // past the control check: here it stops at Discord not having allowed screen share yet
        Assert.NotEqual("control", (await monitor.ShareActAsync("stop", null))?.Code);
    }

    [Fact]
    public void The_Discord_widget_and_the_global_switch_still_allow_every_action()
    {
        Monitor(control: true);
        Assert.True(DiscordMonitor.ControlAllowed(Config(), "join"));
        Assert.True(DiscordMonitor.ControlAllowed(Config(), "inputmode"));
        Monitor(control: false, type: "discord-bar", allowControl: true);
        Assert.True(DiscordMonitor.ControlAllowed(Config(), "join"));
    }

    // ---- the bar inside a Smart Stack: an enabled card counts like a widget on the page ----

    private void StackWithBar(bool cardEnabled, bool pageEnabled)
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = true, clientId = "1", clientSecret = "s", screenShare = true },
            pages = new[]
            {
                new { id = "p", title = "P", enabled = pageEnabled, widgets = new object[] { new { id = "s", type = "stack", x = 0, y = 0, w = 6, h = 2, options = new
                {
                    widgets = new object[] { new { id = "n", type = "note", options = new { text = "front" } }, new { id = "b", type = "discord-bar", enabled = cardEnabled, options = new { control = true } } },
                } } } },
                new { id = "q", title = "Q", enabled = true, widgets = Array.Empty<object>() },
            },
        }));
    }

    [Fact]
    public async Task A_bar_card_in_an_enabled_stack_allows_what_the_bar_does()
    {
        StackWithBar(cardEnabled: true, pageEnabled: true);
        var cfg = Config();
        Assert.True(DiscordMonitor.BarControl(cfg));
        foreach (var action in DiscordMonitor.BarActions) Assert.True(DiscordMonitor.ControlAllowed(cfg, action), action);
        Assert.False(DiscordMonitor.ControlAllowed(cfg, "join"));
        Assert.True(DiscordMonitor.ShareStateWanted(cfg));
        var monitor = new DiscordMonitor(new ConfigStore(_configPath), NullLogger<DiscordMonitor>.Instance);
        Assert.NotEqual("control", (await monitor.ShareActAsync("stop", null))?.Code);
        Assert.Equal("control", (await monitor.ShareActAsync("pick", null))?.Code);
    }

    [Theory]
    [InlineData(false, true)]  // the card is switched off in the stack
    [InlineData(true, false)]  // the stack is on a page that is switched off
    public async Task A_bar_card_that_is_off_allows_nothing(bool cardEnabled, bool pageEnabled)
    {
        StackWithBar(cardEnabled, pageEnabled);
        var cfg = Config();
        Assert.False(DiscordMonitor.BarControl(cfg));
        Assert.False(DiscordMonitor.ControlAllowed(cfg, "mute"));
        Assert.False(DiscordMonitor.ShareStateWanted(cfg));
        var monitor = new DiscordMonitor(new ConfigStore(_configPath), NullLogger<DiscordMonitor>.Instance);
        Assert.Equal("control", (await monitor.ShareActAsync("stop", null))?.Code);
    }

    // ---- the global "allow control" switch (for shortcuts and custom widgets) ----

    [Fact]
    public async Task The_global_switch_allows_control_without_a_widget_option()
    {
        var monitor = Monitor(control: null, allowControl: true);
        Assert.True(DiscordMonitor.ControlAllowed(Config()));
        // allowed, so the press gets as far as needing a connection
        Assert.Equal("Discord is not connected", await monitor.ActAsync("mute", null));
    }

    [Fact]
    public async Task The_global_switch_also_works_with_the_widget_on_a_hidden_page()
    {
        var monitor = Monitor(control: false, pageEnabled: false, allowControl: true);
        Assert.Equal("Discord is not connected", await monitor.ActAsync("leave", null));
    }

    [Fact]
    public void The_global_switch_is_off_by_default()
    {
        File.WriteAllText(_configPath, """{"discord":{"enabled":true,"clientId":"1","clientSecret":"s"}}""");
        var cfg = Config();
        Assert.False(cfg.Discord.AllowControl);
        Assert.False(DiscordMonitor.ControlAllowed(cfg));
    }

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void The_default_settings_ship_with_the_switch_off(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, file);
        File.Copy(path, _configPath, overwrite: true);
        var cfg = Config();
        Assert.False(cfg.Discord.AllowControl);
        Assert.Contains("\"allowControl\": false", File.ReadAllText(path));
    }

    [Fact]
    public void Snapshot_starts_disconnected_and_empty()
    {
        var snap = Monitor(true).Snapshot();
        Assert.False(snap.Connected);
        Assert.Empty(snap.Channels);
        Assert.Empty(snap.Notifications);
    }

    // ---- mute / deafen state ----

    private static DiscordSnapshot Polled() => new(true, null, "me", false, false, null, null, null, [], [], []);

    [Fact]
    public void A_poll_that_started_before_a_button_press_does_not_overwrite_its_state()
    {
        var monitor = Monitor(true);
        monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled());   // connected, unmuted
        var rev = monitor.VoiceRevision;                                       // a poll reads mute=false here...
        monitor.ApplyVoice(JsonNode.Parse("""{"mute":true,"deaf":false}"""));  // ...then a press mutes
        monitor.PublishPoll(rev, false, false, Polled());                      // ...and the old poll finishes
        Assert.True(monitor.Snapshot().Muted);
        monitor.PublishPoll(monitor.VoiceRevision, true, false, Polled());     // the next poll agrees
        Assert.True(monitor.Snapshot().Muted);
    }

    [Fact]
    public void A_poll_started_after_the_press_is_trusted()
    {
        var monitor = Monitor(true);
        monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled());
        monitor.ApplyVoice(JsonNode.Parse("""{"mute":true}"""));
        monitor.PublishPoll(monitor.VoiceRevision, false, true, Polled());     // the user unmuted in the Discord app itself
        Assert.False(monitor.Snapshot().Muted);
        Assert.True(monitor.Snapshot().Deafened);
    }

    // ---- input mode (voice activity / push to talk) ----

    [Theory]
    [InlineData("""{"mode":{"type":"VOICE_ACTIVITY","shortcut":[]}}""", "VOICE_ACTIVITY", false)]
    [InlineData("""{"mode":{"type":"PUSH_TO_TALK","shortcut":[{"type":0,"code":192,"name":"`"}]}}""", "PUSH_TO_TALK", true)]
    [InlineData("""{"mode":{"type":"PUSH_TO_TALK"}}""", "PUSH_TO_TALK", false)]
    [InlineData("""{"mode":{"type":"SOMETHING_NEW","shortcut":[]}}""", null, false)]
    [InlineData("""{"mute":true}""", null, false)]
    [InlineData("""{"mode":{"type":7}}""", null, false)]
    public void The_input_mode_and_ptt_key_are_read_from_the_voice_settings(string json, string? mode, bool pttKey)
    {
        var (m, key) = DiscordMonitor.InputModeOf(JsonNode.Parse(json));
        Assert.Equal(mode, m);
        Assert.Equal(pttKey, key);
    }

    [Fact]
    public void Switching_the_input_mode_shows_at_once_and_an_older_poll_does_not_undo_it()
    {
        var monitor = Monitor(true);
        monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled() with { InputMode = "VOICE_ACTIVITY", PttKey = true });
        var rev = monitor.VoiceRevision;                                           // a poll reads voice activity here...
        monitor.ApplyVoice(JsonNode.Parse("""{"mode":{"type":"PUSH_TO_TALK","shortcut":[{"type":0,"code":192}]}}"""));
        Assert.Equal("PUSH_TO_TALK", monitor.Snapshot().InputMode);
        monitor.PublishPoll(rev, false, false, Polled() with { InputMode = "VOICE_ACTIVITY", PttKey = true }); // ...and finishes late
        Assert.Equal("PUSH_TO_TALK", monitor.Snapshot().InputMode);
        Assert.True(monitor.Snapshot().PttKey);
    }

    [Fact]
    public void A_mute_answer_without_a_mode_keeps_the_input_mode()
    {
        var monitor = Monitor(true);
        monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled() with { InputMode = "PUSH_TO_TALK", PttKey = false });
        monitor.ApplyVoice(JsonNode.Parse("""{"mute":true}"""));
        Assert.Equal("PUSH_TO_TALK", monitor.Snapshot().InputMode);
        Assert.False(monitor.Snapshot().PttKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Switching_the_input_mode_needs_control_like_the_other_buttons(bool? control)
    {
        var error = await Monitor(control).ActAsync("inputmode", null);
        Assert.Contains("turned off", error);
    }

    [Fact]
    public async Task Switching_the_input_mode_needs_a_connection()
    {
        Assert.Equal("Discord is not connected", await Monitor(true).ActAsync("inputmode", null));
    }

    // ---- reconnecting ----

    [Fact]
    public void Reconnects_back_off_from_5_to_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DiscordMonitor.Backoff(0));
        Assert.Equal(TimeSpan.FromSeconds(5), DiscordMonitor.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(10), DiscordMonitor.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(20), DiscordMonitor.Backoff(3));
        Assert.Equal(TimeSpan.FromSeconds(30), DiscordMonitor.Backoff(4));
        Assert.Equal(TimeSpan.FromSeconds(30), DiscordMonitor.Backoff(40));
    }

    // ---- notifications ----

    [Theory]
    [InlineData(null, true, true)]     // the option defaults to on
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]   // a widget on a disabled page does not count
    public void Notifications_are_wanted_only_while_an_enabled_widget_shows_them(bool? option, bool pageEnabled, bool expected)
    {
        Monitor(false, pageEnabled, option);
        Assert.Equal(expected, DiscordMonitor.NotificationsWanted(Config()));
    }

    private static byte[] Frame(int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), body.Length);
        body.CopyTo(buf, 8);
        return buf;
    }

    private static async Task<(int Op, string Body)> ReadFrame(Stream s, CancellationToken ct = default)
    {
        var head = new byte[8];
        await s.ReadExactlyAsync(head, ct);
        var body = new byte[BitConverter.ToInt32(head, 4)];
        await s.ReadExactlyAsync(body, ct);
        return (BitConverter.ToInt32(head, 0), Encoding.UTF8.GetString(body));
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> Pipe()
    {
        var name = "pbd-" + Guid.NewGuid().ToString("N")[..12]; // macOS puts the socket under $TMPDIR, and its path may be at most 104 characters
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        return (server, client);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_PING_from_Discord_is_answered_with_a_PONG_carrying_the_same_payload()
    {
        var monitor = Monitor(false);
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            await server.WriteAsync(Frame(3, """{"nonce":"abc"}"""), cts.Token);
            var (op, body) = await ReadFrame(server);
            Assert.Equal(4, op);
            Assert.Equal("abc", JsonNode.Parse(body)!["nonce"]!.GetValue<string>());
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task A_request_still_waiting_ends_with_no_answer_when_Discord_closes_the_connection()
    {
        // the login prompt waits up to two minutes; when Discord quits meanwhile it must end at once as "no answer"
        // (a dropped connection, retried with back-off), not time out later and count as the user refusing
        var monitor = Monitor(false);
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor._pending["login"] = waiting;
        await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            await server.DisposeAsync();
            await loop.WaitAsync(cts.Token);
            Assert.True(waiting.Task.IsCompletedSuccessfully);
            Assert.Null(await waiting.Task);
        }
    }

    [Fact]
    public async Task Turning_notifications_off_hides_their_text_at_once()
    {
        var monitor = Monitor(false);
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            monitor.NotificationsOn = true;
            await server.WriteAsync(Frame(1, """{"evt":"NOTIFICATION_CREATE","data":{"title":"Ana","body":"secret DM"}}"""), cts.Token);
            await Until(() => monitor.Snapshot().Notifications.Count == 1);
            Monitor(false, notifications: false);  // the widget's notifications are turned off in the settings
            // a write time of its own, so the store sees the change however coarse the file system's clock is
            File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddMinutes(1));
            // the monitor's own pass never runs in this test, so the text going away can only come from Snapshot
            // reading the settings itself
            await Until(() => monitor.Snapshot().Notifications.Count == 0);
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Notification_text_is_dropped_unless_notifications_are_on()
    {
        var monitor = Monitor(false);
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            var note = """{"evt":"NOTIFICATION_CREATE","data":{"title":"Ana","body":"secret DM"}}""";
            await server.WriteAsync(Frame(1, note), cts.Token);
            await server.WriteAsync(Frame(3, "{}"), cts.Token);        // once this is answered, the note before it was handled
            await ReadFrame(server);
            monitor.NotificationsOn = true;
            Assert.Empty(monitor.Snapshot().Notifications);             // dropped, not stored for later
            await server.WriteAsync(Frame(1, note), cts.Token);
            await Until(() => monitor.Snapshot().Notifications.Count == 1);
            monitor.NotificationsOn = false;
            Assert.Empty(monitor.Snapshot().Notifications);             // not sent to the phone while off
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    // ---- who is speaking ----

    private static DiscordSnapshot InRoom(string channel = "c1") => new(true, null, "me", false, false, channel, "Room", "voice",
        [new("7", "Ana", false, false, null), new("8", "Bo", false, false, null)], [], []);

    private static string Speaking(string evt, string user, string? channel = "c1") =>
        new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = evt, ["data"] = new JsonObject { ["user_id"] = user, ["channel_id"] = channel } }.ToJsonString();

    private static bool IsSpeaking(DiscordMonitor m, string id) => m.Snapshot().Members.Single(x => x.Id == id).Speaking;

    [Fact]
    public async Task A_member_speaks_between_SPEAKING_START_and_SPEAKING_STOP()
    {
        var monitor = Monitor(false);
        monitor.PublishPoll(0, false, false, InRoom());
        monitor.SpeakingChannel = "c1";
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            await server.WriteAsync(Frame(1, Speaking("SPEAKING_START", "7")), cts.Token);
            await Until(() => IsSpeaking(monitor, "7"));
            Assert.False(IsSpeaking(monitor, "8"));
            await server.WriteAsync(Frame(1, Speaking("SPEAKING_STOP", "7")), cts.Token);
            await Until(() => !IsSpeaking(monitor, "7"));
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void Speaking_events_from_another_channel_or_outside_a_channel_are_ignored()
    {
        var monitor = Monitor(false);
        monitor.PublishPoll(0, false, false, InRoom());
        monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7","channel_id":"c1"}"""));
        Assert.False(IsSpeaking(monitor, "7"));                                   // not following a channel yet
        monitor.SpeakingChannel = "c1";
        monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7","channel_id":"c2"}"""));
        Assert.False(IsSpeaking(monitor, "7"));                                   // late event from the channel left
        monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7"}"""));
        Assert.True(IsSpeaking(monitor, "7"));                                    // older Discord builds send no channel id
    }

    [Fact]
    public void Switching_channels_forgets_who_was_speaking()
    {
        var monitor = Monitor(false);
        monitor.PublishPoll(0, false, false, InRoom());
        monitor.SpeakingChannel = "c1";
        monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7","channel_id":"c1"}"""));
        Assert.True(IsSpeaking(monitor, "7"));
        monitor.SpeakingChannel = "c2";                                           // you moved; no STOP will come from c1
        monitor.SpeakingChannel = "c1";                                           // and back again
        Assert.False(IsSpeaking(monitor, "7"));
    }

    [Fact]
    public void A_snapshot_of_another_channel_shows_no_one_speaking()
    {
        var monitor = Monitor(false);
        monitor.SpeakingChannel = "c1";
        monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7","channel_id":"c1"}"""));
        monitor.PublishPoll(0, false, false, InRoom("c2"));                       // the poll already sees the new channel
        Assert.False(IsSpeaking(monitor, "7"));
    }

    [Fact]
    public async Task The_answer_to_a_SUBSCRIBE_for_speaking_events_still_reaches_the_request()
    {
        var monitor = Monitor(false);
        monitor.SpeakingChannel = "c1";
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor._pending["sub"] = waiting;
        await using (server) await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            await server.WriteAsync(Frame(1, """{"cmd":"SUBSCRIBE","evt":"SPEAKING_START","nonce":"sub","data":{"evt":"SPEAKING_START"}}"""), cts.Token);
            Assert.Equal("SPEAKING_START", (await waiting.Task.WaitAsync(cts.Token))!["evt"]!.GetValue<string>());
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    // Answers the next request from the agent, after first sending a SPEAKING_START that arrives while it is pending.
    private static async Task AnswerWithStartFirst(Stream server, string cmd, bool ok, CancellationToken ct)
    {
        var req = JsonNode.Parse((await ReadFrame(server, ct)).Body)!;
        Assert.Equal(cmd, req["cmd"]!.GetValue<string>());
        await server.WriteAsync(Frame(1, Speaking("SPEAKING_START", "7", "c2")), ct);
        var answer = new JsonObject { ["cmd"] = cmd, ["nonce"] = req["nonce"]!.GetValue<string>(), ["evt"] = ok ? req["evt"]!.GetValue<string>() : "ERROR",
            ["data"] = ok ? new JsonObject() : new JsonObject { ["message"] = "refused" } };
        await server.WriteAsync(Frame(1, answer.ToJsonString()), ct);
    }

    [Fact]
    public async Task Speaking_events_count_only_once_both_subscriptions_are_in_place()
    {
        var monitor = Monitor(false);
        monitor.PublishPoll(0, false, false, InRoom("c2"));
        monitor.SpeakingChannel = "c1";
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            monitor._io = client;
            var loop = monitor.ReadLoop(client, cts.Token);
            var follow = monitor.FollowSpeaking("c1", "c2", cts.Token);
            // a START during the switch is dropped: with only SPEAKING_START in place, its STOP might never come
            foreach (var cmd in new[] { "UNSUBSCRIBE", "UNSUBSCRIBE", "SUBSCRIBE", "SUBSCRIBE" }) await AnswerWithStartFirst(server, cmd, true, cts.Token);
            Assert.True(await follow);
            Assert.False(IsSpeaking(monitor, "7"));
            await server.WriteAsync(Frame(1, Speaking("SPEAKING_START", "7", "c2")), cts.Token);
            await Until(() => IsSpeaking(monitor, "7"));
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task After_a_refused_subscription_no_speaking_event_counts()
    {
        var monitor = Monitor(false);
        monitor.PublishPoll(0, false, false, InRoom("c2"));
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server) await using (client)
        {
            monitor._io = client;
            var loop = monitor.ReadLoop(client, cts.Token);
            var follow = monitor.FollowSpeaking(null, "c2", cts.Token);
            await AnswerWithStartFirst(server, "SUBSCRIBE", true, cts.Token);
            await AnswerWithStartFirst(server, "SUBSCRIBE", false, cts.Token);           // SPEAKING_STOP refused
            Assert.False(await follow);
            Assert.Null(monitor.SpeakingChannel);
            monitor.ApplySpeaking("SPEAKING_START", JsonNode.Parse("""{"user_id":"7","channel_id":"c2"}"""));
            Assert.False(IsSpeaking(monitor, "7"));
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    // ---- images ----

    [Theory]
    [InlineData("bad", "1", "abc")]          // unknown kind
    [InlineData("guild", "../1", "abc")]     // id must be digits only
    [InlineData("guild", "", "abc")]
    [InlineData("guild", "1", "a/b")]        // hash must be letters, digits or underscore
    [InlineData("user", "1", "")]
    [InlineData("user", "123456789012345678901234567890", "abc")]
    public async Task Image_requests_with_unsafe_ids_are_rejected_without_a_network_call(string kind, string id, string hash)
    {
        Assert.Null(await Monitor(false).ImageAsync(kind, id, hash, CancellationToken.None));
    }

    private static DiscordSnapshot WithKnownImages() => new(true, null, "me", false, false, "c1", "Room", "voice",
        [new("7", "Ana", false, false, "avatar1")], [new("c1", "Room", "Server", "42", "icon1")], []);

    [Fact]
    public async Task Images_are_refused_while_discord_is_off()
    {
        var monitor = Monitor(false, discordEnabled: false);
        monitor.PublishPoll(0, false, false, WithKnownImages());
        Assert.True(monitor.IsKnownImage("guild", "42", "icon1"));       // known to the snapshot, but still refused
        Assert.Null(await monitor.ImageAsync("guild", "42", "icon1", CancellationToken.None));
    }

    [Fact]
    public async Task Only_icons_and_avatars_from_the_current_snapshot_are_served()
    {
        var monitor = Monitor(false);
        Assert.False(monitor.IsKnownImage("guild", "42", "icon1"));       // nothing reported yet
        monitor.PublishPoll(0, false, false, WithKnownImages());
        Assert.True(monitor.IsKnownImage("guild", "42", "icon1"));
        Assert.True(monitor.IsKnownImage("user", "7", "avatar1"));
        Assert.False(monitor.IsKnownImage("guild", "42", "other"));       // right server, a hash the agent did not report
        Assert.False(monitor.IsKnownImage("guild", "43", "icon1"));
        Assert.False(monitor.IsKnownImage("user", "42", "icon1"));        // a server id is not a user id
        Assert.Null(await monitor.ImageAsync("guild", "43", "icon1", CancellationToken.None));
    }

    [Fact]
    public async Task TokenPathFollowsConfigDirectoryAndReadLoopSurvivesMalformedFramesAndClearsOnClose()
    {
        var monitor = Monitor(false);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configPath))!, "discord-token.json"), monitor.TokenPath);

        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // both ends scoped, and the deadline on every read: a missing PONG fails the test instead of hanging it
        await using (server)
        await using (client)
        {
            monitor.NotificationsOn = true;
            monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled());
            var loop = monitor.ReadLoop(client, cts.Token);
            await server.WriteAsync(Frame(1, "[1,2,3]"), cts.Token);
            await server.WriteAsync(Frame(1, """{"nonce":123,"evt":456}"""), cts.Token);
            await server.WriteAsync(Frame(3, """{"nonce":"still-alive"}"""), cts.Token);
            var (op, body) = await ReadFrame(server, cts.Token);
            Assert.Equal(4, op);
            Assert.Contains("still-alive", body);

            await server.DisposeAsync();
            await loop.WaitAsync(cts.Token);
            Assert.False(monitor.Snapshot().Connected);
        }
    }

    // The last poll reply can arrive just before Discord closes the pipe; the poll that was waiting for it finishes
    // after ReadLoop has cleared the snapshot and must not mark the closed session connected again.
    [Fact]
    public async Task A_poll_finishing_after_the_connection_closed_does_not_bring_it_back()
    {
        var monitor = Monitor(false);
        var (server, client) = await Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (server)
        await using (client)
        {
            var loop = monitor.ReadLoop(client, cts.Token);
            monitor.PublishPoll(monitor.VoiceRevision, false, false, Polled());
            Assert.True(monitor.Snapshot().Connected);
            var rev = monitor.VoiceRevision;                 // a poll reads its answers here...
            await server.DisposeAsync();                     // ...Discord closes the pipe...
            await loop.WaitAsync(cts.Token);
            monitor.PublishPoll(rev, false, false, Polled()); // ...and the poll publishes what it read
            Assert.False(monitor.Snapshot().Connected);
        }
    }

    [Fact]
    public void Authorize_asks_for_a_code_without_naming_a_redirect()
    {
        // over IPC Discord refuses redirect_uri (#338, #343); it uses the application's first registered redirect
        var args = DiscordMonitor.AuthorizeArgs("123");
        Assert.Equal("123", args["client_id"]!.GetValue<string>());
        Assert.Equal("code", args["response_type"]!.GetValue<string>());
        Assert.False(args.ContainsKey("redirect_uri"));
        Assert.Contains("rpc", args["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    // An application without a saved redirect gets "Missing redirect_uri" from AUTHORIZE (#316): say where to add it.
    [Fact]
    public void A_refusal_for_a_missing_redirect_says_where_to_add_it()
    {
        var missing = DiscordMonitor.Refusal("OAuth2 Error: invalid_request: Missing \"redirect_uri\" in request.");
        Assert.Contains("Missing \"redirect_uri\"", missing);
        Assert.Contains("http://127.0.0.1 under OAuth2 › Redirects", missing);
        Assert.Equal("Discord login was refused (OAuth2 Error: access_denied).", DiscordMonitor.Refusal("OAuth2 Error: access_denied"));
    }

    // --config with a settings file named like the login file: saving a login must not replace the settings.
    [Fact]
    public void The_login_file_never_is_the_settings_file()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-discord-token-").FullName;
        try
        {
            var settings = Path.Combine(dir, "discord-token.json");
            File.WriteAllText(settings, "{}");
            var monitor = new DiscordMonitor(new ConfigStore(settings), NullLogger<DiscordMonitor>.Instance);
            Assert.NotEqual(Path.GetFullPath(settings), Path.GetFullPath(monitor.TokenPath), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(dir, Path.GetDirectoryName(Path.GetFullPath(monitor.TokenPath)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
