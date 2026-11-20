using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A key's long press: the phone says "held", and the agent runs the key's own long-press shortcut.</summary>
public sealed class ShortcutLongPressTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-long-press-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // a toggle remembers its state in the agent, which is how a test sees which key ran (ids are unique: the memory is static)
    private static Shortcut Key(string id, string? longPress = null) =>
        new() { Id = id, Label = id, Toggle = true, Cmd = "builtin:macro", Steps = ["wait:1"], LongPress = longPress };

    private static bool On(Shortcut s) => Shortcuts.State(s, null, null) == true;

    [Fact]
    public void ATapRunsTheKeyItself()
    {
        var id = "tap-" + Guid.NewGuid().ToString("N");
        var tap = Key(id, id + "-held"); var held = Key(id + "-held");
        var cfg = new AgentConfig { Shortcuts = [tap, held] };
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance));
        Assert.True(On(tap));
        Assert.False(On(held));
    }

    [Fact]
    public void AHoldRunsTheLongPressShortcutAndNotTheKey()
    {
        var id = "hold-" + Guid.NewGuid().ToString("N");
        var tap = Key(id, id + "-held"); var held = Key(id + "-held");
        var cfg = new AgentConfig { Shortcuts = [tap, held] };
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance, hold: true));
        Assert.True(On(held));
        Assert.False(On(tap));
    }

    [Fact]
    public void AHoldOnAKeyWithoutALongPressIsAnError()
    {
        var id = "plain-" + Guid.NewGuid().ToString("N");
        var plain = Key(id);
        var cfg = new AgentConfig { Shortcuts = [plain] };
        Assert.NotNull(Shortcuts.Run(cfg, id, NullLogger.Instance, hold: true));
        Assert.False(On(plain));
    }

    [Fact]
    public void ALongPressIsOneHopAndItsTargetsOwnLongPressIsNotFollowed()
    {
        var id = "chain-" + Guid.NewGuid().ToString("N");
        var a = Key(id, id + "-b"); var b = Key(id + "-b", id + "-c"); var c = Key(id + "-c");
        var cfg = new AgentConfig { Shortcuts = [a, b, c] };
        Assert.Null(Shortcuts.Run(cfg, id, NullLogger.Instance, hold: true));
        Assert.True(On(b));
        Assert.False(On(c));
    }

    [Fact]
    public void SavingDropsALongPressThatPointsAtItselfOrAtNothing()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var json = """
            { "shortcuts": [
              { "id": "a", "label": "A", "cmd": "builtin:lock", "longPress": "b" },
              { "id": "b", "label": "B", "cmd": "builtin:lock", "longPress": "b" },
              { "id": "c", "label": "C", "cmd": "builtin:lock", "longPress": "gone" },
              { "id": "d", "label": "D", "cmd": "builtin:lock", "longPress": "  " } ] }
            """;
        Assert.Null(store.Save(json));
        var keys = store.Get().Shortcuts.ToDictionary(s => s.Id);
        Assert.Equal("b", keys["a"].LongPress);
        Assert.Null(keys["b"].LongPress);
        Assert.Null(keys["c"].LongPress);
        Assert.Null(keys["d"].LongPress);
    }

    [Fact]
    public void ThePhoneLearnsOnlyThatAKeyCanBeHeld()
    {
        var cfg = new AgentConfig { Shortcuts = [Key("a", "secret-target-id"), Key("b")] };
        var text = JsonSerializer.Serialize(cfg.ToPublic());
        using var doc = JsonDocument.Parse(text);
        var keys = doc.RootElement.GetProperty("shortcuts").EnumerateArray().ToDictionary(k => k.GetProperty("Id").GetString()!);
        Assert.True(keys["a"].GetProperty("hold").GetBoolean());
        Assert.False(keys["b"].GetProperty("hold").GetBoolean());
        Assert.DoesNotContain("secret-target-id", text);
    }
}
