using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Shortcut errors on the phone, and toggles held by more than one "while active" rule.</summary>
public sealed class ShortcutRuleFollowUpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-shortcut-rule-").FullName;
    private readonly PushStore _push;

    public ShortcutRuleFollowUpTests() => _push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));

    public void Dispose()
    {
        _push.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Capture<T> : ILogger<T>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        { lock (Lines) Lines.Add(format(state, ex)); }
    }

    // A failed shortcut's exception text can carry configured command text (here a key sequence); the phone gets none of it.
    [Fact]
    public void ThePhoneDoesNotGetTheConfiguredTextOfAFailedShortcut()
    {
        var lampId = "phone-error-lamp-" + Guid.NewGuid().ToString("N");
        var lamp = new Shortcut { Id = lampId, Label = "Lamp", Toggle = true, Cmd = "builtin:macro", OffCmd = "builtin:keys", OffArgs = "secret_off_key" };
        var macro = new Shortcut { Id = "scene-" + lampId, Label = "Scene", Cmd = "builtin:macro", Steps = [$"shortcut:{lampId}"] };
        var cfg = new AgentConfig { Shortcuts = [lamp, macro] };
        var audio = new AudioControl(() => null);

        Assert.IsType<Ok>(Program.RunShortcutForPhone(cfg, macro.Id, NullLogger.Instance, _push, audio)); // switches the lamp on
        var failed = Assert.IsType<ProblemHttpResult>(Program.RunShortcutForPhone(cfg, macro.Id, NullLogger.Instance, _push, audio));
        Assert.DoesNotContain("secret_off_key", failed.ProblemDetails.Detail ?? "");
        Assert.DoesNotContain("secret_off_key", failed.ProblemDetails.Title ?? "");
    }

    [Fact]
    public void ThePhoneStillGetsTheAgentsOwnAccessibilityHint()
    {
        // a shortcut whose error is exactly the agent's fixed hint: the phone sees the hint, not the generic text
        var hinted = Program.HintOrGeneric(KeySender.AccessibilityHint);
        var generic = Program.HintOrGeneric("keys failed: secret_off_key");
        Assert.Equal(KeySender.AccessibilityHint, hinted);
        Assert.DoesNotContain("secret_off_key", generic);
    }

    private RuleEngine Engine(ConfigStore cfg, ILogger<RuleEngine>? log = null) => new(cfg, _push,
        new SessionMonitor(NullLogger<SessionMonitor>.Instance),
        new MediaMonitor(NullLogger<MediaMonitor>.Instance),
        new SystemMonitor(NullLogger<SystemMonitor>.Instance),
        new PrivacyMonitor(NullLogger<PrivacyMonitor>.Instance),
        new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance),
        new ObsMonitor(cfg, NullLogger<ObsMonitor>.Instance),
        new QuotaMonitor(NullLogger<QuotaMonitor>.Instance),
        new GitHubMonitor(NullLogger<GitHubMonitor>.Instance),
        new BatteryMonitor(NullLogger<BatteryMonitor>.Instance),
        new PingMonitor(), new AudioControl(() => null),
        log ?? NullLogger<RuleEngine>.Instance);

    private static string Settings(string lampId, string lampJson, params (string Id, bool Enabled)[] rules) => $$"""
        {
          "shortcuts": [{{lampJson}}],
          "rules": [{{string.Join(",", rules.Select(r =>
              $$"""{ "id": "{{r.Id}}", "name": "{{r.Id}}", "enabled": {{(r.Enabled ? "true" : "false")}}, "when": "push", "match": "mic", "then": "shortcut", "shortcut": "{{lampId}}", "whileActive": true }"""))}}]
        }
        """;

    // Two "while active" rules hold the same toggle on; the first one to end must leave it on for the other.
    [Fact]
    public void AToggleHeldByTwoRulesStaysOnUntilTheLastOneEnds()
    {
        var cfg = new ConfigStore(Path.Combine(_dir, "config.json"));
        var lampId = "held-lamp-" + Guid.NewGuid().ToString("N"); // remembered toggle state is static, keyed by id
        var lampJson = $$"""{ "id": "{{lampId}}", "label": "Lamp", "cmd": "builtin:macro", "toggle": true }""";
        Assert.Null(cfg.Save(Settings(lampId, lampJson, ("a", true), ("b", true))));
        var engine = Engine(cfg);
        var lamp = cfg.Get().Shortcuts.Single();
        Assert.Equal(false, Shortcuts.State(lamp, _push, null));

        _push.Set("mic", JsonSerializer.SerializeToElement(true), null);
        engine.Evaluate();
        Assert.Equal(true, Shortcuts.State(lamp, _push, null));

        Assert.Null(cfg.Save(Settings(lampId, lampJson, ("a", true), ("b", false)))); // b ends, a still holds
        engine.Evaluate();
        Assert.Equal(true, Shortcuts.State(lamp, _push, null));

        Assert.Null(cfg.Save(Settings(lampId, lampJson, ("a", false), ("b", false)))); // the last holder ends
        engine.Evaluate();
        Assert.Equal(false, Shortcuts.State(lamp, _push, null));
    }

    // Switching a held toggle back off can fail; that must show up in the log instead of being dropped.
    [Fact]
    public void AFailedReleaseIsLogged()
    {
        var cfg = new ConfigStore(Path.Combine(_dir, "config.json"));
        var lampId = "release-lamp-" + Guid.NewGuid().ToString("N");
        var lampJson = $$"""{ "id": "{{lampId}}", "label": "Lamp", "cmd": "builtin:macro", "toggle": true, "offCmd": "builtin:keys", "offArgs": "not_a_key" }""";
        Assert.Null(cfg.Save(Settings(lampId, lampJson, ("a", true))));
        var log = new Capture<RuleEngine>();
        var engine = Engine(cfg, log);
        var lamp = cfg.Get().Shortcuts.Single();

        _push.Set("mic", JsonSerializer.SerializeToElement(true), null);
        engine.Evaluate();
        Assert.Equal(true, Shortcuts.State(lamp, _push, null));

        Assert.Null(cfg.Save(Settings(lampId, lampJson, ("a", false))));
        engine.Evaluate();
        lock (log.Lines) Assert.Contains(log.Lines, l => l.Contains("rule a") && l.Contains("back off"));
    }
}
