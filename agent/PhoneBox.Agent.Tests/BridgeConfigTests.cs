using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The phone bridge moves clipboard text off this computer, so every switch is off unless the owner turns it on, in the
/// code and in each default config file, and the phone can read the switch from the public config.
/// </summary>
public sealed class BridgeConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-cfg-" + Guid.NewGuid());

    [Fact]
    public void ClipboardIsOffByDefault() => Assert.False(new BridgeConfig().Clipboard);

    [Theory]
    [InlineData("config.default.json")]
    [InlineData("config.default.linux.json")]
    [InlineData("config.default.macos.json")]
    public void EveryDefaultConfigDeclaresTheBridgeOff(string name)
    {
        using var stream = typeof(AgentConfig).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    [Fact]
    public void PublicConfigCarriesTheBridgeFlags()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"bridge":{"clipboard":true}}""");
        var json = JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic(), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    [Fact]
    public void PublicConfigShowsTheBridgeOffForAFreshInstall()
    {
        var json = JsonSerializer.Serialize(new AgentConfig().ToPublic(), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicConfigSaysWhetherThisComputerHasTheBridge(bool exists)
    {
        // the phone shows its Phone bridge button only where there is a bridge to talk to (macOS), and reads it from here
        var json = JsonSerializer.Serialize(new AgentConfig().ToPublic(bridgeExists: exists), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(exists ? JsonValueKind.True : JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("supported").ValueKind);
    }

    [Fact]
    public void ThePublicConfigDefaultsToWhetherThisSystemHasTheBridge()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new AgentConfig().ToPublic(), ConfigStore.WriteOpts));
        Assert.Equal(OperatingSystem.IsMacOS(), doc.RootElement.GetProperty("bridge").GetProperty("supported").GetBoolean());
        Assert.Equal(OperatingSystem.IsMacOS(), BridgeConfig.PlatformHasBridge);
    }

    [Fact]
    public void ASupportedFlagInTheConfigFileCannotTurnTheBridgeOnWhereThereIsNone()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-supported.json");
        File.WriteAllText(path, """{"bridge":{"clipboard":true,"supported":true}}""");
        var json = JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic(bridgeExists: false), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("supported").ValueKind);
    }

    [Fact]
    public void ANullBridgeSectionBecomesTheDefaultsAndDoesNotBreakTheConfig()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-null.json");
        File.WriteAllText(path, """{"bridge":null}""");
        var config = new ConfigStore(path).Get();
        Assert.NotNull(config.Bridge);
        Assert.False(config.Bridge.Clipboard);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(config.ToPublic(), ConfigStore.WriteOpts));
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("bridge").GetProperty("clipboard").ValueKind);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
