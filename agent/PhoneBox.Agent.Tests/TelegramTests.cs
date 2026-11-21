using System.Text.Json;
using PhoneBox.Agent;
using TL;
using Xunit;

/// <summary>Telegram notifications: Telegram's per-chat settings first (muted, silent, previews, sound), then the PhoneBox chat rules.</summary>
public sealed class TelegramTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-telegram-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_configPath); } catch { } }

    private static TelegramConfig Cfg(string vibrate = "double", string sound = "telegram", bool showText = true) =>
        new() { Enabled = true, Vibrate = vibrate, Sound = sound, ShowText = showText };

    private static PeerNotifySettings Settings(DateTime? muteUntil = null, bool? silent = null, bool? previews = null, NotificationSound? sound = null, bool setSound = false)
    {
        var s = new PeerNotifySettings();
        if (muteUntil is { } m) { s.mute_until = m; s.flags |= PeerNotifySettings.Flags.has_mute_until; }
        if (silent is { } q) { s.silent = q; s.flags |= PeerNotifySettings.Flags.has_silent; }
        if (previews is { } p) { s.show_previews = p; s.flags |= PeerNotifySettings.Flags.has_show_previews; }
        if (setSound) { s.other_sound = sound; s.flags |= PeerNotifySettings.Flags.has_other_sound; }
        return s;
    }

    [Fact]
    public void A_chat_with_no_settings_vibrates_and_plays_the_default_tone()
    {
        var d = TelegramMonitor.Decide(Cfg(), null, null, silentMessage: false, mentioned: false, Now);
        Assert.Equal(new TelegramMonitor.Decision("double", "ding", null, true), d);
    }

    [Fact]
    public void A_chat_muted_in_Telegram_stays_quiet_unless_you_are_mentioned_or_the_rule_says_always()
    {
        var muted = Settings(muteUntil: Now.AddHours(1));
        Assert.Null(TelegramMonitor.Decide(Cfg(), null, muted, false, false, Now));
        Assert.NotNull(TelegramMonitor.Decide(Cfg(), null, muted, false, mentioned: true, Now));
        Assert.NotNull(TelegramMonitor.Decide(Cfg(), new TelegramChatRule { Match = "x", Always = true }, muted, false, false, Now));
        // a mute that has run out no longer counts
        Assert.NotNull(TelegramMonitor.Decide(Cfg(), null, Settings(muteUntil: Now.AddMinutes(-1)), false, false, Now));
    }

    [Fact]
    public void Mute_here_hides_a_chat_on_the_phone_only()
    {
        Assert.Null(TelegramMonitor.Decide(Cfg(), new TelegramChatRule { Match = "x", Mute = true }, null, false, false, Now));
    }

    [Fact]
    public void Silent_chats_and_silent_messages_show_without_sound_or_vibration()
    {
        Assert.Equal(new TelegramMonitor.Decision(null, null, null, true), TelegramMonitor.Decide(Cfg(), null, Settings(silent: true), false, false, Now));
        Assert.Equal(new TelegramMonitor.Decision(null, null, null, true), TelegramMonitor.Decide(Cfg(), null, null, silentMessage: true, false, Now));
    }

    [Fact]
    public void Hidden_previews_in_Telegram_or_in_PhoneBox_hide_the_text()
    {
        Assert.False(TelegramMonitor.Decide(Cfg(), null, Settings(previews: false), false, false, Now)!.Value.ShowText);
        Assert.False(TelegramMonitor.Decide(Cfg(showText: false), null, Settings(previews: true), false, false, Now)!.Value.ShowText);
    }

    [Fact]
    public void Telegram_sound_is_followed_none_ringtone_or_default()
    {
        Assert.Null(TelegramMonitor.Decide(Cfg(), null, Settings(sound: new NotificationSoundNone(), setSound: true), false, false, Now)!.Value.Tone);
        var ring = TelegramMonitor.Decide(Cfg(), null, Settings(sound: new NotificationSoundRingtone { id = 42 }, setSound: true), false, false, Now)!.Value;
        Assert.Equal(42, ring.Ringtone);
        Assert.Equal("ding", ring.Tone); // played if the ringtone cannot be fetched
        Assert.Equal("ding", TelegramMonitor.Decide(Cfg(), null, Settings(sound: null, setSound: true), false, false, Now)!.Value.Tone);
    }

    [Fact]
    public void A_chat_rule_overrides_vibration_and_sound()
    {
        var rule = new TelegramChatRule { Match = "Mom", Vibrate = "long", Sound = "rising" };
        Assert.Equal(new TelegramMonitor.Decision("long", "rising", null, true),
            TelegramMonitor.Decide(Cfg(), rule, Settings(sound: new NotificationSoundRingtone { id = 1 }, setSound: true), false, false, Now));
        Assert.Equal(new TelegramMonitor.Decision(null, null, null, true),
            TelegramMonitor.Decide(Cfg(), new TelegramChatRule { Match = "x", Vibrate = "off", Sound = "none" }, null, false, false, Now));
    }

    [Fact]
    public void Chat_settings_win_over_the_default_for_their_kind_field_by_field()
    {
        var chat = Settings(silent: true);
        var kind = Settings(silent: false, previews: false, muteUntil: Now.AddDays(1));
        var merged = TelegramMonitor.Merge(chat, kind);
        Assert.True(merged.silent);
        Assert.False(merged.show_previews);
        Assert.True(merged.flags.HasFlag(PeerNotifySettings.Flags.has_show_previews));
        Assert.Equal(Now.AddDays(1), merged.mute_until);
    }

    [Theory]
    [InlineData("mom", "Mom", null, 5L, true)]
    [InlineData("@news", "News", "news", 5L, true)]
    [InlineData("news", "Other", "news", 5L, true)]
    [InlineData("5", "Chat", null, 5L, true)]
    [InlineData("-1000000000005", "Chat", null, 5L, true)]
    [InlineData("Mo", "Mom", null, 5L, false)]
    public void Rules_match_the_name_username_or_id(string match, string name, string? username, long id, bool hit)
    {
        var rule = new TelegramChatRule { Match = match };
        Assert.Equal(hit, TelegramMonitor.FindRule([rule], name, username, id) is not null);
    }

    [Fact]
    public void Short_updates_arrive_as_new_messages()
    {
        // WTelegramClient turns updateShortMessage (a private message sent without the full update) into UpdateNewMessage;
        // the monitor relies on that to see every incoming private message.
        var shortMsg = new UpdateShortMessage { id = 1, user_id = 7, message = "hi", date = Now };
        var m = Assert.IsType<Message>(Assert.IsAssignableFrom<UpdateNewMessage>(Assert.Single(shortMsg.UpdateList)).message);
        Assert.Equal("hi", m.message);
        Assert.Equal(7, m.peer_id.ID);
        Assert.True(typeof(UpdateNewMessage).IsAssignableFrom(typeof(UpdateNewChannelMessage)));
    }

    [Fact]
    public void Settings_are_cleaned_up_on_load()
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            telegram = new
            {
                enabled = true, apiId = 0, apiHash = "  abc ", phone = " +380 ", vibrate = "buzz", sound = "siren", volume = 500,
                chats = new object[] { new { match = "  Mom ", vibrate = "long", sound = "nope" }, new { match = " " } },
            },
        }));
        var t = new ConfigStore(_configPath).Get().Telegram;
        Assert.Null(t.ApiId);
        Assert.Equal("abc", t.ApiHash);
        Assert.Equal("+380", t.Phone);
        Assert.Equal("double", t.Vibrate);
        Assert.Equal("telegram", t.Sound);
        Assert.Equal(100, t.Volume);
        var rule = Assert.Single(t.Chats);
        Assert.Equal("Mom", rule.Match);
        Assert.Equal("long", rule.Vibrate);
        Assert.Null(rule.Sound);
    }

    [Fact]
    public void Telegram_settings_never_reach_the_phone()
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new { telegram = new { enabled = true, apiId = 1, apiHash = "secret-hash", phone = "+380" } }));
        var json = JsonSerializer.Serialize(new ConfigStore(_configPath).Get().ToPublic());
        Assert.DoesNotContain("secret-hash", json);
        Assert.DoesNotContain("telegram", json, StringComparison.OrdinalIgnoreCase);
    }
}
