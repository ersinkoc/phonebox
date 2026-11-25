using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Phone notifications on the Mac: what is shown, what is held back, and what is done with a login code. A fake poster
/// and a fake pasteboard stand in for macOS, and the clock is the test's own.
/// </summary>
public sealed class NotificationBridgeTests : IDisposable
{
    private sealed class FakeNotifier : IMacNotifier, IWarmable
    {
        public int Warmed;
        public void Warm() => Warmed++;
        public readonly List<(string Title, string Subtitle, string Body)> Posts = [];
        public readonly List<byte[]?> Icons = [];
        public readonly List<string?> Ids = [];
        public bool Ok = true;
        public Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
        {
            Icons.Add(iconPng);
            Ids.Add(replaceId);
            Posts.Add((title, subtitle, body)); // last: tests wait on Posts.Count and then read Icons/Ids
            return Task.FromResult(Ok);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-notify-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakeNotifier _notifier = new();
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly ClipboardBridge _clipboard;
    private readonly CapturingLogger<NotificationBridge> _log = new();
    private long _now = 1_000_000;
    private int _stamp;
    private List<PhoneSeen> _phones = [];
    private readonly NotificationBridge _bridge;

    public NotificationBridgeTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        SetBridge(notifications: true);
        _clipboard = new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance);
        _bridge = new NotificationBridge(_notifier, _store, _clipboard, () => _now, _log, () => _phones);
    }

    private void SetBridge(bool notifications = false, bool copyCodes = false, bool clipboard = false)
    {
        File.WriteAllText(_config, $"{{\"bridge\":{{\"notifications\":{(notifications ? "true" : "false")},\"copyCodes\":{(copyCodes ? "true" : "false")},\"clipboard\":{(clipboard ? "true" : "false")}}}}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    private static PhoneNotification N(string key, string app = "Messages", string title = "Anna", string text = "hi", string category = "message") =>
        new(key, app, "com.example." + app.ToLowerInvariant(), title, text, category, 1);

    private async Task<NotifyResult> Send(PhoneNotification n, string? device = "phoneA")
    {
        var result = _bridge.Receive(n, device);
        await _bridge.DrainAsync();
        return result;
    }

    [Fact]
    public async Task StartingWithTheSwitchOnWarmsTheNotifierUpAndWithItOffDoesNot()
    {
        await _bridge.StartAsync(CancellationToken.None);
        Assert.Equal(1, _notifier.Warmed);
        await _bridge.StopAsync(CancellationToken.None);
        SetBridge(notifications: false);
        var off = new NotificationBridge(_notifier, _store, _clipboard, () => _now, _log);
        await off.StartAsync(CancellationToken.None);
        Assert.Equal(1, _notifier.Warmed);
        await off.StopAsync(CancellationToken.None);
    }

    // ---- the posting app's icon ----

    private static byte[] Png(int w = 96, int h = 96, int extra = 0)
    {
        var png = new byte[33 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        png[16] = (byte)(w >> 24); png[17] = (byte)(w >> 16); png[18] = (byte)(w >> 8); png[19] = (byte)w;
        png[20] = (byte)(h >> 24); png[21] = (byte)(h >> 16); png[22] = (byte)(h >> 8); png[23] = (byte)h;
        return png;
    }

    [Fact]
    public async Task AnAppsIconTravelsWithItsNotification()
    {
        var icon = Png();
        await Send(N("k1") with { IconPng = icon });
        Assert.Same(icon, Assert.Single(_notifier.Icons));
    }

    [Fact]
    public async Task AnIconSeenOnceIsUsedForTheAppsLaterNotificationsThatCarryNone()
    {
        var icon = Png();
        await Send(N("k1", "Facebook") with { IconPng = icon });
        await Send(N("k2", "Facebook", text: "second"));
        Assert.Equal([icon, icon], _notifier.Icons);
    }

    [Fact]
    public async Task AnIconIsOnlyUsedForItsOwnApp()
    {
        await Send(N("k1", "Facebook") with { IconPng = Png() });
        await Send(N("k2", "Messages"));
        Assert.Null(_notifier.Icons[1]);
    }

    [Fact]
    public async Task ANewerIconReplacesTheOldOne()
    {
        var old = Png(); var fresh = Png(extra: 4);
        await Send(N("k1", "Facebook") with { IconPng = old });
        await Send(N("k2", "Facebook", text: "again") with { IconPng = fresh });
        await Send(N("k3", "Facebook", text: "third"));
        Assert.Same(fresh, _notifier.Icons[2]);
    }

    [Fact]
    public async Task TurningTheSwitchOffForgetsTheIcons()
    {
        await Send(N("k1", "Facebook") with { IconPng = Png() });
        SetBridge(notifications: false);
        await _bridge.DrainAsync();                    // the switch is looked at here: what is held is dropped
        SetBridge(notifications: true);
        await Send(N("k2", "Facebook", text: "after"));
        Assert.Null(_notifier.Icons[^1]);
    }

    [Fact]
    public async Task TheRunningWorkerForgetsTheIconsWhenTheSwitchGoesOffWithNoNotificationToWakeIt()
    {
        var bridge = new NotificationBridge(_notifier, _store, _clipboard, () => _now, _log, () => _phones) { IconCheck = TimeSpan.FromMilliseconds(20) };
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.Receive(N("k1", "Facebook") with { IconPng = Png() }, "phoneA");
            await WaitFor(() => _notifier.Posts.Count == 1);
            SetBridge(notifications: false);              // nothing reaches the bridge while it is off
            await Task.Delay(500);
            SetBridge(notifications: true);
            bridge.Receive(N("k2", "Facebook", text: "after"), "phoneA");
            await WaitFor(() => _notifier.Posts.Count == 2);
            Assert.Null(_notifier.Icons[^1]);
        }
        finally { await bridge.StopAsync(CancellationToken.None); bridge.Dispose(); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task OnlyTheLastHundredAppsKeepTheirIcons()
    {
        for (var i = 0; i < 101; i++) await Send(N("k" + i, "App" + i) with { IconPng = Png() });
        await Send(N("again0", "App0", text: "later"));      // the oldest was forgotten
        await Send(N("again100", "App100", text: "later"));  // the newest was kept
        Assert.Null(_notifier.Icons[^2]);
        Assert.NotNull(_notifier.Icons[^1]);
    }

    // ---- the switch ----

    [Fact]
    public async Task OffSwitchReturnsOffAndPostsNothing()
    {
        SetBridge(notifications: false);
        Assert.Equal(NotifyResult.Off, await Send(N("k1")));
        Assert.Empty(_notifier.Posts);
    }

    // ---- what is shown ----

    [Fact]
    public async Task AMessageShowsTheSenderTheAppAndTheText()
    {
        Assert.Equal(NotifyResult.Queued, await Send(N("k1", "Messages", "Anna", "See you at 8")));
        Assert.Equal([("Anna", "Messages", "See you at 8")], _notifier.Posts);
    }

    [Fact]
    public async Task ANotificationWithoutATitleUsesTheAppName()
    {
        await Send(N("k1", "Messages", "", "hello"));
        Assert.Equal([("Messages", "", "hello")], _notifier.Posts);
    }

    [Fact]
    public async Task ACallUsesTheCallerAsTitleAndIncomingCallAsSubtitle()
    {
        await Send(N("c1", "Phone", "Maria Papadopoulou", "", "call"));
        Assert.Equal([("Maria Papadopoulou", "Incoming call", "")], _notifier.Posts);
    }

    [Fact]
    public async Task ACallThatBecomesAMissedCallIsShownAgainEvenWithTheSameTitleAndText()
    {
        await Send(N("c9|100", "Phone", "Maria", "", "call"));
        await Send(N("c9|100", "Phone", "Maria", "", "missed_call"));   // the same key, title and text: only the kind changed
        Assert.Equal(2, _notifier.Posts.Count);
    }

    [Fact]
    public async Task AMissedCallShowsTheAppNotAMissedCallLabelTheDialerAlreadyWrote()
    {
        // the dialer's own title is "Missed call" and its text the caller: a second "Missed call" would be said twice
        await Send(N("c2", "Phone", "Missed call", "Maria", "missed_call"));
        Assert.Equal([("Missed call", "Phone", "Maria")], _notifier.Posts);
    }

    [Fact]
    public async Task ACallWhoseCallerWasNamedLaterReplacesItsFirstNotification()
    {
        await Send(N("c3|100", "Phone", "6940931040", "", "call"));
        await Send(N("c3|100", "Phone", "Maria Papadopoulou", "", "call"));   // same call, the contact was found
        Assert.Equal(2, _notifier.Posts.Count);                                // a changed title is not a duplicate
        Assert.NotNull(_notifier.Ids[0]);
        Assert.Equal(_notifier.Ids[0], _notifier.Ids[1]);                      // so the second replaces the first on the Mac
    }

    [Fact]
    public async Task ADifferentCallOrPhoneGetsANotificationOfItsOwn()
    {
        await Send(N("c4|100", "Phone", "Anna", "", "call"), "phoneA");
        await Send(N("c4|200", "Phone", "Anna", "", "call"), "phoneA");
        await Send(N("c4|100", "Phone", "Anna", "", "call"), "phoneB");
        Assert.Equal(3, _notifier.Ids.Distinct().Count());
    }

    [Fact]
    public async Task OrdinaryNotificationsHaveNoReplaceId()
    {
        await Send(N("m1"));
        Assert.Null(_notifier.Ids[0]);
    }

    [Fact]
    public async Task TextIsCutToTheLimitsWithAnEllipsis()
    {
        await Send(N("k1", "Messages", new string('t', 201), new string('x', 2001)));
        var (title, _, body) = Assert.Single(_notifier.Posts);
        Assert.Equal(200, title.Length);
        Assert.EndsWith("…", title);
        Assert.Equal(2000, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public async Task ANulInTheTextDoesNotEndIt()
    {
        // osascript receives C strings: a NUL would end the shown text there
        await Send(N("k1", "Messages", "An\0na", "before\0after"));
        Assert.Equal([("An na", "Messages", "before after")], _notifier.Posts);
    }

    [Fact]
    public async Task TextAtTheLimitIsNotCut()
    {
        await Send(N("k1", "Messages", new string('t', 200), new string('x', 2000)));
        var (title, _, body) = Assert.Single(_notifier.Posts);
        Assert.Equal(new string('t', 200), title);
        Assert.Equal(new string('x', 2000), body);
    }

    [Fact]
    public async Task TheSubtitleNamesThePhoneOnlyWhenMoreThanOnePhoneIsAround()
    {
        _phones = [new PhoneSeen("phoneA", "Pixel 9", _now, "Wi-Fi")];
        await Send(N("k1"), "phoneA");
        Assert.Equal("Messages", _notifier.Posts[0].Subtitle);
        _phones = [new PhoneSeen("phoneA", "Pixel 9", _now, "Wi-Fi"), new PhoneSeen("phoneB", "Galaxy S25", _now, "Wi-Fi")];
        await Send(N("k2"), "phoneB");
        Assert.Equal("Messages · Galaxy S25", _notifier.Posts[1].Subtitle);
    }

    [Fact]
    public async Task APhoneNotSeenForTenMinutesDoesNotCount()
    {
        _phones = [new PhoneSeen("phoneA", "Pixel 9", _now, "Wi-Fi"), new PhoneSeen("phoneB", "Galaxy S25", _now - 11 * 60_000, "Wi-Fi")];
        await Send(N("k1"), "phoneA");
        Assert.Equal("Messages", _notifier.Posts[0].Subtitle);
    }

    // ---- the same notification twice ----

    [Fact]
    public async Task SameKeyAndTextPostsOnce()
    {
        Assert.Equal(NotifyResult.Queued, await Send(N("k1")));
        Assert.Equal(NotifyResult.Duplicate, await Send(N("k1")));
        Assert.Single(_notifier.Posts);
    }

    [Fact]
    public async Task SameKeyWithNewTextPostsAgain()
    {
        await Send(N("k1", text: "one"));
        Assert.Equal(NotifyResult.Queued, await Send(N("k1", text: "two")));
        Assert.Equal(["one", "two"], _notifier.Posts.Select(p => p.Body));
    }

    [Fact]
    public async Task SameTextUnderAnotherKeyIsAnotherNotification()
    {
        await Send(N("k1"));
        Assert.Equal(NotifyResult.Queued, await Send(N("k2")));
        Assert.Equal(2, _notifier.Posts.Count);
    }

    // ---- bursts ----

    [Fact]
    public async Task ElevenFromOneAppInTenSecondsBecomeTenPlusOneSummary()
    {
        for (var i = 1; i <= 11; i++) _bridge.Receive(N("k" + i), "phoneA");
        await _bridge.DrainAsync();
        Assert.Equal(10, _notifier.Posts.Count);                 // the summary waits for the end of the window
        Assert.All(_notifier.Posts, p => Assert.Equal("Anna", p.Title));
        _now += 10_000;
        await _bridge.DrainAsync();
        Assert.Equal(11, _notifier.Posts.Count);
        Assert.Equal(("1 more from Messages", "", ""), _notifier.Posts[10]);
    }

    [Fact]
    public async Task ATwelfthMakesItTwoMore()
    {
        for (var i = 1; i <= 12; i++) _bridge.Receive(N("k" + i), "phoneA");
        _now += 10_000;
        await _bridge.DrainAsync();
        Assert.Equal(("2 more from Messages", "", ""), _notifier.Posts.Last());
        Assert.Equal(11, _notifier.Posts.Count);
    }

    [Fact]
    public async Task AWorkerThatKeepsUpDoesNotPostASummaryPerMessage()
    {
        // what the real worker does: it drains after every message. 25 messages 300 ms apart are 10 banners and ONE summary.
        for (var i = 1; i <= 25; i++)
        {
            _bridge.Receive(N("k" + i, "WhatsApp"), "phoneA");
            await _bridge.DrainAsync();
            _now += 300;
        }
        Assert.Equal(10, _notifier.Posts.Count);
        _now += 10_000;
        await _bridge.DrainAsync();
        Assert.Equal(11, _notifier.Posts.Count);
        Assert.Equal(("15 more from WhatsApp", "", ""), _notifier.Posts.Last());
    }

    [Fact]
    public async Task ASummaryWaitsForItsWindowAndOnlyThenIsPosted()
    {
        for (var i = 1; i <= 11; i++) _bridge.Receive(N("k" + i), "phoneA");
        await _bridge.DrainAsync();
        _now += 9_999;
        await _bridge.DrainAsync();
        Assert.Equal(10, _notifier.Posts.Count);
        _now += 1;
        await _bridge.DrainAsync();
        Assert.Equal(11, _notifier.Posts.Count);
        await _bridge.DrainAsync();                              // and it is posted once, not on every drain
        Assert.Equal(11, _notifier.Posts.Count);
    }

    [Fact]
    public async Task TheWorkerKnowsWhenTheNextSummaryIsDue()
    {
        Assert.Null(_bridge.NextSummaryDelay());
        for (var i = 1; i <= 11; i++) _bridge.Receive(N("k" + i), "phoneA");
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), _bridge.NextSummaryDelay());
        _now += 4_000;
        Assert.Equal(TimeSpan.FromMilliseconds(6_000), _bridge.NextSummaryDelay());
        _now += 6_000;
        await _bridge.DrainAsync();
        Assert.Null(_bridge.NextSummaryDelay());                 // the summary went out
    }

    [Fact]
    public async Task AnotherAppIsNotCoalescedWithTheFirst()
    {
        for (var i = 1; i <= 10; i++) _bridge.Receive(N("a" + i, "Messages"), "phoneA");
        for (var i = 1; i <= 5; i++) _bridge.Receive(N("b" + i, "Signal"), "phoneA");
        await _bridge.DrainAsync();
        Assert.Equal(15, _notifier.Posts.Count);
        Assert.DoesNotContain(_notifier.Posts, p => p.Title.Contains("more from"));
    }

    [Fact]
    public async Task TheTenSecondWindowSlides()
    {
        for (var i = 1; i <= 10; i++) _bridge.Receive(N("a" + i), "phoneA");
        await _bridge.DrainAsync();
        _now += 10_001;
        for (var i = 1; i <= 3; i++) _bridge.Receive(N("b" + i), "phoneA");
        await _bridge.DrainAsync();
        Assert.Equal(13, _notifier.Posts.Count);
        Assert.DoesNotContain(_notifier.Posts, p => p.Title.Contains("more from"));
    }

    [Fact]
    public async Task TheWorkerQueueKeepsTheNewestFifty()
    {
        for (var i = 1; i <= 60; i++) _bridge.Receive(N("k" + i, "App" + i, "From" + i), "phoneA");
        await _bridge.DrainAsync();
        Assert.Equal(50, _notifier.Posts.Count);
        Assert.Equal("From11", _notifier.Posts[0].Title);
        Assert.Equal("From60", _notifier.Posts[^1].Title);
    }

    // ---- login codes ----

    [Fact]
    public async Task ACodeIsCopiedWhenTheSwitchIsOnAndExactlyOneCandidate()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Messages", "Bank", "Your verification code is 482913"));
        Assert.Equal("482913", _pb.Text);
        Assert.Equal("Messages · Code copied", _notifier.Posts.Single().Subtitle);
    }

    [Fact]
    public async Task ACodeInTheTitleIsFoundToo()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Authenticator", "OTP 118822", "tap to approve"));
        Assert.Equal("118822", _pb.Text);
    }

    [Fact]
    public async Task AnAmbiguousTextDoesNotFallBackToACodeInTheTitle()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Messages", "OTP 118822", "Your PIN is 1234 and code 5678"));
        Assert.Null(_pb.Text);
    }

    [Fact]
    public async Task ACodeThatTheCutWouldShortenIsCopiedWhole()
    {
        // the shown text ends in "code 1234…"; the code is searched for in what the phone sent
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Messages", "Bank", new string('x', 1989) + " code 12345678"));
        Assert.Equal("12345678", _pb.Text);
        Assert.EndsWith("code 1234…", _notifier.Posts.Single().Body);
    }

    [Fact]
    public async Task ASecondCodeAfterTheCutStillMakesTheTextAmbiguous()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Messages", "OTP 118822", "Your code is 482913. " + new string('x', 2000) + " Your new code is 654321"));
        Assert.Null(_pb.Text);                                  // neither the first code nor the one in the title
        Assert.Equal("Messages", _notifier.Posts.Single().Subtitle);
    }

    [Fact]
    public async Task NoCodeIsCopiedWhenTheCodeSwitchIsOff()
    {
        SetBridge(notifications: true, copyCodes: false);
        await Send(N("k1", "Messages", "Bank", "Your verification code is 482913"));
        Assert.Null(_pb.Text);
        Assert.Equal("Messages", _notifier.Posts.Single().Subtitle);
    }

    [Fact]
    public async Task NoCodeIsCopiedWhenTwoCandidates()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("k1", "Messages", "Bank", "Your PIN is 1234 and code 5678"));
        Assert.Null(_pb.Text);
        Assert.Equal("Messages", _notifier.Posts.Single().Subtitle);
    }

    [Fact]
    public async Task ACallIsNeverSearchedForACode()
    {
        SetBridge(notifications: true, copyCodes: true);
        await Send(N("c1", "Phone", "Code 482913", "", "call"));
        Assert.Null(_pb.Text);
    }

    [Fact]
    public async Task ADuplicateDoesNotCopyTheCodeAgain()
    {
        SetBridge(notifications: true, copyCodes: true);
        var n = N("k1", "Messages", "Bank", "code 482913");
        await Send(n);
        _pb.Copy("something the owner copied meanwhile");
        Assert.Equal(NotifyResult.Duplicate, await Send(n));
        Assert.Equal("something the owner copied meanwhile", _pb.Text);
    }

    [Fact]
    public async Task ACopiedCodeIsNotPublishedToPhones()
    {
        SetBridge(notifications: true, copyCodes: true, clipboard: true);
        _clipboard.PollOnce();                                          // clipboard sharing is on: the bridge is watching
        await Send(N("k1", "Messages", "Bank", "Your code is 482913"));
        Assert.Equal("482913", _pb.Text);
        _clipboard.PollOnce();                                          // sees the change our own write made (settle poll)
        _clipboard.PollOnce();                                          // reads after settle
        Assert.Equal(0, _stream.Seq);                                   // and does not send the code to any phone
        _pb.Copy("482913");                                             // the owner copying the same digits later is still sent
        _clipboard.PollOnce();
        _clipboard.PollOnce();
        Assert.Equal(["482913"], _stream.Since(0, null).Select(e => e.Text));
    }

    // ---- the switch turned off while notifications wait ----

    [Fact]
    public async Task NotificationsQueuedBeforeTheSwitchWentOffAreNeverShown()
    {
        for (var i = 1; i <= 5; i++) _bridge.Receive(N("k" + i, "App" + i), "phoneA");
        SetBridge(notifications: false);
        await _bridge.DrainAsync();
        Assert.Empty(_notifier.Posts);
        SetBridge(notifications: true);                          // turned back on: the old ones are gone, not waiting
        await _bridge.DrainAsync();
        Assert.Empty(_notifier.Posts);
    }

    [Fact]
    public async Task AHeldBackSummaryIsDroppedWhenTheSwitchGoesOff()
    {
        for (var i = 1; i <= 11; i++) _bridge.Receive(N("k" + i), "phoneA");
        await _bridge.DrainAsync();
        SetBridge(notifications: false);
        _now += 10_000;
        await _bridge.DrainAsync();
        Assert.Equal(10, _notifier.Posts.Count);
        Assert.Null(_bridge.NextSummaryDelay());
    }

    [Fact]
    public async Task OnlyTheNotificationsStillQueuedWhenTheSwitchGoesOffAreDropped()
    {
        _bridge.Receive(N("k1"), "phoneA");
        await _bridge.DrainAsync();                              // shown while it was on
        _bridge.Receive(N("k2"), "phoneA");
        SetBridge(notifications: false);
        await _bridge.DrainAsync();
        Assert.Equal(["Anna"], _notifier.Posts.Select(p => p.Title));
        Assert.Single(_notifier.Posts);
    }

    // ---- the log ----

    [Fact]
    public async Task ContentNeverReachesTheLog()
    {
        SetBridge(notifications: true, copyCodes: true);
        _notifier.Ok = false; // a failing poster is logged too
        await Send(N("k1", "Messages", "SECRETTITLE", "SECRETBODY code 482913"));
        _bridge.Receive(N("k1", "Messages", "SECRETTITLE", "SECRETBODY code 482913"), "phoneA"); // a duplicate
        await _bridge.DrainAsync();
        Assert.All(_log.Lines, line =>
        {
            Assert.DoesNotContain("SECRET", line);
            Assert.DoesNotContain("482913", line);
            Assert.DoesNotContain("Messages", line);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
