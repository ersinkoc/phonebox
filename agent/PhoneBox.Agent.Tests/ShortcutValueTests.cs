using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A number sent with a run (#677): only keys that start a program or open an address and give a range take one; the
/// agent checks it, writes it itself and passes it as an argument of its own, at most 4 a second per key.
/// </summary>
public sealed class ShortcutValueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-run-value-").FullName;
    private readonly PushStore _push;
    private readonly AudioControl _audio = new(() => null);

    public ShortcutValueTests() => _push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));

    public void Dispose()
    {
        _push.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static Shortcut Seek(string? args = "--seek", double min = 0, double max = 100, double step = 1) =>
        new() { Id = "seek-" + Guid.NewGuid().ToString("N"), Label = "Seek", Cmd = "player", Args = args, Value = new() { Min = min, Max = max, Step = step } };

    private static string? Checked(Shortcut s, string raw) => ShortcutValues.Check(s, raw, out var v) is null ? v!.Text : null;

    private Shortcut Load(string shortcutJson)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""{ "shortcuts": [ {{shortcutJson}} ] }""");
        return Assert.Single(new ConfigStore(path).Get().Shortcuts);
    }

    /// <summary>Runs the key for the phone and records what it would start, instead of starting it.</summary>
    private (IResult Result, List<ProcessStartInfo> Started) Run(Shortcut sc, string? value, bool hold = false, ValueRateGate? gate = null)
    {
        var started = new List<ProcessStartInfo>();
        Shortcuts.TestStart.Value = started.Add;
        try { return (Program.RunShortcutForPhone(new AgentConfig { Shortcuts = [sc] }, sc.Id, NullLogger.Instance, _push, _audio, hold, value, gate ?? new ValueRateGate()), started); }
        finally { Shortcuts.TestStart.Value = null; }
    }

    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode ?? 200;
    private static string Error(IResult r) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(r).Value)).RootElement.GetProperty("error").GetString()!;

    // ---------------------------------------------------------------- settings

    [Fact]
    public void AProgramKeyKeepsItsRange()
    {
        var s = Load("""{ "id": "seek", "label": "Seek", "cmd": "mpv", "value": { "min": -10, "max": 10, "step": 0.5 } }""");
        Assert.Equal((-10, 10, 0.5), (s.Value!.Min, s.Value.Max, s.Value.Step));
    }

    [Theory]
    [InlineData("""{ "id": "k", "label": "K", "cmd": "builtin:keys", "args": "ctrl+c", "value": { "max": 5 } }""")]
    [InlineData("""{ "id": "k", "label": "K", "cmd": "builtin:macro", "steps": ["wait:1"], "value": { "max": 5 } }""")]
    [InlineData("""{ "id": "k", "label": "K", "cmd": "builtin:phone-mic", "value": { "max": 5 } }""")]
    [InlineData("""{ "id": "k", "label": "K", "cmd": "mpv", "toggle": true, "value": { "max": 5 } }""")]
    public void BuiltInAndOnOffKeysDropTheRange(string json) => Assert.Null(Load(json).Value);

    [Fact]
    public void ARangeIsPutInOrderAndBounded()
    {
        var s = Load("""{ "id": "k", "label": "K", "cmd": "mpv", "value": { "min": 50, "max": -1e300, "step": -3 } }""");
        Assert.Equal((-ShortcutValue.Limit, 50, 1), (s.Value!.Min, s.Value.Max, s.Value.Step));
        Assert.Equal(ShortcutValue.MinStep, Load("""{ "id": "k", "label": "K", "cmd": "mpv", "value": { "step": 1e-20 } }""").Value!.Step);
    }

    [Fact]
    public void BoundsKeepSixDecimalsAndAreTheNumbersTheCheckUses()
    {
        var s = Load("""{ "id": "k", "label": "K", "cmd": "mpv", "value": { "min": 0.1234567890123456, "max": 0.1234567890123456, "step": 0.0000015 } }""");
        Assert.Equal((0.123457, 0.123457, 0.000002), (s.Value!.Min, s.Value.Max, s.Value.Step));
        Assert.Equal("0.123457", Checked(s, "0.123457"));
        Assert.Null(Checked(s, "0.1234567890123456"));
    }

    [Fact]
    public void ABoundOnATieRoundsToTheEvenDigit() =>
        Assert.Equal(0.000002, Load("""{ "id": "k", "label": "K", "cmd": "mpv", "value": { "step": 0.0000025 } }""").Value!.Step);

    [Fact]
    public void ARangeWithoutItsFieldsUsesZeroToAHundredInWholeSteps()
    {
        var s = Load("""{ "id": "k", "label": "K", "cmd": "mpv", "value": {} }""");
        Assert.Equal((0, 100, 1), (s.Value!.Min, s.Value.Max, s.Value.Step));
    }

    // ---------------------------------------------------------------- range, step and formatting

    [Theory]
    [InlineData("0", "0")]
    [InlineData("100", "100")]
    [InlineData("37", "37")]
    [InlineData("007", "7")]
    [InlineData("37.000", "37")]
    [InlineData("-0", "0")]
    public void ANumberInTheRangeIsWrittenByTheAgent(string raw, string text) => Assert.Equal(text, Checked(Seek(), raw));

    [Theory]
    [InlineData("-1")]
    [InlineData("100.5")]
    [InlineData("101")]
    [InlineData("99999999999999999999")]
    public void ANumberOutsideTheRangeIsRefused(string raw) => Assert.Null(Checked(Seek(), raw));

    [Fact]
    public void StepsCountFromTheLowestValue()
    {
        var odd = Seek(min: 1, max: 9, step: 2);
        Assert.Equal("3", Checked(odd, "3"));
        Assert.Null(Checked(odd, "4"));
        var fine = Seek(min: -1, max: 1, step: 0.1);
        Assert.Equal("-0.3", Checked(fine, "-0.30"));
        Assert.Equal("0.7", Checked(fine, "0.7"));
        Assert.Null(Checked(fine, "0.75"));
        Assert.Null(Checked(Seek(), "37.5"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("+5")]
    [InlineData("--5")]
    [InlineData("5.")]
    [InlineData(".5")]
    [InlineData("1e2")]
    [InlineData("1,5")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("5 --verbose")]
    [InlineData("5\"")]
    [InlineData("5;calc")]
    [InlineData("٥")]
    [InlineData("100.0000000000000000000000000001")] // would round to 100 as a decimal
    [InlineData("1.0000000000001")]
    [InlineData("1234567890123456")]
    public void OnlyAPlainNumberIsTaken(string raw) =>
        Assert.Equal("The value must be a plain number, such as 37 or -2.5.", ShortcutValues.Check(Seek(), raw, out _));

    [Fact]
    public void TheNumberDoesNotFollowTheComputersLanguage()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // decimal comma
            Assert.Equal("2.5", Checked(Seek(step: 0.5), "2.5"));
            Assert.Null(Checked(Seek(step: 0.5), "2,5"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    // ---------------------------------------------------------------- where the number goes

    [Theory]
    [InlineData("--seek", "--seek 37")]
    [InlineData("--seek   ", "--seek 37")]
    [InlineData(null, "37")]
    [InlineData("seek {value} --fast", "seek 37 --fast")]
    [InlineData("{value} a {value}", "37 a 37")]
    public void TheNumberIsAddedOrTakesThePlaceOfAWholePlaceholder(string? args, string expected)
    {
        var sc = Seek(args);
        Assert.Null(ShortcutValues.Check(sc, "37", out var v));
        Assert.Equal(expected, Shortcuts.StartInfo(sc, v).Arguments);
    }

    [Theory]
    [InlineData("--seek={value}")]
    [InlineData("{value}%")]
    [InlineData("a{value} b")]
    [InlineData("\"{value}\"")]
    [InlineData("--title \"at {value} s\"")] // inside a quoted argument, though spaces surround it
    [InlineData("--seek\u00a0{value}")]       // a no-break space does not separate arguments
    [InlineData("--seek\n{value}")]
    public void APlaceholderInsideAWordIsRefused(string args) =>
        Assert.Contains("must be an argument of its own", ShortcutValues.Check(Seek(args), "37", out _));

    [Theory]
    [InlineData("--seek", "--seek")]
    [InlineData("seek {value} --fast", "seek  --fast")]
    [InlineData("{value}", "")]
    public void RunWithoutANumberTheKeyLeavesThePlaceholderOut(string args, string expected) =>
        Assert.Equal(expected, Shortcuts.StartInfo(Seek(args), null).Arguments);

    [Fact]
    public void AQuoteEscapedWithABackslashDoesNotOpenAQuotedArgument() =>
        Assert.Equal("--say \\\" 37", Shortcuts.StartInfo(Seek("--say \\\" {value}"), Check("37", Seek("--say \\\" {value}"))).Arguments);

    [Theory]
    [InlineData("--title \"hello")]          // the number would be appended inside the open quote
    [InlineData("--title \"hello {value}")]
    public void AnOpenQuoteIsRefused(string args)
    {
        Assert.NotNull(ShortcutValues.Check(Seek(args), "37", out _));
        Assert.Throws<InvalidOperationException>(() => ShortcutValues.Apply(args, new CheckedValue("37")));
    }

    [Fact]
    public void AClosedQuoteBeforeThePlaceholderIsFine() =>
        Assert.Equal("--title \"a b\" 37", Shortcuts.StartInfo(Seek("--title \"a b\" {value}"), Check("37", Seek("--title \"a b\" {value}"))).Arguments);

    private static CheckedValue Check(string raw, Shortcut s) { Assert.Null(ShortcutValues.Check(s, raw, out var v)); return v!; }

    [Fact]
    public void AKeyThatTakesNoValueKeepsItsArgumentsAsTheyAre()
    {
        var plain = new Shortcut { Id = "plain", Label = "Plain", Cmd = "echo", Args = "{value} stays" };
        Assert.Equal("{value} stays", Shortcuts.StartInfo(plain, null).Arguments);
    }

    // ---------------------------------------------------------------- the phone's request

    [Fact]
    public void AValueReachesTheProgramAsItsOwnArgument()
    {
        var sc = Seek("--seek {value} --quiet", step: 0.5);
        var (r, started) = Run(sc, "37.50");
        Assert.Equal(200, Status(r));
        Assert.Equal("--seek 37.5 --quiet", Assert.Single(started).Arguments);
        Assert.Equal("player", started[0].FileName);
        Assert.True(started[0].UseShellExecute);
    }

    [Fact]
    public void ARunWithoutAValueStillWorks()
    {
        var (r, started) = Run(Seek("--seek {value}"), null);
        Assert.Equal(200, Status(r));
        Assert.Equal("--seek", Assert.Single(started).Arguments);
    }

    [Fact]
    public void ARefusedValueSaysWhyAndStartsNothing()
    {
        var (r, started) = Run(Seek(), "250");
        Assert.Equal(400, Status(r));
        Assert.Equal("The value must be from 0 to 100.", Error(r));
        Assert.Empty(started);
    }

    [Fact]
    public void AKeyWithoutARangeRefusesAValue()
    {
        var sc = new Shortcut { Id = "open-" + Guid.NewGuid().ToString("N"), Label = "Open", Cmd = "player", Args = "--seek" };
        var (r, started) = Run(sc, "5");
        Assert.Equal(400, Status(r));
        Assert.StartsWith("This shortcut takes no value.", Error(r));
        Assert.Empty(started);
    }

    [Fact]
    public void ABuiltInKeyRefusesAValueEvenWithARange()
    {
        // a range a hand-written file could give before the settings are normalized; both refusals come before any typing
        var text = new Shortcut { Id = "text-" + Guid.NewGuid().ToString("N"), Label = "Type", Cmd = "builtin:text", Args = "hi", Value = new() };
        Assert.Equal(400, Status(Run(text, "5").Result));
        Assert.Equal("this shortcut takes no value", Shortcuts.Execute(text, NullLogger.Instance, number: new CheckedValue("5")));
    }

    [Fact]
    public void ALongPressTakesNoValue()
    {
        var sc = Seek();
        var (r, started) = Run(sc, "5", hold: true);
        Assert.Equal(400, Status(r));
        Assert.Empty(started);
    }

    [Fact]
    public void AnUnknownKeyWithAValueGetsTheUsualGenericError()
    {
        Assert.Equal(500, Status(Program.RunShortcutForPhone(new AgentConfig(), "nope", NullLogger.Instance, _push, _audio, value: "5")));
    }

    // ---------------------------------------------------------------- at most 4 a second

    [Fact]
    public void TheGateTakesFourValuesASecondPerKey()
    {
        long now = 0;
        var gate = new ValueRateGate(() => now);
        for (var i = 0; i < ValueRateGate.PerSecond; i++) { Assert.True(gate.TryTake("a")); now += 10; }
        Assert.False(gate.TryTake("a"));
        Assert.True(gate.TryTake("b")); // another key has its own four
        now = 999;
        Assert.False(gate.TryTake("a"));
        now = 1000; // the first one is a second old
        Assert.True(gate.TryTake("a"));
        Assert.False(gate.TryTake("a"));
    }

    [Fact]
    public void KeysThatWentQuietAreForgotten()
    {
        long now = 0;
        var gate = new ValueRateGate(() => now);
        // keys renamed or deleted one after another: their ids do not pile up
        for (var i = 0; i < 1000; i++) { Assert.True(gate.TryTake("old-" + i)); now += 100; Assert.True(gate.Tracked <= 33); }
        // a key still in use within its second keeps its count
        for (var i = 0; i < ValueRateGate.PerSecond; i++) gate.TryTake("busy");
        for (var i = 0; i < 40; i++) gate.TryTake("other-" + i);
        Assert.False(gate.TryTake("busy"));
    }

    [Fact]
    public void AValueOverTheLimitAnswers429AndStartsNothing()
    {
        long now = 0;
        var gate = new ValueRateGate(() => now);
        var sc = Seek();
        for (var i = 0; i < ValueRateGate.PerSecond; i++) Assert.Equal(200, Status(Run(sc, i.ToString(), gate: gate).Result));
        var (r, started) = Run(sc, "50", gate: gate);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(r));
        Assert.Empty(started);
        now = 1000;
        Assert.Single(Run(sc, "50", gate: gate).Started);
    }

    [Fact]
    public void ARefusedValueUsesNoSlot()
    {
        long now = 0;
        var gate = new ValueRateGate(() => now);
        var sc = Seek();
        for (var i = 0; i < 10; i++) Run(sc, "1e5", gate: gate);
        for (var i = 0; i < ValueRateGate.PerSecond; i++) Assert.Equal(200, Status(Run(sc, "1", gate: gate).Result));
    }

    [Fact]
    public void TapsWithoutAValueAreNotLimited()
    {
        long now = 0;
        var gate = new ValueRateGate(() => now);
        var sc = Seek();
        for (var i = 0; i < 10; i++) Assert.Equal(200, Status(Run(sc, null, gate: gate).Result));
        Assert.True(gate.TryTake(sc.Id));
    }
}
