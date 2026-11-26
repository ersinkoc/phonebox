using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class MediaTimelineTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.5, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]
    public void NormalizePlaybackRateDefaultsInvalidValuesToOne(double? input, double expected) =>
        Assert.Equal(expected, MediaTimeline.NormalizePlaybackRate(input));

    [Theory]
    [InlineData(10_000, 5_000, true, 1.0, 120_000, 15_000)]
    [InlineData(10_000, 5_000, true, 1.5, 120_000, 17_500)]
    [InlineData(10_000, 5_000, true, 2.0, 120_000, 20_000)]
    [InlineData(10_000, 5_000, true, 0.5, 120_000, 12_500)]
    [InlineData(10_000, 5_000, false, 2.0, 120_000, 10_000)]
    [InlineData(10_000, 100_000, true, 2.0, 120_000, 120_000)]
    [InlineData(10_000, 100_000, true, 2.0, 0, 210_000)]
    [InlineData(10_000, -5_000, true, 2.0, 120_000, 10_000)]
    [InlineData(-500, 0, false, 1.0, 120_000, 0)]
    [InlineData(10_000, 5_000, true, null, 120_000, 15_000)]
    [InlineData(10_000, 5_000, true, 0.0, 120_000, 15_000)]
    public void AdvancePositionMsScalesByPlaybackRateAndClamps(
        long positionMs, double elapsedMs, bool playing, double? playbackRate, long durationMs, long expected) =>
        Assert.Equal(expected, MediaTimeline.AdvancePositionMs(positionMs, elapsedMs, playing, playbackRate, durationMs));

    [Fact]
    public void AdvancePositionMsHandlesNonFiniteAndOverflowElapsedSafely()
    {
        Assert.Equal(10_000, MediaTimeline.AdvancePositionMs(10_000, double.NaN, true, 2.0, 120_000));
        Assert.Equal(10_000, MediaTimeline.AdvancePositionMs(10_000, double.PositiveInfinity, true, 2.0, 120_000));
        Assert.Equal(120_000, MediaTimeline.AdvancePositionMs(10_000, double.MaxValue, true, 2.0, 120_000));
        Assert.Equal(long.MaxValue, MediaTimeline.AdvancePositionMs(10_000, double.MaxValue, true, 2.0, 0));
    }

    [Fact]
    public void MediaSnapshotJsonDefaultsAndPreservesPlaybackRate()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var defaultSnap = new MediaSnapshot("YouTube", "Video", "Channel", "", true, 30_000, 600_000, 0, true);
        Assert.Equal(1.0, defaultSnap.PlaybackRate);
        var defaultJson = JsonSerializer.Serialize(defaultSnap, opts);
        Assert.Contains("\"playbackRate\":1", defaultJson);

        var fastSnap = defaultSnap with { PlaybackRate = 1.75 };
        var fastJson = JsonSerializer.Serialize(fastSnap, opts);
        Assert.Contains("\"playbackRate\":1.75", fastJson);
        Assert.Equal(1.75, JsonSerializer.Deserialize<MediaSnapshot>(fastJson, opts)!.PlaybackRate);

        const string legacyJson = """{"app":"YouTube","title":"Video","artist":"Channel","album":"","playing":true,"posMs":30000,"durMs":600000,"artId":0,"canSeek":true}""";
        var parsedLegacy = JsonSerializer.Deserialize<MediaSnapshot>(legacyJson, opts)!;
        Assert.Equal(1.0, parsedLegacy.PlaybackRate);
    }
}
