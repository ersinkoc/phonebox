using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A cycle key runs the next key of its list on each tap, and remembers which ran last. Keys are replaced by a recorder:
/// a "keys:hold:NAME" step reports that it started and waits until the test releases it, so overlapping taps happen in
/// a fixed order instead of by sleeping; "keys:not_a_key" fails.
/// </summary>
[Collection("Macro recorders")]
public sealed class ShortcutCycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-cycle-").FullName;
    private readonly ConcurrentDictionary<string, (ManualResetEventSlim Started, ManualResetEventSlim Release)> _gates = new();
    private readonly ConcurrentBag<Task> _taps = [];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public ShortcutCycleTests() => Shortcuts.TestKeys = spec =>
    {
        if (spec == "not_a_key") throw new InvalidOperationException("unknown key: not_a_key");
        if (!spec.StartsWith("hold:")) return;
        var gate = Gate(spec[5..]);
        gate.Started.Set();
        if (!gate.Release.Wait(Patience)) throw new TimeoutException($"{spec} was never released");
    };

    public void Dispose()
    {
        // let every tap still in flight finish before the static hook goes, so none reaches the next test's recorder
        foreach (var (_, release) in _gates.Values) release.Set();
        try { Task.WaitAll([.. _taps], Patience); } catch (AggregateException) { }
        Shortcuts.TestKeys = null;
        foreach (var (started, release) in _gates.Values) { started.Dispose(); release.Dispose(); }
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A tap on another thread, joined when the test ends.</summary>
    private Task<string?> Tap(AgentConfig cfg, string id)
    {
        var tap = Task.Run(() => Shortcuts.Run(cfg, id, NullLogger.Instance));
        _taps.Add(tap);
        return tap;
    }

    /// <summary>The tap failed on its bad key, not on a gate that timed out.</summary>
    private static void FailedOnTheBadKey(string? result) => Assert.Contains("not_a_key", result ?? "");

    private (ManualResetEventSlim Started, ManualResetEventSlim Release) Gate(string name) => _gates.GetOrAdd(name, _ => (new(), new()));
    private void Started(string name) => Assert.True(Gate(name).Started.Wait(Patience), $"{name} never started");
    private void Release(string name) => Gate(name).Release.Set();

    // a toggle remembers its state in the agent, which is how a test sees which key ran (ids are unique: the memory is static)
    private static Shortcut Lamp(string id) => new() { Id = id, Label = id, Toggle = true, Cmd = "builtin:macro", Steps = ["wait:1"] };
    private static Shortcut Cycle(string id, params string[] keys) => new() { Id = id, Label = id, Cmd = "builtin:cycle", Cycle = [.. keys] };
    private static bool On(Shortcut s) => Shortcuts.State(s, null, null) == true;
    private static string Id() => "cy-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void EachTapRunsTheNextKeyAndTheListStartsAgain()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), c = Lamp(id + "c"), key = Cycle(id, a.Id, b.Id, c.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, c, key] };
        foreach (var expected in new[] { a, b, c, a })
        {
            var before = new[] { a, b, c }.ToDictionary(s => s.Id, On);
            Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
            foreach (var s in new[] { a, b, c }) Assert.Equal(s == expected ? !before[s.Id] : before[s.Id], On(s));
        }
    }

    [Fact]
    public void AKeyThatFailsIsTriedAgainOnTheNextTap()
    {
        var id = Id();
        var bad = new Shortcut { Id = id + "bad", Label = "bad", Cmd = "builtin:keys", Args = "not_a_key" };
        var good = Lamp(id + "good");
        var key = Cycle(id, bad.Id, good.Id);
        var cfg = new AgentConfig { Shortcuts = [bad, good, key] };
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.Null(Shortcuts.CycleStates(cfg));
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance)); // still the failing key, not the next one
        Assert.False(On(good));
    }

    [Fact]
    public void ThePhoneIsToldWhichKeyRanLast()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, key] };
        Assert.Null(Shortcuts.CycleStates(cfg)); // never tapped
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(a.Id, Shortcuts.CycleStates(cfg)![id]);
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(b.Id, Shortcuts.CycleStates(cfg)![id]);
        key.Cycle = [a.Id]; // the list shrank since: a stale position is not sent
        Assert.Null(Shortcuts.CycleStates(cfg));
    }

    [Fact]
    public void TwoTapsAtOnceRunTwoDifferentKeys()
    {
        var id = Id();
        // the first key is held until the second tap has run, so the second tap arrives while the first still runs
        var slow = new Shortcut { Id = id + "slow", Label = "slow", Toggle = true, Cmd = "builtin:macro", Steps = ["keys:hold:slow"] };
        Shortcut fast = Lamp(id + "fast"), key = Cycle(id, slow.Id, fast.Id);
        var cfg = new AgentConfig { Shortcuts = [slow, fast, key] };
        var first = Tap(cfg, id);
        Started("slow");
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Release("slow");
        Assert.Null(first.Result);
        Assert.True(On(slow));
        Assert.True(On(fast));   // the second tap ran the second key, not the slow one again
        Assert.Equal(slow.Id, Shortcuts.CycleStates(cfg)![id]); // the one that finished last is the one shown
    }

    [Fact]
    public void AKeyThatIsStillRunningOrFailedIsNotReportedAsTheOneThatRan()
    {
        var id = Id();
        var slow = new Shortcut { Id = id + "slow", Label = "slow", Cmd = "builtin:macro", Steps = ["keys:hold:slow", "keys:not_a_key"] };
        var key = Cycle(id, slow.Id, id + "other");
        var cfg = new AgentConfig { Shortcuts = [slow, Lamp(id + "other"), key] };
        var tap = Tap(cfg, id);
        Started("slow");
        Assert.Null(Shortcuts.CycleStates(cfg)); // claimed, but not run yet: the face does not change
        Release("slow");
        FailedOnTheBadKey(tap.Result);           // and it failed
        Assert.Null(Shortcuts.CycleStates(cfg)); // so it never counts
    }

    [Fact]
    public void WhenTwoOverlappingTapsBothFailTheNextTapRetriesTheEarlierKey()
    {
        var id = Id();
        Shortcut a = new() { Id = id + "a", Label = "a", Toggle = true, Cmd = "builtin:macro", Steps = ["keys:hold:a", "keys:not_a_key"] };
        Shortcut b = new() { Id = id + "b", Label = "b", Toggle = true, Cmd = "builtin:macro", Steps = ["keys:hold:b", "keys:not_a_key"] };
        var key = Cycle(id, a.Id, b.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, key] };
        var first = Tap(cfg, id);
        Started("a");                                          // the first tap claimed a
        var second = Tap(cfg, id);
        Started("b");                                          // the second claimed b while a still ran
        Release("a");
        FailedOnTheBadKey(first.Result);                       // a failed first
        Release("b");
        FailedOnTheBadKey(second.Result);
        a.Steps = ["wait:1"]; b.Steps = ["wait:1"];           // both work now
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.True(On(a));                                    // it was a that failed first: it is tried again
        Assert.False(On(b));
    }

    [Fact]
    public void AnOldFailureDoesNotUndoANewerClaimAfterAFullWrap()
    {
        var id = Id();
        Shortcut slowBad = new() { Id = id + "a", Label = "a", Cmd = "builtin:macro", Steps = ["keys:hold:a", "keys:not_a_key"] };
        Shortcut b = Lamp(id + "b"), c = Lamp(id + "c");
        var key = Cycle(id, slowBad.Id, b.Id, c.Id);
        var cfg = new AgentConfig { Shortcuts = [slowBad, b, c, key] };
        var first = Tap(cfg, id); // claims a, held
        Started("a");
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));                 // b
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));                 // c
        Release("a");
        FailedOnTheBadKey(first.Result);                                          // a failed: it is next, once
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance));              // a again (it fails the same way, but it is the one tried)
        Assert.Equal(c.Id, Shortcuts.CycleStates(cfg)![id]);
    }

    [Fact]
    public void EditingTheListStartsTheCycleOver()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var cfg = new AgentConfig { Shortcuts = [a, b, key] };
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Shortcuts.Run(cfg, id, NullLogger.Instance);
        Assert.Equal(b.Id, Shortcuts.CycleStates(cfg)![id]); // b ran last
        key.Cycle = [b.Id, a.Id];                           // the same keys, in the other order
        Assert.Null(Shortcuts.CycleStates(cfg));            // position 1 would now mean a: not sent
        var bBefore = On(b);
        Shortcuts.Run(cfg, id, NullLogger.Instance);        // starts from the first key of the new list
        Assert.Equal(b.Id, Shortcuts.CycleStates(cfg)![id]);
        Assert.Equal(!bBefore, On(b));
    }

    [Fact]
    public void KeysThatCycleIntoEachOtherStopInsteadOfRunningForever()
    {
        var id = Id();
        Shortcut a = Cycle(id + "a", id + "b", id + "b"), b = Cycle(id + "b", id + "a", id + "a");
        var cfg = new AgentConfig { Shortcuts = [a, b] };
        Assert.NotNull(Shortcuts.Run(cfg, a.Id, NullLogger.Instance));
    }

    [Fact]
    public void AMacroCanRunACycleKey()
    {
        var id = Id();
        Shortcut a = Lamp(id + "a"), b = Lamp(id + "b"), key = Cycle(id, a.Id, b.Id);
        var macro = new Shortcut { Id = id + "m", Label = "m", Cmd = "builtin:macro", Steps = [$"shortcut:{id}", $"shortcut:{id}"] };
        var cfg = new AgentConfig { Shortcuts = [a, b, key, macro] };
        Assert.Null(Shortcuts.Run(cfg, macro.Id, NullLogger.Instance));
        Assert.True(On(a));
        Assert.True(On(b));
    }

    [Fact]
    public void SavingKeepsOnlyTheKeysThatExist_AndOnlyForCycleKeys()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var json = """
            { "shortcuts": [
              { "id": "a", "label": "A", "cmd": "builtin:lock" },
              { "id": "b", "label": "B", "cmd": "builtin:lock" },
              { "id": "c", "label": "C", "cmd": "builtin:cycle", "cycle": [" a ", "gone", "c", "b", "a"] },
              { "id": "d", "label": "D", "cmd": "builtin:lock", "cycle": ["a", "b"] } ] }
            """;
        Assert.Null(store.Save(json));
        var keys = store.Get().Shortcuts.ToDictionary(s => s.Id);
        Assert.Equal(["a", "b", "a"], keys["c"].Cycle);
        Assert.Null(keys["d"].Cycle);
    }

    [Fact]
    public void ACycleLeftWithTooFewKeysStillSavesAndActsAsThatKey()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config2.json"));
        // "missing" does not exist, so the cycle is left with one key: saving must not fail (a rename elsewhere would block every save)
        Assert.Null(store.Save("""{ "shortcuts": [ { "id": "a", "label": "A", "cmd": "builtin:lock" }, { "id": "c", "label": "Cycle", "cmd": "builtin:cycle", "cycle": ["a", "missing"] } ] }"""));
        Assert.Equal(["a"], store.Get().Shortcuts.Single(s => s.Id == "c").Cycle);

        var id = Id();
        Shortcut one = Lamp(id + "one"), key = Cycle(id, one.Id), none = Cycle(id + "none");
        var cfg = new AgentConfig { Shortcuts = [one, key, none] };
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.False(On(one));                                   // two taps, the one key ran twice (on, then off)
        Assert.Null(Shortcuts.Run(cfg, none.Id, NullLogger.Instance)); // an empty list does nothing
    }

    [Fact]
    public void ACycleKeyHasNoOnOffSettingsOfItsOwn()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config3.json"));
        const string json = """
            { "shortcuts": [
              { "id": "a", "label": "A", "cmd": "builtin:lock" },
              { "id": "b", "label": "B", "cmd": "builtin:lock" },
              { "id": "c", "label": "Cycle", "cmd": "builtin:cycle", "cycle": ["a", "b"], "toggle": true, "stateKey": "k", "offCmd": "builtin:lock" } ] }
            """;
        Assert.Null(store.Save(json));
        var c = store.Get().Shortcuts.Single(s => s.Id == "c");
        Assert.False(c.Toggle);
        Assert.Null(c.StateKey);
        Assert.Null(c.OffCmd);
    }

    [Fact]
    public void ThePhoneLearnsOnlyThatAKeyCyclesNotWhatItCyclesThrough()
    {
        var cfg = new AgentConfig { Shortcuts = [Lamp("x"), Lamp("y"), Cycle("z", "x", "y")] };
        var text = JsonSerializer.Serialize(cfg.ToPublic());
        using var doc = JsonDocument.Parse(text);
        var keys = doc.RootElement.GetProperty("shortcuts").EnumerateArray().ToDictionary(k => k.GetProperty("Id").GetString()!);
        Assert.True(keys["z"].GetProperty("cycle").GetBoolean());
        Assert.False(keys["x"].GetProperty("cycle").GetBoolean());
        Assert.DoesNotContain("\"cycle\":[", text);
    }
}
