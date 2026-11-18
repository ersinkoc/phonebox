using PhoneBox.Agent;
using Xunit;

public sealed class MediaArtworkTests
{
    private static AlbumArt Image(byte value) => new([value], "image/png");
    [Fact]
    public async Task LateChangesAreRecheckedWithoutAnEventAndStableImagesAreThrottled()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now); var reads = 0;
        Task<AlbumArt?> Read(CancellationToken _) { reads++; return Task.FromResult<AlbumArt?>(Image((byte)reads)); }
        await cache.UpdateAsync("track", 0, Read);
        now = 500; await cache.UpdateAsync("track", 0, Read); Assert.Equal(1, reads);
        now = 9000; await cache.UpdateAsync("track", 0, Read); Assert.Equal(1, reads);
        now = 30000; await cache.UpdateAsync("track", 0, Read);
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldAsyncReadCannotRestoreArtworkAfterTrackChangeOrClear(bool clear)
    {
        var cache = new MediaArtworkCache();
        var pending = new TaskCompletionSource<AlbumArt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = cache.UpdateAsync("old", 0, _ => pending.Task);
        if (clear) cache.Clear();
        else await cache.UpdateAsync("new", 0, _ => Task.FromResult<AlbumArt?>(Image(2)));
        pending.SetResult(Image(1)); await oldRead;
        if (clear) Assert.Null(cache.Current);
        else Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
    }
    [Fact]
    public async Task MissingEmptyAndFailedThumbnailsAreRetriedAndCancellationCannotPublish()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now);
        await cache.UpdateAsync("track", 0, null);
        await cache.UpdateAsync("track", 0, _ => Task.FromResult<AlbumArt?>(new([], "image/png")));
        now = 1000;
        await Assert.ThrowsAsync<IOException>(() => cache.UpdateAsync("track", 0, _ => throw new IOException()));
        now = 2000; await cache.UpdateAsync("track", 0, _ => Task.FromResult<AlbumArt?>(Image(2)));
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.UpdateAsync("track", 1, _ => Task.FromResult<AlbumArt?>(Image(3)), cts.Token));
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
    }
    [Fact]
    public async Task DelayedChromiumThumbnailReplacesThePreviousVideosImage()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now);
        await cache.UpdateAsync("browser|first", 0, _ => Task.FromResult<AlbumArt?>(Image(1)));
        now = 1000;
        await cache.UpdateAsync("browser|second", 0, _ => Task.FromResult<AlbumArt?>(Image(1)));
        var staleId = cache.Id;
        now = 3000;
        await cache.UpdateAsync("browser|second", 0, _ => Task.FromResult<AlbumArt?>(Image(2)));
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
        Assert.True(cache.Id > staleId);
    }
    [Fact]
    public async Task MetadataEventRefreshesArtworkForAnUnchangedTitle()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now);
        await cache.UpdateAsync("browser|live", 0, _ => Task.FromResult<AlbumArt?>(Image(1)));
        now = 60_000;
        await cache.UpdateAsync("browser|live", 1, _ => Task.FromResult<AlbumArt?>(Image(2)));
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
    }
    [Fact]
    public async Task IdenticalArtworkDoesNotChangeItsCacheId()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now); var reads = 0;
        Task<AlbumArt?> Read(CancellationToken _) { reads++; return Task.FromResult<AlbumArt?>(Image(1)); }
        await cache.UpdateAsync("same", 0, Read); var id = cache.Id;
        now = 1000; await cache.UpdateAsync("same", 0, Read);
        Assert.Equal(id, cache.Id); Assert.NotNull(cache.Current);
    }

    [Fact]
    public async Task CancelledReadsNeitherWipePreviousTrackArtworkNorThrottleImmediateRetry()
    {
        long now = 0; var cache = new MediaArtworkCache(() => now);
        await cache.UpdateAsync("track-1", 0, _ => Task.FromResult<AlbumArt?>(Image(1)));

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cache.UpdateAsync("track-2", 0, _ => Task.FromResult<AlbumArt?>(Image(9)), preCancelled.Token));
        Assert.Equal(new byte[] { 1 }, cache.Current!.Bytes);

        now = 10_000;
        using var midCancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inflight = cache.UpdateAsync("track-1", 1, async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Image(2);
        }, midCancel.Token);
        await entered.Task;
        midCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inflight);

        now = 10_050;
        await cache.UpdateAsync("track-1", 1, _ => Task.FromResult<AlbumArt?>(Image(2)));
        Assert.Equal(new byte[] { 2 }, cache.Current!.Bytes);
    }
}
