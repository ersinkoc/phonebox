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
        Assert.Null(store.Save("image/jpeg", Jpeg));
        var (bytes, type) = store.Read()!.Value;
        Assert.Equal(Jpeg, bytes);
        Assert.Equal("image/jpeg", type);
        var first = store.Revision();
        Assert.NotNull(first);
        Assert.Equal(first, new WallpaperStore(_dir).Revision()); // the same after a restart

        Assert.Null(store.Save("image/png", Png));
        Assert.Equal("image/png", store.Read()!.Value.ContentType);
        Assert.NotEqual(first, store.Revision());
        Assert.False(File.Exists(Path.Combine(_dir, "wallpaper.jpg"))); // the other format went
    }

    [Fact]
    public void RemovingLeavesNoWallpaper()
    {
        var store = new WallpaperStore(_dir);
        Assert.Null(store.Save("image/png", Png));
        store.Remove();
        Assert.Null(store.Read());
        Assert.Null(store.Revision());
    }

    [Theory]
    [InlineData("image/gif", "Only JPEG, PNG or WEBP")]
    [InlineData("image/svg+xml", "Only JPEG, PNG or WEBP")] // no scripts in a picture the phone loads
    [InlineData("image/png", "does not look like a valid PNG")] // JPEG bytes sent as a PNG
    public void OtherFilesAreRefused(string type, string error)
    {
        var store = new WallpaperStore(_dir);
        Assert.Contains(error, store.Save(type, Jpeg));
        Assert.Null(store.Read());
    }

    [Fact]
    public void EmptyAndTooLargeUploadsAreRefused()
    {
        var store = new WallpaperStore(_dir);
        Assert.Contains("empty", store.Save("image/jpeg", []));
        Assert.Contains("larger than", store.Save("image/jpeg", null));
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
