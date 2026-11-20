using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>if: … else … endif in a macro. The keyboard and the process list are replaced by recorders.</summary>
[CollectionDefinition("Macro recorders", DisableParallelization = true)]
public sealed class MacroRecorderCollection;

[Collection("Macro recorders")]
public sealed class MacroConditionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-macro-if-").FullName;
    private readonly List<string> _events = [];
    private readonly List<string> _asked = [];
    private readonly HashSet<string> _running = [];
    private readonly PushStore _push;

    public MacroConditionTests()
    {
        _push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        Shortcuts.TestKeys = k => _events.Add(k);
        Shortcuts.TestType = t => _events.Add("type:" + t);
        MacroFlow.TestRunning = p => { _asked.Add(p); return _running.Contains(p); };
    }

    public void Dispose()
    {
        Shortcuts.TestKeys = null; Shortcuts.TestType = null; MacroFlow.TestRunning = null;
        _push.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string? Macro(params string[] steps) => Macro(new AgentConfig(), steps);
    private string? Macro(AgentConfig cfg, params string[] steps) =>
        Shortcuts.Execute(new Shortcut { Id = "m", Label = "m", Cmd = "builtin:macro", Steps = [.. steps] }, NullLogger.Instance, cfg, push: _push, audio: new AudioControl(() => null));

    [Fact]
    public void WhenTheConditionHoldsTheThenStepsRunAndTheElseStepsDoNot()
    {
        _running.Add("spotify");
        Assert.Null(Macro("if:running:spotify", "keys:a", "else", "keys:b", "endif", "keys:c"));
        Assert.Equal(["a", "c"], _events);
    }

    [Fact]
    public void WhenItDoesNotHoldTheElseStepsRun()
    {
        Assert.Null(Macro("if:running:spotify", "keys:a", "else", "keys:b", "endif", "keys:c"));
        Assert.Equal(["b", "c"], _events);
    }

    [Fact]
    public void AnIfWithoutAnElseSkipsItsStepsOrRunsThem()
    {
        Assert.Null(Macro("if:running:spotify", "keys:a", "endif", "keys:z"));
        Assert.Equal(["z"], _events);
        _events.Clear(); _running.Add("spotify");
        Assert.Null(Macro("if:running:spotify", "keys:a", "endif", "keys:z"));
        Assert.Equal(["a", "z"], _events);
    }

    [Fact]
    public void ABangTurnsTheConditionAround_AndTheColonAfterElseAndEndifIsOptional()
    {
        Assert.Null(Macro("if:!running:obs64", "keys:a", "else:", "keys:b", "endif:"));
        Assert.Equal(["a"], _events);
    }

    [Fact]
    public void BlocksNest_AndASkippedBlockDoesNotEvenAskItsInnerConditions()
    {
        _running.Add("inner");
        Assert.Null(Macro("if:running:outer", "if:running:inner", "keys:1", "else", "keys:2", "endif", "else", "keys:3", "endif"));
        Assert.Equal(["3"], _events);
        Assert.Equal(["outer"], _asked);

        _events.Clear(); _asked.Clear(); _running.Add("outer");
        Assert.Null(Macro("if:running:outer", "if:running:inner", "keys:1", "else", "keys:2", "endif", "else", "keys:3", "endif"));
        Assert.Equal(["1"], _events);
    }

    [Fact]
    public void ATypoAnywhereStopsTheMacroBeforeAnyStepRuns()
    {
        foreach (var bad in new[]
        {
            new[] { "keys:a", "if:muted", "keys:b" },                  // no endif
            new[] { "keys:a", "endif" },                                // endif without an if
            new[] { "keys:a", "else", "keys:b" },                      // else without an if
            new[] { "if:muted", "else", "else", "endif", "keys:a" },   // two elses
            new[] { "keys:a", "if:sunny", "endif" },                    // unknown condition
            new[] { "keys:a", "if:running:", "endif" },                 // missing value
            new[] { "keys:a", "if:muted:please", "endif" },             // muted takes no value
            new[] { "keys:a", "if:running:.exe", "endif" },             // no program name once .exe is taken off
            new[] { "keys:a", "if:muted", "if:on:nope", "endif", "endif" }, // a missing key, even in a block that would be skipped
        })
        {
            _events.Clear();
            Assert.NotNull(Macro(new AgentConfig { Shortcuts = [new Shortcut { Id = "known", Label = "k", Cmd = "builtin:lock" }] }, bad));
            Assert.Empty(_events);
        }
    }

    [Fact]
    public void ABareEndIsStillTheEndKey()
    {
        Assert.Null(Macro("end", "keys:a"));
        Assert.Equal(["end", "a"], _events);
    }

    [Fact]
    public void MutedOnAndPushConditionsReadTheirState()
    {
        var lampId = "macro-if-lamp-" + Guid.NewGuid().ToString("N");
        var lamp = new Shortcut { Id = lampId, Label = "Lamp", Toggle = true, Cmd = "builtin:keys", Args = "f13" };
        var cfg = new AgentConfig { Shortcuts = [lamp] };

        // not muted (no audio device in a test), lamp off, nothing pushed
        Assert.Null(Macro(cfg, "if:muted", "keys:muted", "endif", $"if:!on:{lampId}", "keys:lamp-off", "endif", "if:!push:mic", "keys:mic-off", "endif"));
        Assert.Equal(["lamp-off", "mic-off"], _events);

        // switch the lamp on (the agent remembers a tap) and push a value
        _events.Clear();
        Assert.Null(Shortcuts.Run(cfg, lampId, NullLogger.Instance, _push));
        _events.Clear();
        _push.Set("mic", JsonSerializer.SerializeToElement(true), null);
        Assert.Null(Macro(cfg, $"if:on:{lampId}", "keys:lamp-on", "endif", "if:push:mic", "keys:mic-on", "endif"));
        Assert.Equal(["lamp-on", "mic-on"], _events);
    }

    [Fact]
    public void AnUnknownShortcutInAnOnConditionStopsTheMacroBeforeItsFirstStep()
    {
        var cfg = new AgentConfig { Shortcuts = [new Shortcut { Id = "known", Label = "k", Cmd = "builtin:lock", Toggle = true }] };
        Assert.NotNull(Macro(cfg, "keys:a", "if:on:nope", "keys:b", "endif"));
        Assert.Empty(_events);
        Assert.Null(Macro(cfg, "keys:a", "if:on:known", "keys:b", "endif")); // an existing key is fine, whatever its state
        Assert.Equal(["a"], _events);
    }
}
