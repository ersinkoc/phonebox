using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record StockQuote(
    string Code, string Name, double Price, double PrevClose,
    double Change, double ChangePct, string Market, string Time, string? EnglishName = null);

/// <summary>Live quotes for the Stocks widget, pulled from Tencent's qt.gtimg.cn feed
/// for the watchlist in Config.Stocks. Quotes are fetched on demand and cached for ~15 s;
/// a failed fetch serves the last good answer with a flag.</summary>
public sealed partial class StockService(ConfigStore config, ILogger<StockService> log)
{
    private const int TimeoutMs = 6000;

    static StockService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly Lock _gate = new();
    private readonly HttpClient _http = Http;
    private Dictionary<string, StockQuote> _cache = [];
    private long _lastAttempt;
    private long _updatedAt;
    private string? _error;
    private string _symbolsKey = "";

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMilliseconds(TimeoutMs), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    internal StockService(ConfigStore config, ILogger<StockService> log, HttpClient http) : this(config, log)
        => _http = http;

    [GeneratedRegex(@"v_(\w+)=""([^""]*)""")]
    private static partial Regex LineRe();

    [GeneratedRegex(@"\A(?:(?:sh|sz|bj)[0-9]{6}|hk[0-9]{5}|us[a-z][a-z0-9]{0,7})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    internal static string? NormalizeCode(string? code)
    {
        var value = code?.Trim().ToLowerInvariant();
        return value is not null && CodePattern().IsMatch(value) ? value : null;
    }

    /// <summary>Quotes for the watchlist saved on the agent.</summary>
    public (List<StockQuote> Quotes, long Updated, string? Error) Snapshot()
    {
        var list = config.Get().Stocks.ToArray();
        if (list.Length == 0) return ([], 0, null);

        var key = string.Join(",", list);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (key != _symbolsKey || now - _lastAttempt > 15_000)
            {
                foreach (var stale in _cache.Keys.Except(list).ToList()) _cache.Remove(stale);
                Fetch(list, now);
                _symbolsKey = key;
                _lastAttempt = now;
            }
            var quotes = list.Select(c => _cache.TryGetValue(c, out var q) ? q : null).Where(q => q is not null).Cast<StockQuote>().ToList();
            return (quotes, _updatedAt, _error);
        }
    }

    private void Fetch(string[] symbols, long now)
    {
        try
        {
            var url = "https://qt.gtimg.cn/q=" + string.Join(",", symbols.Select(Sanitize));
            var bytes = _http.GetByteArrayAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
            // qt.gtimg.cn answers in GB18030 (some HK names use chars outside GB2312)
            var text = Encoding.GetEncoding("GB18030").GetString(bytes);
            var next = new Dictionary<string, StockQuote>();
            foreach (Match m in LineRe().Matches(text))
            {
                var q = Parse(m.Groups[1].Value, m.Groups[2].Value.Split('~'));
                if (q is not null && symbols.Contains(q.Code, StringComparer.Ordinal)) next[q.Code] = q;
            }
            if (next.Count == 0)
            {
                _error = "no quotes";
                return;
            }
            foreach (var (code, q) in next) _cache[code] = q;
            if (next.Count == symbols.Length)
            {
                _updatedAt = now;
                _error = null;
            }
            else _error = "incomplete quotes";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "stock fetch failed");
            _error = "fetch failed";
        }
    }

    /// <summary>Intraday minute prices ("分时图") for one symbol, as [hhmm, price] pairs,
    /// plus the previous close (the 0% baseline). Served through the agent so the phone
    /// never talks to the quote feed itself.</summary>
    public Task<MinuteResult> MinutesAsync(string code)
    {
        var selected = NormalizeCode(code);
        var watchlist = config.Get().Stocks;
        if (selected is null || !watchlist.Contains(selected, StringComparer.Ordinal))
            return Task.FromResult(new MinuteResult(null, 0, "", 0, "code not in watchlist"));
        lock (_minuteGate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var stale in _minutes.Keys.Except(watchlist).ToList()) _minutes.Remove(stale);
            if (_minutes.TryGetValue(selected, out var cached) && now - cached.Attempted < 30_000)
                return cached.Request;
            var previous = cached.Request is { IsCompletedSuccessfully: true } request ? request.Result : null;
            var fetch = FetchMinutesAsync(selected, previous, now);
            _minutes[selected] = (now, fetch);
            return fetch;
        }
    }

    public sealed record MinuteResult(List<object[]>? Points, double PrevClose, string Date, long Updated, string? Error);
    private readonly Lock _minuteGate = new();
    private readonly Dictionary<string, (long Attempted, Task<MinuteResult> Request)> _minutes = [];

    private async Task<MinuteResult> FetchMinutesAsync(string code, MinuteResult? previous, long now)
    {
        try
        {
            // the proxy endpoint also returns the qt block with yesterday's close
            var url = "https://proxy.finance.qq.com/ifzqgtimg/appstock/app/minute/query?code=" + Sanitize(code);
            var json = await _http.GetStringAsync(url);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var node = doc.RootElement.GetProperty("data").GetProperty(Sanitize(code));
            var root = node.GetProperty("data");
            var date = root.TryGetProperty("date", out var dt) ? dt.GetString() ?? "" : "";
            var pts = new List<object[]>();
            foreach (var row in root.GetProperty("data").EnumerateArray())
            {
                // each row: "hhmm price volume amount"
                var parts = row.GetString()?.Split(' ') ?? [];
                if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var p) && double.IsFinite(p) && p > 0)
                    pts.Add([parts[0], p]);
            }
            if (pts.Count == 0) throw new InvalidDataException("No usable minute prices");
            double prev = 0;
            if (node.TryGetProperty("qt", out var qt) &&
                qt.TryGetProperty(Sanitize(code), out var qa) && qa.GetArrayLength() > 4 &&
                double.TryParse(qa[4].GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pv) && double.IsFinite(pv) && pv > 0)
                prev = pv;
            return new MinuteResult(pts, prev, date, now, null);
        }
        catch (Exception ex)
        {
            log.LogDebug("stock minutes fetch failed: {Error}", ex.GetType().Name);
            return previous?.Points is not null ? previous with { Error = "stale" } : new(null, 0, "", 0, "fetch failed");
        }
    }

    private static string Sanitize(string code) =>
        // the feed expects lowercase exchange prefixes and a plain symbol
        code.StartsWith("us") ? "us" + code[2..].ToUpperInvariant() : code;

    private static StockQuote? Parse(string code, string[] f)
    {
        // fields: 1 name, 3 price, 4 prev close, 31 change, 32 change %, 30 quote time (A-shares)
        var normalized = NormalizeCode(code);
        if (normalized is null || f.Length < 34) return null;
        try
        {
            var name = f[1].Trim();
            var price = ParseD(f[3]);
            var prev = ParseD(f[4]);
            if (price <= 0) return null;
            var change = ParseD(f[31]);
            var pct = ParseD(f[32]);
            var market = normalized[..2] switch { "hk" => "HK", "us" => "US", _ => "A" };
            var englishName = market is "HK" or "US" && f.Length > 46 ? f[46].Trim() : "";
            return new StockQuote(normalized, name, price, prev, change, pct, market, f[30],
                englishName.Length > 0 ? englishName : null);
        }
        catch { return null; }
    }

    private static double ParseD(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
}
