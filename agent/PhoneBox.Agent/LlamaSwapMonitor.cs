using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// One request the router is serving right now. Model and path only - never headers, the remote IP, or anything
/// from the prompt or the answer. Only Id, Model and ElapsedMs reach the phone; Path and RespBytes never leave
/// the server (see the projection in the /api/llamaswap handler).
/// </summary>
public sealed record LlamaSwapInflight(string Id, string Model, string Path, int ElapsedMs, long RespBytes);

/// <summary>
/// One refresh of the llama-swap server: what is connected, which models are loaded, today's counts from its
/// request log, the requests it is serving right now, and the generation speed of the newest finished request.
/// Counts and model names only - nothing from the prompts or answers ever leaves the server.
/// </summary>
public sealed record LlamaSwapSnapshot(
    bool Connected,
    string? Error,
    string? Version,
    IReadOnlyList<string> LoadedModels,
    double? GenTps,
    int? TodayRequests,
    long? TodayInputTokens,
    long? TodayOutputTokens,
    DateTime? LastRequestAt,
    int Inflight,
    // The requests themselves: the elapsed seconds the phone shows for the in-flight count come from here.
    IReadOnlyList<LlamaSwapInflight> InflightRequests);

/// <summary>
/// Polls the local llama-swap server (the OpenAI-compatible LLM router): /api/version, /v1/models
/// and /api/metrics/activity, and reads the in-flight list over its /api/events stream. Runs only while Settings › General › llama-swap has an address;
/// an empty address polls nothing and the snapshot stays null.
/// </summary>
public sealed class LlamaSwapMonitor(ConfigStore config, ILogger<LlamaSwapMonitor> log) : BackgroundService
{
    internal LlamaSwapMonitor(ILogger<LlamaSwapMonitor> log) : this(null!, log) { }
    /// <summary>For tests: the server address, overriding the setting.</summary>
    internal string? UrlOverride { get; set; }
    /// <summary>For tests: a fake handler instead of the network.</summary>
    internal HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>For tests: the "today" boundary follows this clock.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;
    /// <summary>For tests: the UTC clock the in-flight bookkeeping runs on, so a local DST jump or a timezone
    /// clock change cannot corrupt the elapsed times.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The longest an in-flight request is trusted without its remove event: a dropped event would
    /// otherwise pin the in-flight count - and the phone's fast poll - forever.</summary>
    internal static readonly TimeSpan MaxInflightAge = TimeSpan.FromHours(1);

    /// <summary>The request log window the router answers at most in one page: today's counts are exact by
    /// asking for the day's range and reading every page it reports.</summary>
    internal const int ActivityWindow = 999;
    /// <summary>The most of one REST answer this side buffers: a router that answers with a huge body must not
    /// make the agent allocate it.</summary>
    internal const int MaxBodyBytes = 1024 * 1024;
    /// <summary>The longest one stream line may be: a router (or a proxy) that sends an endless line must not
    /// make the reader grow without bound. Longer lines are discarded.</summary>
    internal const int MaxLineChars = 64 * 1024;
    /// <summary>The most activity pages one refresh walks: a router (or a proxy) that reports a huge total must
    /// not make the agent issue an unbounded number of requests while it holds the refresh gate.</summary>
    internal const int MaxActivityPages = 20;

    /// <summary>The longest one /api/events connection is kept open: a half-dead socket blocks its read forever
    /// and leaves the in-flight list stale until MaxInflightAge, so each connection is dropped after this and
    /// the next one takes a fresh snapshot of what is really in flight.</summary>
    internal static readonly TimeSpan MaxStreamLifetime = TimeSpan.FromMinutes(10);
    /// <summary>For tests: the per-connection lifetime the stream gives up after.</summary>
    internal TimeSpan StreamLifetime { get; init; } = MaxStreamLifetime;
    /// <summary>The longest the wait for the /api/events response headers before giving up: a server that
    /// accepts the connection and sends nothing would otherwise hold the stream loop until the whole lifetime
    /// runs out, and an address change could not reach the wait either (the active read is not registered
    /// before the headers arrive).</summary>
    internal static readonly TimeSpan MaxHeaderWait = TimeSpan.FromSeconds(15);
    /// <summary>For tests: the deadline the wait for the stream's response headers gives up after.</summary>
    internal TimeSpan HeaderWait { get; init; } = MaxHeaderWait;
    /// <summary>The longest one REST call (headers and body) may take: with ResponseHeadersRead the client's own
    /// timeout covers only the headers, so a router that sends them and stalls the body would hold the refresh
    /// gate until shutdown without this.</summary>
    internal static readonly TimeSpan MaxRestCall = TimeSpan.FromSeconds(15);
    /// <summary>For tests: the deadline one REST call gives up after.</summary>
    internal TimeSpan RestCallTimeout { get; init; } = MaxRestCall;
    /// <summary>For tests: the first wait before the stream reconnects after a failure, and the longest the
    /// doubling wait grows to.</summary>
    internal TimeSpan StreamRetryMin { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan StreamRetryMax { get; init; } = TimeSpan.FromSeconds(30);

    private HttpClient? _http;
    private HttpClient Http => _http ??= NewHttpClient(HttpHandler);

    // The /api/events stream is long-lived: the poll client's 8-second timeout would hang it up, so it gets its own.
    private HttpClient? _sseHttp;
    private HttpClient SseHttp => _sseHttp ??= NewHttpClient(HttpHandler, infinite: true);

    private static HttpClient NewHttpClient(HttpMessageHandler? handler, bool infinite = false)
    {
        var owner = handler is null ? new SocketsHttpHandler { AllowAutoRedirect = false } : null;
        var http = new HttpClient(handler ?? owner!) { Timeout = infinite ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(8) };
        http.MaxResponseContentBufferSize = MaxBodyBytes;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        return http;
    }

    // A snapshot is published together with the router that produced it: an older refresh that finishes after
    // the address moved must never be shown as the new router's data.
    private sealed record PublishedSnapshot(Uri Url, LlamaSwapSnapshot Snapshot);
    private volatile PublishedSnapshot? _published;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The active REST refresh: an address change cancels the retired walk, so a stalled body cannot hold the
    // gate - and the new router's first snapshot - until shutdown.
    // Taken and released with Interlocked only: a retired call's cleanup must never erase a newer call's handle.
    private CancellationTokenSource? _activeRefresh;
    // Cancelled when the address changes: it ends the stream loop's reconnect wait (up to 30 s after a run of
    // failures, or with no address at all), so a newly entered address shows its in-flight list at once.
    private CancellationTokenSource _streamWake = new();
    // Cancelled when the effective refresh interval changes, so a saved new interval applies now, not after the
    // old one (up to an hour) has run out.
    private CancellationTokenSource _pollWake = new();
    private int _addressGeneration;
    // The active /api/events read, if any: a cleared or changed address cancels it - a quiet old router would
    // otherwise keep the old stream, and its in-flight list, for the rest of the stream's lifetime.
    // Like the refresh handle, taken and released with Interlocked only.
    private CancellationTokenSource? _activeRead;
    private Uri? _lastUrl;

    /// <summary>The last refresh; null while nothing has been polled yet or the address is not set. Also null
    /// the moment the address changes and until the next refresh: the snapshot belongs to the router the old
    /// address pointed at, and it must not keep showing as if it were the new router's.</summary>
    public LlamaSwapSnapshot? Snapshot()
        => HasAddress && _published is { } published && UrlMatches(published.Url) ? published.Snapshot : null;

    /// <summary>What /api/llamaswap answers with: the snapshot and the live rows of one router. An offline snapshot
    /// carries no rows (a stream that reconnected before the next good poll must not keep the phone on its fast
    /// in-flight poll), and rows read while the address moved may be the new router's, so the answer is then
    /// "loading" (null) rather than the old router's numbers with the new router's requests.</summary>
    public (LlamaSwapSnapshot? Snapshot, IReadOnlyList<LlamaSwapInflight> Inflight) ReadForPhone()
    {
        var published = _published;
        if (!HasAddress || published is null || !UrlMatches(published.Url)) return (null, []);
        if (!published.Snapshot.Connected) return (published.Snapshot, []);
        var rows = Inflight;
        return UrlMatches(published.Url) ? (published.Snapshot, rows) : (null, []);
    }

    /// <summary>Whether an address is configured at all. The endpoint reports it separately from the snapshot:
    /// the snapshot is also null before the first refresh has finished, and the widget must say "set the
    /// address", not "offline", in that case.</summary>
    public bool HasAddress
        => Uri.TryCreate(Url(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private readonly object _inflightLock = new();
    private readonly List<LlamaSwapInflight> _inflight = new();
    // When each request first showed up, so its elapsed time keeps growing between events instead of freezing at the
    // last one the router sent.
    private readonly Dictionary<string, DateTime> _firstSeen = new();

    /// <summary>What leaves the machine per in-flight request: the id, the model, and how long the request has
    /// been running. Path and RespBytes stay on the server; the /api/llamaswap handler builds its answer from
    /// this projection only.</summary>
    public sealed record LlamaSwapInflightView(string Id, string Model, int ElapsedMs);
    public static IReadOnlyList<LlamaSwapInflightView> InflightView(IReadOnlyList<LlamaSwapInflight> inflight)
        => inflight.Select(r => new LlamaSwapInflightView(r.Id, r.Model, r.ElapsedMs)).ToList();

    /// <summary>The requests the router is serving right now, from the /api/events stream. ElapsedMs is live: it is
    /// measured against the UTC clock, not frozen at the last event the router sent. Entries that survived a
    /// dropped remove event are dropped after <see cref="MaxInflightAge"/>, so a lost event can never pin the
    /// in-flight count (and the phone's fast poll) forever.</summary>
    public IReadOnlyList<LlamaSwapInflight> Inflight { get {
        lock (_inflightLock)
        {
            var now = UtcNow();
            var stale = _inflight.Where(r => now - _firstSeen[r.Id] > MaxInflightAge).Select(r => r.Id).ToList();
            foreach (var id in stale)
                _firstSeen.Remove(id);
            _inflight.RemoveAll(r => stale.Contains(r.Id));
            return _inflight
                .Select(r => r with { ElapsedMs = (int)Math.Min(Math.Max(0, (now - _firstSeen[r.Id]).TotalMilliseconds), int.MaxValue) })
                .ToList();
        }
    } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config is null) return; // tests drive RefreshAsync directly
        await Task.WhenAll(PollLoopAsync(stoppingToken), StreamLoopAsync(stoppingToken), AddressWatchAsync(stoppingToken));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wake = Volatile.Read(ref _pollWake);
            await RefreshAsync(ct);
            using var waitCt = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(RefreshSeconds()), waitCt.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    private int RefreshSeconds() => Math.Clamp(config.Get()?.LlamaSwap.RefreshSeconds ?? 60, 10, 3600);

    private void WakePoll()
    {
        var old = Interlocked.Exchange(ref _pollWake, new CancellationTokenSource());
        old.Cancel();
    }

    // RefreshAsync sees an address change only on its poll interval, and a quiet stream sits blocked in its
    // read until then: this loop notices it itself, every second. The check is cheap - the config store re-reads
    // the file only when its write time moved - so it costs a file-stat between polls.
    private async Task AddressWatchAsync(CancellationToken ct)
    {
        var last = BaseOf(Url());
        var lastInterval = RefreshSeconds();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var interval = RefreshSeconds();
            if (interval != lastInterval)
            {
                // a saved new interval: refresh now and wait the new interval from here
                lastInterval = interval;
                WakePoll();
            }
            var cur = BaseOf(Url());
            if (!Uri.Equals(cur, last))
            {
                last = cur;
                CancelRefresh();
                EndStream();
                ClearInflight();
                WakeStream();
                // A new address is a new router: refresh it at once, not at the next poll (which can be an hour
                // away at the maximum interval). Run it off the watcher so another address change is still
                // noticed, and let the newest change cancel the retired walk.
                var generation = ++_addressGeneration;
                _ = RefreshAfterAddressChangeAsync(ct, generation);
            }
        }
    }

    private async Task RefreshAfterAddressChangeAsync(CancellationToken ct, int generation)
    {
        if (generation != _addressGeneration) return;
        try { await RefreshAsync(ct); }
        catch (OperationCanceledException) { }
    }

    /// <summary>The configured address as a base URI, or null when it is not one: the watcher and the refresh
    /// must treat <c>http://h:1/</c> and <c>http://h:1</c> as the same router, so both compare Uris, not strings.</summary>
    private static Uri? BaseOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u : null;

    // The router pushes its in-flight request list over the same event stream its own web UI subscribes to. The
    // stream is the only view of the requests while they run: the REST endpoints only know the finished ones.
    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var backoff = StreamRetryMin;
        while (!ct.IsCancellationRequested)
        {
            // Taken before the attempt: an address change during the attempt then skips the wait after it, too.
            var wake = Volatile.Read(ref _streamWake);
            var ok = false;
            try
            {
                if (Uri.TryCreate(Url(), UriKind.Absolute, out var baseUri) && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
                {
                    await StreamOnceAsync(baseUri, ct);
                    ok = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "llama-swap events stream failed");
                // No stream is open now (this loop runs one at a time), so nothing will send the remove events
                // for the rows the last one left: drop them instead of reporting them live until the next good
                // connection or MaxInflightAge.
                ClearInflight();
            }
            backoff = ok ? StreamRetryMin : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, StreamRetryMax.Ticks));
            using (var waitCt = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token))
            {
                try { await Task.Delay(backoff, waitCt.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            // A new address starts over: its first failure must not wait as long as the old address's last one.
            if (wake.IsCancellationRequested) backoff = StreamRetryMin;
        }
    }

    /// <summary>Reads one /api/events stream until the server closes it. The router resends the whole in-flight
    /// list as a snapshot on every connection, so the state needs no handshake.</summary>
    internal async Task StreamOnceAsync(Uri baseUri, CancellationToken ct = default)
    {
        // The read's cancellation is registered before the request goes out: EndStream (a cleared or changed
        // address) must reach the wait for the response headers, too, not only a read that is already running.
        using var readCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _activeRead, readCt);
        try
        {
            // an address cleared or changed before the handle was registered found nothing to cancel: do not send
            if (!UrlMatches(baseUri)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "api/events"));
            req.Headers.Accept.ParseAdd("text/event-stream");
            using var headerDeadline = new CancellationTokenSource(HeaderWait);
            using var headerCt = CancellationTokenSource.CreateLinkedTokenSource(ct, readCt.Token, headerDeadline.Token);
            using var res = await SseHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCt.Token);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"llama-swap /api/events -> {(int)res.StatusCode}");
            // A proxy or web app that answers 200 to any path is not an event stream: treating it as a healthy
            // connection would reconnect once a second forever. Only a real event stream resets the backoff.
            if (!string.Equals(res.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"llama-swap /api/events -> {res.Content.Headers.ContentType?.MediaType ?? "no content type"}");
            // The lifetime starts once the stream is established: a slow answer to the request (or the header
            // wait) must not spend the healthy stream's ten minutes before the first line is read.
            using var lifetime = new CancellationTokenSource(StreamLifetime);
            using var streamCt = CancellationTokenSource.CreateLinkedTokenSource(readCt.Token, lifetime.Token);
            ClearInflight();
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            try { await ReadLoopAsync(reader, baseUri, ct, streamCt); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        }
        finally
        {
            // only this read's own handle is released: a newer stream registered meanwhile keeps its own
            Interlocked.CompareExchange(ref _activeRead, null, readCt);
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, Uri baseUri, CancellationToken ct, CancellationTokenSource readCt)
    {
        try
        {
            // The loop ends, not just the connection: a cleared or changed address stops the stream - the old
            // router's events would otherwise keep feeding the new one - and the lifetime ends a healthy stream
            // the same way the server closing it does, a normal end, not a failure.
            while (!ct.IsCancellationRequested && UrlMatches(baseUri))
            {
                var line = await ReadLineCappedAsync(reader, readCt.Token);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line["data:".Length..].TrimStart();
                JsonDocument doc;
                try { doc = JsonDocument.Parse(payload); } catch (JsonException) { continue; }
                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "inflight") continue;
                    if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                    if (data.ValueKind == JsonValueKind.String) ApplyInflightEvent(data.GetString()!, baseUri, readCt.Token);
                    else if (data.ValueKind == JsonValueKind.Object) ApplyInflightEvent(data.GetRawText(), baseUri, readCt.Token);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // The lifetime, or a cleared or changed address, ended the read: a normal end, so the reconnect loop
            // counts it as one and opens the stream to the current address.
        }
    }

    /// <summary>Reads one stream line, discarding a line longer than <see cref="MaxLineChars"/> instead of growing
    /// an unbounded buffer. A discarded line returns an empty string, which the loop skips; null means EOF.</summary>
    private static async Task<string?> ReadLineCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new char[1];
        var oversized = false;
        while (true)
        {
            if (await reader.ReadAsync(one.AsMemory(0, 1), ct) == 0)
                return oversized ? "" : line.Length == 0 ? null : TrimCr(line).ToString();
            var c = one[0];
            if (c == '\n') return oversized ? "" : TrimCr(line).ToString();
            if (oversized) continue;
            if (line.Length >= MaxLineChars) { oversized = true; line.Clear(); continue; }
            line.Append(c);
        }
    }

    private static StringBuilder TrimCr(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r') line.Length--;
        return line;
    }

    /// <summary>Whether the configured address is still the one this stream came from. Comparing Uris, not
    /// strings: the settings may show <c>http://h:1/</c> where the stream came from <c>http://h:1</c>.</summary>
    private bool UrlMatches(Uri baseUri)
        => Uri.TryCreate(Url(), UriKind.Absolute, out var cur) && cur == baseUri;

    /// <summary>Cancels the /api/events read, if there is one: a cleared or changed address must not keep the
    /// old router's stream - quiet or not - feeding the panel until the stream's lifetime runs out.</summary>
    private void EndStream()
    {
        // One atomic take: a read and a separate null write would erase a stream registered in between,
        // which then could not be stopped by a cleared address.
        var read = Interlocked.Exchange(ref _activeRead, null);
        try { read?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // the read loop already ended and disposed its token source: there is nothing left to cancel
        }
    }

    /// <summary>Ends the stream loop's reconnect wait: called on an address change, so the new address is tried
    /// at once and its backoff starts over. The old source is cancelled, not disposed: the loop may still be
    /// reading its token.</summary>
    private void WakeStream()
    {
        var old = Interlocked.Exchange(ref _streamWake, new CancellationTokenSource());
        old.Cancel();
    }

    /// <summary>Cancels the REST refresh in flight, if any: an address change must not keep waiting for the old
    /// router's stalled body while the new router has no snapshot yet.</summary>
    private void CancelRefresh()
    {
        var refresh = Interlocked.Exchange(ref _activeRefresh, null);
        try { refresh?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // the refresh already finished and disposed its token source: there is nothing left to cancel
        }
    }

    /// <summary>
    /// Applies one in-flight event from the router: "snapshot" replaces the whole list, "upsert" adds or updates
    /// one request, "remove" drops it. Malformed events and entries are dropped, not fatal: a bad entry must
    /// not wipe the list it arrives in.
    /// </summary>
    /// <param name="from">The router the event came from, when it comes from the stream: a line already read can
    /// land after the address changed and <see cref="RefreshAsync"/> cleared the list, so it is dropped, not
    /// applied - the old router's request ids, models and elapsed times are not the new router's.</param>
    /// <param name="stream">The read the event came from: a line already read when a failed poll ended the stream
    /// is dropped, not applied after the list was cleared and the offline state published. Callers end the
    /// stream before they clear the list, and the check runs under the list's lock, so a late line either lands
    /// before the clear or not at all.</param>
    internal void ApplyInflightEvent(string payload, Uri? from = null, CancellationToken stream = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var op = doc.RootElement.TryGetProperty("operation", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            lock (_inflightLock)
            {
                if (stream.IsCancellationRequested || (from is not null && !UrlMatches(from))) return;
                var now = UtcNow();
                void Add(LlamaSwapInflight r)
                {
                    // a re-upsert of the same request keeps its original start; only a first sighting sets it
                    if (!_firstSeen.ContainsKey(r.Id)) _firstSeen[r.Id] = now - TimeSpan.FromMilliseconds(r.ElapsedMs);
                    _inflight.Add(r);
                }
                if (op == "snapshot")
                {
                    // A snapshot without a requests array is malformed: keep the live list it cannot replace.
                    // An explicit empty array is a valid replacement and clears the list.
                    if (!doc.RootElement.TryGetProperty("requests", out var rs) || rs.ValueKind != JsonValueKind.Array) return;
                    _inflight.Clear();
                    _firstSeen.Clear();
                    foreach (var r in rs.EnumerateArray())
                        if (Entry(r) is { } r1) Add(r1);
                }
                else if (op == "upsert" && doc.RootElement.TryGetProperty("request", out var one) && Entry(one) is { } r2)
                {
                    _inflight.RemoveAll(r => r.Id == r2.Id);
                    Add(r2);
                }
                else if (op == "remove" && doc.RootElement.TryGetProperty("id", out var id) && IdOf(id) is { } gone)
                {
                    _inflight.RemoveAll(r => r.Id == gone);
                    _firstSeen.Remove(gone);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "llama-swap in-flight event did not parse");
        }
    }

    /// <summary>The id in the one spelling this side keeps: a numbered request is stored as its number, so an
    /// upsert and its remove always meet.</summary>
    private static string? IdOf(JsonElement id)
        => id.ValueKind == JsonValueKind.String ? id.GetString()
         : id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture)
         : null;

    private static LlamaSwapInflight? Entry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id)) return null;
        // the router may number a request instead of naming it: both ids are opaque to this side, and a remove
        // must find an upsert either way, so the two kinds share one translation
        if (IdOf(id) is not { } idStr) return null;
        static string S(JsonElement p, string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        // absent stays 0 (the router omits a field it has not filled yet); present but not an integer is
        // malformed, and drops the entry, not the list
        static long? N(JsonElement p, string k)
            => !p.TryGetProperty(k, out var v) ? 0
             : v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
             : (long?)null;
        if (N(e, "elapsed_ms") is not { } elapsed || N(e, "resp_bytes") is not { } bytes) return null;
        return new LlamaSwapInflight(idStr, S(e, "model"), S(e, "req_path"), (int)Math.Min(elapsed, int.MaxValue), bytes);
    }

    private void ClearInflight()
    {
        lock (_inflightLock) { _inflight.Clear(); _firstSeen.Clear(); }
    }

    /// <summary>Reads the three endpoints once. Concurrent refreshes are serialized, not duplicated.</summary>
    internal async Task RefreshAsync(CancellationToken ct = default)
    {
        var url = Url();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            CancelRefresh();
            _published = null;
            // No (valid) address: the phone must not keep seeing the last router's requests as in flight.
            EndStream();
            ClearInflight();
            _lastUrl = null;
            return;
        }
        if (_lastUrl is not null && _lastUrl != baseUri)
        {
            // A new address is a new router: the old in-flight list, the stream to the old router, and the old
            // snapshot belong to the old server. Until the new one has answered, the panel says "loading" - the
            // old router's version and counts must not keep showing as if they were the new router's.
            CancelRefresh();
            EndStream();
            ClearInflight();
            WakeStream();
            _published = null;
        }
        _lastUrl = baseUri;
        using var refreshCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gateHeld = false;
        try
        {
            await _gate.WaitAsync(refreshCt.Token);
            gateHeld = true;
            // Only the gate holder is cancelable: a waiter registered earlier would replace the holder's handle, and
            // an address change would then cancel the waiter instead of the walk to the old router. A waiter whose
            // address changed meanwhile sends nothing.
            Volatile.Write(ref _activeRefresh, refreshCt);
            if (!UrlMatches(baseUri)) return;
            var version = await GetAsync(baseUri, "api/version", refreshCt.Token);
            if (version is null)
            {
                // A router that no longer answers is offline, whatever the stream's local state says: a powered
                // off host sends no reset, so keeping the old list would leave stale rows and the fast poll up
                // for the stream's lifetime. The next connection fills the list again if the router is alive.
                if (UrlMatches(baseUri) && !refreshCt.IsCancellationRequested)
                {
                    EndStream();
                    ClearInflight();
                    Publish(baseUri, Offline());
                }
                return;
            }
            var models = await GetOptionalAsync(baseUri, "v1/models", refreshCt.Token);
            var today = await ReadActivityAsync(baseUri, refreshCt.Token);
            if (refreshCt.IsCancellationRequested || !UrlMatches(baseUri)) return;

            var inflight = Inflight; // one read: the count and the list it belongs to must agree
            Publish(baseUri, new LlamaSwapSnapshot(
                true, null, ParseVersion(version), ParseLoadedModels(models),
                today.Tps, today.Requests, today.Input, today.Output,
                today.LastRequestAt, inflight.Count, inflight));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // A retired refresh: its address is no longer the configured one, so it publishes nothing.
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "llama-swap refresh failed");
            if (!refreshCt.IsCancellationRequested && UrlMatches(baseUri))
            {
                EndStream();
                ClearInflight();
                Publish(baseUri, Offline());
            }
        }
        finally
        {
            if (gateHeld) _gate.Release();
            Interlocked.CompareExchange(ref _activeRefresh, null, refreshCt);
        }
    }

    private void Publish(Uri baseUri, LlamaSwapSnapshot snapshot)
    {
        if (UrlMatches(baseUri)) _published = new PublishedSnapshot(baseUri, snapshot);
    }

    private LlamaSwapSnapshot Offline() => new(false, "offline", null, [], null, null, null, null, null, 0, []);

    private string Url()
    {
        // Config.Normalize keeps the trailing slash when the address has a path, and the relative endpoints
        // (api/version, api/events) must resolve under that path - so it is kept, not trimmed.
        var u = UrlOverride ?? config?.Get()?.LlamaSwap.Url ?? "";
        return u.Trim();
    }

    private async Task<JsonElement?> GetAsync(Uri baseUri, string path, CancellationToken ct)
    {
        using var callCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCt.CancelAfter(RestCallTimeout);
        try
        {
            using var res = await Http.GetAsync(new Uri(baseUri, path), HttpCompletionOption.ResponseHeadersRead, callCt.Token);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength is > MaxBodyBytes) return null;
            await using var body = await res.Content.ReadAsStreamAsync(callCt.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var n = await body.ReadAsync(chunk, callCt.Token);
                if (n == 0) break;
                if (buffer.Length + n > MaxBodyBytes) return null;
                buffer.Write(chunk, 0, n);
            }
            buffer.Position = 0;
            using var doc = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }, callCt.Token);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // This call's own deadline: the activity walk treats it as an incomplete page, and the version
            // poll as an unavailable endpoint. A host or retired-refresh cancellation still propagates.
            return null;
        }
    }

    /// <summary>An endpoint the snapshot can do without (the models, the request log): a reset connection or a
    /// truncated body there is missing data, not an offline router - only the version poll decides that. Host
    /// shutdown and a retired refresh still cancel.</summary>
    private async Task<JsonElement?> GetOptionalAsync(Uri baseUri, string path, CancellationToken ct)
    {
        try { return await GetAsync(baseUri, path, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            log.LogDebug(ex, "llama-swap {Path} unavailable", path);
            return null;
        }
    }

    private static string? ParseVersion(JsonElement? el)
        => el is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> ParseLoadedModels(JsonElement? el)
    {
        var list = new List<string>();
        if (el is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var loaded = m.TryGetProperty("status", out var st)
                    && st.ValueKind == JsonValueKind.Object
                    && st.TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.String
                    && v.GetString() == "loaded";
                if (!loaded || !m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                list.Add(id.GetString()!);
            }
        }
        return list;
    }

    private static long? Num(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    /// <summary>
    /// Today's counts from the request log, asked for the local day's range so the count is exact: the page's
    /// total is the number of requests since local midnight, and every page it reports is summed for the tokens.
    /// The generation speed is the newest finished request's own measurement, the one the panel shows as "Speed".
    /// Null fields when the log endpoint is unavailable. When no request has finished today, the newest entry
    /// overall still supplies the speed and the time of the last request.
    /// </summary>
    private async Task<(int? Requests, long? Input, long? Output, DateTime? LastRequestAt, double? Tps)> ReadActivityAsync(Uri baseUri, CancellationToken ct)
    {
        var day = Now().Date;
        var start = Uri.EscapeDataString(new DateTimeOffset(day).ToString("o", CultureInfo.InvariantCulture));
        var firstPage = await GetOptionalAsync(baseUri, $"api/metrics/activity?start={start}&limit={ActivityWindow}&page=1", ct);
        if (firstPage is not { ValueKind: JsonValueKind.Object } page
            || !page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return (null, null, null, null, null);
        int? total = page.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
            && totalEl.TryGetInt32(out var totalN) && totalN >= 0 ? totalN : null;
        long input = 0, output = 0, inDay = 0, seen = 0;
        DateTime? last = null;
        double? tps = null;
        var newestRead = false;
        var sawOlder = false;
        var complete = true;
        var rowIds = new HashSet<long>();
        // The newest row id of the first page: the total is the first page's, so a row with a higher id finished
        // after the walk began and belongs to neither the count nor the sums.
        long? newestId = null;
        for (var pageNo = 1; ; pageNo++)
        {
            if (pageNo > 1)
            {
                var next = await GetOptionalAsync(baseUri, $"api/metrics/activity?start={start}&limit={ActivityWindow}&page={pageNo}", ct);
                if (next is not { ValueKind: JsonValueKind.Object } nextPage
                    || !nextPage.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Array) { complete = false; break; }
                // Without row ids a shift cannot be told apart from new rows: a total that moved leaves the sums unknown.
                if (newestId is null && total is { } before && nextPage.TryGetProperty("total", out var nowEl)
                    && nowEl.ValueKind == JsonValueKind.Number && nowEl.TryGetInt32(out var nowN) && nowN != before) { complete = false; break; }
            }
            else
            {
                foreach (var entry in data.EnumerateArray())
                    if (RowIdOf(entry) is { } id && (newestId is null || id > newestId)) newestId = id;
            }
            // rows that do not count (not an object, already counted, or newer than the walk) do not cover the total
            var skipped = 0;
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                // The router pages by offset, newest first: requests that finish between two pages push the last
                // rows of one page onto the next. Their ids are the same, so they are counted once - and they do not
                // count towards coverage either, or the walk would stop short of the total. The new rows that pushed
                // them have higher ids than the first page's newest and are left out.
                if (RowIdOf(entry) is { } rowN && ((pageNo > 1 && rowN > newestId) || !rowIds.Add(rowN))) { skipped++; continue; }
                if (!newestRead) { newestRead = true; tps = TpsOf(entry); }
                var ts = TimestampOf(entry);
                if (ts is not null)
                {
                    if (last is null || ts > last) last = ts;
                    // The day range should already exclude yesterday; keep the guard for a router that ignores it.
                    // Entries are newest first, so the first older one ends the walk and the count is what we saw.
                    if (ts < day) { sawOlder = true; break; }
                }
                inDay++;
                input += entry.TryGetProperty("tokens", out var tk) && tk.ValueKind == JsonValueKind.Object ? Num(tk, "input_tokens") ?? 0 : 0;
                output += entry.TryGetProperty("tokens", out var tk2) && tk2.ValueKind == JsonValueKind.Object ? Num(tk2, "output_tokens") ?? 0 : 0;
            }
            if (sawOlder) break;
            // Coverage is the distinct rows that actually arrived, not the page size we asked for: a router may cap
            // ?limit lower than 999, and one that omits ?total is walked until an empty page. A walk that cannot
            // reach the total within the page cap reports unknown token sums.
            seen += data.GetArrayLength() - skipped;
            if (total is { } expected)
            {
                if (seen >= expected) break;
                if (data.GetArrayLength() == 0) { complete = false; break; }
            }
            else if (data.GetArrayLength() == 0) break;
            if (pageNo >= MaxActivityPages) { complete = false; break; }
        }
        // An empty day, whether the router said so (total 0) or a complete walk without a total found nothing.
        if ((total is 0 || (total is null && complete && inDay == 0)) && last is null)
        {
            // Nothing finished today: the widget still shows the newest finished request's speed and time.
            var newest = await GetOptionalAsync(baseUri, "api/metrics/activity?limit=1&page=1", ct);
            if (newest is { ValueKind: JsonValueKind.Object } n
                && n.TryGetProperty("data", out var nd) && nd.ValueKind == JsonValueKind.Array
                && nd.GetArrayLength() > 0 && nd[0].ValueKind == JsonValueKind.Object)
            {
                tps = TpsOf(nd[0]);
                last = TimestampOf(nd[0]);
            }
        }
        var requests = sawOlder
            ? (complete ? (int?)Math.Min(inDay, int.MaxValue) : null)
            : total ?? (complete ? (int?)Math.Min(inDay, int.MaxValue) : null);
        return (requests, complete ? input : null, complete ? output : null, last, tps);
    }

    private static long? RowIdOf(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var id)
           && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) ? n : null;

    private static double? TpsOf(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object
           && entry.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
           && tokens.TryGetProperty("tokens_per_second", out var tps) && tps.ValueKind == JsonValueKind.Number
           && tps.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    private static DateTime? TimestampOf(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object
           && entry.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
           && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime() : null;

    public override void Dispose()
    {
        _http?.Dispose();
        _sseHttp?.Dispose();
        _gate.Dispose();
        base.Dispose();
    }
}
