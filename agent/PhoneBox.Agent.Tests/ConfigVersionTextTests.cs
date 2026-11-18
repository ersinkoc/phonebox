using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The settings page keeps the version a reply gives it and sends it back in X-Config-Version. A JSON number this size
/// reaches JavaScript rounded, so the next save after uploading or removing a shortcut icon was refused as a conflict.
/// </summary>
public sealed class ConfigVersionTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-version-" + Guid.NewGuid().ToString("N"));
    public ConfigVersionTextTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TheVersionGoesOutAsTextThatComesBackExactly()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        var store = new ConfigStore(path);

        // why it can't be a number: file-time ticks are past the largest integer a JavaScript number holds exactly
        Assert.True(store.Version > (1L << 53));

        using var reply = JsonDocument.Parse(JsonSerializer.Serialize(new { version = store.VersionText }));
        var version = reply.RootElement.GetProperty("version");
        Assert.Equal(JsonValueKind.String, version.ValueKind);

        // what the page sends back after an icon upload must pass the conflict check
        Assert.False(store.VersionConflict(version.GetString()));
        Assert.Null(store.Save(JsonSerializer.Serialize(store.Get(), ConfigStore.WriteOpts), long.Parse(version.GetString()!)));
    }

    [Fact]
    public void ARoundedVersionIsRefused()
    {
        // the old behaviour, for the record: what JSON.parse makes of the number is not the version any more
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, new DateTime(639_000_000_000_000_123, DateTimeKind.Utc));
        var store = new ConfigStore(path);
        var rounded = ((long)(double)store.Version).ToString();
        Assert.NotEqual(store.VersionText, rounded);
        Assert.True(store.VersionConflict(rounded));
    }
}
