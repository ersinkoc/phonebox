using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The clipboard bridge: what it accepts from the phone, what it publishes from the Mac, and the rules that keep one
/// copy from bouncing between the two. A fake pasteboard stands in for the Mac's, so no test touches a real clipboard.
/// </summary>
public sealed class ClipboardBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-clip-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly ClipboardBridge _bridge;
    private int _stamp;

    public ClipboardBridgeTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        SetSwitch(true);
        _bridge = new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance);
    }

    /// <summary>Writes the config file with a new write time each time, which is how ConfigStore notices a change.</summary>
    private void SetSwitch(bool on)
    {
        File.WriteAllText(_config, "{\"bridge\":{\"clipboard\":" + (on ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    /// <summary>
    /// A change is read one poll after it is seen, once the change counter has stayed put for that poll (a password manager
    /// adds its marker after the text without a new count): so a copy needs two polls before it can go out.
    /// </summary>
    private void Settle()
    {
        _bridge.PollOnce();
        _bridge.PollOnce();
    }

    private static string Of(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    // ---- from the phone ----

    [Fact]
    public void ReceiveWritesThePasteboardAndPublishesToOtherPhones()
    {
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal("hello", _pb.Text);
        var seen = Assert.Single(_stream.Since(0, "phoneB"));
        Assert.Equal(("clipboard", "hello", "phoneA"), (seen.Name, seen.Text, seen.Origin));
        Assert.Empty(_stream.Since(0, "phoneA"));
    }

    [Fact]
    public void OffSwitchRefusesAndWritesNothing()
    {
        SetSwitch(false);
        Assert.Equal(ClipboardResult.Off, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal(0, _pb.Writes);
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AtTheLimitIsStoredOneByteOverIsRefused()
    {
        var atLimit = Of("a", ClipboardBridge.MaxBytes);
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone(atLimit, "phoneA"));
        Assert.Equal(atLimit, _pb.Text);
        var seq = _stream.Seq;
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("a", ClipboardBridge.MaxBytes + 1), "phoneA"));
        Assert.Equal(atLimit, _pb.Text);   // refused, not truncated, and nothing written
        Assert.Equal(seq, _stream.Seq);    // nothing published either
    }

    [Fact]
    public void LimitCountsBytesNotCharacters()
    {
        // 40 000 Greek letters are 40 000 characters but 80 000 bytes; 32 768 of them are exactly the limit
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("λ", 40_000), "phoneA"));
        Assert.Equal(ClipboardResult.Stored, _bridge.ReceiveFromPhone(Of("λ", ClipboardBridge.MaxBytes / 2), "phoneA"));
        Assert.Equal(ClipboardResult.TooLarge, _bridge.ReceiveFromPhone(Of("🌍", ClipboardBridge.MaxBytes / 4 + 1), "phoneA"));
    }

    [Fact]
    public void WhenTheMacRefusesTheWriteNothingIsPublished()
    {
        _pb.Fail = true;
        Assert.Equal(ClipboardResult.Failed, _bridge.ReceiveFromPhone("hello", "phoneA"));
        Assert.Equal(0, _stream.Seq);
    }

    // ---- from the Mac ----

    [Fact]
    public void FirstPollNeverPublishesTheExistingClipboard()
    {
        _pb.Copy("already there");
        _bridge.PollOnce();
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AMacCopyIsPublishedOnceWithNoOrigin()
    {
        _bridge.PollOnce();
        _pb.Copy("copied on the Mac");
        _bridge.PollOnce();
        _bridge.PollOnce();
        var e = Assert.Single(_stream.Since(0, null));
        Assert.Equal(("clipboard", "copied on the Mac", (string?)null), (e.Name, e.Text, e.Origin));
    }

    [Fact]
    public void ConcealedOrNonTextItemsAreNotPublished()
    {
        _bridge.PollOnce();
        _pb.Copy(null);
        Settle();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void AMacCopyOverTheLimitIsNotPublished()
    {
        _bridge.PollOnce();
        _pb.Copy(Of("a", ClipboardBridge.MaxBytes + 1));
        Settle();
        Assert.Equal(0, _stream.Seq);
    }

    // ---- loop prevention ----

    [Fact]
    public void TextJustReceivedIsNotEchoedBack()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phoneA");   // our own write moves the change counter
        Settle();
        Assert.DoesNotContain(_stream.Since(0, null), e => e.Origin is null);
        Assert.Single(_stream.Since(0, null));     // only the phone's own event
    }

    [Fact]
    public void ReCopyingAnOlderValueIsSentAgain()
    {
        _bridge.PollOnce();
        _pb.Copy("A");
        Settle();
        _bridge.ReceiveFromPhone("B", "phone1");
        Settle();                                   // the echo of B: swallowed
        _pb.Copy("A");
        Settle();                                   // the owner copies A again on purpose
        Assert.Equal(["A", "B", "A"], _stream.Since(0, null).Select(e => e.Text));
        Assert.Equal([null, "phone1", null], _stream.Since(0, null).Select(e => e.Origin));
    }

    [Fact]
    public void ACopyThatArrivesWhileTheClipboardIsBeingReadIsNotSharedFromAMixedRead()
    {
        _bridge.PollOnce();
        _pb.Copy("first");
        _bridge.PollOnce();                           // seen; read on the next pass
        _pb.AfterRead = () => _pb.Copy("second");   // a second copy lands between the two reads of one poll
        _bridge.PollOnce();
        _pb.AfterRead = null;
        Assert.Empty(_stream.Since(0, null));        // the mixed read shares nothing
        Settle();                                     // the next passes see the second copy and share it, whole
        Assert.Equal(["second"], _stream.Since(0, null).Select(e => e.Text));
    }

    [Fact]
    public void AWordThePhoneWroteBeforeTheFirstPollIsStillSentWhenTheOwnerCopiesItLater()
    {
        _bridge.ReceiveFromPhone("B", "phone1");    // before the first look at the clipboard: that look only notes what is there
        _bridge.PollOnce();
        _pb.Copy("B");                              // the owner copies the same words on purpose
        Settle();
        Assert.Equal([("B", "phone1"), ("B", null)], _stream.Since(0, null).Select(e => (e.Text, e.Origin)));
    }

    [Fact]
    public async Task TwoPhonesWritingAtOnceEndWithTheStreamsLastEventEqualToTheClipboard()
    {
        // the publish happens inside the lock that guards the write, so the order of the events is the order of the writes
        for (var round = 0; round < 200; round++)
        {
            var pb = new FakePasteboard();
            var stream = new BridgeStream();
            var bridge = new ClipboardBridge(pb, _store, stream, NullLogger<ClipboardBridge>.Instance);
            await Task.WhenAll(Task.Run(() => bridge.ReceiveFromPhone("A", "phoneA")), Task.Run(() => bridge.ReceiveFromPhone("B", "phoneB")));
            Assert.Equal(pb.Text, stream.Since(0, null)[^1].Text);
        }
    }

    [Fact]
    public void CopyingTheSameTextTheAgentJustWroteLaterIsSentAgain()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phone1");
        Settle();                                   // echo swallowed, the guard is spent
        _pb.Copy("B");
        Settle();                                   // a deliberate copy of the same words
        Assert.Equal([("B", "phone1"), ("B", null)], _stream.Since(0, null).Select(e => (e.Text, e.Origin)));
    }

    [Fact]
    public void TheEchoGuardEndsAtTheFirstChangeAfterOurWriteEvenWhenThatChangeIsDifferentText()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phone1");
        _pb.Copy("Z");                              // the owner copies something else before the next poll
        Settle();                                   // Z goes out, and B's guard must not survive it
        _pb.Copy("B");
        Settle();                                   // hours later: B copied on purpose
        Assert.Equal(["B", "Z", "B"], _stream.Since(0, null).Select(e => e.Text));
    }

    [Fact]
    public void TheEchoGuardEndsAtTheFirstChangeAfterOurWriteEvenWhenThatChangeIsNotShareable()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("B", "phone1");
        _pb.Copy(null);                             // a password manager's item replaces it, unreadable to the bridge
        Settle();
        _pb.Copy("B");
        Settle();
        Assert.Equal(["B", "B"], _stream.Since(0, null).Select(e => e.Text));
        Assert.Equal(["phone1", null], _stream.Since(0, null).Select(e => e.Origin));
    }

    [Fact]
    public void ADeliberateCopyOfTheSameTextBeforeTheFirstPollIsStillShared()
    {
        _bridge.PollOnce();
        _bridge.ReceiveFromPhone("same", "phone1");
        _pb.Copy("same");
        Settle();
        Assert.Equal(["phone1", null], _stream.Since(0, null).Select(e => e.Origin));
    }

    [Fact]
    public void APrivacyMarkerAppliedAfterReadingTheTextPreventsPublication()
    {
        _bridge.PollOnce();
        _pb.Copy("fixture text");
        _bridge.PollOnce();
        _pb.BeforeShare = _pb.Mark;
        _bridge.PollOnce();
        Assert.Empty(_stream.Since(0, null));
    }

    [Fact]
    public void ATransientReadFailureDoesNotDiscardTheCopy()
    {
        _bridge.PollOnce();
        _pb.Copy("new text");
        _bridge.PollOnce();
        _pb.AfterRead = () => throw new InvalidOperationException();
        Assert.Throws<InvalidOperationException>(_bridge.PollOnce);
        _pb.AfterRead = null;
        _bridge.PollOnce();
        Assert.Equal("new text", Assert.Single(_stream.Since(0, null)).Text);
    }

    // ---- the switch ----

    [Fact]
    public void NothingIsPublishedAfterTheSwitchIsTurnedOff()
    {
        _bridge.PollOnce();
        SetSwitch(false);
        _pb.Copy("copied while off");
        Settle();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void TurningTheSwitchOnDoesNotPublishWhatIsAlreadyCopied()
    {
        SetSwitch(false);
        _bridge.PollOnce();
        _pb.Copy("copied while off");
        SetSwitch(true);
        _bridge.PollOnce();                         // the first poll after turning on only notes where the clipboard is
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
        _pb.Copy("copied while on");
        Settle();
        Assert.Equal(["copied while on"], _stream.Since(0, null).Select(e => e.Text));
    }

    [Fact]
    public void ATickThatFailsIsSwallowed()
    {
        _pb.ThrowOnCount = true;
        _bridge.Tick();                             // must not throw: the timer keeps running
        _pb.ThrowOnCount = false;
        _bridge.Tick();
        _pb.Copy("after the failure");
        _bridge.Tick();
        _bridge.Tick();
        Assert.Equal(["after the failure"], _stream.Since(0, null).Select(e => e.Text));
    }

    // ---- password managers: the marker that comes after the text ----

    [Fact]
    public void APasswordManagerThatAddsItsConcealedMarkerAfterTheTextIsNeverShared()
    {
        // clearContents -> setString(password) -> setString("", concealed type): the count moves on the first call only
        _bridge.PollOnce();
        _pb.Copy("hunter2");                        // clearContents and the password: a new count, no marker yet
        _bridge.PollOnce();                         // the change is seen here, between the manager's two calls
        _pb.Mark();                                 // the concealed marker arrives, the count stays the same
        _bridge.PollOnce();
        _bridge.PollOnce();
        Assert.Equal(0, _stream.Seq);
    }

    [Fact]
    public void ACopyIsReadOnlyOnceTheCounterHasStayedPutForAPoll()
    {
        _bridge.PollOnce();
        _pb.Copy("first");
        _bridge.PollOnce();                         // seen
        _pb.Copy("second");
        _bridge.PollOnce();                         // the counter moved again: wait once more
        Assert.Equal(0, _stream.Seq);
        _bridge.PollOnce();
        Assert.Equal(["second"], _stream.Since(0, null).Select(e => e.Text));
    }

    // ---- the switch, inside the lock ----

    [Fact]
    public void TurningTheSwitchOffForgetsTheKeptTexts()
    {
        _bridge.PollOnce();
        _pb.Copy("copied on the Mac");
        Settle();
        _bridge.ReceiveFromPhone("from the phone", "phoneA");
        Assert.Equal(2, _stream.Since(0, null).Count);
        SetSwitch(false);
        _bridge.PollOnce();
        Assert.Empty(_stream.Since(0, null));       // nothing copied stays in memory once sharing is off
    }

    [Fact]
    public void APhoneWriteRacingTheSwitchOffIsRefusedNotWrittenAfterIt()
    {
        _bridge.PollOnce();
        _pb.Copy("x");
        _bridge.PollOnce();                         // seen; the next poll reads it while holding the bridge's lock
        ClipboardResult? phone = null;
        Thread? writer = null;
        _pb.AfterRead = () =>
        {
            // a phone's write arrives now, while the switch is still on, and waits for the lock; then the owner turns sharing off
            writer = new Thread(() => phone = _bridge.ReceiveFromPhone("late", "phoneA"));
            writer.Start();
            for (var i = 0; i < 500 && (writer.ThreadState & ThreadState.WaitSleepJoin) == 0; i++) Thread.Sleep(5);
            Thread.Sleep(50);
            SetSwitch(false);
        };
        _bridge.PollOnce();
        _pb.AfterRead = null;
        Assert.True(writer!.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(ClipboardResult.Off, phone);
        Assert.Equal(0, _pb.Writes);
    }

    // ---- macOS refusing the clipboard ----

    [Fact]
    public void WhenMacOSGivesNoTextTheBridgeWarnsOnceWithoutTheTextAndSaysSo()
    {
        var log = new ListLogger<ClipboardBridge>();
        var bridge = new ClipboardBridge(_pb, _store, _stream, log);
        bridge.PollOnce();
        _pb.Deny = true;
        _pb.Copy("secret words");
        bridge.PollOnce(); bridge.PollOnce();
        _pb.Copy("more secret words");
        bridge.PollOnce(); bridge.PollOnce();
        Assert.True(bridge.Refused);
        var warning = Assert.Single(log.Lines);
        Assert.DoesNotContain("secret", warning);
        Assert.Equal(0, _stream.Seq);
        _pb.Deny = false;                           // the owner allows it in System Settings
        _pb.Copy("allowed now");
        bridge.PollOnce(); bridge.PollOnce();
        Assert.False(bridge.Refused);
        Assert.Equal(["allowed now"], _stream.Since(0, null).Select(e => e.Text));
    }

    [Fact]
    public void ASettingThatDeniesTheClipboardIsReportedWhenSharingStarts()
    {
        var log = new ListLogger<ClipboardBridge>();
        var bridge = new ClipboardBridge(_pb, _store, _stream, log);
        _pb.AccessDenied = true;
        bridge.PollOnce();
        Assert.True(bridge.Refused);
        Assert.Single(log.Lines);
        SetSwitch(false);
        bridge.PollOnce();
        Assert.False(bridge.Refused);               // off: there is nothing to report
    }

    [Fact]
    public void AnItemWithoutTextIsNotARefusal()
    {
        var log = new ListLogger<ClipboardBridge>();
        var bridge = new ClipboardBridge(_pb, _store, _stream, log);
        bridge.PollOnce();
        _pb.Copy(null);                             // an image, say
        bridge.PollOnce(); bridge.PollOnce();
        _pb.Copy("pw");
        _pb.Mark();                                 // a password manager's copy
        bridge.PollOnce(); bridge.PollOnce();
        Assert.False(bridge.Refused);
        Assert.Empty(log.Lines);
    }

    // ---- the event list ----

    [Fact]
    public void EventsSkipTheirOriginDevice()
    {
        _stream.Publish("clipboard", "t", "phoneA");
        Assert.Empty(_stream.Since(0, "phoneA"));
        Assert.Single(_stream.Since(0, "phoneB"));
        Assert.Single(_stream.Since(0, null));
    }

    [Fact]
    public void TheStreamKeepsOnlyTheLastTwentyAndNumbersThemInOrder()
    {
        for (var i = 1; i <= 25; i++) _stream.Publish("clipboard", "t" + i, null);
        var all = _stream.Since(0, null);
        Assert.Equal(20, all.Count);
        Assert.Equal(6, all[0].Seq);
        Assert.Equal(25, _stream.Seq);
        Assert.Equal(["t24", "t25"], _stream.Since(23, null).Select(e => e.Text));
    }

    [Fact]
    public void PublishingWakesAListener()
    {
        var woke = 0;
        _stream.Emitted += () => woke++;
        _stream.Publish("clipboard", "t", null);
        Assert.Equal(1, woke);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
