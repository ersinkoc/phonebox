using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Spectrum bars: the band analysis, and that capture is only wanted for a widget on an enabled page.</summary>
public sealed class SpectrumAnalyzerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-spectrum-" + Guid.NewGuid().ToString("N"));

    public SpectrumAnalyzerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static float[] Sine(double hz, double amplitude, int rate = 48000) =>
        Enumerable.Range(0, SpectrumAnalyzer.FftSize).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    [Fact]
    public void SilenceIsAllZero() =>
        Assert.All(SpectrumAnalyzer.Analyze(new float[SpectrumAnalyzer.FftSize], 48000), b => Assert.Equal(0, b));

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ACorruptSampleDoesNotBlankTheRestOfTheWindow(float corruptSample)
    {
        var clean = Sine(1000, 0.5);
        clean[512] = 0;
        var expected = SpectrumAnalyzer.Analyze(clean, 48000);
        Assert.True(expected.Max() > 0);

        clean[512] = corruptSample;
        var actual = SpectrumAnalyzer.Analyze(clean, 48000);
        Assert.True(expected.SequenceEqual(actual),
            $"EXPECTED: intact spectrum, peak={expected.Max()} | ACTUAL: peak={actual.Max()}");
    }

    [Fact]
    public void BassLandsInTheBassBars()
    {
        // below ~200 Hz a band is narrower than an FFT bin (23 Hz at 48 kHz), so a low tone lights a few neighbours;
        // it must still stay in the lowest bars
        var bands = SpectrumAnalyzer.Analyze(Sine(60, 0.5), 48000);
        Assert.InRange(Array.IndexOf(bands, bands.Max()), 0, 8);
        Assert.All(bands.Skip(16), b => Assert.True(b < bands.Max() / 2));
    }

    [Theory]
    [InlineData(250, 48000)]
    [InlineData(1000, 48000)]
    [InlineData(8000, 44100)]
    public void AToneLightsItsOwnBand(double hz, int rate)
    {
        var bands = SpectrumAnalyzer.Analyze(Sine(hz, 0.5, rate), rate);
        Assert.Equal(SpectrumAnalyzer.Bands, bands.Length);
        Assert.All(bands, b => Assert.InRange(b, 0, 100));
        // the loudest band is the one whose log-spaced range holds the tone (40 Hz – 16 kHz over 48 bands)
        var expected = (int)Math.Floor(Math.Log(hz / 40) / Math.Log(16000.0 / 40) * SpectrumAnalyzer.Bands);
        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.InRange(loudest, expected - 1, expected + 1);
        Assert.True(bands[loudest] >= 60, $"band {loudest} = {bands[loudest]}");
        // far away from the tone, the window keeps the leakage low
        Assert.True(bands.Where((_, i) => Math.Abs(i - loudest) > 6).All(b => b < bands[loudest] / 2));
    }

    [Fact]
    public void LouderIsTaller()
    {
        var quiet = SpectrumAnalyzer.Analyze(Sine(1000, 0.02), 48000).Max();
        var loud = SpectrumAnalyzer.Analyze(Sine(1000, 0.5), 48000).Max();
        Assert.True(loud > quiet, $"{loud} <= {quiet}");
    }

    [Theory]
    [InlineData(true, -19.3f, 1f)]    // hardware volume: loopback is at full level already
    [InlineData(false, 0f, 1f)]       // software volume at 100 %
    [InlineData(false, -20f, 10f)]    // software volume at -20 dB: undo it
    [InlineData(false, -65f, 100f)]   // nearly silent slider: at most +40 dB
    [InlineData(false, 3f, 1f)]       // never turned down
    [InlineData(false, float.NaN, 1f)]
    public void LoopbackGainUndoesSoftwareVolume(bool hardware, float db, float gain) =>
        Assert.Equal(gain, SpectrumAnalyzer.LoopbackGain(hardware, db), 3);

    [Fact]
    public void SoftwareVolumeNoLongerFlattensTheBars()
    {
        // a monitor at 28 % (-19.3 dB) hands over music 19 dB quieter: with the gain the bars match full volume
        var full = SpectrumAnalyzer.Analyze(Sine(1000, 0.05), 48000).Max();
        var turnedDown = Sine(1000, 0.05 * Math.Pow(10, -19.3 / 20));
        var gain = SpectrumAnalyzer.LoopbackGain(false, -19.3f);
        var restored = SpectrumAnalyzer.Analyze(turnedDown.Select(s => s * gain).ToArray(), 48000).Max();
        Assert.True(SpectrumAnalyzer.Analyze(turnedDown, 48000).Max() < full - 20);
        Assert.InRange(restored, full - 1, full + 1);
    }

    private static byte[] FloatFrames(int frames, params float[] frame) =>
        Enumerable.Repeat(frame, frames).SelectMany(f => f).SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void AVolumeChangeOnlyScalesAudioCapturedAfterIt()
    {
        // a monitor at -40 dB (gain 100) for the first block, then turned up to 0 dB (gain 1) before the next one arrives:
        // the samples already in the ring keep the gain they were captured with
        var ring = new float[8];
        var pos = SpectrumAnalyzer.Downmix(FloatFrames(4, 0.001f, 0.001f), 2, 32, true, 8, SpectrumAnalyzer.LoopbackGain(false, -40), ring, 0);
        pos = SpectrumAnalyzer.Downmix(FloatFrames(4, 0.1f, 0.1f), 2, 32, true, 8, SpectrumAnalyzer.LoopbackGain(false, 0), ring, pos);
        Assert.Equal(0, pos);
        Assert.All(ring, s => Assert.Equal(0.1f, s, 4)); // the same level before and after the change
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)] // DualSense
    [InlineData(8)] // 7.1
    public void StereoOnASurroundOutputKeepsItsLevel(int channels)
    {
        // stereo content fills the front pair only; the other channels are silent
        var frame = new float[channels];
        frame[0] = frame[1] = 0.5f;
        var ring = new float[4];
        SpectrumAnalyzer.Downmix(FloatFrames(4, frame), channels, 32, true, channels * 4, 1, ring, 0);
        Assert.All(ring, s => Assert.Equal(0.5f, s, 4));
    }

    [Fact]
    public void PcmIsDownmixedToo()
    {
        var ring = new float[2];
        var pcm16 = new short[] { 16384, 16384, -16384, -16384 }.SelectMany(BitConverter.GetBytes).ToArray();
        SpectrumAnalyzer.Downmix(pcm16, 2, 16, false, 4, 1, ring, 0);
        Assert.Equal([0.5f, -0.5f], ring);
    }

    [Fact]
    public void OnlyFullWindowsAreAnalyzed() =>
        Assert.Throws<ArgumentException>(() => SpectrumAnalyzer.Analyze(new float[100], 48000));

    private SpectrumAnalyzer Analyzer(bool widget, bool pageEnabled = true)
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"s","type":"spectrum","x":0,"y":0,"w":6,"h":3}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":{{widgets}} },
                      {"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4}]}]}
            """);
        return new SpectrumAnalyzer(new ConfigStore(path), NullLogger<SpectrumAnalyzer>.Instance);
    }

    [Fact]
    public void CaptureIsRefusedWithoutTheWidgetOnAnEnabledPage()
    {
        using var none = Analyzer(widget: false);
        Assert.False(none.Want());
        Assert.False(none.Active);
        Assert.Null(none.Frame());

        using var hidden = Analyzer(widget: true, pageEnabled: false);
        Assert.False(hidden.Want());
        Assert.False(hidden.Active);
    }

    [Fact]
    public void TheWidgetOnAnEnabledPageIsShown()
    {
        using var shown = Analyzer(widget: true);
        Assert.True(shown.Shown());
    }

    [Fact]
    public void ThePhoneMayAskForBandsOverWifi()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/spectrum")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/spectrum/raw")));
    }
}
