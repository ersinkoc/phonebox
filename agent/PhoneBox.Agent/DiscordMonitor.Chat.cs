using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record DiscordChatMessage(string Id, string Author, string? AuthorId, string? Avatar, string? Color, bool Bot,
    string Text, long Ts, int Attachments, string? Embed);
public sealed record DiscordChatChannel(string Id, string Name, bool Unread);
public sealed record DiscordChatServer(string Id, string Name, string? Icon, bool Unread);

/// <summary>
/// The Discord widget's chat (read-only), behind <see cref="DiscordConfig.Chat"/> (off by default, asks for
/// messages.read). The phone asks for one channel at a time (<see cref="ChatAsync"/>); the agent reads its last
/// messages with GET_CHANNEL and follows new ones with MESSAGE_CREATE for the channels opened on the phone, which also
/// makes the new-message dots. Message text is kept in memory only, never logged or written to a file, and dropped when
/// the connection to Discord ends or the setting goes off.
/// </summary>
public sealed partial class DiscordMonitor
{
    /// <summary>The most messages kept and sent per channel, and the most channels followed for new messages.</summary>
    internal const int ChatMessages = 30, ChatFollowed = 25;

    private readonly Lock _chatLock = new();
    // all guarded by _chatLock
    private readonly Dictionary<string, List<DiscordChatMessage>> _chatMsgs = [];    // channel id -> last messages
    private readonly Dictionary<string, long> _chatReadAt = [];                     // channel id -> when GET_CHANNEL last ran
    private readonly Dictionary<string, string> _chatLatest = [];                   // channel id -> newest message from someone else
    private readonly Dictionary<string, string> _chatSeen = [];                     // channel id -> newest message shown on the phone
    private readonly List<string> _chatFollowed = [];                               // channels with MESSAGE_CREATE, oldest first
    private readonly Dictionary<string, List<DiscordChannel>> _guildText = [];      // server id -> its text channels (names only)
    private string? _chatChannel;                                                   // null: the chat of the voice channel you are in

    private volatile string? _meId; // your Discord user id: your own messages don't make a new-message dot
    private readonly SemaphoreSlim _chatSub = new(1, 1); // one (un)subscription at a time, so the list matches what Discord was told

    /// <summary>The chat answers only while its setting is on and a Discord widget is on an enabled page.</summary>
    internal static bool ChatWanted(AgentConfig cfg) =>
        cfg.Discord is { Enabled: true, Chat: true } && cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "discord");

    private bool ChatOn => _granted.Contains(ChatScope) && ChatWanted(config.Get());

    private void KeepTextChannels(string guildId, IEnumerable<DiscordChannel> text)
    {
        lock (_chatLock) _guildText[guildId] = [.. text];
    }

    /// <summary>The connection ended: forgets the messages, and what was read about its channels.</summary>
    internal void ClearChat()
    {
        lock (_chatLock) { ForgetMessages(); _chatFollowed.Clear(); _guildText.Clear(); }
    }

    private void ForgetMessages() // under _chatLock
    {
        _chatMsgs.Clear(); _chatReadAt.Clear(); _chatLatest.Clear(); _chatSeen.Clear(); _chatChannel = null;
    }

    /// <summary>
    /// The chat is off: forgets the messages (the channel names stay for when it is turned on again) and unsubscribes
    /// from the followed channels, so Discord stops sending their messages. One that fails is tried again next pass.
    /// </summary>
    internal async Task StopChat(CancellationToken ct)
    {
        lock (_chatLock) ForgetMessages();
        await _chatSub.WaitAsync(ct);
        try { while (Oldest() is { } channel && await Unfollow(channel, ct)) { } }
        finally { _chatSub.Release(); }
    }

    private int FollowedCount() { lock (_chatLock) return _chatFollowed.Count; }
    private string? Oldest() { lock (_chatLock) return _chatFollowed.Count > 0 ? _chatFollowed[0] : null; }

    private async Task<bool> Unfollow(string channel, CancellationToken ct) // under _chatSub
    {
        try { await Request("UNSUBSCRIBE", new JsonObject { ["channel_id"] = channel }, "MESSAGE_CREATE", ct, TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("discord: unfollowing a channel: {Msg}", ex.Message); return false; }
        lock (_chatLock) { _chatFollowed.Remove(channel); _chatMsgs.Remove(channel); _chatReadAt.Remove(channel); _chatLatest.Remove(channel); }
        return true;
    }

    internal bool IsChatAuthor(string id, string hash)
    {
        lock (_chatLock) return _chatMsgs.Values.Any(l => l.Any(m => m.AuthorId == id && m.Avatar == hash));
    }

    /// <summary>A new message in a followed channel: kept for the chat and, from someone else, a new-message dot.</summary>
    internal void OnChatMessage(JsonNode? data)
    {
        if (!ChatOn || data is not JsonObject d || JsonStr(d["channel_id"]) is not { } channel || d["message"] is not JsonObject m) return;
        var msg = ParseMessage(m, null);
        if (msg is null) return;
        lock (_chatLock)
        {
            // again under the lock: a message that passed the check above as the chat went off must not outlive StopChat
            if (!ChatOn || !_chatFollowed.Contains(channel)) return;
            if (!_chatMsgs.TryGetValue(channel, out var list)) _chatMsgs[channel] = list = []; // its history is still being read
            if (list.All(x => x.Id != msg.Id))
            {
                list.Add(msg);
                if (list.Count > ChatMessages) list.RemoveRange(0, list.Count - ChatMessages);
            }
            if (msg.AuthorId != _meId) _chatLatest[channel] = msg.Id;
        }
    }

    /// <summary>Discord ids grow with time, so comparing them (as numbers) orders messages.</summary>
    internal static bool Newer(string? a, string? b) =>
        a is not null && (b is null || (ulong.TryParse(a, out var x) && ulong.TryParse(b, out var y) ? x > y : string.CompareOrdinal(a, b) > 0));

    private bool UnreadLocked(string channel) =>
        _chatLatest.TryGetValue(channel, out var latest) && Newer(latest, _chatSeen.GetValueOrDefault(channel));

    /// <summary>Whether a channel followed for new messages has one you haven't seen on the phone (the widget's 💬 dot).</summary>
    public bool ChatUnread()
    {
        if (!ChatOn) return false;
        lock (_chatLock) return _chatLatest.Keys.Any(UnreadLocked);
    }

    /// <summary>
    /// The chat the phone shows: the chosen channel's last messages and the servers (with a dot when one of their followed
    /// channels has news). With <paramref name="picker"/> the phone shows its channel picker instead: no message is read
    /// or counted as seen, and <paramref name="guild"/> adds that server's text channels.
    /// </summary>
    public async Task<object> ChatAsync(string? guild, bool picker, CancellationToken ct)
    {
        if (!ChatWanted(config.Get())) return new { enabled = false };
        var snap = _snap;
        if (!ChatOn || !snap.Connected) return new { enabled = true, ready = false, error = snap.Error };
        string? chosen;
        lock (_chatLock) chosen = _chatChannel;
        var channelId = chosen ?? snap.ChannelId;
        object? channel = null;
        List<DiscordChatMessage> messages = [];
        if (channelId is not null)
        {
            var (name, guildName) = ChannelName(channelId, snap);
            channel = new { id = channelId, name, guild = guildName, voice = chosen is null };
            if (!picker)
            {
                await Follow(channelId, ct);
                if (!ChatOn) return new { enabled = false }; // turned off meanwhile: its messages are not even read
                messages = await Messages(channelId, ct);
            }
        }
        if (!ChatOn) return new { enabled = false }; // turned off while Discord was answering
        lock (_chatLock)
        {
            if (messages.Count > 0) _chatSeen[channelId!] = messages[^1].Id; // shown now: seen
            List<DiscordChatServer> servers = [.. _guildText.Where(g => g.Value.Count > 0)
                .Select(g => new DiscordChatServer(g.Key, g.Value[0].Guild ?? "?", g.Value[0].GuildIcon, g.Value.Any(c => UnreadLocked(c.Id))))];
            // by name: GET_CHANNELS has no order, and the order would need a GET_CHANNEL each, which also returns messages
            List<DiscordChatChannel>? channels = !picker || guild is null ? null
                : [.. _guildText.GetValueOrDefault(guild, []).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new DiscordChatChannel(c.Id, c.Name, UnreadLocked(c.Id)))];
            return new { enabled = true, ready = true, picker, channel, voice = chosen is null, callName = snap.ChannelName, messages, servers, channels };
        }
    }

    private (string? Name, string? Guild) ChannelName(string id, DiscordSnapshot snap)
    {
        if (id == snap.ChannelId) return (snap.ChannelName, snap.Channels.FirstOrDefault(c => c.Id == id)?.Guild);
        lock (_chatLock)
            foreach (var list in _guildText.Values)
                if (list.FirstOrDefault(c => c.Id == id) is { } c) return (c.Name, c.Guild);
        return (null, null);
    }

    /// <summary>Chooses the chat: a text channel the agent listed, or "voice" for the voice channel you are in.</summary>
    public string? ChatChoose(string? channel)
    {
        if (!ChatOn) return "the chat is turned off";
        lock (_chatLock)
        {
            if (channel == "voice") { _chatChannel = null; return null; }
            if (channel is null || !_guildText.Values.Any(l => l.Any(c => c.Id == channel))) return "unknown channel";
            _chatChannel = channel;
            return null;
        }
    }

    /// <summary>"Open in Discord": shows the chosen chat in the Discord app on the computer. It needs a Discord widget's Control option: <see cref="DiscordConfig.AllowControl"/> is for the voice actions only.</summary>
    public async Task<string?> ChatOpenAsync()
    {
        if (!WidgetControl(config.Get())) return "controlling Discord is turned off in the widget's settings";
        if (!ChatOn) return "the chat is turned off";
        string? id;
        lock (_chatLock) id = _chatChannel;
        id ??= _snap.ChannelId;
        if (id is null) return "no channel";
        // checked again as the frame goes out: Control or the chat turned off while another write held the pipe
        try
        {
            await Request("SELECT_TEXT_CHANNEL", new JsonObject { ["channel_id"] = id }, null, CancellationToken.None,
                stillValid: () => WidgetControl(config.Get()) && ChatOn);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Follows a channel's new messages (MESSAGE_CREATE), dropping the longest-followed one past <see cref="ChatFollowed"/>.</summary>
    private async Task Follow(string channel, CancellationToken ct)
    {
        await _chatSub.WaitAsync(ct);
        try
        {
            if (!ChatOn) return;
            lock (_chatLock) if (_chatFollowed.Remove(channel)) { _chatFollowed.Add(channel); return; }
            // room first: when Discord doesn't take the oldest one back, this channel is read but not followed
            while (FollowedCount() >= ChatFollowed && Oldest() is { } oldest)
                if (!await Unfollow(oldest, ct)) return;
            var io = _io;
            // kept only once Discord took it, so a failed one is asked for again the next time the channel is read
            await Request("SUBSCRIBE", new JsonObject { ["channel_id"] = channel }, "MESSAGE_CREATE", ct);
            // A connection that ended answers its pending requests with nothing, after ClearChat: such an answer must not
            // mark the channel as followed on the next one. (_io is taken away before that last ClearChat.)
            lock (_chatLock) if (io is not null && ReferenceEquals(_io, io)) _chatFollowed.Add(channel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("discord: following a channel: {Msg}", ex.Message); }
        finally { _chatSub.Release(); }
    }

    /// <summary>The channel's last messages: read with GET_CHANNEL at most every 10 s (new ones arrive with MESSAGE_CREATE in between).</summary>
    private async Task<List<DiscordChatMessage>> Messages(string channel, CancellationToken ct)
    {
        lock (_chatLock)
            if (_chatMsgs.TryGetValue(channel, out var cached) && _chatReadAt.TryGetValue(channel, out var readAt) && Environment.TickCount64 - readAt < 10_000)
                return [.. cached];
        // what was kept before this read: the read's answer replaces it (a deleted message goes), only messages that
        // arrived while it was on its way are added to it
        HashSet<string> before;
        lock (_chatLock) before = _chatMsgs.TryGetValue(channel, out var kept) ? [.. kept.Select(m => m.Id)] : [];
        try
        {
            // the chat turned off after the caller looked: no read goes to Discord
            var data = await Request("GET_CHANNEL", new JsonObject { ["channel_id"] = channel }, null, ct, stillValid: () => ChatOn);
            var names = new Dictionary<string, string>();
            lock (_chatLock) foreach (var c in _guildText.Values.SelectMany(l => l)) names.TryAdd(c.Id, c.Name);
            var list = (data?["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(m => ParseMessage(m, names)).OfType<DiscordChatMessage>()
                .OrderBy(m => ulong.TryParse(m.Id, out var n) ? n : 0).TakeLast(ChatMessages).ToList();
            lock (_chatLock)
            {
                if (!ChatOn) return [];                             // turned off meanwhile
                if (!_chatFollowed.Contains(channel)) return list; // not followed (any more): show, don't keep
                // messages that arrived while the history was on its way are newer than what it holds
                var newest = list.Count > 0 ? list[^1].Id : null;
                if (_chatMsgs.TryGetValue(channel, out var live))
                    list = [.. list.Concat(live.Where(m => !before.Contains(m.Id) && Newer(m.Id, newest))).TakeLast(ChatMessages)];
                _chatMsgs[channel] = list;
                _chatReadAt[channel] = Environment.TickCount64;
                // from the history as it is now: a deleted message must not leave a dot behind
                if (list.LastOrDefault(m => m.AuthorId != _meId)?.Id is { } fromOthers) _chatLatest[channel] = fromOthers;
                else _chatLatest.Remove(channel);
                return [.. list];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug("discord: reading a channel: {Msg}", ex.Message); // never the text
            lock (_chatLock) return ChatOn && _chatMsgs.TryGetValue(channel, out var old) ? [.. old] : [];
        }
    }

    [GeneratedRegex(@"<@!?(\d+)>")] private static partial Regex MentionRx();
    [GeneratedRegex(@"<@&\d+>")] private static partial Regex RoleRx();
    [GeneratedRegex(@"<#(\d+)>")] private static partial Regex ChannelRx();
    [GeneratedRegex(@"<a?:(\w+):\d+>")] private static partial Regex EmojiRx();
    [GeneratedRegex(@"<t:(\d+)(?::\w)?>")] private static partial Regex TimeRx();

    /// <summary>One message as the phone shows it: plain text with mentions, channels, emoji and timestamps written out.</summary>
    internal static DiscordChatMessage? ParseMessage(JsonObject m, IReadOnlyDictionary<string, string>? channelNames)
    {
        var id = JsonStr(m["id"]);
        if (id is null) return null;
        var author = m["author"] as JsonObject;
        var mentions = (m["mentions"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(u => (Id: JsonStr(u["id"]), Name: JsonStr(u["global_name"]) ?? JsonStr(u["username"])))
            .Where(u => u.Id is not null).GroupBy(u => u.Id!).ToDictionary(g => g.Key, g => g.First().Name ?? "user");
        var text = JsonStr(m["content"]) ?? "";
        text = MentionRx().Replace(text, x => "@" + mentions.GetValueOrDefault(x.Groups[1].Value, "user"));
        text = RoleRx().Replace(text, "@role");
        text = ChannelRx().Replace(text, x => "#" + (channelNames?.GetValueOrDefault(x.Groups[1].Value) ?? "channel"));
        text = EmojiRx().Replace(text, x => ":" + x.Groups[1].Value + ":");
        text = TimeRx().Replace(text, x => long.TryParse(x.Groups[1].Value, out var s) && s <= 253402300799 // the last second of year 9999
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : x.Value);
        var ts = DateTimeOffset.TryParse(JsonStr(m["timestamp"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() : 0;
        var embed = (m["embeds"] as JsonArray)?.OfType<JsonObject>().Select(e => JsonStr(e["title"]) ?? JsonStr(e["url"])).FirstOrDefault(s => s is not null);
        return new DiscordChatMessage(id,
            JsonStr(m["nick"]) ?? JsonStr(author?["global_name"]) ?? JsonStr(author?["username"]) ?? "?",
            JsonStr(author?["id"]), JsonStr(author?["avatar"]), JsonStr(m["author_color"]),
            author?["bot"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            text.Length > 2000 ? text[..2000] : text, ts, (m["attachments"] as JsonArray)?.Count ?? 0, embed);
    }
}
