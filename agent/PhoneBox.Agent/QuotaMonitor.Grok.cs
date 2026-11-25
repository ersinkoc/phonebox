using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class QuotaMonitor
{
    /// <summary>The Grok CLI's auth file (read only). Tests point it at a file of their own.</summary>
    internal string GrokAuthPath { get; init; } = GrokQuota.DefaultAuthPath();

    private volatile QuotaProvider? _grok;
    private long _grokNextAt;
    private int _grokFailures;
    private int _grokOffline;
    private string? _grokRefused;
    private string? _grokPlan;
    private string? _grokPlanPrint;
    private long _grokPlanAt;
    private long _grokRefusedAt;

    internal const long GrokHealthyMs = 180_000;
    private const long GrokTokenRecheckMs = 60_000;
    private const long GrokRefusedRetryMs = 1_800_000;
    private const long GrokOfflineRetryMs = 30_000;
    internal const int GrokOfflineRetries = 3;
    private const long GrokUnrecognizedMs = 900_000;
    private const long GrokMaxWaitMs = 3_605_000;
    internal const long GrokPlanCacheMs = 6 * 3_600_000;
    private const string GrokRefusedMessage = "Grok did not accept the sign-in; it will refresh when Grok runs";

    private enum GrokResult { Ok, Limited, Refused, Offline, Unrecognized, Failed }

    /// <summary>
    /// One pass of the usage loop for Grok. The bearer in the auth file is sent to the CLI billing host and
    /// nowhere else. It is not refreshed and not written into the quota snapshot.
    /// </summary>
    internal async Task GrokTickAsync(long nowMs, CancellationToken ct)
    {
        // nowMs was read before this pass; the auth read and the request take time too.
        var started = Environment.TickCount64;
        // Only while an enabled page shows Grok quotas: otherwise the auth file is not read and nothing is sent.
        if (!IsGrokEnabled())
        {
            _grok = null;
            return;
        }
        // A clock set back would otherwise hold the next check for as long as it moved; a clock set forward
        // keeps a pending Retry-After instead of asking at once.
        if (_grokNextAt - nowMs > GrokMaxWaitMs) _grokNextAt = nowMs + GrokMaxWaitMs;
        if (nowMs < _grokNextAt) return;

        string? json = null;
        try
        {
            if (File.Exists(GrokAuthPath)) json = await File.ReadAllTextAsync(GrokAuthPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // the CLI may be rewriting the file; the next pass reads it
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            _grok = null;
            _grokNextAt = nowMs + GrokHealthyMs;
            return;
        }

        var auth = GrokQuota.ReadAuth(json, nowMs);
        if (auth.Kind != GrokQuota.AuthKind.Ready || auth.SignIn is null)
        {
            _grok = KeepGrok(_grok?.Plan ?? "Grok", auth.Kind == GrokQuota.AuthKind.Expired
                ? "Token expired; it will refresh when Grok runs" : "Not signed in to Grok");
            _grokNextAt = nowMs + GrokTokenRecheckMs;
            return;
        }

        var signIn = auth.SignIn;
        var print = Fingerprint(signIn.Token);
        if (print == _grokRefused && nowMs - _grokRefusedAt is >= 0 and < GrokRefusedRetryMs)
        {
            _grok = KeepGrok(signIn.PlanFallback, GrokRefusedMessage);
            _grokNextAt = nowMs + GrokTokenRecheckMs;
            return;
        }

        var (result, retryAfter) = await FetchGrok(signIn, nowMs, ct);
        // Retry-After counts from the answer, not from when the request left.
        var answeredMs = nowMs + Math.Max(0, Environment.TickCount64 - started);
        long next;
        switch (result)
        {
            case GrokResult.Ok:
                _grokFailures = _grokOffline = 0;
                next = nowMs + GrokHealthyMs;
                break;
            case GrokResult.Refused:
                _grokRefused = print;
                _grokRefusedAt = nowMs;
                next = nowMs + GrokTokenRecheckMs;
                break;
            case GrokResult.Offline when _grokOffline <= GrokOfflineRetries:
                next = nowMs + GrokOfflineRetryMs;
                break;
            case GrokResult.Unrecognized:
                next = nowMs + GrokUnrecognizedMs;
                break;
            case GrokResult.Limited:
                next = answeredMs + (long)Math.Clamp((retryAfter ?? TimeSpan.FromMinutes(5)).TotalMilliseconds, 0, 3_600_000);
                _grok = KeepGrok(signIn.PlanFallback, $"Grok is limiting quota checks; trying again at {DateTimeOffset.FromUnixTimeMilliseconds(next).ToLocalTime():HH:mm}");
                break;
            default:
                if (result != GrokResult.Offline) _grokOffline = 0;
                next = nowMs + GrokBackoffMs(++_grokFailures);
                break;
        }
        _grokNextAt = next;
    }

    private async Task<(GrokResult Result, TimeSpan? RetryAfter)> FetchGrok(GrokQuota.SignIn signIn, long nowMs, CancellationToken ct)
    {
        try
        {
            using var req = GrokRequest(GrokQuota.BillingUrl, signIn.Token);
            using var res = await Http.SendAsync(req, ct);
            _grokOffline = 0;
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return (GrokResult.Limited, RetryAfter(res.Headers.RetryAfter) ?? TimeSpan.FromMinutes(5));
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _grok = KeepGrok(signIn.PlanFallback, GrokRefusedMessage);
                return (GrokResult.Refused, null);
            }
            if (!res.IsSuccessStatusCode)
            {
                log.LogDebug("grok quota: HTTP {Status}", (int)res.StatusCode);
                _grok = KeepGrok(signIn.PlanFallback, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
                return (GrokResult.Failed, null);
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            var credits = GrokQuota.ParseCredits(body, nowMs);
            if (credits is null)
            {
                log.LogDebug("grok quota: unrecognized reply ({Length} bytes)", body.Length);
                _grok = KeepGrok(signIn.PlanFallback, "Grok sent a quota reply this version doesn't recognize");
                return (GrokResult.Unrecognized, null);
            }
            var plan = await GrokPlan(signIn.Token, nowMs, ct) ?? credits.Value.Tier ?? signIn.PlanFallback;
            List<QuotaLimit> limits = [];
            if (credits.Value.Percent is { } pct)
                limits.Add(new QuotaLimit(credits.Value.Label, pct, credits.Value.ResetsAt, credits.Value.WindowSec, "normal"));
            var error = limits.Count == 0 && !credits.Value.HasPrepaid ? "Grok did not report a usage percent" : null;
            _grok = new QuotaProvider("grok", "Grok", plan, limits, credits.Value.Extra, nowMs, error);
            return (GrokResult.Ok, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            log.LogDebug("grok quota: {Msg}", ex.Message);
            if (++_grokOffline > GrokOfflineRetries || _grok is not { Limits.Count: > 0 })
                _grok = KeepGrok(signIn.PlanFallback, "Could not reach Grok: " + ex.Message);
            return (GrokResult.Offline, null);
        }
        catch (Exception ex)
        {
            log.LogDebug("grok quota: {Msg}", ex.Message);
            _grok = KeepGrok(signIn.PlanFallback, "Could not fetch quota: " + ex.Message);
            return (GrokResult.Failed, null);
        }
    }

    /// <summary>
    /// The plan name from Grok's settings, kept for a few hours: it rarely changes, and this halves the requests.
    /// Asked again for another sign-in, and never once Grok is no longer shown (the setting can change mid-request).
    /// </summary>
    private async Task<string?> GrokPlan(string token, long nowMs, CancellationToken ct)
    {
        var print = Fingerprint(token);
        if (_grokPlan is { } cached && print == _grokPlanPrint && nowMs - _grokPlanAt is >= 0 and < GrokPlanCacheMs)
            return cached;
        if (!IsGrokEnabled()) return print == _grokPlanPrint ? _grokPlan : null;
        var plan = await PlanFromSettings(token, ct);
        if (plan is not null)
        {
            _grokPlan = plan;
            _grokPlanPrint = print;
            _grokPlanAt = nowMs;
        }
        return plan ?? (print == _grokPlanPrint ? _grokPlan : null);
    }

    /// <summary>The plan name is optional. Two seconds is the whole budget, so a stuck call cannot hold the usage we already have.</summary>
    private async Task<string?> PlanFromSettings(string token, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var req = GrokRequest(GrokQuota.SettingsUrl, token);
            using var res = await Http.SendAsync(req, budget.Token);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(budget.Token);
            return GrokQuota.ParseSettingsPlan(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            log.LogDebug("grok plan: {Msg}", ex.Message);
            return null;
        }
    }

    private static HttpRequestMessage GrokRequest(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private static long GrokBackoffMs(int failures) =>
        (long)TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Clamp(failures, 1, 10) - 1))).TotalMilliseconds;

    private QuotaProvider KeepGrok(string plan, string error) =>
        _grok is { Limits.Count: > 0 } prev ? prev with { Plan = plan, Error = error }
            : new QuotaProvider("grok", "Grok", plan, [], null, null, error);
}
