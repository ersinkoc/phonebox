using System.Net.Http;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Latest posts from the East Money stock forum (guba.eastmoney.com).
/// The list page embeds a "var article_list = {...}" JSON block; the phone never
/// talks to East Money itself, this feed is what the panel calls instead.</summary>
public static partial class GubaFeed
{
    private const int MaxPosts = 15;                    // hard cap; the widget picks fewer
    private const long CacheMs = 10 * 60 * 1000;        // the forum does not need to be fresh
    private const long RetryMs = 5 * 60 * 1000;         // keep the retry pace gentle: East Money rate-limits hard

    private static readonly object Gate = new();
    private sealed record Entry(long Updated, List<object>? Posts, string? Error);
    private static readonly Dictionary<string, (long Attempted, Task<Entry> Request)> Cache = new(StringComparer.Ordinal);

    [GeneratedRegex(@"var article_list\s*=\s*(\{.*?\});", RegexOptions.Singleline)]
    private static partial Regex ListRe();

    [GeneratedRegex(@"\A[0-9]{6}\z")]
    private static partial Regex CodeRe();

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    /// <summary>Latest posts for a 6-digit stock code (e.g. "002317").
    /// <paramref name="count"/> is how many the widget wants (1..15).
    /// When the feed is rate-limited, the last successful list is served (staleness
    /// beats an empty widget).</summary>
    public static async Task<(List<object>? Posts, long Updated, string? Error)> LatestAsync(string? code, int count = 10)
    {
        code = (code ?? "").Trim();
        if (!CodeRe().IsMatch(code)) return (null, 0, "bad code");
        count = Math.Clamp(count, 1, MaxPosts);
        Task<Entry> request;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Cache.TryGetValue(code, out var cached);
            var previous = cached.Request is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
            request = cached.Request!;
            if (request is null || (previous is not null && now - cached.Attempted >= (previous.Error is null ? CacheMs : RetryMs)))
            {
                if (request is null && Cache.Count >= 128) Cache.Remove(Cache.MinBy(item => item.Value.Attempted).Key);
                request = FetchAsync(code, previous, now);
                Cache[code] = (now, request);
            }
        }
        var entry = await request;
        return (entry.Posts?.Take(count).ToList(), entry.Updated, entry.Error);
    }

    private static async Task<Entry> FetchAsync(string code, Entry? previous, long now)
    {
        static async Task<List<object>?> Try(Func<Task<List<object>?>> read)
        {
            try { return await read(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                or InvalidOperationException or FormatException) { return null; }
        }
        var posts = await Try(() => TryFetchPage(code)) ?? await Try(() => TryFetchApi(code));
        return posts is not null ? new Entry(now, posts, null)
            : new Entry(previous?.Updated ?? 0, previous?.Posts, previous?.Posts is null ? "fetch failed" : "stale");
    }

    /// <summary>Parse the list page's embedded article_list JSON. Returns null when the
    /// page is a captcha / rate-limit stub.</summary>
    private static async Task<List<object>?> TryFetchPage(string code)
    {
        var url = $"https://guba.eastmoney.com/list,{code},f.html";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        using var response = await Http.SendAsync(req);
        if (!response.IsSuccessStatusCode) return null;
        var html = await response.Content.ReadAsStringAsync();
        return ParsePage(html);
    }

    internal static List<object>? ParsePage(string html)
    {
        var match = ListRe().Match(html);
        if (!match.Success) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(match.Groups[1].Value);
            return ReadPosts(doc.RootElement);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Backup: the gbapi JSON endpoint (also rate-limits, sometimes later).</summary>
    private static async Task<List<object>?> TryFetchApi(string code)
    {
        var url = $"https://gbapi.eastmoney.com/webarticlelist/api/Article/Articlelist?code={code}&type=0&ps={MaxPosts}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        using var response = await Http.SendAsync(req);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return ReadPosts(doc.RootElement);
    }

    private static List<object>? ReadPosts(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !root.TryGetProperty("re", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        return Posts(list);
    }

    private static List<object> Posts(System.Text.Json.JsonElement arr)
    {
        var posts = new List<object>();
        foreach (var a in arr.EnumerateArray())
        {
            if (posts.Count >= MaxPosts) break;
            if (a.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            // skip pinned announcements and ads: they are not "latest chatter"
            if (a.TryGetProperty("post_top_status", out var top) && top.ValueKind == System.Text.Json.JsonValueKind.Number && top.TryGetInt32(out var topStatus) && topStatus != 0) continue;
            static string S(System.Text.Json.JsonElement e, string name)
            {
                // fields like post_id/user_id come back as numbers sometimes
                return e.TryGetProperty(name, out var v)
                    ? v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() ?? "" : v.ToString()
                    : "";
            }
            posts.Add(new
            {
                id = S(a, "post_id"),
                title = S(a, "post_title"),
                author = S(a, "user_nickname"),
                time = S(a, "post_publish_time"),
                reads = a.TryGetProperty("post_click_count", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number && r.TryGetInt64(out var reads) ? reads : 0,
                comments = a.TryGetProperty("post_comment_count", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetInt64(out var comments) ? comments : 0,
            });
        }
        return posts;
    }
}
