using PhoneBox.Agent;
using Xunit;

/// <summary>A shortcut's icon size is one of a few presets; anything else in config.json falls back to the default.</summary>
public sealed class ShortcutIconSizeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-icon-size-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Shortcut Load(string? iconSizeJson)
    {
        var path = Path.Combine(_dir, "config.json");
        var field = iconSizeJson is null ? "" : $", \"iconSize\": {iconSizeJson}";
        File.WriteAllText(path, $$"""{ "shortcuts": [ { "id": "k", "label": "K", "cmd": "builtin:lock"{{field}} } ] }""");
        return Assert.Single(new ConfigStore(path).Get().Shortcuts);
    }

    [Theory]
    [InlineData("\"m\"", "m")]
    [InlineData("\"l\"", "l")]
    [InlineData("\"max\"", "max")]
    [InlineData("\" Max \"", "max")] // trimmed and case-insensitive
    public void AKnownPresetIsKept(string json, string expected) => Assert.Equal(expected, Load(json).IconSize);

    [Theory]
    [InlineData(null)]
    [InlineData("\"\"")]
    [InlineData("\"huge\"")]
    [InlineData("\"100%\"")]
    public void NoPresetOrAnUnknownOneMeansTheDefault(string? json) => Assert.Null(Load(json).IconSize);

    // a hand-edited value of the wrong type must not stop the rest of config.json from loading
    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{ \"x\": 1 }")]
    [InlineData("[\"max\"]")]
    public void AValueThatIsNotTextIsDroppedAndTheRestOfTheConfigStillLoads(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""
            { "shortcuts": [
                { "id": "a", "label": "A", "cmd": "builtin:lock", "iconSize": {{json}} },
                { "id": "b", "label": "B", "cmd": "builtin:lock", "iconSize": "l" } ] }
            """);
        var shortcuts = new ConfigStore(path).Get().Shortcuts;
        Assert.Equal(["a", "b"], shortcuts.Select(s => s.Id));
        Assert.Null(shortcuts[0].IconSize);
        Assert.Equal("l", shortcuts[1].IconSize);
    }
}
