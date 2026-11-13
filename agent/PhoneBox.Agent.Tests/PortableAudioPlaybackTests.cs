using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PortableAudioPlaybackTests
{
    [Fact]
    public void BuildStartInfoFormatsEachPlayerAndVolumeDeterministically()
    {
        var ffplay = PortableDeckNative.BuildStartInfo("ffplay", "/usr/bin/ffplay", "https://stream.example/live.mp3", 35);
        Assert.Equal(["-nodisp", "-autoexit", "-vn", "-sn", "-loglevel", "error", "-volume", "35", "-i", "https://stream.example/live.mp3"], ffplay.ArgumentList);

        var mpv = PortableDeckNative.BuildStartInfo("mpv", "/usr/bin/mpv", "/tmp/bell.wav", 120);
        Assert.Equal(["--no-config", "--no-video", "--really-quiet", "--volume=100", "--", "/tmp/bell.wav"], mpv.ArgumentList);

        var cvlc = PortableDeckNative.BuildStartInfo("cvlc", "/usr/bin/cvlc", "https://stream.example/live", 35);
        Assert.Equal(["--intf", "dummy", "--no-one-instance", "--play-and-exit", "--no-video", "--quiet", "--gain=0.35", "--", "https://stream.example/live"], cvlc.ArgumentList);

        var afplay = PortableDeckNative.BuildStartInfo("afplay", "/usr/bin/afplay", "/tmp/bell.wav", 50);
        Assert.Equal(["-v", "0.5", "/tmp/bell.wav"], afplay.ArgumentList);
    }

    [Fact]
    public void ActionStateReflectsResolvedPlayerAvailability()
    {
        var audio = new AudioControl();
        using var media = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        var action = new DeckAction { Id = "radio-1", Kind = "radio", Label = "Test FM", Target = "https://stream.example/live.mp3" };

        using var unavailable = new PortableDeckNative(audio, media, _ => null);
        Assert.Null(unavailable.ActionState(action));

        using var available = new PortableDeckNative(audio, media, _ => ("ffplay", "/usr/bin/ffplay"));
        Assert.Equal("Stopped", available.ActionState(action));
    }

    [Fact]
    public void PlayRejectsMissingFilesAndInvalidTargetsBeforeSpawning()
    {
        var audio = new AudioControl();
        using var media = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(audio, media, _ => ("ffplay", "/usr/bin/ffplay"));

        Assert.Throws<ArgumentException>(() => native.Play("s1", "  ", null, 50));
        Assert.Throws<FileNotFoundException>(() => native.Play("s1", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".wav"), null, 50));
    }

    [Fact]
    public void PlayThrowsNotSupportedWhenNoPlayerIsInstalled()
    {
        var audio = new AudioControl();
        using var media = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        using var native = new PortableDeckNative(audio, media, _ => null);

        Assert.Throws<NotSupportedException>(() => native.Play("r1", "https://stream.example/live.mp3", null, 60));
    }
}
