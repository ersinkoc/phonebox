using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class WallpaperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-wallpaper-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Fact]
    public void APictureIsSavedReadAndGivenARevision()
    {
        var store = new WallpaperStore(_dir);
        Assert.Null(store.Revision());
        Assert.Null(store.Save(Jpeg));
        var (bytes, type) = store.Read()!.Value;
        Assert.Equal(Jpeg, bytes);
        Assert.Equal("image/jpeg", type);
        var first = store.Revision();
        Assert.NotNull(first);
        Assert.Equal(first, new WallpaperStore(_dir).Revision()); // the same after a restart

        Assert.Null(store.Save(Png));
        Assert.Equal("image/png", store.Read()!.Value.ContentType);
        Assert.NotEqual(first, store.Revision());
        Assert.False(File.Exists(Path.Combine(_dir, "wallpaper.jpg"))); // the other format went
    }

    [Fact]
    public void RemovingLeavesNoWallpaper()
    {
        var store = new WallpaperStore(_dir);
        Assert.Null(store.Save(Png));
        store.Remove();
        Assert.Null(store.Read());
        Assert.Null(store.Revision());
    }

    [Fact]
    public void TheTypeComesFromTheBytesNotFromTheBrowser()
    {
        var store = new WallpaperStore(_dir);
        // a PNG whatever the request said (browsers may send no type or application/octet-stream)
        Assert.Null(store.Save(Png));
        Assert.Equal("image/png", store.Read()!.Value.ContentType);
        Assert.True(File.Exists(Path.Combine(_dir, "wallpaper.png")));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' })]                   // GIF
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)' ', (byte)'/', (byte)'>' })]         // SVG: nothing scriptable
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })]
    public void OtherFilesAreRefused(byte[] bytes)
    {
        var store = new WallpaperStore(_dir);
        Assert.Contains("Only JPEG, PNG or WEBP", store.Save(bytes));
        Assert.Null(store.Read());
    }

    [Fact]
    public void APictureChangedOrDeletedByHandIsNoticed()
    {
        var store = new WallpaperStore(_dir);
        Assert.Null(store.Save(Jpeg));
        var first = store.Revision();
        var file = Path.Combine(_dir, "wallpaper.jpg");
        File.WriteAllBytes(file, [.. Jpeg, 1, 2, 3]); // replaced by hand
        Assert.NotEqual(first, store.Revision());
        File.Delete(file);                             // deleted by hand
        Assert.Null(store.Revision());
        Assert.Null(store.Remove());                   // nothing left to remove is not an error
    }

    [Fact]
    public void EmptyAndTooLargeUploadsAreRefused()
    {
        var store = new WallpaperStore(_dir);
        Assert.Contains("empty", store.Save([]));
        Assert.Contains("larger than 4 MB", store.Save(null));
    }

    [Fact]
    public async Task ReadBoundedStopsPastTheLimit()
    {
        Assert.Null(await WallpaperStore.ReadBounded(new MemoryStream(new byte[WallpaperStore.MaxBytes + 1])));
        Assert.Equal(WallpaperStore.MaxBytes, (await WallpaperStore.ReadBounded(new MemoryStream(new byte[WallpaperStore.MaxBytes])))!.Length);
    }

    [Fact]
    public void ThePhoneGetsTheRevisionAndTheLookButNotTheUpload()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"display":{"wallpaper":{"dim":200,"blur":-3}}}""");
        var cfg = new ConfigStore(path).Get();
        Assert.Equal(90, cfg.Display.Wallpaper.Dim); // normalized
        Assert.Equal(0, cfg.Display.Wallpaper.Blur);

        // a revision past 2^53 as text: as a JavaScript number its last digits would be lost
        var json = JsonSerializer.Serialize(cfg.ToPublic(wallpaperRev: 9019088123725100162), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("9019088123725100162", doc.RootElement.GetProperty("wallpaperRev").GetString());
        Assert.Equal(90, doc.RootElement.GetProperty("display").GetProperty("wallpaper").GetProperty("dim").GetInt32());

        // the picture is served to the phone; uploading and removing it stay on this computer
        Assert.True(Lan.AllowedOverLan(new PathString("/api/wallpaper")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/settings/wallpaper")));
    }
}
