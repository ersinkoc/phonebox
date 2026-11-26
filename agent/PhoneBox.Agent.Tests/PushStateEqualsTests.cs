using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

// A toggle's "State from a pushed value" as key=value: one value pushed to /api/push/layout lights one of several keys.
public sealed class PushStateEqualsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-push-eq-{Guid.NewGuid():N}.json");
    private readonly PushStore _push;

    public PushStateEqualsTests() => _push = new PushStore(NullLogger<PushStore>.Instance, _path);
    public void Dispose() { try { File.Delete(_path); } catch { } }

    private bool? On(string stateKey) => Shortcuts.State(new Shortcut { Id = "k", Toggle = true, StateKey = stateKey }, _push, null);

    [Theory]
    [InlineData("RU")]        // a plain-text body (curl -d RU), as ReadBody passes it on
    [InlineData("ru")]        // letter case aside
    [InlineData("\"RU\"")]    // a JSON string body
    [InlineData("\" RU \"")]  // JSON strings are kept as sent: surrounding spaces aside
    [InlineData("{\"value\":\"RU\",\"sub\":\"keyboard\"}")] // the webhook payload shape
    public void OnlyTheKeyForThePushedLayoutIsOn(string body)
    {
        var data = body.StartsWith('"') || body.StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(body) : PushStore.ParseValue(body);
        Assert.Null(_push.Set("layout", data, null));
        Assert.Equal(true, On("layout=RU"));
        Assert.Equal(false, On("layout=US"));
        Assert.Equal(false, On("layout=UA"));
        Assert.Equal(true, On(" layout = ru "));
    }

    [Fact]
    public void NumbersAndBooleansCompareByValue()
    {
        _push.Set("profile", PushStore.ParseValue("2"), null);
        Assert.Equal(true, On("profile=2"));
        Assert.Equal(true, On("profile=2.0"));
        Assert.Equal(false, On("profile=3"));
        Assert.Equal(false, On("profile=two"));
        Assert.Equal(false, On("profile="));
        // large ids are compared digit for digit, not rounded through double
        _push.Set("id", PushStore.ParseValue("9007199254740992"), null);
        Assert.Equal(true, On("id=9007199254740992"));
        Assert.Equal(false, On("id=9007199254740993"));
        _push.Set("flag", PushStore.ParseValue("false"), null);
        Assert.Equal(true, On("flag=false"));
        Assert.Equal(false, On("flag=true"));
    }

    [Fact]
    public void AMissingKeyOrAnObjectWithoutAValueIsOff()
    {
        Assert.Equal(false, On("layout=RU"));
        _push.Set("sensor", JsonSerializer.Deserialize<JsonElement>("""{"title":"Sensor"}"""), null);
        Assert.Equal(false, On("sensor=Sensor"));
    }

    [Fact]
    public void APlainKeyKeepsItsOnOffMeaning()
    {
        _push.Set("layout", PushStore.ParseValue("RU"), null);
        Assert.Equal(true, On("layout"));           // any text other than 0 / false / off / no is on, as before
        _push.Set("layout", PushStore.ParseValue("off"), null);
        Assert.Equal(false, On("layout"));
        Assert.Equal(true, On("layout=off"));       // while "=off" asks for exactly that value
    }

    [Fact]
    public void AMacroConditionTakesTheSameForm()
    {
        _push.Set("layout", PushStore.ParseValue("UA"), null);
        Assert.True(MacroFlow.Evaluate("push:layout=UA", null, _push, null));
        Assert.False(MacroFlow.Evaluate("push:layout=RU", null, _push, null));
        Assert.True(MacroFlow.Evaluate("!push:layout=RU", null, _push, null));
    }
}
