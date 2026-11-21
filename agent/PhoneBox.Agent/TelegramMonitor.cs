using System.Collections.Concurrent;
using System.Text.Json;
using TL;
using Message = TL.Message;
using User = TL.User;

namespace PhoneBox.Agent;

/// <summary>What the settings page shows about the Telegram login. Local only: never sent to the phone.</summary>
/// <param name="State">off | setup | signed-out | code | password | connecting | connected | error</param>
public sealed record TelegramStatus(string State, string? Error, string? User, IReadOnlyList<string> RecentChats);

/// <summary>
/// The user's own Telegram account over MTProto (WTelegramClient), signed in as one more device. New messages become
/// notifications on the phone, following each chat's notification settings in Telegram (muted, silent, show previews
/// and its sound, a custom ringtone too), so they work without Windows notifications for Telegram Desktop. On top of
/// that, Settings › General › Telegram can give a chat its own vibration and sound, mute it on the phone only, or let it
/// through while it is muted in Telegram. Nothing is marked as read and nothing is sent. The login is kept in
/// telegram.session next to the settings (encrypted with the api_hash, owner-only on macOS and Linux).
/// </summary>
public sealed class TelegramMonitor : BackgroundService
{
    internal static readonly HashSet<string> Vibrations = ["off", "short", "double", "long", "triple", "heartbeat"];
    internal static readonly HashSet<string> Tones = ["ding", "chime", "pop", "rising", "knock"];
    internal static bool IsSound(string? s) => s is "telegram" or "none" || s is not null && Tones.Contains(s);

    private const string DefaultTone = "ding";
    private const int MaxSoundBytes = 1024 * 1024;
    private static readonly TimeSpan NotifyCacheFor = TimeSpan.FromMinutes(10);

    private readonly ConfigStore _config;
    private readonly PushStore _push;
    private readonly ILogger<TelegramMonitor> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WTelegram.Client? _client;
    private Stream? _sessionStream;
    private (int, string, string)? _clientFor;
    private volatile bool _broken;

    private volatile string _state = "off";
    private volatile string? _error;
    private volatile string? _user;

    private readonly ConcurrentDictionary<long, User> _users = new();
    private readonly ConcurrentDictionary<long, ChatBase> _chats = new();
    private readonly ConcurrentDictionary<string, (PeerNotifySettings Settings, DateTime At)> _notify = new();
    private readonly ConcurrentDictionary<long, (byte[] Data, string Type)> _sounds = new();
    private readonly LinkedList<string> _recent = new();

    public TelegramMonitor(ConfigStore config, PushStore push, ILogger<TelegramMonitor> log)
    {
        _config = config; _push = push; _log = log;
        // WTelegramClient writes its own log to the console by default; keep only problems, in the agent's log.
        WTelegram.Helpers.Log = (level, text) => { if (level >= 3) log.LogDebug("telegram: {Text}", text); };
    }

    public TelegramStatus Status()
    {
        string[] recent;
        lock (_recent) recent = [.. _recent];
        return new(_state, _error, _user, recent);
    }

    /// <summary>
    /// telegram.session next to the settings file; a settings file that is itself named so (--config) gets a sidecar
    /// named after it instead, so saving the login can never replace the settings.
    /// </summary>
    internal string SessionPath
    {
        get
        {
            var settings = Path.GetFullPath(_config.Path);
            var path = Path.Combine(Path.GetDirectoryName(settings) ?? AppPaths.ConfigDir, "telegram.session");
            return string.Equals(path, settings, StringComparison.OrdinalIgnoreCase) ? settings + ".telegram.session" : path;
        }
    }

    private bool HasSession => File.Exists(SessionPath) && new FileInfo(SessionPath).Length > 0;

    private static (int, string, string)? KeyOf(TelegramConfig c) =>
        c.ApiId is { } id && c.ApiHash is { } hash && c.Phone is { } phone ? (id, hash, phone) : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Pass(ct); failures = 0; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures++;
                _state = "error"; _error = UserError(ex);
                _log.LogInformation("telegram: {Msg}", _error);
                await _gate.WaitAsync(ct);
                try { DropClient(); } finally { _gate.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, Math.Min(5, failures - 1)))), ct);
                continue;
            }
            await Task.Delay(2000, ct);
        }
        await _gate.WaitAsync(CancellationToken.None);
        try { DropClient(); } finally { _gate.Release(); }
    }

    /// <summary>One look at the settings: drops the client when Telegram is off or the account changed, resumes a saved login.</summary>
    private async Task Pass(CancellationToken ct)
    {
        var cfg = _config.Get().Telegram;
        await _gate.WaitAsync(ct);
        try
        {
            var key = cfg.Enabled ? KeyOf(cfg) : null;
            if (_client is not null && (key is null || key != _clientFor || _broken)) DropClient();
            if (!cfg.Enabled) { SetState("off"); return; }
            if (key is null) { SetState("setup", "Enter the api_id, api_hash and phone number"); return; }
            if (_client is not null) return; // connected, or a login waiting for the code or password
            if (!HasSession) { if (_state is not "error") SetState("signed-out"); return; }
            SetState("connecting");
            var next = await NewClient(key.Value).Login(key.Value.Item3);
            if (next is null) { await Connected(); return; }
            // The saved login no longer works (signed out on another device): Telegram has just sent a code, but no
            // one asked for it, so forget the session and wait for Sign in instead of starting over at every launch.
            DropClient();
            File.Delete(SessionPath);
            SetState("signed-out", "Telegram ended the saved login. Sign in again.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Settings › Sign in: a fresh login, Telegram sends a code to the account's other devices (or by SMS).</summary>
    public async Task<TelegramStatus> StartLogin()
    {
        var cfg = _config.Get().Telegram;
        if (!cfg.Enabled || KeyOf(cfg) is not { } key) return Status() with { Error = "Turn Telegram on and enter the api_id, api_hash and phone number, then save" };
        await _gate.WaitAsync();
        try
        {
            DropClient();
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
            await Step(await NewClient(key).Login(key.Item3));
        }
        catch (Exception ex) { DropClient(); SetState("signed-out", UserError(ex)); }
        finally { _gate.Release(); }
        return Status();
    }

    /// <summary>Settings › the code from Telegram, or the cloud password (two-step verification).</summary>
    public async Task<TelegramStatus> Submit(string value)
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is null || _state is not ("code" or "password")) return Status() with { Error = "Press Sign in first" };
            await Step(await _client.Login(value.Trim()));
        }
        catch (RpcException ex) when (ex.Message is "PHONE_CODE_INVALID" or "PASSWORD_HASH_INVALID")
        {
            _error = ex.Message == "PHONE_CODE_INVALID" ? "Wrong code, try again" : "Wrong password, try again";
        }
        catch (Exception ex) { DropClient(); SetState("signed-out", UserError(ex)); }
        finally { _gate.Release(); }
        return Status();
    }

    public async Task<TelegramStatus> Logout()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null && _state == "connected")
                try { await _client.Auth_LogOut(); } catch (Exception ex) { _log.LogDebug(ex, "telegram: log out"); }
            DropClient();
            if (File.Exists(SessionPath)) File.Delete(SessionPath);
            SetState("signed-out");
        }
        finally { _gate.Release(); }
        return Status();
    }

    private async Task Step(string? next)
    {
        switch (next)
        {
            case null: await Connected(); break;
            case "verification_code": SetState("code"); break;
            case "password": SetState("password"); break;
            default:
                DropClient();
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
                SetState("signed-out", next == "name" ? "There is no Telegram account with this phone number" : $"Telegram asks for \"{next}\", which PhoneBox cannot provide");
                break;
        }
    }

    private WTelegram.Client NewClient((int Id, string Hash, string Phone) key)
    {
        var path = SessionPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _sessionStream = new FileStream(path, options);
        AppPaths.RestrictToOwner(path);
        var version = typeof(TelegramMonitor).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        string? Config(string what) => what switch
        {
            "api_id" => key.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "api_hash" => key.Hash,
            "phone_number" => key.Phone,
            "device_model" => "PhoneBox",
            "app_version" => version,
            _ => null,
        };
        var client = new WTelegram.Client(Config, _sessionStream);
        client.OnUpdates += OnUpdates;
        client.OnOther += o => { if (o is ReactorError) _broken = true; return Task.CompletedTask; };
        _client = client; _clientFor = key; _broken = false;
        return client;
    }

    private void DropClient()
    {
        try { _client?.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "telegram: close"); }
        try { _sessionStream?.Dispose(); } catch (IOException) { }
        _client = null; _sessionStream = null; _clientFor = null;
        _notify.Clear();
    }

    private void SetState(string state, string? error = null)
    {
        _state = state; _error = error;
        if (state != "connected") _user = null;
    }

    private async Task Connected()
    {
        var client = _client!;
        _state = "connected"; _error = null;
        _user = client.User is { } me ? NameOf(me) : null;
        _log.LogInformation("telegram: connected");
        // Names of the chats, so a message whose update carries only ids still gets a title.
        try { (await client.Messages_GetAllDialogs()).CollectUsersChats(_users, _chats); }
        catch (Exception ex) { _log.LogDebug(ex, "telegram: could not list the chats"); }
    }

    // ------------------------------------------------------------------------------------------------- messages

    private async Task OnUpdates(UpdatesBase updates)
    {
        try
        {
            updates.CollectUsersChats(_users, _chats);
            foreach (var update in updates.UpdateList)
                switch (update)
                {
                    case UpdateNewMessage { message: Message m }: await OnMessage(m); break;
                    case UpdateNotifySettings: _notify.Clear(); break; // changed in Telegram: read them again
                }
        }
        catch (Exception ex) { _log.LogDebug(ex, "telegram: update"); }
    }

    private async Task OnMessage(Message m)
    {
        if (m.flags.HasFlag(Message.Flags.out_)) return;
        if (DateTime.UtcNow - m.date > TimeSpan.FromMinutes(2)) return; // late delivery after a reconnect: not news any more
        var cfg = _config.Get().Telegram;
        if (!cfg.Enabled || _client is not { } client) return;

        var chat = Describe(m.peer_id);
        if (chat is null) return;
        var settings = await EffectiveSettings(client, m.peer_id, chat.Value.Kind);
        var rule = FindRule(cfg.Chats, chat.Value.Name, chat.Value.Username, m.peer_id.ID);
        var decision = Decide(cfg, rule, settings, m.flags.HasFlag(Message.Flags.silent), m.flags.HasFlag(Message.Flags.mentioned), DateTime.UtcNow);
        Remember(chat.Value.Name);
        if (decision is null) return;

        var sender = m.from_id is PeerUser fu && chat.Value.Kind != "user" && _users.TryGetValue(fu.user_id, out var su) ? NameOf(su) : null;
        string text = decision.Value.ShowText ? Preview(m) : "New message";
        if (sender is not null && decision.Value.ShowText) text = $"{sender}: {text}";
        var payload = new Dictionary<string, object?>
        {
            ["title"] = chat.Value.Name,
            ["text"] = text.Length > 300 ? text[..300] + "…" : text,
            ["level"] = "info",
            ["source"] = "telegram",
            ["vibrate"] = decision.Value.Vibrate is { } v ? v : false,
            ["sound"] = decision.Value.Tone,
            ["soundId"] = decision.Value.Ringtone?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["soundVolume"] = cfg.Volume,
        };
        _push.Emit("notify", JsonSerializer.SerializeToElement(payload));
    }

    internal readonly record struct Decision(string? Vibrate, string? Tone, long? Ringtone, bool ShowText);

    /// <summary>
    /// Whether and how a message is announced on the phone: Telegram's settings for the chat first (muted, silent,
    /// show previews, sound), then the PhoneBox rule for it. Null means no notification.
    /// </summary>
    internal static Decision? Decide(TelegramConfig cfg, TelegramChatRule? rule, PeerNotifySettings? s, bool silentMessage, bool mentioned, DateTime now)
    {
        if (rule?.Mute == true) return null;
        var muted = s is not null && s.mute_until > now;
        if (muted && !mentioned && rule?.Always != true) return null;
        var showText = cfg.ShowText && (s is null || !s.flags.HasFlag(PeerNotifySettings.Flags.has_show_previews) || s.show_previews);
        var silent = silentMessage || s is not null && s.flags.HasFlag(PeerNotifySettings.Flags.has_silent) && s.silent;
        if (silent) return new(null, null, null, showText);
        var vibrate = rule?.Vibrate ?? cfg.Vibrate;
        var sound = rule?.Sound ?? cfg.Sound;
        string? tone = null; long? ringtone = null;
        if (sound == "telegram")
            switch (TelegramSound(s))
            {
                case NotificationSoundNone: break;
                case NotificationSoundRingtone r: ringtone = r.id; tone = DefaultTone; break; // the tone plays if the file cannot be had
                default: tone = DefaultTone; break; // Telegram's default sound, or a sound built into a phone app
            }
        else if (sound != "none") tone = sound;
        return new(vibrate == "off" ? null : vibrate, tone, ringtone, showText);
    }

    /// <summary>The sound Telegram plays for the chat: the desktop one (other_sound) first, then the Android and iOS ones.</summary>
    internal static NotificationSound? TelegramSound(PeerNotifySettings? s)
    {
        if (s is null) return null;
        if (s.flags.HasFlag(PeerNotifySettings.Flags.has_other_sound)) return s.other_sound;
        if (s.flags.HasFlag(PeerNotifySettings.Flags.has_android_sound)) return s.android_sound;
        if (s.flags.HasFlag(PeerNotifySettings.Flags.has_ios_sound)) return s.ios_sound;
        return null;
    }

    /// <summary>The chat's own settings, each one it does not set taken from the default for its kind (private chats, groups, channels).</summary>
    internal static PeerNotifySettings Merge(PeerNotifySettings? chat, PeerNotifySettings? kind)
    {
        chat ??= new(); kind ??= new();
        var r = new PeerNotifySettings();
        void Take(PeerNotifySettings.Flags flag, Action<PeerNotifySettings> copy)
        {
            var from = chat.flags.HasFlag(flag) ? chat : kind.flags.HasFlag(flag) ? kind : null;
            if (from is null) return;
            copy(from);
            r.flags |= flag;
        }
        Take(PeerNotifySettings.Flags.has_show_previews, f => r.show_previews = f.show_previews);
        Take(PeerNotifySettings.Flags.has_silent, f => r.silent = f.silent);
        Take(PeerNotifySettings.Flags.has_mute_until, f => r.mute_until = f.mute_until);
        Take(PeerNotifySettings.Flags.has_other_sound, f => r.other_sound = f.other_sound);
        Take(PeerNotifySettings.Flags.has_android_sound, f => r.android_sound = f.android_sound);
        Take(PeerNotifySettings.Flags.has_ios_sound, f => r.ios_sound = f.ios_sound);
        return r;
    }

    internal static TelegramChatRule? FindRule(IEnumerable<TelegramChatRule> rules, string name, string? username, long id)
    {
        foreach (var r in rules)
        {
            var m = r.Match.Trim();
            if (m.Length == 0) continue;
            if (string.Equals(m, name, StringComparison.OrdinalIgnoreCase)) return r;
            if (username is not null && string.Equals(m.TrimStart('@'), username, StringComparison.OrdinalIgnoreCase)) return r;
            if (long.TryParse(m, out var n) && (n == id || n == -id || n == -1000000000000 - id)) return r; // also the Bot API style ids
        }
        return null;
    }

    private async Task<PeerNotifySettings?> EffectiveSettings(WTelegram.Client client, Peer peer, string kind)
    {
        InputPeer? input = peer switch
        {
            PeerUser u when _users.TryGetValue(u.user_id, out var user) => user,
            PeerChat c when _chats.TryGetValue(c.chat_id, out var chat) => chat,
            PeerChannel c when _chats.TryGetValue(c.channel_id, out var chat) => chat,
            _ => null,
        };
        var own = input is null ? null : await Cached($"p{peer.ID}", () => client.Account_GetNotifySettings(new InputNotifyPeer { peer = input }));
        InputNotifyPeerBase kindPeer = kind switch { "user" => new InputNotifyUsers(), "channel" => new InputNotifyBroadcasts(), _ => new InputNotifyChats() };
        var byKind = await Cached("k" + kind, () => client.Account_GetNotifySettings(kindPeer));
        return own is null && byKind is null ? null : Merge(own, byKind);
    }

    private async Task<PeerNotifySettings?> Cached(string key, Func<Task<PeerNotifySettings>> fetch)
    {
        if (_notify.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < NotifyCacheFor) return hit.Settings;
        try
        {
            var s = await fetch();
            _notify[key] = (s, DateTime.UtcNow);
            return s;
        }
        catch (Exception ex) { _log.LogDebug(ex, "telegram: notification settings"); return hit.Settings; }
    }

    private (string Name, string? Username, string Kind)? Describe(Peer peer) => peer switch
    {
        PeerUser u => _users.TryGetValue(u.user_id, out var user) ? (NameOf(user), user.MainUsername, "user") : ("Telegram", null, "user"),
        PeerChat c => (_chats.TryGetValue(c.chat_id, out var chat) ? chat.Title : "Telegram group", null, "group"),
        PeerChannel c => _chats.TryGetValue(c.channel_id, out var chat)
            ? (chat.Title, chat.MainUsername, chat is Channel { IsChannel: true } ? "channel" : "group")
            : ("Telegram", null, "group"),
        _ => null,
    };

    internal static string NameOf(User u)
    {
        var name = $"{u.first_name} {u.last_name}".Trim();
        return name.Length > 0 ? name : u.MainUsername ?? "Telegram";
    }

    internal static string Preview(Message m)
    {
        if (!string.IsNullOrWhiteSpace(m.message)) return m.message.Trim();
        return m.media switch
        {
            MessageMediaPhoto => "Photo",
            MessageMediaDocument { document: Document d } when d.attributes?.OfType<DocumentAttributeSticker>().Any() == true => "Sticker",
            MessageMediaDocument { document: Document d } when d.attributes?.OfType<DocumentAttributeAudio>().Any(a => a.flags.HasFlag(DocumentAttributeAudio.Flags.voice)) == true => "Voice message",
            MessageMediaDocument { document: Document d } when d.mime_type?.StartsWith("video/", StringComparison.Ordinal) == true => "Video",
            MessageMediaDocument => "File",
            MessageMediaGeo or MessageMediaGeoLive or MessageMediaVenue => "Location",
            MessageMediaContact => "Contact",
            MessageMediaPoll => "Poll",
            _ => "New message",
        };
    }

    private void Remember(string name)
    {
        lock (_recent)
        {
            _recent.Remove(name);
            _recent.AddFirst(name);
            while (_recent.Count > 12) _recent.RemoveLast();
        }
    }

    // ------------------------------------------------------------------------------------------------- ringtones

    /// <summary>A ringtone the user saved in Telegram (the only ones a chat can use as its sound), for the phone to play.</summary>
    public async Task<(byte[] Data, string Type)?> Sound(long id)
    {
        if (_sounds.TryGetValue(id, out var hit)) return hit;
        if (_client is not { } client || _state != "connected") return null;
        try
        {
            if (await client.Account_GetSavedRingtones(0) is not Account_SavedRingtones saved) return null;
            if (saved.ringtones.OfType<Document>().FirstOrDefault(d => d.id == id) is not { } doc || doc.size > MaxSoundBytes) return null;
            using var ms = new MemoryStream();
            await client.DownloadFileAsync(doc, ms);
            var sound = (ms.ToArray(), string.IsNullOrEmpty(doc.mime_type) ? "audio/mpeg" : doc.mime_type);
            if (_sounds.Count > 32) _sounds.Clear();
            _sounds[id] = sound;
            return sound;
        }
        catch (Exception ex) { _log.LogDebug(ex, "telegram: ringtone"); return null; }
    }

    internal static string UserError(Exception ex) => ex switch
    {
        RpcException { Message: "API_ID_INVALID" } => "Telegram refused the api_id / api_hash. Copy them again from my.telegram.org",
        RpcException { Message: "PHONE_NUMBER_INVALID" } => "Telegram does not know this phone number. Use the international format, e.g. +380…",
        RpcException { Message: "PHONE_NUMBER_BANNED" } => "Telegram has banned this phone number",
        RpcException { Code: 420 } r => $"Telegram asks to wait {r.X} seconds before trying again",
        RpcException r => "Telegram: " + r.Message,
        IOException or System.Net.Sockets.SocketException or TimeoutException => "Could not reach Telegram",
        _ => ex.Message,
    };
}
