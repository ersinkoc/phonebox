using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhoneBox.Agent;
using Xunit;

/// <summary>Grok credit window from fixture JSON. These tests never read ~/.grok or call the network.</summary>
public sealed class GrokQuotaTests : IDisposable
{
    private const string Token = "fixture-bearer";
    private readonly string _root = Directory.CreateTempSubdirectory("phonebox-grok-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds();

    private static readonly long Now = At("2026-11-20T12:00:00Z");

    private void WriteAuth(string json) => File.WriteAllText(Path.Combine(_root, "auth.json"), json);

    private void WriteReady(string token = Token, string expires = "2026-12-01T00:00:00Z", string name = "https://auth.x.ai::fixture") =>
        WriteAuth("{\"" + name + "\":{\"key\":\"" + token + "\",\"expires_at\":\"" + expires + "\",\"auth_mode\":\"oidc\",\"email\":\"person@example.test\"}}");

    private (QuotaMonitor Agent, Script Http, ListLog Log) Agent()
    {
        var http = new Script();
        var log = new ListLog();
        return (new QuotaMonitor(log) { GrokAuthPath = Path.Combine(_root, "auth.json"), HttpHandler = http }, http, log);
    }

    [Fact]
    public void SuperGrokEntryBeatsTheLegacyOneAndRejectsManagementKeys()
    {
        var json = """
            {
              "https://accounts.x.ai/sign-in": {"key":"legacy-bearer","expires_at":"2026-12-01T00:00:00Z","auth_mode":"oidc"},
              "https://auth.x.ai::other": {"key":"xai-management","expires_at":"2026-12-01T00:00:00Z","auth_mode":"oidc"},
              "https://auth.x.ai::fixture": {"key":"fixture-bearer","expires_at":"2026-12-01T00:00:00Z","auth_mode":"oidc"}
            }
            """;
        var auth = GrokQuota.ReadAuth(json, Now);
        Assert.Equal(GrokQuota.AuthKind.Ready, auth.Kind);
        Assert.Equal(Token, auth.SignIn!.Token);
        Assert.Equal("SuperGrok", auth.SignIn.PlanFallback);
    }

    [Fact]
    public void ExpiredAndCookieTokensAreNotUsable()
    {
        Assert.Equal(GrokQuota.AuthKind.Expired, GrokQuota.ReadAuth(
            """{"https://auth.x.ai::fixture":{"key":"fixture-bearer","expires_at":"2020-01-01T00:00:00Z","auth_mode":"oidc"}}""", Now).Kind);
        Assert.Equal(GrokQuota.AuthKind.None, GrokQuota.ReadAuth(
            """{"https://auth.x.ai::fixture":{"key":"Cookie: sso=abc","expires_at":"2026-12-01T00:00:00Z","auth_mode":"oidc"}}""", Now).Kind);
    }

    [Fact]
    public void WeeklyCreditsKeepTheBreakdownAndThePrepaidBalance()
    {
        var reading = GrokQuota.ParseCredits("""
            {"config":{
              "creditUsagePercent":41.5,
              "subscriptionTier":"supergrok",
              "currentPeriod":{"start":"2026-11-17T22:00:00Z","end":"2026-11-24T22:00:00Z"},
              "productUsage":[
                {"product":"GrokBuild","usagePercent":30},
                {"product":"GrokChat","usagePercent":11.5},
                {"product":"GrokImagine","usagePercent":0},
                {"product":"GrokAppBuilder","usagePercent":0}],
              "prepaidBalance":{"val":1446}}}
            """, Now);
        Assert.NotNull(reading);
        Assert.Equal(41.5, reading.Value.Percent);
        Assert.Equal("Weekly", reading.Value.Label);
        Assert.Equal(7 * 86400L, reading.Value.WindowSec);
        Assert.Equal(At("2026-11-24T22:00:00Z"), reading.Value.ResetsAt);
        Assert.Equal("SuperGrok", reading.Value.Tier);
        Assert.Equal("Grok Build 30% · Grok Chat 11.5% · Prepaid $14.46", reading.Value.Extra);
    }

    [Fact]
    public void OnDemandRatioDropsProductSharesAndABadShareDropsTheBreakdown()
    {
        var ratio = GrokQuota.ParseCredits("""
            {"config":{"onDemandUsed":{"val":25},"onDemandCap":{"val":50},
              "billingPeriodStart":"2026-11-01T00:00:00Z","billingPeriodEnd":"2026-12-01T00:00:00Z",
              "productUsage":[{"product":"GrokBuild","usagePercent":50}]}}
            """, Now);
        Assert.Equal(50, ratio!.Value.Percent);
        Assert.Equal("Monthly", ratio.Value.Label);
        Assert.Null(ratio.Value.Extra);

        var mismatch = GrokQuota.ParseCredits("""{"config":{"creditUsagePercent":40,"productUsage":[{"product":"GrokBuild","usagePercent":10}]}}""", Now);
        Assert.Equal(40, mismatch!.Value.Percent);
        Assert.Null(mismatch.Value.Extra);

        var broken = GrokQuota.ParseCredits("""{"config":{"creditUsagePercent":10,"productUsage":[{"product":"","usagePercent":10}]}}""", Now);
        Assert.Null(broken!.Value.Extra);

        var clamped = GrokQuota.ParseCredits("""{"config":{"creditUsagePercent":140,"productUsage":[{"product":"GrokBuild","usagePercent":140}]}}""", Now);
        Assert.Equal(100, clamped!.Value.Percent);
        Assert.Equal("Grok Build 140%", clamped.Value.Extra);
    }

    [Fact]
    public void EmptyPrepaidBalanceIsZeroAndABlankBodyIsNotAReading()
    {
        var wallet = GrokQuota.ParseCredits("""{"config":{"prepaidBalance":{}}}""", Now);
        Assert.True(wallet!.Value.HasPrepaid);
        Assert.Null(wallet.Value.Percent);
        Assert.Equal("Prepaid $0.00", wallet.Value.Extra);
        Assert.Null(GrokQuota.ParseCredits("""{"config":{}}""", Now));
        Assert.Equal("SuperGrok Heavy", GrokQuota.ParseSettingsPlan("""{"subscription_tier_display":"super grok heavy"}"""));
    }

    [Fact]
    public async Task ASignedInAccountPublishesTheCreditWindow()
    {
        WriteReady();
        var (agent, http, log) = Agent();
        http.Reply = req => req.RequestUri!.AbsolutePath.EndsWith("/settings", StringComparison.Ordinal)
            ? Json("""{"subscription_tier_display":"SuperGrok Heavy"}""")
            : Json("""{"config":{"creditUsagePercent":41.5,"subscriptionTier":"supergrok","currentPeriod":{"start":"2026-11-17T22:00:00Z","end":"2026-11-24T22:00:00Z"},"productUsage":[{"product":"GrokBuild","usagePercent":30},{"product":"GrokChat","usagePercent":11.5}],"prepaidBalance":{"val":1446}}}""");
        await agent.GrokTickAsync(Now, default);

        var grok = Assert.Single(agent.Snapshot(Now), p => p.Id == "grok");
        Assert.Equal("SuperGrok Heavy", grok.Plan);
        Assert.Equal("Weekly", Assert.Single(grok.Limits).Label);
        Assert.Equal(41.5, grok.Limits[0].Percent);
        Assert.Equal(2, http.Urls.Count);
        Assert.All(http.Tokens, t => Assert.Equal(Token, t));
        Assert.All(http.Marks, m => Assert.Equal("xai-grok-cli", m));
        var published = JsonSerializer.Serialize(agent.Snapshot(Now), ConfigStore.WriteOpts);
        Assert.DoesNotContain(Token, published, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", published, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Lines, line => line.Contains(Token, StringComparison.Ordinal));

        await agent.GrokTickAsync(Now + 60_000, default);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task AnExpiredTokenIsNotSent()
    {
        WriteReady(expires: "2020-01-01T00:00:00Z");
        var (agent, http, _) = Agent();
        http.Reply = _ => throw new InvalidOperationException("the expired token was sent");
        await agent.GrokTickAsync(Now, default);
        Assert.Empty(http.Urls);
        var grok = Assert.Single(agent.Snapshot(Now), p => p.Id == "grok");
        Assert.Contains("expired", grok.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusalIsNotLoggedAndTheSameTokenIsNotSentAgain()
    {
        WriteReady();
        var (agent, http, log) = Agent();
        http.Reply = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await agent.GrokTickAsync(Now, default);
        await agent.GrokTickAsync(Now + 60_000, default);
        Assert.Single(http.Urls);
        Assert.Contains("did not accept", Assert.Single(agent.Snapshot(Now), p => p.Id == "grok").Error, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Lines, line => line.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSignInOmitsTheProvider()
    {
        var (agent, http, _) = Agent();
        await agent.GrokTickAsync(Now, default);
        Assert.DoesNotContain(agent.Snapshot(Now), p => p.Id == "grok");
        Assert.Empty(http.Urls);

        WriteAuth("{}");
        await agent.GrokTickAsync(Now + GrokQuotaTests.Wait, default);
        Assert.Contains("Not signed in", Assert.Single(agent.Snapshot(Now), p => p.Id == "grok").Error, StringComparison.Ordinal);
        Assert.Empty(http.Urls);
    }

    private const long Wait = 180_000;

    [Fact]
    public void SavedGrokStaysAndAnUnknownProviderDoesNot()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"codexBar":{"providers":["claude","grok","cursor"]}}""");
        var saved = new ConfigStore(path).Get().CodexBar.Providers;
        Assert.Equal(["claude", "grok"], saved);

        var fresh = Path.Combine(_root, "fresh.json");
        File.WriteAllText(fresh, "{}");
        Assert.Contains("grok", new ConfigStore(fresh).Get().CodexBar.Providers);
    }

    [Fact]
    public void AFreshCodexBarReadingWinsAndAStaleOneDoesNot()
    {
        var (agent, _, _) = Agent();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var local = new QuotaProvider("grok", "Grok", "SuperGrok", [new QuotaLimit("Weekly", 10, null, 604800, "normal")], null, now, null);
        var fresh = new QuotaProvider("grok", "Grok", "CodexBar", [new QuotaLimit("Weekly", 99, null, 604800, "normal")], null, now, null, Source: "codexbar");
        agent.SetStateForTest(grok: local, external: [fresh]);
        Assert.Equal(99, Assert.Single(agent.Snapshot(now), p => p.Id == "grok").Limits[0].Percent);

        var stale = fresh with { UpdatedAt = now - 700_000 };
        agent.SetStateForTest(grok: local, external: [stale]);
        Assert.Equal(10, Assert.Single(agent.Snapshot(now), p => p.Id == "grok").Limits[0].Percent);
        Assert.Single(agent.Snapshot(now), p => p.Id == "grok");
    }

    private static HttpResponseMessage Json(string body)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK);
        res.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return res;
    }

    private const string Credits = """{"config":{"creditUsagePercent":10}}""";

    private (QuotaMonitor Agent, Script Http) Configured(string config)
    {
        WriteReady();
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, config);
        var http = new Script { Reply = _ => Json(Credits) };
        return (new QuotaMonitor(new ConfigStore(path), new ListLog()) { GrokAuthPath = Path.Combine(_root, "auth.json"), HttpHandler = http }, http);
    }

    // A second, enabled page without quotas: the agent re-enables the first page when every page is off.
    private static string Page(string widget, bool enabled = true) =>
        "{\"pages\":[{\"id\":\"p\",\"title\":\"P\",\"enabled\":" + (enabled ? "true" : "false") + ",\"widgets\":[" + widget + "]},"
        + "{\"id\":\"other\",\"title\":\"Other\",\"enabled\":true,\"widgets\":[{\"id\":\"n\",\"type\":\"note\",\"x\":0,\"y\":0,\"w\":4,\"h\":2,\"options\":{}}]}]}";

    [Theory]
    [InlineData("""{"id":"q","type":"quotas","x":0,"y":0,"w":12,"h":6,"options":{"providers":["claude","grok"]}}""", true)]
    [InlineData("""{"id":"q","type":"codexbar","x":0,"y":0,"w":12,"h":6,"options":{"providers":["grok"]}}""", true)]
    [InlineData("""{"id":"q","type":"quotas","x":0,"y":0,"w":12,"h":6,"options":{"providers":["claude","codex"]}}""", false)]
    [InlineData("""{"id":"r","type":"quotaring","x":0,"y":0,"w":3,"h":3,"options":{"provider":"grok"}}""", true)]
    [InlineData("""{"id":"r","type":"quotaring","x":0,"y":0,"w":3,"h":3,"options":{"provider":"claude"}}""", false)]
    [InlineData("""{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4,"options":{"status":true,"providers":["grok"]}}""", true)]
    [InlineData("""{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4,"options":{"status":false,"providers":["grok"]}}""", false)]
    [InlineData("""{"id":"n","type":"note","x":0,"y":0,"w":6,"h":4,"options":{}}""", false)]
    public async Task GrokIsAskedOnlyWhileAnEnabledPageShowsIt(string widget, bool asked)
    {
        var (agent, http) = Configured(Page(widget));
        await agent.GrokTickAsync(Now, default);
        Assert.Equal(asked, http.Urls.Count > 0);
        if (!asked) Assert.DoesNotContain(agent.Snapshot(Now), p => p.Id == "grok");
    }

    [Fact]
    public async Task AWidgetOnADisabledPageDoesNotAskGrok()
    {
        var (agent, http) = Configured(Page("""{"id":"q","type":"quotas","x":0,"y":0,"w":12,"h":6,"options":{"providers":["grok"]}}""", enabled: false));
        await agent.GrokTickAsync(Now, default);
        Assert.Empty(http.Urls);
    }

    [Fact]
    public async Task AQuotasWidgetWithoutItsOwnListFollowsTheGlobalOne()
    {
        var widget = """{"id":"q","type":"quotas","x":0,"y":0,"w":12,"h":6,"options":{}}""";
        var (on, onHttp) = Configured(Page(widget)[..^1] + ""","codexBar":{"providers":["claude","grok"]}}""");
        await on.GrokTickAsync(Now, default);
        Assert.NotEmpty(onHttp.Urls);

        var (off, offHttp) = Configured(Page(widget)[..^1] + ""","codexBar":{"providers":["claude","codex","antigravity"]}}""");
        await off.GrokTickAsync(Now, default);
        Assert.Empty(offHttp.Urls);
    }

    [Fact]
    public async Task ThePlanNameIsAskedOnceInAFewHours()
    {
        WriteReady();
        var (agent, http, _) = Agent();
        http.Reply = req => req.RequestUri!.AbsolutePath.EndsWith("/settings", StringComparison.Ordinal)
            ? Json("""{"subscription_tier_display":"SuperGrok"}""") : Json(Credits);
        await agent.GrokTickAsync(Now, default);
        await agent.GrokTickAsync(Now + QuotaMonitor.GrokHealthyMs, default);
        Assert.Single(http.Urls, u => u.Contains("/settings", StringComparison.Ordinal));
        Assert.Equal("SuperGrok", Assert.Single(agent.Snapshot(Now), p => p.Id == "grok").Plan);

        await agent.GrokTickAsync(Now + QuotaMonitor.GrokPlanCacheMs + 1, default);
        Assert.Equal(2, http.Urls.Count(u => u.Contains("/settings", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ALimitedReplyWaitsForRetryAfter(HttpStatusCode status)
    {
        WriteReady();
        var (agent, http, _) = Agent();
        http.Reply = _ =>
        {
            var res = new HttpResponseMessage(status);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return res;
        };
        var started = Environment.TickCount64;
        await agent.GrokTickAsync(Now, default);
        // The wait starts at the reply. The whole call bounds that elapsed time on a busy runner.
        var elapsed = Environment.TickCount64 - started;
        Assert.Single(http.Urls);
        await agent.GrokTickAsync(Now + 119_000, default);
        Assert.Single(http.Urls);
        await agent.GrokTickAsync(Now + 120_000 + elapsed, default);
        Assert.Equal(2, http.Urls.Count);
        Assert.Contains("limiting", Assert.Single(agent.Snapshot(Now), p => p.Id == "grok").Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALimitedReplyWithoutRetryAfterWaitsFiveMinutes()
    {
        WriteReady();
        var (agent, http, _) = Agent();
        http.Reply = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var started = Environment.TickCount64;
        await agent.GrokTickAsync(Now, default);
        var elapsed = Environment.TickCount64 - started;
        await agent.GrokTickAsync(Now + 299_000, default);
        Assert.Single(http.Urls);
        await agent.GrokTickAsync(Now + 300_000 + elapsed, default);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task OfflineChecksRetryQuicklyThenBackOff()
    {
        WriteReady();
        var (agent, http, _) = Agent();
        http.Reply = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "no route");
        var t = Now;
        for (var i = 0; i <= QuotaMonitor.GrokOfflineRetries; i++)
        {
            await agent.GrokTickAsync(t, default);
            Assert.Equal(i + 1, http.Urls.Count);
            t += 30_000;
        }
        // The retries are used up: the next check backs off for a minute instead of 30 seconds.
        await agent.GrokTickAsync(t, default);
        Assert.Equal(QuotaMonitor.GrokOfflineRetries + 1, http.Urls.Count);
        await agent.GrokTickAsync(t + 30_000, default);
        Assert.Equal(QuotaMonitor.GrokOfflineRetries + 2, http.Urls.Count);
        Assert.Contains("Could not reach Grok", Assert.Single(agent.Snapshot(Now), p => p.Id == "grok").Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClockSetBackKeepsAPendingRetryAfter()
    {
        WriteReady();
        var (agent, http, _) = Agent();
        http.Reply = _ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return res;
        };
        await agent.GrokTickAsync(Now, default);
        // The clock jumps back two hours: the wait is capped, not dropped, so nothing is asked at once.
        await agent.GrokTickAsync(Now - 7_200_000, default);
        Assert.Single(http.Urls);
    }

    private sealed class Script : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> Tokens { get; } = [];
        public List<string> Marks { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = static _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            Tokens.Add(request.Headers.Authorization?.Parameter ?? "");
            Marks.Add(request.Headers.TryGetValues("x-xai-token-auth", out var marks) ? string.Join(',', marks) : "");
            return Task.FromResult(Reply(request));
        }
    }

    // A prepaid-only reading (a balance, no limits) is a good reading: a failed CodexBar answer must not hide it, but a
    // fresh CodexBar reading with limits still wins.
    [Fact]
    public void APrepaidOnlyReadingIsNotHiddenByAFailedCodexBarAnswer()
    {
        const long now = 1_800_000_000_000;
        var prepaid = new QuotaProvider("grok", "Grok", "SuperGrok", [], "$12.40 prepaid", now, null);
        var failed = new QuotaProvider("grok", "Grok", "", [], null, now, "CodexBar: not signed in", Source: "codexbar");
        Assert.Same(prepaid, QuotaMonitor.GrokSelect(prepaid, failed, now));
        Assert.Same(prepaid, QuotaMonitor.GrokSelect(prepaid, null, now));
        var limit = new QuotaLimit("Credits", 40, null, null, "ok");
        var fresh = new QuotaProvider("grok", "Grok", "", [limit], null, now - 1000, null, Source: "codexbar");
        Assert.Equal("codexbar", QuotaMonitor.GrokSelect(prepaid, fresh, now)!.Source);
        var errorReading = prepaid with { Error = "Grok: offline" };
        Assert.Equal("CodexBar: not signed in", QuotaMonitor.GrokSelect(errorReading, failed, now)!.Error);
    }

    private sealed class ListLog : ILogger<QuotaMonitor>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
