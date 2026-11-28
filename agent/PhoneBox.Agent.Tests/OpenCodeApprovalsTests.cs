using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// OpenCode permission prompts: the queue is polled, offered to the phone through ApprovalStore, and the answer is
/// posted back as once / always / reject. A prompt answered on the desktop first is withdrawn without a reply.
/// </summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with the other approval tests
public sealed class OpenCodeApprovalsTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public long Now = 100_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    /// <summary>The part of OpenCode the monitor talks to: known directories (from the session list), one queue, and a reply route.</summary>
    private sealed class FakeOpenCode : HttpMessageHandler
    {
        private readonly object _lock = new();
        /// <summary>The queue as /api/permission/request answers it; null turns every read into a 404.</summary>
        public string? QueueJson = """{"data":[]}""";
        /// <summary>A second service's queue (the "b" port): a second OpenCode, native or inside WSL.</summary>
        public string? QueueJsonB = """{"data":[]}""";
        /// <summary>The pending forms as /api/form answers them; null turns every read into a 404.</summary>
        public string? FormsJson = """{"data":[]}""";
        /// <summary>The session list /api/session answers; its directories are what the queues get polled under.</summary>
        public string SessionsJson;
        public readonly List<string> Replies = [];
        public readonly List<string> ReplyUrls = [];
        public readonly List<string> FormReplies = [];
        public readonly List<string> Calls = [];
        /// <summary>The queue reads fail (a 503, as a slow or restarting service answers) while this is set.</summary>
        public volatile bool QueuesFail;
        /// <summary>Only the question queue (/api/form) fails while this is set.</summary>
        public volatile bool FormsFail;

        /// <param name="directory">The project directory the session list reports; a real path so the project name
        /// works out the same on Windows and Unix (Path.GetFileName).</param>
        public FakeOpenCode(string directory) => SessionsJson = JsonSerializer.Serialize(new
        { data = new[] { new { id = "ses_1", location = new { directory } } } });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            // AbsoluteUri keeps the escaping the agent sent; ToString() would turn %20 back into a space, so a temp
            // directory with a space in it (C:\Users\First Last\...) would never match the expected query
            var url = req.RequestUri!.AbsoluteUri;
            lock (_lock) Calls.Add(req.Method.Method + " " + url);
            if (req.Method == HttpMethod.Post && url.Contains("/permission/") && url.EndsWith("/reply"))
            {
                using var reader = new StreamReader(req.Content!.ReadAsStream(ct), Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                lock (_lock) { Replies.Add(body); ReplyUrls.Add(url); }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.Method == HttpMethod.Post && url.Contains("/form/"))
            {
                using var reader = new StreamReader(req.Content!.ReadAsStream(ct), Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                lock (_lock) FormReplies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if ((QueuesFail || FormsFail) && url.Contains("/api/form") && req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (QueuesFail && (url.Contains("/api/form") || url.Contains("/api/permission/request")))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var data = url.Contains("/api/session")
                ? SessionsJson
                : url.Contains("/api/form") ? FormsJson
                : url.Contains("/api/permission/request") ? (url.Contains(":28999") ? QueueJsonB : QueueJson) : null;
            return new HttpResponseMessage(data is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(data ?? "", Encoding.UTF8, "application/json") };
        }
    }

    private const string QueueOne = """{"data":[{"id":"per_1","sessionID":"ses_1","action":"shell","resources":["git push -f"],"message":"Push the branch?"}]}""";
    private const string FormOne = """{"data":[{"id":"frm_1","sessionID":"ses_1","title":"Questions","metadata":{"kind":"question"},"fields":[{"key":"q0","title":"Route","description":"Which route should we take?","type":"string","custom":true,"options":[{"value":"a","label":"Route A - fast","description":"Fastest"},{"value":"b","label":"Route B - safe","description":"Safest"}]}]}]}""";

    private readonly Clock _clock = new();
    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-opencode-approvals-").FullName;
    private readonly PushStore _events;
    private readonly string _servicePath;

    /// <summary>A real project directory (Path.GetFileName works on it the same way on every OS).</summary>
    private static string ProjDir { get; } = Path.Combine(Path.GetTempPath(), "pb-proj");

    private static string QueueOf(int count, string? save = null) => "{\"data\":[" + string.Join(",",
        Enumerable.Range(1, count).Select(i => "{\"id\":\"per_" + i + "\",\"sessionID\":\"ses_1\",\"action\":\"shell\","
            + "\"resources\":[\"cmd " + i + "\"]" + (save is null ? "" : ",\"save\":" + save) + "}")) + "]}";

    public OpenCodeApprovalsTests()
    {
        _events = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        _servicePath = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_servicePath)!);
        File.WriteAllText(_servicePath, """{"url":"http://127.0.0.1:28894","password":"pw","version":"2.3.2"}""");
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _events.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>OpenCode prompts are off by default now, so the tests turn them on explicitly (with the WSL half on,
    /// which the two-service test relies on).</summary>
    private OpenCodeApprovals Monitor(ApprovalStore store, FakeOpenCode fake, string? servicePath = null)
    {
        var path = Path.Combine(_dir, "cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"opencode":{"phoneApprovals":true,"watchWsl":true,"alwaysCountdownSec":10}}""");
        return new OpenCodeApprovals(store, NullLogger<OpenCodeApprovals>.Instance,
            config: new ConfigStore(path), http: fake, clock: _clock, servicePaths: [servicePath ?? _servicePath]);
    }

    private static async Task WaitUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(20);
        Assert.True(done(), what);
    }

    [Fact]
    public void TheServiceFileGivesTheUrlAndPassword()
    {
        Assert.Equal(_servicePath, OpenCodeApprovals.ServicePath(homeDir: _dir));
        var s = OpenCodeApprovals.ReadService(_servicePath);
        Assert.Equal("http://127.0.0.1:28894", s!.Url);
        Assert.Equal("pw", s.Password);
        Assert.Equal("2.3.2", s.Version);
        Assert.Null(OpenCodeApprovals.ServicePath(homeDir: Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void AServiceFileWithoutAPasswordIsNotAService() =>
        Assert.Null(OpenCodeApprovals.ReadService(WriteRaw("""{"url":"http://127.0.0.1:1"}""")));

    private string WriteRaw(string json)
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task APendingPromptIsOfferedAndAllowAnswersOnce()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);

        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("per_1", a.Id);
        Assert.Equal("OpenCode", a.Source);
        Assert.Equal("pb-proj", a.Project);
        Assert.Equal(ProjDir, a.Cwd);
        Assert.Equal("ses_1", a.Session);
        Assert.Equal("Run a command", a.Tool);
        Assert.Equal("Push the branch?\n\ngit push -f", a.Detail);
        Assert.Equal("permission", a.Kind);
        Assert.False(a.Always); // no save pattern in the request: the card offers no Allow always

        Assert.True(store.Decide("per_1", "allow"));
        await WaitUntil(() => fake.Replies.Count == 1, "the reply never reached OpenCode");
        Assert.Equal("""{"decision":"once"}""", fake.Replies[0]);
    }

    [Fact]
    public async Task DenyRepliesRejectAndAlwaysRepliesAlways()
    {
        foreach (var (decision, expected) in new[] { ("deny", "reject"), ("always", "always") })
        {
            // the always case needs a card that offers it: OpenCode's request carries what it would save
            var fake = new FakeOpenCode(ProjDir) { QueueJson = decision == "always" ? QueueOf(1, save: """["echo *"]""") : QueueOne };
            var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
            var monitor = Monitor(store, fake);
            await monitor.PollOnceAsync(CancellationToken.None);
            _clock.Now += 30_000; // past the countdown, so "always" is allowed to be taken
            Assert.True(store.Decide("per_1", decision));
            await WaitUntil(() => fake.Replies.Count == 1, $"{decision} never reached OpenCode");
            Assert.Equal($$"""{"decision":"{{expected}}"}""", fake.Replies[0]);
        }
    }

    [Fact]
    public async Task APromptAnsweredOnTheDesktopIsWithdrawnWithoutAReply()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot());

        fake.QueueJson = """{"data":[]}"""; // the desktop answered while the phone was thinking
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 0, "the withdrawn prompt stayed on the phone");
        Assert.Empty(fake.Replies);
    }

    [Fact]
    public async Task AQueueThatCannotBeReadKeepsItsCards()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        var card = Assert.Single(store.Snapshot());

        fake.QueuesFail = true; // OpenCode is slow or restarting for a round: that is not "answered on the desktop"
        await monitor.PollOnceAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(card.Id, Assert.Single(store.Snapshot()).Id);

        fake.QueuesFail = false;
        fake.QueueJson = """{"data":[]}"""; // read again, and now really gone
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 0, "the answered prompt stayed on the phone");
        Assert.Empty(fake.Replies);
    }

    [Fact]
    public async Task OneQueueFailingDoesNotHoldBackTheOther()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne, FormsJson = FormOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, store.Snapshot().Count); // the permission and the question

        // the desktop answered the permission while the question queue fails: the permission card goes, the
        // question card (whose queue could not be read) stays
        fake.QueueJson = """{"data":[]}""";
        fake.FormsFail = true;
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 1, "the answered permission stayed on the phone");
        Assert.Equal("question", Assert.Single(store.Snapshot()).Kind);
    }

    [Fact]
    public async Task APromptArrivingWithoutAPhoneIsOfferedOnceOneConnects()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, 0); // no phone has polled in a while
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(store.Snapshot()); // nothing offered yet, so nothing could be replied either
        Assert.Empty(fake.Replies);     // OpenCode's own prompt stays in charge

        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue); // the phone shows up later
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot()); // the still-pending prompt reaches it now
    }

    [Fact]
    public async Task TheQueuesAreNotReadWhileNoPhoneIsListening()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var monitor = Monitor(new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock), fake);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, 0);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(fake.Calls); // nothing fetched at all: OpenCode's queues are only read for a waiting phone
    }

    [Fact]
    public async Task WithoutAServiceNothingIsOffered()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake, servicePath: Path.Combine(_dir, "missing.json"));
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task TheQueueIsPolledPerKnownDirectory()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne };
        var monitor = Monitor(new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock), fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        // the directories come from the session list (every session knows where it lives — that works for WSL too)
        Assert.Contains(fake.Calls, c => c.Contains("/api/session") && !c.Contains("/reply"));
        Assert.Contains(fake.Calls, c => c.Contains("/api/permission/request?location%5Bdirectory%5D=" + Uri.EscapeDataString(ProjDir)));
    }

    /// <summary>Native OpenCode and OpenCode inside WSL are two services; both queues are offered, and each answer
    /// goes back to the service that asked.</summary>
    [Fact]
    public async Task TwoServicesArePolledIndependentlyAndAnsweredSeparately()
    {
        var native = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        var wsl = Path.Combine(_dir, "wsl", ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        File.WriteAllText(wsl, """{"url":"http://127.0.0.1:28999","password":"pw2","version":"2.3.2"}""");
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne, QueueJsonB = """{"data":[{"id":"per_2","sessionID":"ses_9","action":"edit","resources":["main.rs"]}]}""" };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var path = Path.Combine(_dir, "cfg-two-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"opencode":{"phoneApprovals":true,"watchWsl":true}}""");
        var monitor = new OpenCodeApprovals(store, NullLogger<OpenCodeApprovals>.Instance,
            config: new ConfigStore(path), http: fake, clock: _clock, servicePaths: [native, wsl]);

        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, store.Snapshot().Count);

        Assert.True(store.Decide("per_1", "allow"));
        await WaitUntil(() => fake.Replies.Count == 1, "the native reply never reached OpenCode");
        Assert.True(store.Decide("per_2", "deny"));
        await WaitUntil(() => fake.Replies.Count == 2, "the WSL reply never reached OpenCode");
        Assert.Contains("28894", fake.ReplyUrls[0]);
        Assert.Contains("28999", fake.ReplyUrls[1]);
        Assert.Equal("""{"decision":"once"}""", fake.Replies[0]);
        Assert.Equal("""{"decision":"reject"}""", fake.Replies[1]);
    }

    /// <summary>A card that does not offer Allow always never takes it — the hook's Claude Code cards included. The
    /// hook still maps "always" to a one-time allow, but it can only ever receive it from a card that offered it.</summary>
    [Fact]
    public async Task TheClaudeHookRejectsAlwaysOnItsOwnCards()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, "{}");
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, TimeProvider.System);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var input = JsonDocument.Parse("""{"tool_name":"Bash","tool_input":{"command":"ls"},"cwd":"C:\\p","session_id":"s1"}""").RootElement.Clone();

        using var cts = new CancellationTokenSource();
        var hook = ClaudeHook.HandleAsync(input, new ConfigStore(cfgPath), store, sessions, cts.Token);
        await WaitUntil(() => store.Snapshot().Count == 1, "the hook never offered the prompt");
        Assert.False(store.Snapshot()[0].Always); // the card offers no Allow always
        Assert.False(store.Decide(store.Snapshot()[0].Id, "always"));
        Assert.Single(store.Snapshot()); // the prompt is still waiting: nothing was answered
        cts.Cancel(); // the user answered in the terminal; the hook gives up waiting
        Assert.Null(await hook);
    }

    /// <summary>Allow always is tied to its card here as well: refused before the countdown, accepted once it is over.</summary>
    [Fact]
    public async Task AlwaysWaitsForTheCountdownAndNamesThePattern()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOf(1, save: """["echo *"]""") };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.True(a.Always);
        Assert.Equal("echo *", a.AlwaysPattern); // the card says what Allow always would save
        Assert.Equal(100_000 + 10_000, a.AlwaysNotBefore); // the default countdown, stamped by the monitor

        Assert.False(store.Decide("per_1", "always")); // the countdown is not over; the card is still live
        Assert.Single(store.Snapshot());
        _clock.Now += 10_001;
        Assert.True(store.Decide("per_1", "always"));
        await WaitUntil(() => fake.Replies.Count == 1, "the reply never reached OpenCode");
        Assert.Equal("""{"decision":"always"}""", fake.Replies[0]);
    }

    /// <summary>An expired card's slot is free for new requests, and the timed-out request does not come back on
    /// every window — OpenCode's own prompt is on the desktop, and the phone must not buzz every 90 s.</summary>
    [Fact]
    public async Task TimedOutOffersMakeRoomForNewOnesWithoutComingBack()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOf(9) };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(8, store.Snapshot().Count); // the cap

        _clock.Now += 90_001; // the windows were stamped 90 s out: every card is past its expiry now
        await monitor.PollOnceAsync(CancellationToken.None);
        // the ninth request (starved by the cap before) takes a freed slot; the eight timed-out ones do not come back
        await WaitUntil(() => store.Snapshot().Any(a => a.Id == "per_9"), "the starved request never took a freed slot");
        Assert.DoesNotContain(store.Snapshot(), a => a.Id is "per_1" or "per_2" or "per_8");

        fake.QueueJson = QueueOf(8); // the same eight requests are still queued on OpenCode's side
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(store.Snapshot(), a => a.Id is "per_1" or "per_8"); // no buzz every window

        fake.QueueJson = """{"data":[{"id":"per_new","sessionID":"ses_1","action":"edit","resources":["main.rs"]}]}""";
        await monitor.PollOnceAsync(CancellationToken.None);
        // per_9 left the queue, so its card is withdrawn; that cleanup runs on the offer's own task
        await WaitUntil(() => store.Snapshot().Count == 1, "the withdrawn per_9 stayed on the phone");
        var fresh = Assert.Single(store.Snapshot());
        Assert.Equal("per_new", fresh.Id); // a new request takes a slot
        Assert.All(store.Snapshot(), a => Assert.True(a.ExpiresAt > _clock.Now));
    }

    [Fact]
    public void ActionsBecomeCardHeadlines()
    {
        string[] expected = ["Run a command", "Edit a file", "Fetch a web page", "Use myserver › mytool"];
        string[] actual = [OpenCodeApprovals.ToolTitle("shell"), OpenCodeApprovals.ToolTitle("edit"),
            OpenCodeApprovals.ToolTitle("webfetch"), OpenCodeApprovals.ToolTitle("myserver_mytool")];
        Assert.Equal(expected, actual);
    }

    // fail-closed whitelist: only the known read-only actions pass;
    // everything else is high risk — shell, edit, execute (it runs
    // AI-written code), external_directory (it opens a folder outside
    // the project), any MCP tool (the underscore shape) and any action
    // the whitelist does not name, be it a plugin's or one OpenCode adds
    // in the future.
    [Fact]
    public void WriteClassActionsAreMarkedHighRisk()
    {
        Assert.True(OpenCodeApprovals.IsHighRisk("shell"));
        Assert.True(OpenCodeApprovals.IsHighRisk("edit"));
        Assert.True(OpenCodeApprovals.IsHighRisk("myserver_mytool")); // an MCP tool
        Assert.False(OpenCodeApprovals.IsHighRisk("read"));
        Assert.False(OpenCodeApprovals.IsHighRisk("glob"));
        Assert.False(OpenCodeApprovals.IsHighRisk("grep"));
        Assert.False(OpenCodeApprovals.IsHighRisk("webfetch"));
        Assert.False(OpenCodeApprovals.IsHighRisk("websearch"));
        Assert.True(OpenCodeApprovals.IsHighRisk("subagent"));   // the agent it starts may write
        Assert.False(OpenCodeApprovals.IsHighRisk("skill"));
        Assert.False(OpenCodeApprovals.IsHighRisk("question"));  // questions are never permissions
        Assert.True(OpenCodeApprovals.IsHighRisk("execute"));    // it runs AI-written code
        Assert.True(OpenCodeApprovals.IsHighRisk("external_directory")); // not on the read-only whitelist
    }

    // fail-closed: anything outside the read-only whitelist is high risk —
    // an empty action, a plugin's own action, a word no one has named yet
    [Theory]
    [InlineData("", true)]
    [InlineData("someplugin_action", true)]
    [InlineData("custom_action", true)]
    [InlineData("weird", true)]
    [InlineData("read", false)]
    public void UnknownActionsAreHighRisk(string action, bool expected) =>
        Assert.Equal(expected, OpenCodeApprovals.IsHighRisk(action));

    // a permission request whose action is a write class carries HighRisk on the
    // card; the question form channel never does (questions are not permissions)
    [Fact]
    public async Task AShellPromptIsOfferedHighRiskAndAQuestionNeverIs()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = QueueOne, FormsJson = """{"data":[]}""" };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("Run a command", a.Tool);   // the queue's shell action reached the card's headline
        Assert.True(a.HighRisk);

        var fake2 = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = FormOne };
        var store2 = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor2 = Monitor(store2, fake2);
        await monitor2.PollOnceAsync(CancellationToken.None);
        Assert.False(Assert.Single(store2.Snapshot()).HighRisk); // the question form is on the card, and never marked
    }

    [Fact]
    public async Task AnAgentQuestionIsOfferedWithItsChoicesAndTheAnswerReachesTheForm()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = FormOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("frm_1", a.Id);
        Assert.Equal("question", a.Kind);
        Assert.Equal("Route", a.Tool);              // the field's title is the headline
        Assert.Equal("Which route should we take?", a.Detail);
        Assert.Equal(2, a.Options!.Length);
        Assert.Equal("Route A - fast", a.Options[0].Label);
        Assert.Equal("Fastest", a.Options[0].Description);
        Assert.True(a.Custom);                      // "type your own answer" is allowed

        Assert.True(store.Decide("frm_1", "answer", "b"));
        await WaitUntil(() => fake.FormReplies.Count == 1, "the answer never reached the form");
        Assert.Equal("""{"answer":{"q0":"b"}}""", fake.FormReplies[0]);
    }

    [Fact]
    public async Task AQuestionAnsweredOnTheDesktopIsWithdrawnWithoutAReply()
    {
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = FormOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot());

        fake.FormsJson = """{"data":[]}"""; // the OpenCode app answered while the phone was thinking
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 0, "the withdrawn question stayed on the phone");
        Assert.Empty(fake.FormReplies);
    }

    [Fact]
    public async Task AFormWithoutTapAnswersIsShownAsTextOnly()
    {
        var multi = """{"data":[{"id":"frm_2","sessionID":"ses_1","title":"Details","fields":[{"key":"a","title":"Name","description":"Your name?","type":"string"},{"key":"b","title":"OK?","type":"boolean"}]}]}""";
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = multi };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("question", a.Kind);
        Assert.Equal("OpenCode has a question", a.Tool);
        Assert.Contains("Name: Your name?", a.Detail);
        Assert.Null(a.Options);   // nothing to tap: answered at the PC
        Assert.False(a.Custom);
        Assert.False(store.Decide(a.Id, "answer", "whatever")); // not an answer this card takes
        Assert.True(store.Decide(a.Id, "skip"));                // "Got it" ends the card…
        await WaitUntil(() => store.Snapshot().Count == 0, "the dismissed question stayed on the phone");
        await WaitUntil(() => fake.FormReplies.Count == 0, "a text-only form was answered after all");
    }

    /// <summary>"Got it" on a question the phone cannot answer: nothing goes back to OpenCode, and the same form is
    /// not offered again on every poll while it stays queued. A new form is.</summary>
    [Fact]
    public async Task GotItDismissesAQuestionWithoutReplyingAndItDoesNotBounceBack()
    {
        var noTap = """{"data":[{"id":"frm_4","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Where?","description":"Answer at the PC.","type":"string"}]}]}""";
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = noTap };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        var a = Assert.Single(store.Snapshot());
        Assert.False(store.Decide(a.Id, "answer", "somewhere")); // nothing to tap and no custom field
        Assert.True(store.Decide(a.Id, "skip"));
        await WaitUntil(() => store.Snapshot().Count == 0, "the card never ended");

        await monitor.PollOnceAsync(CancellationToken.None); // the form is still queued on OpenCode's side
        Assert.Empty(store.Snapshot());                      // …but it is not offered again

        fake.FormsJson = """{"data":[{"id":"frm_5","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Where?","description":"Answer at the PC.","type":"string"}]}]}""";
        await monitor.PollOnceAsync(CancellationToken.None); // a new question is a new card
        Assert.Single(store.Snapshot());
        Assert.Empty(fake.FormReplies);
    }

    /// <summary>A question with fixed choices only takes one of them; with a custom field anything typed goes too.</summary>
    [Fact]
    public async Task AnAnswerMustBeOneOfTheChoicesUnlessTheCardAllowsTyping()
    {
        var fixedChoices = """{"data":[{"id":"frm_6","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Route","type":"string","options":[{"value":"a","label":"Route A"},{"value":"b","label":"Route B"}]}]}]}""";
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = fixedChoices };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        var a = Assert.Single(store.Snapshot());
        Assert.False(a.Custom);
        Assert.False(store.Decide(a.Id, "answer", "zzz")); // not one of the card's choices
        Assert.Single(store.Snapshot());
        Assert.True(store.Decide(a.Id, "answer", "b"));
        await WaitUntil(() => fake.FormReplies.Count == 1, "the answer never reached the form");
        Assert.Equal("""{"answer":{"q0":"b"}}""", fake.FormReplies[0]);

        // the same question with a custom field: any typed answer goes
        fake.FormsJson = """{"data":[{"id":"frm_7","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Route","type":"string","custom":true,"options":[{"value":"a","label":"Route A"},{"value":"b","label":"Route B"}]}]}]}""";
        var store2 = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor2 = Monitor(store2, fake);
        await monitor2.PollOnceAsync(CancellationToken.None);
        var b = Assert.Single(store2.Snapshot());
        Assert.True(b.Custom);
        Assert.False(store2.Decide(b.Id, "answer", "   ")); // blank once trimmed: no answer at all
        Assert.Single(store2.Snapshot());
        Assert.True(store2.Decide(b.Id, "answer", "route C, please"));
        await WaitUntil(() => fake.FormReplies.Count == 2, "the custom answer never reached the form");
        Assert.Equal("""{"answer":{"q0":"route C, please"}}""", fake.FormReplies[1]);
    }

    [Fact]
    public void TheCountdownIsClampedAndReachesThePhone()
    {
        var path = Path.Combine(_dir, "cfg-countdown.json");
        File.WriteAllText(path, """{"opencode": {"alwaysCountdownSec": 500}}""");
        var cfg = new ConfigStore(path).Get();
        Assert.Equal(60, cfg.OpenCode.AlwaysCountdownSec);
        Assert.False(cfg.OpenCode.PhoneApprovals); // off until the settings page turns it on
        Assert.False(cfg.OpenCode.Sessions);       // sessions are opt-in like Cursor's
        Assert.False(cfg.OpenCode.WatchWsl);       // WSL watching is opt-in too
        Assert.False(cfg.OpenCode.ShareSessionTitles);
    }

    [Fact]
    public void TheServiceMustBeOnThisMachinesLoopback()
    {
        var write = (string json) =>
        {
            var path = Path.Combine(_dir, "svc-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        };
        Assert.NotNull(OpenCodeApprovals.ReadService(write("""{"url":"http://127.0.0.1:28894","password":"pw"}""")));
        Assert.NotNull(OpenCodeApprovals.ReadService(write("""{"url":"http://localhost:28894/","password":"pw"}""")));
        Assert.NotNull(OpenCodeApprovals.ReadService(write("""{"url":"http://[::1]:28894","password":"pw"}""")));
        Assert.Null(OpenCodeApprovals.ReadService(write("""{"url":"http://192.168.1.20:28894","password":"pw"}""")));  // another machine
        Assert.Null(OpenCodeApprovals.ReadService(write("""{"url":"http://10.0.0.5:28894","password":"pw"}""")));      // another network
        Assert.Null(OpenCodeApprovals.ReadService(write("""{"url":"https://127.0.0.1:28894","password":"pw"}""")));    // not the plain local service
        Assert.Null(OpenCodeApprovals.ReadService(write("""{"url":"file:///etc/opencode","password":"pw"}""")));
        Assert.Null(OpenCodeApprovals.ReadService(write("""{"url":"not a url","password":"pw"}""")));
    }

    /// <summary>An answerable card whose answer literally reads "skip" — a choice named that, or a typed one on a
    /// custom field — is an answer and goes back to OpenCode; only a card with nothing to tap or type is dismissed.</summary>
    [Fact]
    public async Task AnAnswerValuedSkipIsAnAnswerRatherThanADismissal()
    {
        var form = """{"data":[{"id":"frm_8","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Next","description":"What next?","type":"string","custom":true}]}]}""";
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = form };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        var a = Assert.Single(store.Snapshot());
        Assert.True(a.Custom); // the phone could have typed "skip" as a real answer
        Assert.True(store.Decide(a.Id, "answer", "skip"));
        await WaitUntil(() => fake.FormReplies.Count == 1, "the typed answer never reached the form");
        Assert.Equal("""{"answer":{"q0":"skip"}}""", fake.FormReplies[0]);

        // the same for a fixed choice that is named "skip"
        var choices = """{"data":[{"id":"frm_9","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Next","type":"string","options":[{"value":"skip","label":"Skip this"},{"value":"go","label":"Go on"}]}]}]}""";
        fake.FormsJson = choices;
        var store2 = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor2 = Monitor(store2, fake);
        await monitor2.PollOnceAsync(CancellationToken.None);
        var b = Assert.Single(store2.Snapshot());
        Assert.True(store2.Decide(b.Id, "answer", "skip"));
        await WaitUntil(() => fake.FormReplies.Count == 2, "the choice never reached the form");
        Assert.Equal("""{"answer":{"q0":"skip"}}""", fake.FormReplies[1]);
    }

    /// <summary>A Claude question card has no answers to tap: a typed answer is not an answer it takes, so the
    /// question stays waiting and Claude Code asks in the terminal.</summary>
    [Fact]
    public async Task TheClaudeHookIgnoresAnswerValues()
    {
        var cfgPath = Path.Combine(_dir, "config2.json");
        File.WriteAllText(cfgPath, "{}");
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, TimeProvider.System);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var input = JsonDocument.Parse("""{"tool_name":"AskUserQuestion","tool_input":{"questions":[]},"cwd":"C:\\p","session_id":"s2"}""").RootElement.Clone();

        using var cts = new CancellationTokenSource();
        var hook = ClaudeHook.HandleAsync(input, new ConfigStore(cfgPath), store, sessions, cts.Token);
        await WaitUntil(() => store.Snapshot().Count == 1, "the hook never offered the question");
        Assert.False(store.Decide(store.Snapshot()[0].Id, "answer", "Route A"));
        Assert.Single(store.Snapshot()); // still waiting: nothing was answered
        cts.Cancel();
        Assert.Null(await hook); // no output: Claude Code asks in the terminal as usual
    }

    /// <summary>The settings the agent writes carry the section as "opencode" — the key the settings page binds and
    /// the config files use; the camelCase writer must not turn it into "openCode".</summary>
    [Fact]
    public void TheSettingsFileWritesTheSectionAsOpencode()
    {
        var json = JsonSerializer.Serialize(new AgentConfig(), ConfigStore.WriteOpts);
        Assert.Contains("\"opencode\":", json);
        Assert.DoesNotContain("\"openCode\"", json);
    }

    /// <summary>A choice value with padding is matched exactly as the card offered it, not after trimming: the
    /// trimmed text would never hit a value the card carries.</summary>
    [Fact]
    public async Task AnAnswerIsMatchedAgainstTheChoicesRaw()
    {
        var padded = """{"data":[{"id":"frm_10","sessionID":"ses_1","title":"Questions","fields":[{"key":"q0","title":"Route","type":"string","options":[{"value":" Route B ","label":"Route B"},{"value":"a","label":"Route A"}]}]}]}""";
        var fake = new FakeOpenCode(ProjDir) { QueueJson = """{"data":[]}""", FormsJson = padded };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        var a = Assert.Single(store.Snapshot());
        Assert.True(store.Decide(a.Id, "answer", " Route B ")); // exactly the value the card offered
        await WaitUntil(() => fake.FormReplies.Count == 1, "the answer never reached the form");
        Assert.Equal("""{"answer":{"q0":" Route B "}}""", fake.FormReplies[0]);
    }

    /// <summary>A withdrawn offer's cleanup must not drop a newer card's pending entry when the same id was
    /// re-offered in the meantime: the new card has to stay answerable.</summary>
    [Fact]
    public async Task AWithdrawnOfferDoesNotDropAReofferedCardsPending()
    {
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var first = new CancellationTokenSource();
        var a1 = new Approval("per_1", "OpenCode", "s", "p", "", "Run a command", "detail", null, now, now + 60_000);
        var asking1 = store.AskAsync(a1, first.Token);
        var a2 = new Approval("per_1", "OpenCode", "s", "p", "", "Run a command", "detail", null, now, now + 60_000);
        var asking2 = store.AskAsync(a2, CancellationToken.None); // the same id, re-offered while the first waits
        first.Cancel(); // the withdrawn offer's wait ends; its cleanup must not remove the newer pending
        Assert.Null(await asking1);
        Assert.True(store.Decide("per_1", "allow"), "the re-offered card lost its pending entry to the withdrawn offer's cleanup");
        Assert.Equal("allow", await asking2);
    }

    /// <summary>The WSL default user's home comes from /etc/wsl.conf (or the registry's DefaultUid through
    /// /etc/passwd); without a resolvable default user the distro is skipped, so other users' homes are never read.
    /// A full scan reads the default user's service.json only — the other user's is never touched.</summary>
    [Fact]
    public void TheDefaultUsersHomeIsResolvedAndOthersAreNotRead()
    {
        var share = Path.Combine(_dir, "wsl.localhost") + Path.DirectorySeparatorChar;
        var root = Path.Combine(_dir, "wsl.localhost", "Ubuntu");
        var conf = Path.Combine(root, "etc", "wsl.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
        File.WriteAllText(conf, "[boot]\nsystemd=true\n\n[user]\ndefault=haruki\n");
        var passwd = Path.Combine(root, "etc", "passwd");
        File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/bash\nharuki:x:1000:1000:Haruki:/home/haruki:/bin/bash\nother:x:1001:1001:Other:/home/other:/bin/zsh\n");
        Directory.CreateDirectory(Path.Combine(root, "home", "haruki", ".local", "state", "opencode"));
        Directory.CreateDirectory(Path.Combine(root, "home", "other", ".local", "state", "opencode"));
        File.WriteAllText(Path.Combine(root, "home", "haruki", ".local", "state", "opencode", "service.json"), """{"url":"http://127.0.0.1:49374","password":"pw","version":"2.3.2"}""");
        File.WriteAllText(Path.Combine(root, "home", "other", ".local", "state", "opencode", "service.json"), """{"url":"http://127.0.0.1:49375","password":"pw2","version":"2.3.2"}""");

        Assert.Equal("/home/haruki", OpenCodeServices.DefaultUserHome(root, "Ubuntu"));
        var services = OpenCodeServices.WslServices(enabled: true, distroNames: ["Ubuntu"], shareRoot: share);
        var mine = Assert.Single(services); // the other user's service is never read
        Assert.Equal("http://127.0.0.1:49374", mine.Info.Url);
        Assert.Contains("haruki", mine.Path);

        // a wsl.conf user without a passwd line falls back to /home/<name>, still distro-absolute
        var root3 = Path.Combine(_dir, "wsl.localhost", "Alpine");
        Directory.CreateDirectory(Path.Combine(root3, "etc"));
        File.WriteAllText(Path.Combine(root3, "etc", "wsl.conf"), "[user]\ndefault=alice\n");
        Directory.CreateDirectory(Path.Combine(root3, "home", "alice"));
        Assert.Equal("/home/alice", OpenCodeServices.DefaultUserHome(root3, "Alpine"));

        // without a resolvable default user the distro is skipped entirely
        var root2 = Path.Combine(_dir, "wsl.localhost", "Debian");
        Directory.CreateDirectory(Path.Combine(root2, "home", "someone"));
        Assert.Null(OpenCodeServices.DefaultUserHome(root2, "Debian"));
    }
}
