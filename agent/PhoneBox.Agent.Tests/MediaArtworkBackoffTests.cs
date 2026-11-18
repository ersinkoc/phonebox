using PhoneBox.Agent;
using Xunit;

/// <summary>A thumbnail that keeps failing (too large, empty, unreadable) is not reopened on every poll.</summary>
public sealed class MediaArtworkBackoffTests
{
    [Fact]
    public async Task RepeatedFailuresBackOffAndATrackChangeResetsIt()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now); var reads = 0;
        Task<AlbumArt?> Fail(CancellationToken _) { reads++; return Task.FromResult<AlbumArt?>(null); }

        await cache.UpdateAsync("track", 0, Fail);
        now = 100; await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(2, reads); // one immediate retry
        now = 500; await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(2, reads);
        now = 1100; await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(3, reads);
        now = 2000; await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(3, reads); // the pause grows
        now = 3100;
        await Assert.ThrowsAsync<IOException>(() => cache.UpdateAsync("track", 0, _ => { reads++; throw new IOException(); }));
        Assert.Equal(4, reads);
        now = 3200; await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(4, reads); // an exception counts as a failure too

        await cache.UpdateAsync("next", 0, Fail);
        Assert.Equal(5, reads);
        await cache.UpdateAsync("next", 0, _ => Task.FromResult<AlbumArt?>(new([1], "image/png")));
        Assert.Equal(new byte[] { 1 }, cache.Current!.Bytes);
    }

    [Fact]
    public async Task CancellationIsNotCountedAsAFailure()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now); var reads = 0;
        Task<AlbumArt?> Fail(CancellationToken _) { reads++; return Task.FromResult<AlbumArt?>(null); }

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.UpdateAsync("track", 0, ct =>
        {
            reads++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AlbumArt?>(null);
        }, cts.Token));
        await cache.UpdateAsync("track", 0, Fail);
        await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(3, reads);
        await cache.UpdateAsync("track", 0, Fail);
        Assert.Equal(3, reads);
    }
}
