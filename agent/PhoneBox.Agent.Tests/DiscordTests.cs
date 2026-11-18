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

    private DiscordMonitor Monitor(bool? control, bool pageEnabled = true, bool? notifications = null, bool discordEnabled = true)
    {
        var options = new Dictionary<string, object>();
        if (control is { } c) options["control"] = c;
        if (notifications is { } n) options["notifications"] = n;
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = discordEnabled, clientId = "1", clientSecret = "s" },
            // At least one page must stay enabled (the config repairs an all-disabled list), so a second page holds no widgets.
            pages = new[]
            {
                new { id = "p", title = "P", enabled = pageEnabled, widgets = new object[] { new { id = "d", type = "discord", x = 0, y = 0, w = 4, h = 5, options } } },
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
