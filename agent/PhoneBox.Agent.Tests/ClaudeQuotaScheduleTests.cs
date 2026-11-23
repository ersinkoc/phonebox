using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The Claude quota check through its seams: a fake HTTP handler and a credentials file of the test's own, so nothing
/// here reads the real token or reaches api.anthropic.com. The usage endpoint is rate limited per account and shared
/// with Claude Code, so these tests count the requests PhoneBox would send.
/// </summary>
public sealed class ClaudeQuotaScheduleTests : IDisposable
{
    private const long Minute = 60_000;
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-claude-schedule-").FullName;
    private readonly FakeAnthropic _api = new();
    private readonly long _t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Creds => Path.Combine(_dir, "credentials.json");

    private string? _keychain;

    private QuotaMonitor Agent() => new(NullLogger<QuotaMonitor>.Instance)
    {
        ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
        CredentialsPath = Creds,
        HttpHandler = _api,
        ReadKeychain = () => _keychain,
    };

    private static string SignInJson(string token, long expiresAt) =>
        $$$"""{"claudeAiOauth":{"accessToken":"{{{token}}}","expiresAt":{{{expiresAt}}},"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""";

    private void SignIn(string token, long expiresAt) => File.WriteAllText(Creds,
        $$$"""{"claudeAiOauth":{"accessToken":"{{{token}}}","expiresAt":{{{expiresAt}}},"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");

    private const string Usage = """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z","severity":"normal"}]}""";

    private static QuotaProvider Claude(QuotaMonitor agent) => Assert.Single(agent.Snapshot(), p => p.Id == "claude");

    [Fact]
    public async Task AFreshReadingIsNotFetchedAgainWhenTheAgentRestarts()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        await Agent().ClaudeTickAsync(_t0, default);
        Assert.Equal(1, _api.Calls);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + Minute);
        await restarted.ClaudeTickAsync(_t0 + Minute, default);
        Assert.Equal(1, _api.Calls); // the reading is a minute old: no request
        Assert.Equal(42, Assert.Single(Claude(restarted).Limits).Percent);

        await restarted.ClaudeTickAsync(_t0 + 3 * Minute + 1, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ARunningAgentUsesTheReadingAnotherOneJustFetched()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var first = Agent();
        var second = Agent();
        await first.ClaudeTickAsync(_t0, default);
        await second.ClaudeTickAsync(_t0 + 10_000, default);
        Assert.Equal(1, _api.Calls);
        Assert.Equal(42, Assert.Single(Claude(second).Limits).Percent);
    }

    [Fact]
    public async Task ARetryAfterIsKeptAcrossARestart()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 230);
        await Agent().ClaudeTickAsync(_t0, default);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + Minute);
        await restarted.ClaudeTickAsync(_t0 + Minute, default);
        await restarted.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(1, _api.Calls); // the server asked for 230 s
        Assert.Contains("limiting", Claude(restarted).Error);

        _api.Respond(HttpStatusCode.OK, Usage);
        await restarted.ClaudeTickAsync(_t0 + 236_000, default);
        Assert.Equal(2, _api.Calls);
        Assert.Null(Claude(restarted).Error);
    }

    [Fact]
    public async Task ARenewedTokenIsUsedOnTheNextPassAfterAnExpiredOne()
    {
        SignIn("old", _t0 - Minute);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 4 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(0, _api.Calls); // no request with an expired token
        Assert.Contains("expired", Claude(agent).Error);

        SignIn("new", _t0 + 3_600_000); // Claude Code renewed it
        await agent.ClaudeTickAsync(_t0 + 4 * Minute + 30_000, default);
        await agent.ClaudeTickAsync(_t0 + 5 * Minute, default);
        Assert.Equal(1, _api.Calls); // used within a minute
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task APlanWithoutATokenSaysClaudeDesktopDoesNotShareItsSignIn()
    {
        // what the Claude Desktop app leaves: the plan, no access token (#247)
        File.WriteAllText(Creds, """{"claudeAiOauth":{"subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}}""");
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
        Assert.Equal(QuotaMonitor.NoCliSignIn, Claude(agent).Error);
    }

    [Fact]
    public async Task ATokenThatExpiresWithinAMinuteIsNotSent()
    {
        SignIn("almost", _t0 + 20_000);
        await Agent().ClaudeTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task ARefusedTokenIsNotSentAgainUntilClaudeCodeRenewsIt()
    {
        SignIn("revoked", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid token"}""");
        var agent = Agent();
        for (var t = _t0; t < _t0 + 30 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(1, _api.Calls);
        Assert.NotNull(Claude(agent).Error);

        SignIn("renewed", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        await agent.ClaudeTickAsync(_t0 + 30 * Minute, default);
        Assert.Equal(2, _api.Calls);
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task ARefusalIsNotPermanent()
    {
        // a 401 from a proxy or an outage: the same token is tried again after half an hour
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Unauthorized, "");
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 61 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(3, _api.Calls); // at 0, 30 and 60 minutes
    }

    [Fact]
    public async Task AForbiddenReplyBacksOffLikeOtherErrors()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Forbidden, "<html>challenge</html>");
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 8 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(4, _api.Calls); // at 0, 1, 3 and 7 minutes
        Assert.Contains("403", Claude(agent).Error);
    }

    [Fact]
    public async Task AClockSetBackDoesNotStopTheChecks()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        // the clock was a day ahead and NTP corrected it
        var back = _t0 - 24 * 60 * Minute;
        var t = back;
        while (_api.Calls < 2 && t < back + 3 * 60 * Minute) { t += 30_000; await agent.ClaudeTickAsync(t, default); }
        Assert.Equal(2, _api.Calls);
        Assert.InRange(t - back, 0, 3_605_000 + 60_000); // the longest wait, plus the 30 s passes on either side

        // and from then on the normal 3 minutes, with no leftover from the old schedule
        for (var u = t + 30_000; u <= t + 3 * Minute + 30_000; u += 30_000) await agent.ClaudeTickAsync(u, default);
        Assert.Equal(3, _api.Calls);
    }

    [Fact]
    public async Task AnUnreachableNetworkIsRetriedSoonWithoutShowingAnError()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        // the computer woke up and the network is not back yet
        _api.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "No route to host"));
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Null(Claude(agent).Error);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);

        _api.Respond(HttpStatusCode.OK, Usage);
        await agent.ClaudeTickAsync(_t0 + 3 * Minute + 30_000, default);
        Assert.Equal(3, _api.Calls);
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task ANetworkThatStaysDownIsReportedAndBacksOff()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Fail(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known"));
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 10 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.NotNull(Claude(agent).Error);
        // three quick retries 30 s apart, then the back-off (1, 2, 4 … minutes)
        Assert.InRange(_api.Calls, 5, 7);
    }

    [Fact]
    public async Task AReplyWithoutLimitsKeepsTheLastReadingAndTheCache()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        _api.Respond(HttpStatusCode.OK, """{"something":"new"}""");
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);
        Assert.NotNull(Claude(agent).Error);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + 4 * Minute);
        Assert.Equal(42, Assert.Single(Claude(restarted).Limits).Percent);

        // an unknown reply format won't change by asking again soon
        for (var t = _t0 + 3 * Minute + 30_000; t < _t0 + 18 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task AHtmlErrorPageWithStatus200KeepsTheLastReading()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        _api.Respond(HttpStatusCode.OK, "<html>captive portal</html>");
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);
    }

    [Fact]
    public async Task AConnectFailureOfTheRealHandlerIsAConnectionError()
    {
        // the quick retry relies on SocketsHttpHandler reporting a failed connect (through the IPv4-first callback)
        // as HttpRequestError.ConnectionError
        var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = (ctx, ct) => QuotaMonitor.ConnectIpv4First(ctx.DnsEndPoint, ct) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/"));
        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"2026-11-16T10:00:00Z\"")]
    [InlineData("1.5e300")]
    public async Task AnExpiryThatIsNotAWholeNumberDoesNotEndTheChecks(string expiresAt)
    {
        // JsonElement.TryGetInt64 throws on a non-number; that used to end the whole usage loop
        File.WriteAllText(Creds, $$$"""{"claudeAiOauth":{"accessToken":"token-1","expiresAt":{{{expiresAt}}}}}""");
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal(1, _api.Calls); // an unknown expiry is left to the server
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task ARefusedTokenStaysRefusedAcrossARestart()
    {
        SignIn("revoked", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Unauthorized, "");
        await Agent().ClaudeTickAsync(_t0, default);

        var restarted = Agent();
        for (var t = _t0 + 2 * Minute; t < _t0 + 30 * Minute; t += 30_000) await restarted.ClaudeTickAsync(t, default);
        Assert.Equal(1, _api.Calls);
        Assert.NotNull(Claude(restarted).Error);

        SignIn("renewed", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        await restarted.ClaudeTickAsync(_t0 + 31 * Minute, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ALongerWaitAnotherAgentGotMeanwhileIsKept()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        // while this request runs, another agent on the computer gets a 429 that asks for 230 s
        _api.OnSend = () => File.WriteAllText(agent.ClaudeSchedulePath,
            $$"""{"nextAt":{{_t0 + 230_000}},"failures":1,"error":"Anthropic is limiting quota checks"}""");
        await agent.ClaudeTickAsync(_t0, default);
        _api.OnSend = null;
        await agent.ClaudeTickAsync(_t0 + 200_000, default);
        Assert.Equal(1, _api.Calls);
        await agent.ClaudeTickAsync(_t0 + 231_000, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ARetryAfterCountsFromTheAnswer()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 120);
        var agent = Agent();
        _api.OnSend = () => Thread.Sleep(2_000); // a slow answer
        await agent.ClaudeTickAsync(_t0, default);
        _api.OnSend = null;
        await agent.ClaudeTickAsync(_t0 + 125_500, default); // 120 + 5 s after the request went out, not after the answer
        Assert.Equal(1, _api.Calls);
        await agent.ClaudeTickAsync(_t0 + 128_000, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ARefusalAnotherAgentGotMeanwhileIsNotOverwritten()
    {
        SignIn("revoked", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.ServiceUnavailable, "");
        var agent = Agent();
        // while this request runs, another agent sends the same token and gets a 401
        var print = Print("revoked");
        _api.OnSend = () => File.WriteAllText(agent.ClaudeSchedulePath,
            $$"""{"nextAt":{{_t0 + 60_000}},"failures":0,"refused":"{{print}}","refusedAt":{{_t0}}}""");
        await agent.ClaudeTickAsync(_t0, default);
        _api.OnSend = null;

        var restarted = Agent();
        for (var t = _t0 + 10 * Minute; t < _t0 + 30 * Minute; t += 30_000) await restarted.ClaudeTickAsync(t, default);
        Assert.Equal(1, _api.Calls);
    }

    private static string Print(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    [Fact]
    public async Task AnHourLongWaitAnotherAgentGotIsKeptAfterASlowAnswer()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        _api.OnSend = () =>
        {
            // a second into this pass another agent is told to wait an hour (+5 s); this answer then takes 2 s
            File.WriteAllText(agent.ClaudeSchedulePath, $$"""{"nextAt":{{_t0 + 1_000 + 3_605_000}},"failures":1}""");
            Thread.Sleep(2_000);
        };
        await agent.ClaudeTickAsync(_t0, default);
        _api.OnSend = null;
        await agent.ClaudeTickAsync(_t0 + 10 * Minute, default);
        Assert.Equal(1, _api.Calls);
    }

    [Fact]
    public async Task ANewerRefusalOfTheSameTokenIsKept()
    {
        SignIn("revoked", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Unauthorized, "");
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        // half an hour later this agent tries the token again; meanwhile another agent gets a fresh 401 for it
        _api.Respond(HttpStatusCode.ServiceUnavailable, "");
        _api.OnSend = () => File.WriteAllText(agent.ClaudeSchedulePath,
            $$"""{"nextAt":{{_t0 + 31 * Minute}},"failures":0,"refused":"{{Print("revoked")}}","refusedAt":{{_t0 + 30 * Minute}}}""");
        await agent.ClaudeTickAsync(_t0 + 30 * Minute, default);
        _api.OnSend = null;
        Assert.Equal(2, _api.Calls);

        var restarted = Agent();
        for (var t = _t0 + 36 * Minute; t < _t0 + 60 * Minute; t += 30_000) await restarted.ClaudeTickAsync(t, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ARestartKeepsTheBackOffGoing()
    {
        SignIn("token-1", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.Forbidden, "");
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);
        await agent.ClaudeTickAsync(_t0 + Minute, default); // second failure: next at 3 minutes

        var restarted = Agent();
        await restarted.ClaudeTickAsync(_t0 + 3 * Minute, default); // third failure: the next one waits 4 minutes
        await restarted.ClaudeTickAsync(_t0 + 5 * Minute, default);
        Assert.Equal(3, _api.Calls);
        await restarted.ClaudeTickAsync(_t0 + 7 * Minute, default);
        Assert.Equal(4, _api.Calls);
    }

    // macOS: Claude Code renews its sign-in in the Keychain. A credentials file left behind by another tool or an older
    // version keeps a token that expired hours ago; it must not hide the renewed one.
    [Fact]
    public async Task AStaleFileGivesWayToTheRenewedKeychainSignIn()
    {
        SignIn("stale-file-token", _t0 - 4 * 3_600_000);
        _keychain = SignInJson("keychain-token", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal(1, _api.Calls);
        Assert.Equal("keychain-token", _api.LastToken);
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task AValidFileIsUsedWithoutReadingTheKeychain()
    {
        SignIn("file-token", _t0 + 3_600_000);
        var reads = 0;
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
            CredentialsPath = Creds,
            HttpHandler = _api,
            ReadKeychain = () => { reads++; return SignInJson("keychain-token", _t0 + 7_200_000); },
        };
        _api.Respond(HttpStatusCode.OK, Usage);
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal(0, reads); // each Keychain read starts the security tool: not while the file is good
        Assert.Equal("file-token", _api.LastToken);
    }

    [Fact]
    public async Task WhenBothAreExpiredTheMessageStaysAndNothingIsSent()
    {
        SignIn("stale-file-token", _t0 - 4 * 3_600_000);
        _keychain = SignInJson("old-keychain-token", _t0 - 3_600_000);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
        Assert.Contains("expired", Claude(agent).Error);
    }

    [Theory]
    [InlineData(-4, 1, "keychain")]   // the file expired, the Keychain was renewed
    [InlineData(1, 2, "keychain")]    // (the caller reads the Keychain only once the file has expired)
    [InlineData(-4, -2, "keychain")]  // both expired: the later one, so the message says what Claude Code last saved
    [InlineData(-1, -4, "file")]
    public void TheSignInThatExpiresLaterIsUsed(int fileHours, int keychainHours, string expected)
    {
        var file = SignInJson("file", _t0 + fileHours * 3_600_000L);
        var keychain = SignInJson("keychain", _t0 + keychainHours * 3_600_000L);
        var chosen = QuotaMonitor.PickCredentials(file, keychain);
        Assert.Equal(expected == "file" ? file : keychain, chosen);
    }

    [Fact]
    public void AFileWithoutATokenOrNoKeychainAnswerIsHandled()
    {
        var keychain = SignInJson("keychain", _t0 + 3_600_000);
        const string desktopOnly = """{"claudeAiOauth":{"subscriptionType":"max"}}""";
        Assert.Equal(keychain, QuotaMonitor.PickCredentials(desktopOnly, keychain));
        Assert.Equal(keychain, QuotaMonitor.PickCredentials(null, keychain));
        Assert.Equal(desktopOnly, QuotaMonitor.PickCredentials(desktopOnly, null));
        Assert.Equal(desktopOnly, QuotaMonitor.PickCredentials(desktopOnly, "not json"));
        Assert.Null(QuotaMonitor.PickCredentials(null, null));
    }

    [Fact]
    public async Task WithoutASignInOnWindowsTheOneInsideWslIsUsed()
    {
        var wsl = Path.Combine(_dir, "wsl-credentials.json");
        File.WriteAllText(wsl, SignInJson("wsl-token", _t0 + 3_600_000));
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
            CredentialsPath = Creds, // not written: Claude Code is signed in only inside WSL
            HttpHandler = _api,
            FindWslCredentials = () => [wsl],
        };
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal("wsl-token", _api.LastToken);
        Assert.NotEmpty(Claude(agent).Limits);
    }

    [Fact]
    public async Task ASignInOnWindowsWinsOverTheOneInsideWsl()
    {
        SignIn("windows-token", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
            CredentialsPath = Creds,
            HttpHandler = _api,
            FindWslCredentials = () => throw new InvalidOperationException("WSL must not be looked at"),
        };
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal("windows-token", _api.LastToken);
    }

    [Fact]
    public async Task ClaudeDesktopsPlanOnlyFileOnWindowsDoesNotHideTheSignInInsideWsl()
    {
        // what Claude Desktop leaves in ~/.claude/.credentials.json: the plan, no Claude Code token
        File.WriteAllText(Creds, """{"claudeAiOauth":{"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");
        var wsl = Path.Combine(_dir, "wsl-credentials.json");
        File.WriteAllText(wsl, SignInJson("wsl-token", _t0 + 3_600_000));
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
            CredentialsPath = Creds,
            HttpHandler = _api,
            FindWslCredentials = () => [wsl],
        };
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal("wsl-token", _api.LastToken);
    }

    [Fact]
    public async Task ADistroWithOnlyAPlanIsPassedOverForTheNextOne()
    {
        var planOnly = Path.Combine(_dir, "wsl-a.json");
        File.WriteAllText(planOnly, """{"claudeAiOauth":{"subscriptionType":"pro"}}""");
        var signedIn = Path.Combine(_dir, "wsl-b.json");
        File.WriteAllText(signedIn, SignInJson("second-distro", _t0 + 3_600_000));
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance)
        {
            ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
            CredentialsPath = Creds,
            HttpHandler = _api,
            FindWslCredentials = () => [Path.Combine(_dir, "missing.json"), planOnly, signedIn],
        };
        await agent.ClaudeTickAsync(_t0, default);
        Assert.Equal("second-distro", _api.LastToken);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    [InlineData("""{"claudeAiOauth":{"subscriptionType":"max"}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"t"}}""", true)]
    public void ASignInNeedsAnAccessToken(string? json, bool expected) => Assert.Equal(expected, QuotaMonitor.HasCliSignIn(json));

    private sealed class FakeAnthropic : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _next = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        public int Calls { get; private set; }
        public string? LastToken { get; private set; }
        public Action? OnSend { get; set; }

        public void Respond(HttpStatusCode status, string body, int? retryAfterSeconds = null) => _next = () =>
        {
            var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is { } s) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return res;
        };

        public void Fail(Exception ex) => _next = () => throw ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastToken = request.Headers.Authorization?.Parameter;
            OnSend?.Invoke();
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
            return Task.FromResult(_next());
        }
    }
}
