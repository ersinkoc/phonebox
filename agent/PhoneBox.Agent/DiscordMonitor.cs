using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record DiscordMember(string Id, string Name, bool Muted, bool Deafened, string? Avatar, bool Speaking = false);
/// <param name="People">Who is in the voice channel: only read for favourites (see <see cref="DiscordFavorites"/>), null otherwise.</param>
public sealed record DiscordChannel(string Id, string Name, string? Guild, string? GuildId, string? GuildIcon,
    IReadOnlyList<DiscordMember>? People = null);
public sealed record DiscordNotification(long Ts, string Title, string Body);
/// <param name="InputMode">"VOICE_ACTIVITY" or "PUSH_TO_TALK" (Discord's input mode); null until Discord reported it.</param>
/// <param name="PttKey">Whether a push-to-talk key is set in Discord: with push to talk on and no key, nobody hears you.</param>
public sealed record DiscordSnapshot(bool Connected, string? Error, string? User, bool Muted, bool Deafened,
    string? ChannelId, string? ChannelName, string? ChannelKind, IReadOnlyList<DiscordMember> Members,
    IReadOnlyList<DiscordChannel> Channels, IReadOnlyList<DiscordNotification> Notifications,
    string? InputMode = null, bool PttKey = false);

/// <summary>
/// Discord through the desktop app's local IPC (the same one rich presence uses: a named pipe on Windows, a socket on
/// Linux / macOS). Needs a Discord developer application (client id + secret) and a one-time "Authorize" click in
/// Discord. Reads the voice channel you are in (also DM / group calls), mute and deafen, the voice channels you can
/// join and the notifications Discord shows, and runs the widget's buttons. Nothing leaves the computer except the
/// OAuth token exchange with discord.com.
/// </summary>
public sealed partial class DiscordMonitor(ConfigStore config, ILogger<DiscordMonitor> log) : BackgroundService
{
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write", "rpc.notifications.read"];
    /// <summary>Only asked for while screen share is on (<see cref="DiscordConfig.ScreenShare"/>), so nobody else has to approve PhoneBox again.</summary>
    internal static readonly string[] ShareScopes = ["rpc.screenshare.read", "rpc.screenshare.write"];
    /// <summary>Only asked for while the chat setting is on (<see cref="DiscordConfig.Chat"/>), so nobody else has to approve PhoneBox again.</summary>
    internal const string ChatScope = "messages.read";
    /// <summary>The permissions the settings want beyond the base ones (<see cref="Scopes"/>).</summary>
    internal static string[] ExtraScopes(DiscordConfig cfg) =>
        [.. cfg.ScreenShare ? ShareScopes : [], .. cfg.Chat ? new[] { ChatScope } : []];
    private const string Redirect = "http://127.0.0.1";
    private static readonly string[] SpeakingEvents = ["SPEAKING_START", "SPEAKING_STOP"];

    private volatile DiscordSnapshot _snap = Empty(null);
    private readonly object _lock = new();
    private readonly List<DiscordNotification> _notes = [];
    internal readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new(); // internal for tests
    internal readonly SemaphoreSlim _send = new(1, 1); // internal for tests
    internal Stream? _io; // internal for tests

    private static DiscordSnapshot Empty(string? error) => new(false, error, null, false, false, null, null, null, [], [], []);

    private volatile bool _notesWanted;

    /// <summary>Whether Discord notifications are subscribed to (and sent to the phone): only while an enabled Discord widget shows them.</summary>
    internal bool NotificationsOn { get => _notesWanted; set => _notesWanted = value; }

    internal static bool NotificationsWanted(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "discord" && w.Bool("notifications") != false);

    /// <summary>
    /// The subscription follows the settings once per pass; this also checks the settings right now, so turning
    /// notifications, the widget's page or Discord off hides their text at once instead of after the current pass.
    /// </summary>
    private bool NotesNow() => _notesWanted && config.Get() is var cfg && cfg.Discord.Enabled && NotificationsWanted(cfg);

    public DiscordSnapshot Snapshot()
    {
        var wanted = NotesNow();
        lock (_lock)
        {
            var s = _snap;
            var members = _speaking.Count == 0 || s.ChannelId != _speakingChannel ? s.Members
                : [.. s.Members.Select(m => _speaking.Contains(m.Id) ? m with { Speaking = true } : m)];
            return s with { Members = members, Notifications = wanted ? [.. _notes] : [] };
        }
    }

    // Who is talking in the voice channel you are in, from Discord's SPEAKING_START / SPEAKING_STOP events (guarded by _lock).
    private readonly HashSet<string> _speaking = [];
    private string? _speakingChannel;

    /// <summary>
    /// The channel whose speaking events count. Setting it (on joining, switching or leaving a channel, and on
    /// disconnect) forgets who was speaking, so a ring never carries over to another channel or a later call.
    /// </summary>
    internal string? SpeakingChannel
    {
        get { lock (_lock) return _speakingChannel; }
        set { lock (_lock) { _speakingChannel = value; _speaking.Clear(); } }
    }

    /// <summary>
    /// Moves the speaking subscriptions (covered by the rpc.voice.read scope) from the channel you were in to the one you
    /// are in now, or sets them up again after a failure. No speaking event counts until both SPEAKING_START and
    /// SPEAKING_STOP are in place: a START from the old channel, or one that arrives while only START is subscribed,
    /// could leave a ring on that no STOP clears. False when Discord refused or didn't answer.
    /// </summary>
    internal async Task<bool> FollowSpeaking(string? from, string? to, CancellationToken ct)
    {
        SpeakingChannel = null;
        try
        {
            if (from is not null)
                foreach (var e in SpeakingEvents) await Request("UNSUBSCRIBE", new JsonObject { ["channel_id"] = from }, e, ct);
            if (to is not null)
                foreach (var e in SpeakingEvents) await Request("SUBSCRIBE", new JsonObject { ["channel_id"] = to }, e, ct);
            SpeakingChannel = to;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug("discord: speaking events: {Msg}", ex.Message);
            return false;
        }
    }

    internal void ApplySpeaking(string evt, JsonNode? data)
    {
        var user = JsonStr((data as JsonObject)?["user_id"]);
        var channel = JsonStr((data as JsonObject)?["channel_id"]);
        lock (_lock)
        {
            // an event still on its way from the channel you just left, or one that arrives after leaving, is dropped
            if (user is null || _speakingChannel is null || (channel is not null && channel != _speakingChannel)) return;
            if (evt == "SPEAKING_START") _speaking.Add(user); else _speaking.Remove(user);
        }
    }

    /// <summary>The user cancelled, the approval timed out or Discord refused the secret: retrying cannot help until the settings change.</summary>
    /// <param name="prompt">Whether the approval itself was declined or not answered in time (not a failure after it).</param>
    private sealed class LoginRefusedException(string message, bool prompt = false) : Exception(message) { public bool Prompt => prompt; }

    // The permissions of the current login, and the optional ones (ExtraScopes) that were declined, each on its own. A
    // declined one is not asked for again until its setting goes off, the client ID or secret changes or Discord is
    // switched off and on.
    private volatile string[] _granted = [];
    private readonly HashSet<string> _declined = []; // only touched by the connection's own loop, like _declinedFor
    private (string? Id, string? Secret) _declinedFor;

    /// <summary>The optional permissions to ask for: those the settings want now, without the declined ones.</summary>
    private string[] WantedScopes(DiscordConfig cfg)
    {
        var extras = ExtraScopes(cfg);
        if (_declinedFor != (cfg.ClientId, cfg.ClientSecret)) { _declinedFor = (cfg.ClientId, cfg.ClientSecret); _declined.Clear(); }
        _declined.IntersectWith(extras); // its setting is off: turned on again, it is asked for again
        return [.. extras.Where(s => !_declined.Contains(s))];
    }

    /// <summary>A setting that needs another permission was turned on: log in again to ask for it, unless it was declined.</summary>
    internal bool NeedsLogin(DiscordConfig cfg) => WantedScopes(cfg).Except(_granted).Any();

    private volatile bool _loggedIn;

    /// <summary>5 s after the first failure, doubling up to 30 s while Discord stays unreachable.</summary>
    internal static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(30, 5 * Math.Pow(2, Math.Max(0, failures - 1))));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _life = ct;
        string? lastError = null;
        (string? Id, string? Secret)? refusedFor = null;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get().Discord;
            if (!cfg.Enabled) { _snap = Empty(null); refusedFor = null; _declined.Clear(); failures = 0; await Task.Delay(2000, ct); continue; }
            // After a refused login, stay quiet (no new prompt, no new token request) until the client ID or secret change or Discord is switched off and on.
            if (refusedFor is { } refused && refused == (cfg.ClientId, cfg.ClientSecret)) { await Task.Delay(2000, ct); continue; }
            refusedFor = null;
            _loggedIn = false;
            try { await RunConnection(cfg, ct); }
            catch (LoginRefusedException ex)
            {
                refusedFor = (cfg.ClientId, cfg.ClientSecret);
                _snap = Empty(ex.Message);
                log.LogInformation("discord: {Msg}", ex.Message);
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var msg = ex is IOException or TimeoutException or SocketException ? "Discord is not running" : ex.Message;
                _snap = Empty(msg);
                if (msg != lastError) { log.LogInformation("discord: {Msg}", msg); lastError = msg; }
            }
            failures = _loggedIn ? 0 : failures + 1;
            if (!config.Get().Discord.Enabled) { _snap = Empty(null); refusedFor = null; failures = 0; continue; }
            await Task.Delay(Backoff(failures), ct);
        }
    }

    private async Task RunConnection(DiscordConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            throw new Exception("Enter the application's client ID and secret (Settings › General › Discord)");
        await using var io = await OpenIpc(ct);
        _ipcProcess = IpcProcess(io);
        // handshake
        await WriteFrame(io, 0, new JsonObject { ["v"] = 1, ["client_id"] = cfg.ClientId }, ct);
        var ready = (await ReadFrame(io, ct))?.Body ?? throw new IOException("Discord closed the connection");
        if (ready["evt"]?.GetValue<string>() != "READY") throw new Exception(ready["data"]?["message"]?.GetValue<string>() ?? "Discord refused the client ID");
        _io = io;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() => ReadLoop(io, stop.Token), CancellationToken.None);
        Task<Dictionary<string, IReadOnlyList<DiscordMember>>>? roomsTask = null; // reads beside the loop, so a slow answer never holds up mute or deafen
        try
        {
            _snap = Empty("Waiting for you to approve PhoneBox in Discord…");
            var user = await Authenticate(cfg, ct);
            _loggedIn = true;
            log.LogInformation("discord: connected");
            var subscribed = false;
            _shareSubscribed = false;
            _shareRefused = false;
            _guildChannels.Clear();
            var channelsAt = DateTime.MinValue;
            var channelsEvery = TimeSpan.FromSeconds(60);
            var rooms = new Dictionary<string, IReadOnlyList<DiscordMember>>(); // favourite voice channels: who is in them
            long roomsAt = 0; // when the last read was started
            string? speakingFor = null;
            long speakingRetryAt = 0; // after a failed (un)subscribe: when to set the channel's subscriptions up again
            while (!ct.IsCancellationRequested && !reader.IsCompleted && config.Get().Discord is { Enabled: true } now && !NeedsLogin(now))
            {
                // Notification text (DM content) is only asked for, and only sent on, while a widget shows it.
                var wantNotes = NotificationsWanted(config.Get());
                if (wantNotes != subscribed)
                {
                    await Request(wantNotes ? "SUBSCRIBE" : "UNSUBSCRIBE", new JsonObject(), "NOTIFICATION_CREATE", ct);
                    subscribed = wantNotes;
                    if (!wantNotes) lock (_lock) _notes.Clear();
                    _notesWanted = wantNotes;
                }
                await FollowShare(ct);
                if (!ChatOn) await StopChat(ct); // the chat went off: forget its messages and tell Discord to stop sending them
                if (DateTime.UtcNow - channelsAt > channelsEvery)
                {
                    channelsEvery = await RefreshVoiceChannels(ct) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(10);
                    channelsAt = DateTime.UtcNow;
                }
                var known = _guildChannels.Values.SelectMany(v => v).ToList();
                var wanted = DiscordFavorites.WantedRooms(config.Get(), known);
                if (roomsTask is { IsCompleted: true })
                {
                    if (roomsTask.IsCompletedSuccessfully) rooms = await roomsTask;
                    roomsTask = null;
                }
                if (wanted.Count > 0 && roomsTask is null && (roomsAt == 0 || Environment.TickCount64 - roomsAt > 10_000))
                {
                    roomsAt = Environment.TickCount64;
                    roomsTask = ReadRooms(wanted, stop.Token, stillWanted: id => DiscordFavorites.WantedRooms(config.Get(), known).Any(c => c.Id == id));
                }
                // only what is wanted right now: a favourite removed since the read started shows nobody any more
                var show = wanted.Select(c => c.Id).ToHashSet();
                List<DiscordChannel> channels = [.. known.Select(c => show.Contains(c.Id) && rooms.TryGetValue(c.Id, out var p) ? c with { People = p } : c)];
                var rev = Volatile.Read(ref _voiceRev);
                var vs = await Request("GET_VOICE_SETTINGS", null, null, ct);
                var sel = await Request("GET_SELECTED_VOICE_CHANNEL", null, null, ct);
                var selId = sel?["id"]?.GetValue<string>();
                if (selId != speakingFor || (speakingRetryAt != 0 && Environment.TickCount64 >= speakingRetryAt))
                {
                    // A failure only costs the rings for this channel and the rest of the widget keeps working; the
                    // channel's two subscriptions are set up again from scratch, at most every 30 s.
                    speakingRetryAt = await FollowSpeaking(speakingFor, selId, ct) ? 0 : Environment.TickCount64 + 30_000;
                    speakingFor = selId;
                }
                var members = MembersOf(sel?["voice_states"]);
                var kind = sel?["type"]?.GetValue<int>() switch { 1 => "call", 3 => "group call", 2 => "voice", 13 => "stage", _ => sel is null ? null : "voice" };
                var (inputMode, pttKey) = InputModeOf(vs);
                PublishPoll(rev, vs?["mute"]?.GetValue<bool>() == true, vs?["deaf"]?.GetValue<bool>() == true,
                    new DiscordSnapshot(true, null, user, false, false,
                        sel?["id"]?.GetValue<string>(), sel is null ? null : (sel["name"]?.GetValue<string>() ?? (kind == "call" ? "Direct call" : "Call")), kind, members, channels, [],
                        inputMode, pttKey));
                await Task.Delay(2000, ct);
            }
        }
        finally
        {
            _io = null;
            _notesWanted = false;
            SpeakingChannel = null;
            _granted = [];
            ClearShare();
            ClearChat(); // message text never outlives the connection
            stop.Cancel();
            try { await reader; } catch { }
            if (roomsTask is not null) try { await roomsTask; } catch { }
            lock (_lock)
            {
                _notes.Clear();
                if (_snap.Connected) _snap = Empty(config.Get().Discord.Enabled ? "Discord is not running" : null);
            }
            foreach (var p in _pending.Values) p.TrySetResult(null);
            _pending.Clear();
        }
    }

    /// <summary>
    /// Logs in with the saved login, or asks you in Discord (once) and saves the answer in the settings folder. With an
    /// optional permission wanted that the saved login lacks, Discord is asked for it first; when that is declined, not
    /// answered or not granted, only its feature stays off: the login goes on with the base permissions.
    /// </summary>
    internal async Task<string> Authenticate(DiscordConfig cfg, CancellationToken ct)
    {
        _granted = [];
        var wanted = WantedScopes(cfg);
        var tokens = LoadTokens(cfg.ClientId!);
        string? user = null;
        // a login saved by a version that didn't keep the scopes was never asked for any of the optional ones
        if (!HasScopes(tokens?.Scope, wanted))
        {
            try { user = await Login(cfg, wanted, ct); }
            catch (LoginRefusedException ex) when (ex.Prompt || tokens is not null)
            {
                string[] declined = [.. wanted.Where(s => !HasScopes(tokens?.Scope, s))]; // the saved login keeps the others
                log.LogInformation("discord: {Msg} Going on without {Scopes}.", ex.Message, string.Join(' ', declined));
                _declined.UnionWith(declined);
            }
        }
        if (user is null && tokens is not null)
        {
            if (tokens.Expires < DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
            {
                tokens = await Exchange(cfg, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh }, ct, tokens.Scope);
                if (tokens is not null) SaveTokens(tokens); // at once: the refresh token it replaces may no longer work
            }
            if (tokens is not null)
                try { user = await Auth(tokens, ct); } catch (Exception ex) { log.LogInformation("discord: saved login no longer works ({Msg})", ex.Message); }
        }
        user ??= await Login(cfg, WantedScopes(cfg), ct);
        // logged in, but without a permission that was wanted: asking again every pass would not change that
        _declined.UnionWith(wanted.Except(_granted));
        return user;
    }

    // AUTHORIZE (asks you in Discord) → token exchange → AUTHENTICATE
    private async Task<string> Login(DiscordConfig cfg, string[] extras, CancellationToken ct)
    {
        const string retry = " PhoneBox will not ask again until you change the Discord settings (or turn Discord off and save, wait a few seconds, then turn it on and save again).";
        JsonNode? granted;
        try { granted = await Request("AUTHORIZE", AuthorizeArgs(cfg.ClientId, extras), null, ct, TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { throw new LoginRefusedException("PhoneBox was not approved in Discord in time." + retry, prompt: true); }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException)) { throw new LoginRefusedException(Refusal(ex.Message) + retry, prompt: true); }
        // no answer at all means the connection ended (Discord closed or restarted): that is not a refusal
        var code = granted?["code"]?.GetValue<string>() ?? throw new IOException("Discord closed the connection");
        Tokens? tokens;
        try { tokens = await Exchange(cfg, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect }, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // approved in Discord, but the code couldn't be traded (discord.com unreachable, or its token service failing):
            // the code is single-use, so a retry would show the prompt again every 30 s; stop like after a refusal
            throw new LoginRefusedException($"PhoneBox could not finish the Discord login ({ex.Message})." + retry);
        }
        tokens = tokens
            ?? throw new LoginRefusedException("Discord refused the client secret or the redirect (add http://127.0.0.1 under OAuth2 › Redirects)." + retry);
        try { return await Auth(tokens, ct, isNew: true); }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException)) { throw new LoginRefusedException($"Discord refused the new login ({ex.Message})." + retry); }
    }

    /// <summary>
    /// The AUTHORIZE request over Discord's local socket. <c>redirect_uri</c> is only allowed over the WebSocket
    /// transport: over IPC it is refused with "Redirect URI cannot be used in the RPC OAuth2 Authorization flow" (#338,
    /// #343). With <c>response_type: code</c> and no redirect, Discord uses the application's first registered
    /// redirect, which the token exchange then names (<see cref="Redirect"/>). An application with no saved redirect
    /// gets <c>invalid_request: Missing "redirect_uri"</c>, with or without <c>response_type</c> (#316, <see cref="Refusal"/>).
    /// </summary>
    internal static JsonObject AuthorizeArgs(string? clientId, params string[] extras) => new()
    {
        ["client_id"] = clientId,
        ["response_type"] = "code",
        ["scopes"] = new JsonArray(Scopes.Concat(extras).Select(s => (JsonNode)s).ToArray()),
    };

    /// <summary>
    /// Why AUTHORIZE was refused. <c>Missing "redirect_uri"</c> means the application has no saved redirect (#316), but
    /// it reads like a fault in the request, so the message says where to add it.
    /// </summary>
    internal static string Refusal(string discordMessage) =>
        discordMessage.Contains("Missing \"redirect_uri\"", StringComparison.Ordinal)
            ? $"Discord login was refused ({discordMessage}). Add {Redirect} under OAuth2 › Redirects in the Discord Developer Portal and click Save Changes."
            : $"Discord login was refused ({discordMessage}).";

    /// <summary>Whether a space-separated scope list (the token answer's <c>scope</c>) holds every one of <paramref name="wanted"/>.</summary>
    internal static bool HasScopes(string? scopes, params string[] wanted) =>
        !wanted.Except(scopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []).Any();

    /// <param name="isNew">A login just approved: saved once Discord takes it, so one it refuses never replaces a login that works.</param>
    private async Task<string> Auth(Tokens tokens, CancellationToken ct, bool isNew = false)
    {
        var r = await Request("AUTHENTICATE", new JsonObject { ["access_token"] = tokens.Access }, null, ct);
        // Discord names the scopes of this login; the token answer's list is the fallback
        _meId = JsonStr(r?["user"]?["id"]);
        _granted = r?["scopes"] is JsonArray scopes ? [.. scopes.Select(JsonStr).OfType<string>()] : tokens.Scope?.Split(' ') ?? [];
        // A permission taken back in Discord (also while its setting is off): the saved login must not claim it any longer,
        // or turning the setting on would not ask for it.
        var withdrawn = r?["scopes"] is JsonArray && (tokens.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []).Except(_granted).Any();
        if (withdrawn) SaveTokens(tokens with { Scope = string.Join(' ', _granted) });
        else if (isNew && r is not null) SaveTokens(tokens);
        return r?["user"]?["global_name"]?.GetValue<string>() ?? r?["user"]?["username"]?.GetValue<string>() ?? "Discord";
    }

    /// <param name="Scope">The scopes Discord granted, space separated (missing in a login saved by an older version).</param>
    private sealed record Tokens(string ClientId, string Access, string Refresh, long Expires, string? Scope = null);

    /// <summary>
    /// discord-token.json next to the settings file; a settings file that is itself named so (--config) gets a sidecar
    /// named after it instead, so saving a login can never replace the settings.
    /// </summary>
    internal string TokenPath
    {
        get
        {
            var settings = Path.GetFullPath(config.Path);
            var path = Path.Combine(Path.GetDirectoryName(settings) ?? AppPaths.ConfigDir, "discord-token.json");
            return string.Equals(path, settings, StringComparison.OrdinalIgnoreCase) ? settings + ".discord-token.json" : path;
        }
    }

    private Tokens? LoadTokens(string clientId)
    {
        try
        {
            var t = JsonSerializer.Deserialize<Tokens>(File.ReadAllText(TokenPath));
            return t is not null && t.ClientId == clientId ? t : null;
        }
        catch { return null; }
    }

    internal Func<HttpClient> TokenHttp = () => new() { Timeout = TimeSpan.FromSeconds(15) }; // internal for tests

    private async Task<Tokens?> Exchange(DiscordConfig cfg, Dictionary<string, string> form, CancellationToken ct, string? scope = null)
    {
        form["client_id"] = cfg.ClientId!; form["client_secret"] = cfg.ClientSecret!;
        using var http = TokenHttp();
        using var res = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), ct);
        // 400 and 401 are OAuth2's answers for a rejected code, redirect or client secret (RFC 6749 5.2); anything else
        // (429, 5xx) is Discord having a moment, so it takes the reconnect back-off instead of counting as a refusal
        if (res.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Discord's token service answered HTTP {(int)res.StatusCode}");
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var t = new Tokens(cfg.ClientId!, j?["access_token"]?.GetValue<string>() ?? "", j?["refresh_token"]?.GetValue<string>() ?? "",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (j?["expires_in"]?.GetValue<long>() ?? 3600), JsonStr(j?["scope"]) ?? scope);
        return t.Access == "" ? null : t;
    }

    private void SaveTokens(Tokens t)
    {
        try { var tokenPath = TokenPath; Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!); AppPaths.WriteAllTextPrivate(tokenPath, JsonSerializer.Serialize(t)); } catch (Exception ex) { log.LogDebug(ex, "discord: could not save the login"); }
    }

    // Voice channels per server, kept between refreshes: a server Discord does not answer for right now keeps its last list.
    private readonly Dictionary<string, List<DiscordChannel>> _guildChannels = [];
    private readonly Dictionary<string, string?> _guildIcons = [];

    [System.Text.RegularExpressions.GeneratedRegex(@"/icons/\d+/([A-Za-z0-9_]{1,64})\.")]
    private static partial System.Text.RegularExpressions.Regex IconUrl();

    // Server icons and avatars come from Discord's image host. The phone asks the agent for them (and never talks to
    // Discord itself); the agent fetches each image once and keeps it in memory, up to a total size.
    private const long ImageCacheLimit = 8 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, (byte[] Data, string Type)> _images = new();
    private long _imageBytes; // guarded by _imageLock
    private readonly Lock _imageLock = new();
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Only icons and avatars the agent itself reported (a server's icon, a member of your current call or of a favourite voice channel) are served.</summary>
    internal bool IsKnownImage(string kind, string id, string hash)
    {
        var s = _snap;
        return kind switch
        {
            "guild" => s.Channels.Any(c => c.GuildId == id && c.GuildIcon == hash),
            "user" => s.Members.Any(m => m.Id == id && m.Avatar == hash)
                || s.Channels.Any(c => c.People?.Any(m => m.Id == id && m.Avatar == hash) == true) || IsChatAuthor(id, hash),
            _ => false,
        };
    }

    /// <summary>kind is "guild" or "user". Refused while Discord is off, for ids that are not in the current snapshot, and for anything that would not be a safe URL part.</summary>
    public async Task<(byte[] Data, string Type)?> ImageAsync(string kind, string id, string hash, CancellationToken ct)
    {
        if (!config.Get().Discord.Enabled) return null;
        if (kind is not ("guild" or "user") || id.Length is 0 or > 24 || !id.All(char.IsAsciiDigit)
            || hash.Length is 0 or > 64 || !hash.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
        if (!IsKnownImage(kind, id, hash)) return null;
        var key = $"{kind}/{id}/{hash}";
        if (_images.TryGetValue(key, out var hit)) return hit;
        try
        {
            using var res = await ImageHttp.GetAsync($"https://cdn.discordapp.com/{(kind == "guild" ? "icons" : "avatars")}/{id}/{hash}.png?size=64", ct);
            if (!res.IsSuccessStatusCode) return null;
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            if (data.Length is 0 or > 512 * 1024 || PhotoFrame.MimeOf(data) is not { } mime) return null;
            // the download stays outside the lock; lookup, eviction, insertion and the byte count change together
            lock (_imageLock)
            {
                if (_images.TryGetValue(key, out var raced)) return raced;
                if (_imageBytes + data.Length > ImageCacheLimit) { _images.Clear(); _imageBytes = 0; }
                _images[key] = (data, mime);
                _imageBytes += data.Length;
                return (data, mime);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Refreshes the voice channel list; returns false when some server did not answer, so the caller retries soon.</summary>
    private async Task<bool> RefreshVoiceChannels(CancellationToken ct)
    {
        var complete = true;
        try
        {
            var guilds = (await Request("GET_GUILDS", null, null, ct))?["guilds"]?.AsArray() ?? [];
            var seen = new HashSet<string>();
            log.LogDebug("discord: {N} servers reported", guilds.Count);
            foreach (var g in guilds.Take(60))
            {
                var gid = g?["id"]?.GetValue<string>(); var gname = g?["name"]?.GetValue<string>();
                if (gid is null) continue;
                seen.Add(gid);
                try
                {
                    if (!_guildIcons.ContainsKey(gid))
                    {
                        // GET_GUILD carries the icon url; asked once per server
                        var url = (await Request("GET_GUILD", new JsonObject { ["guild_id"] = gid }, null, ct))?["icon_url"]?.GetValue<string>();
                        _guildIcons[gid] = url is not null && IconUrl().Match(url) is { Success: true } m ? m.Groups[1].Value : null;
                    }
                    var icon = _guildIcons[gid];
                    var chans = (await Request("GET_CHANNELS", new JsonObject { ["guild_id"] = gid }, null, ct))?["channels"]?.AsArray() ?? [];
                    _guildChannels[gid] = chans
                        .Where(c => c?["type"]?.GetValue<int>() is 2 or 13 && c["id"] is not null)
                        .Select(c => new DiscordChannel(c!["id"]!.GetValue<string>(), c["name"]?.GetValue<string>() ?? c["id"]!.GetValue<string>(), gname, gid, icon)).ToList();
                    // the same answer lists the text channels: kept for the chat (names only, no messages)
                    KeepTextChannels(gid, chans
                        .Where(c => c?["type"]?.GetValue<int>() is 0 or 5 && c["id"] is not null)
                        .Select(c => new DiscordChannel(c!["id"]!.GetValue<string>(), c["name"]?.GetValue<string>() ?? c["id"]!.GetValue<string>(), gname, gid, icon)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { complete = false; log.LogDebug("discord: channels of {Guild} failed: {Msg}", gname, ex.Message); }
            }
            // Discord sometimes reports only the servers it has loaded; keep what we saw (cleared on reconnect) instead of dropping them.
            if (seen.Count == 0) complete = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { complete = false; log.LogDebug("discord: listing servers failed: {Msg}", ex.Message); }
        return complete;
    }

    private static string? JsonStr(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The people in a voice channel, from its <c>voice_states</c> (GET_SELECTED_VOICE_CHANNEL / GET_CHANNEL).</summary>
    internal static List<DiscordMember> MembersOf(JsonNode? voiceStates) =>
        voiceStates is JsonArray a
            ? [.. a.Select(v => new DiscordMember(
                JsonStr(v?["user"]?["id"]) ?? "",
                JsonStr(v?["nick"]) ?? JsonStr(v?["user"]?["global_name"]) ?? JsonStr(v?["user"]?["username"]) ?? "?",
                v?["voice_state"]?["mute"]?.GetValueKind() == JsonValueKind.True || v?["voice_state"]?["self_mute"]?.GetValueKind() == JsonValueKind.True,
                v?["voice_state"]?["deaf"]?.GetValueKind() == JsonValueKind.True || v?["voice_state"]?["self_deaf"]?.GetValueKind() == JsonValueKind.True,
                JsonStr(v?["user"]?["avatar"])))]
            : [];

    private static readonly TimeSpan RoomWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Who is in the favourite voice channels. Only channels with people are kept; one Discord refuses is left out until
    /// the next pass, and when Discord is slow to answer the pass ends there instead of asking for the others too. Each
    /// one is asked for only while it is still wanted: the widget's page or option may be turned off during a pass.
    /// </summary>
    internal async Task<Dictionary<string, IReadOnlyList<DiscordMember>>> ReadRooms(IReadOnlyList<DiscordChannel> wanted, CancellationToken ct, TimeSpan? wait = null, Func<string, bool>? stillWanted = null)
    {
        var rooms = new Dictionary<string, IReadOnlyList<DiscordMember>>();
        foreach (var c in wanted)
        {
            if (ct.IsCancellationRequested) break;
            if (stillWanted?.Invoke(c.Id) == false) continue;
            try
            {
                var people = MembersOf((await Request("GET_CHANNEL", new JsonObject { ["channel_id"] = c.Id }, null, ct, wait ?? RoomWait))?["voice_states"]);
                if (people.Count > 0) rooms[c.Id] = people;
            }
            catch (TimeoutException) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("discord: people in {Channel}: {Msg}", c.Id, ex.Message); }
        }
        return rooms;
    }

    /// <summary>Whether the agent reported this server ("guild") or voice channel ("channel"): favourites can only be ids from its own list.</summary>
    public bool Knows(string kind, string id)
    {
        var s = _snap;
        return kind switch
        {
            "guild" => s.Channels.Any(c => c.GuildId == id),
            "channel" => s.Channels.Any(c => c.Id == id),
            _ => false,
        };
    }

    internal async Task ReadLoop(Stream io, CancellationToken ct)
    {
        lock (_lock) _closed = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrame(io, ct);
                if (frame is null) break;
                var (op, msg) = frame.Value;
                if (op == 3) // PING: answer with the same payload, through the same lock as every other write
                {
                    await _send.WaitAsync(ct);
                    try { await WriteFrame(io, 4, msg as JsonObject ?? new JsonObject(), ct); } finally { _send.Release(); }
                    continue;
                }
                if (op == 4 || msg is not JsonObject obj) continue; // PONG or non-object payload
                var evt = JsonStr(obj["evt"]);
                if (JsonStr(obj["cmd"]) == "DISPATCH" && evt is "SPEAKING_START" or "SPEAKING_STOP")
                {
                    ApplySpeaking(evt, obj["data"]);
                    continue;
                }
                if (evt == "SCREENSHARE_STATE_UPDATE" && JsonStr(obj["cmd"]) == "DISPATCH")
                {
                    OnShareState(obj["data"]);
                    continue;
                }
                if (evt == "MESSAGE_CREATE" && JsonStr(obj["cmd"]) == "DISPATCH")
                {
                    OnChatMessage(obj["data"]); // only kept while the chat is on
                    continue;
                }
                if (evt == "NOTIFICATION_CREATE")
                {
                    if (!NotesNow() || obj["data"] is not JsonObject d) continue;
                    var title = JsonStr(d["title"]) ?? "";
                    var body = JsonStr(d["body"]) ?? JsonStr((d["message"] as JsonObject)?["content"]) ?? "";
                    lock (_lock) { _notes.Insert(0, new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), title, body)); if (_notes.Count > 20) _notes.RemoveAt(20); }
                    continue;
                }
                var id = JsonStr(obj["nonce"]);
                if (id is null || !_pending.TryRemove(id, out var tcs)) continue;
                if (evt == "ERROR") tcs.TrySetException(new Exception(JsonStr((obj["data"] as JsonObject)?["message"]) ?? "Discord refused the request"));
                else tcs.TrySetResult(obj["data"]?.DeepClone());
            }
        }
        finally
        {
            _notesWanted = false;
            ClearShare();
            ClearChat();
            lock (_lock)
            {
                _closed = true; // a poll still finishing for this connection must not bring "connected" back
                _notes.Clear();
                if (_snap.Connected) _snap = Empty(config.Get().Discord.Enabled ? "Discord is not running" : null);
            }
            // the connection ended (Discord closed or restarted): answer whatever still waits, so a pending login
            // prompt ends now as "no answer" instead of timing out as a refusal two minutes later
            foreach (var p in _pending.Values) p.TrySetResult(null);
        }
    }

    /// <summary>Thrown by <see cref="Request"/> when <c>stillValid</c> says no once the send lock is held: nothing was sent.</summary>
    private sealed class StaleRequestException() : Exception("the state changed before the request was sent");

    private async Task<JsonNode?> Request(string cmd, JsonObject? args, string? evt, CancellationToken ct, TimeSpan? wait = null, Func<bool>? stillValid = null)
    {
        var io = _io ?? throw new Exception("Discord is not connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["cmd"] = cmd, ["nonce"] = id, ["args"] = args ?? new JsonObject() };
        if (evt is not null) msg["evt"] = evt;
        // The deadline covers waiting for the send lock, the write and the answer, so a stalled pipe cannot block forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(5));
        await using var registration = timeout.Token.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException("Discord did not answer")); });
        try
        {
            await _send.WaitAsync(timeout.Token);
            try
            {
                // what the caller decided on may have changed while it waited for the lock (a toggle would undo it)
                if (stillValid?.Invoke() == false) throw new StaleRequestException();
                await WriteFrame(io, 1, msg, timeout.Token);
            }
            finally { _send.Release(); }
            return await tcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Discord did not answer"); }
        finally { _pending.TryRemove(id, out _); }
    }

    // One button press at a time, so two quick toggles each start from the state the previous one produced.
    private readonly SemaphoreSlim _act = new(1, 1);
    private int _voiceRev; // bumped each time a button press publishes new mute/deafen state
    private bool _closed;  // guarded by _lock: the current connection's ReadLoop has ended

    /// <summary>Publishes a poll. A button press that finished since the poll read <paramref name="rev"/> is newer than what the poll saw, so it keeps its mute/deafen/input mode state.</summary>
    internal void PublishPoll(int rev, bool muted, bool deafened, DiscordSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_closed) return; // the connection this poll came from has ended (ReadLoop)
            var stale = rev != _voiceRev;
            _snap = stale
                ? snapshot with { Muted = _snap.Muted, Deafened = _snap.Deafened, InputMode = _snap.InputMode, PttKey = _snap.PttKey }
                : snapshot with { Muted = muted, Deafened = deafened };
        }
    }

    /// <summary>
    /// Discord's input mode from GET_VOICE_SETTINGS / SET_VOICE_SETTINGS (<c>mode.type</c>), and whether a push-to-talk
    /// key is set (<c>mode.shortcut</c> has keys). Anything other than the two known modes counts as unknown (null).
    /// </summary>
    internal static (string? Mode, bool PttKey) InputModeOf(JsonNode? vs)
    {
        if (vs?["mode"] is not JsonObject mode) return (null, false);
        var type = JsonStr(mode["type"]);
        return (type is "VOICE_ACTIVITY" or "PUSH_TO_TALK" ? type : null, mode["shortcut"] is JsonArray keys && keys.Count > 0);
    }

    internal int VoiceRevision => Volatile.Read(ref _voiceRev);

    /// <summary>
    /// The widget's buttons: mute, deafen, inputmode (switch between voice activity and push to talk), leave, join (a
    /// voice channel id from the list the agent already sent).
    /// </summary>
    public async Task<string?> ActAsync(string action, string? channel)
    {
        if (!ControlAllowed(config.Get(), action))
            return "controlling Discord is turned off in the widget's settings and in Settings › General › Discord";
        await _act.WaitAsync();
        try
        {
            // Checked again here: the widget's option or page may have been switched off while this press was queued.
            if (!ControlAllowed(config.Get(), action)) return "controlling Discord is turned off in the widget's settings and in Settings › General › Discord";
            var s = _snap;
            if (!s.Connected) return "Discord is not connected";
            switch (action)
            {
                case "mute": ApplyVoice(await Request("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = !s.Muted }, null, CancellationToken.None)); break;
                case "deafen": ApplyVoice(await Request("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = !s.Deafened }, null, CancellationToken.None)); break;
                // Only the mode's type is sent, so Discord keeps the push-to-talk key, delay and sensitivity as they are.
                case "inputmode" when s.InputMode is not null:
                    ApplyVoice(await Request("SET_VOICE_SETTINGS", new JsonObject
                    {
                        ["mode"] = new JsonObject { ["type"] = s.InputMode == "PUSH_TO_TALK" ? "VOICE_ACTIVITY" : "PUSH_TO_TALK" },
                    }, null, CancellationToken.None));
                    break;
                case "inputmode": return "Discord has not reported its input mode yet";
                case "leave": await Request("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null }, null, CancellationToken.None); break;
                case "join" when !string.IsNullOrEmpty(channel) && s.Channels.Any(c => c.Id == channel):
                    await Request("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = channel, ["force"] = true }, null, CancellationToken.None); break;
                default: return "unknown action";
            }
            log.LogInformation("discord: {Action} {Channel}", action, channel);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _act.Release(); }
    }

    /// <summary>
    /// Control needs a Discord widget with the control option on, on a page that is enabled, or the global
    /// <see cref="DiscordConfig.AllowControl"/> switch (off by default) for shortcuts and custom widgets.
    /// </summary>
    /// <param name="action">The action asked for; null asks whether any is allowed (the panel's buttons). The
    /// current-channel bar's own Control option allows only the actions it shows: mute, deafen and leave.</param>
    public static bool ControlAllowed(AgentConfig cfg, string? action = null) =>
        cfg.Discord.AllowControl || WidgetControl(cfg) || (action is null || BarActions.Contains(action)) && BarControl(cfg);

    /// <summary>The voice actions the current-channel bar's Control option allows.</summary>
    internal static readonly string[] BarActions = ["mute", "deafen", "leave"];

    /// <summary>
    /// The current-channel bar with its Control option on, on an enabled page. It allows <see cref="BarActions"/> and
    /// stopping a screen share, nothing else: starting a share, the app list, the chat and "Open in Discord" stay with
    /// the Discord widget's own Control (<see cref="WidgetControl"/>).
    /// </summary>
    public static bool BarControl(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "discord-bar" && w.Bool("control") == true);

    /// <summary>A Discord widget with the control option on, on a page that is enabled: the global switch does not count.</summary>
    public static bool WidgetControl(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "discord" && w.Bool("control") == true);

    // Discord answers a settings change with the new settings; take the state from that, not from the next poll.
    internal void ApplyVoice(JsonNode? vs)
    {
        if (vs is null) return;
        var (mode, pttKey) = InputModeOf(vs);
        lock (_lock)
        {
            var s = _snap;
            _voiceRev++;
            _snap = s with
            {
                Muted = vs["mute"]?.GetValue<bool>() ?? s.Muted,
                Deafened = vs["deaf"]?.GetValue<bool>() ?? s.Deafened,
                InputMode = mode ?? s.InputMode,
                PttKey = mode is null ? s.PttKey : pttKey,
            };
        }
    }

    /// <summary>Stops with the agent: what the monitor starts on the side (the updater that shows Discord) ends with it.</summary>
    private CancellationToken _life;
    /// <summary>The process name of the Discord this agent talks to (Discord, DiscordPTB or DiscordCanary), when Windows says.</summary>
    private volatile string? _ipcProcess;

    /// <summary>Which process holds the other end of the pipe: the Discord build the requests go to. Windows only.</summary>
    private static string? IpcProcess(Stream io)
    {
#if WINDOWS
        try
        {
            if (io is NamedPipeClientStream pipe && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
        }
        catch { } // exited meanwhile, or not allowed: the builds are then tried in order
#endif
        return null;
    }
#if WINDOWS
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
#endif

    // ---- IPC transport: 4-byte opcode + 4-byte length (little endian) + UTF-8 JSON ----

    private static async Task<Stream> OpenIpc(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(500, ct);
                    return pipe;
                }
                var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await sock.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(dir, $"discord-ipc-{i}")), ct); }
                catch { sock.Dispose(); throw; }
                return new NetworkStream(sock, true);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException) { }
        }
        throw new IOException("Discord is not running");
    }

    private static async Task WriteFrame(Stream io, int op, JsonObject body, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var buf = new byte[8 + json.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), json.Length);
        json.CopyTo(buf, 8);
        await io.WriteAsync(buf, ct);
        await io.FlushAsync(ct);
    }

    private static async Task<(int Op, JsonNode? Body)?> ReadFrame(Stream io, CancellationToken ct)
    {
        var head = new byte[8];
        if (!await ReadExact(io, head, ct)) return null;
        var op = BitConverter.ToInt32(head, 0); var len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1 << 20) throw new IOException("Discord sent something unexpected");
        var body = new byte[len];
        if (!await ReadExact(io, body, ct)) return null;
        if (op == 2) return null; // close
        return (op, body.Length == 0 ? null : JsonNode.Parse(body));
    }

    private static async Task<bool> ReadExact(Stream io, byte[] buf, CancellationToken ct)
    {
        for (var got = 0; got < buf.Length;)
        {
            var n = await io.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
