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
