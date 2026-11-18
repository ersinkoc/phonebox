using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Photo frame's folder rules: bounded scan, no links or cloud-only files, id-only serving, gating.</summary>
public sealed class PhotoFrameTests : IDisposable
{
    // a 16 x 16 baseline JPEG header (SOI + SOF0) and a PNG signature + IHDR of the given size
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];
    private static byte[] PngOf(int w, int h) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
         (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0];
    private static readonly byte[] Png = PngOf(16, 16);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-photos-" + Guid.NewGuid().ToString("N"));
    private string Photos => Path.Combine(_dir, "photos");

    public PhotoFrameTests() => Directory.CreateDirectory(Photos);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private PhotoFrame Frame(bool widget = true, bool pageEnabled = true, bool subfolders = true)
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"photos":{"folder":{{JsonSerializer.Serialize(Photos)}},"subfolders":{{(subfolders ? "true" : "false")}}},
             "pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":{{widgets}} },
                      {"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4}]}]}
            """);
        return new PhotoFrame(new ConfigStore(path), NullLogger<PhotoFrame>.Instance) { CacheDir = Path.Combine(_dir, "cache") };
    }

    [Fact]
    public void BroadOrMissingFoldersAreRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.GetPathRoot(home)!));
        Assert.NotNull(PhotoFrame.RefuseRoot(home));
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.GetDirectoryName(home)!));
        Assert.NotNull(PhotoFrame.RefuseRoot("relative/folder"));
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.Combine(_dir, "missing")));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(PhotoFrame.RefuseRoot(@"\\server\share\photos"));
            Assert.NotNull(PhotoFrame.RefuseRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        else Assert.NotNull(PhotoFrame.RefuseRoot("/etc"));
        Assert.Null(PhotoFrame.RefuseRoot(Photos));
    }

    [Fact]
    public void TheScanKeepsImagesOnlyAndFollowsTheSubfolderSetting()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        File.WriteAllBytes(Path.Combine(Photos, "b.PNG"), Png);
        File.WriteAllText(Path.Combine(Photos, "notes.txt"), "not a photo");
        File.WriteAllBytes(Path.Combine(Photos, "empty.jpg"), []);
        Directory.CreateDirectory(Path.Combine(Photos, "trip"));
        File.WriteAllBytes(Path.Combine(Photos, "trip", "c.jpeg"), Jpeg);

        var all = PhotoFrame.Scan(Photos, subfolders: true);
        Assert.Equal(["a.jpg", "b.PNG", "c.jpeg"], all.Photos.Select(p => Path.GetFileName(p.Path)).Order().ToArray());
        Assert.Null(all.Error);
        var top = PhotoFrame.Scan(Photos, subfolders: false);
        Assert.Equal(2, top.Photos.Count);
        Assert.Equal(PhotoFrame.IdOf(Photos, "trip/c.jpeg"), all.Photos.Single(p => p.Path.EndsWith("c.jpeg")).Id);
        Assert.NotEqual(PhotoFrame.IdOf(Photos, "a.jpg"), PhotoFrame.IdOf(Path.Combine(_dir, "other"), "a.jpg")); // per folder
    }

    [Fact]
    public void LinksOutOfTheFolderAreSkipped()
    {
        var outside = Path.Combine(_dir, "secret.jpg");
        File.WriteAllBytes(outside, Jpeg);
        try { File.CreateSymbolicLink(Path.Combine(Photos, "link.jpg"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink rights on this machine
        Assert.Empty(PhotoFrame.Scan(Photos, subfolders: true).Photos);
    }

    [Fact]
    public void LinkedFoldersAreNeverEntered()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "secret.jpg"), Jpeg);
        var link = Path.Combine(Photos, "jn");
        if (OperatingSystem.IsWindows())
        {
            // a junction needs no special rights
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, outside }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            p.WaitForExit(10_000);
        }
        else Directory.CreateSymbolicLink(link, outside);
        Assert.True(Directory.Exists(link));
        Assert.Empty(PhotoFrame.Scan(Photos, subfolders: true).Photos);
    }

    [Fact]
    public void ACloudSyncedEntryIsNotALink()
    {
        // OneDrive files and folders are reparse points without a link target; junctions and symlinks have one
        Assert.False(PhotoFrame.IsLink(FileAttributes.Archive | FileAttributes.ReparsePoint, () => null));
        Assert.True(PhotoFrame.IsLink(FileAttributes.Directory | FileAttributes.ReparsePoint, () => @"C:\elsewhere"));
        Assert.False(PhotoFrame.IsLink(FileAttributes.Archive, () => throw new InvalidOperationException("not asked without the attribute")));
    }

    [Fact]
    public void SizesComeFromTheHeaders()
    {
        Assert.Equal((16, 16), PhotoFrame.SizeOf(Jpeg, "image/jpeg"));
        Assert.Equal((4000, 3000), PhotoFrame.SizeOf(PngOf(4000, 3000), "image/png"));
        Assert.Equal((320, 200), PhotoFrame.SizeOf([.. "GIF89a"u8, 0x40, 0x01, 0xC8, 0x00], "image/gif"));
        byte[] vp8x = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0x7F, 0x07, 0, 0x37, 0x04, 0];
        Assert.Equal((1920, 1080), PhotoFrame.SizeOf(vp8x, "image/webp"));
        Assert.Null(PhotoFrame.SizeOf([0xFF, 0xD8, 0xFF, 0xDA, 0, 2], "image/jpeg"));
    }

    [Fact]
    public async Task ASmallFileWithHugeDimensionsIsNotSentAsItIs()
    {
        // 20000 x 20000 in a few bytes: decoding it would take an old phone's WebView down
        File.WriteAllBytes(Path.Combine(Photos, "bomb.png"), PngOf(20000, 20000));
        var frame = Frame();
        frame.ScanNow();
        Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "bomb.png"), CancellationToken.None)).Bytes);
    }

    [Fact]
    public void ClearingTheFolderDeletesTheScaledCopies()
    {
        var path = Path.Combine(_dir, "config.json");
        var frame = Frame();
        var cache = Path.Combine(_dir, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "old.jpg"), Jpeg);
        File.WriteAllText(path, File.ReadAllText(path).Replace(System.Text.Json.JsonSerializer.Serialize(Photos), "\"\""));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Empty(frame.List().Ids);
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void ClearingTheFolderWithoutTheWidgetStillDeletesTheCopies()
    {
        var path = Path.Combine(_dir, "config.json");
        var frame = Frame();
        var cache = Path.Combine(_dir, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "old.jpg"), Jpeg);
        // the widget is gone and the folder cleared in the same save: the phone never asks again, the save does it
        File.WriteAllText(path, """{ "pages": [ { "id": "p", "title": "P", "widgets": [] } ], "photos": { "folder": "" } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        frame.SettingsSaved();
        Assert.False(Directory.Exists(cache));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive, false)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00040000, true)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((FileAttributes)0x00400000, true)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (OneDrive Files On-Demand)
    public void CloudOnlyFilesAreRecognised(FileAttributes a, bool cloudOnly) => Assert.Equal(cloudOnly, PhotoFrame.CloudOnly(a));

    [Fact]
    public void TheTypeComesFromTheBytesNotTheName()
    {
        Assert.Equal("image/jpeg", PhotoFrame.MimeOf(Jpeg));
        Assert.Equal("image/png", PhotoFrame.MimeOf(Png));
        Assert.Equal("image/gif", PhotoFrame.MimeOf("GIF89a\0\0\0\0\0\0"u8));
        Assert.Equal("image/webp", PhotoFrame.MimeOf("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(PhotoFrame.MimeOf("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8));
        Assert.Null(PhotoFrame.MimeOf("<html><script>"u8));
    }

    [Fact]
    public async Task PhotosAreServedByIdOnly()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        File.WriteAllText(Path.Combine(Photos, "fake.jpg"), "<html>not an image</html>");
        var frame = Frame();
        frame.ScanNow();
        var (ids, error, _) = frame.List();
        Assert.Null(error);
        Assert.Equal(2, ids.Count);

        var real = await frame.Get(PhotoFrame.IdOf(Photos, "a.jpg"), CancellationToken.None);
        Assert.Equal(Jpeg, real.Bytes);
        Assert.Equal("image/jpeg", real.Type);

        Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "fake.jpg"), CancellationToken.None)).Bytes); // not an image inside
        Assert.Null((await frame.Get("../../config.json", CancellationToken.None)).Bytes);
        Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "missing.jpg"), CancellationToken.None)).Bytes);
    }

    [Fact]
    public async Task NothingIsListedOrServedWithoutTheWidgetOnAnEnabledPage()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        foreach (var frame in new[] { Frame(widget: false), Frame(pageEnabled: false) })
        {
            Assert.False(frame.Shown());
            var (ids, error, _) = frame.List();
            Assert.Empty(ids);
            Assert.NotNull(error);
            Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "a.jpg"), CancellationToken.None)).Bytes);
        }
    }

    [Fact]
    public void TheFolderNeverReachesThePhone()
    {
        var path = Path.Combine(_dir, "config.json");
        _ = Frame();
        var pub = JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic());
        Assert.DoesNotContain("phonebox-photos-", pub);
    }

    [Fact]
    public async Task InFlightScansDoNotOutliveFolderChangesAndZeroOrOverflowPngSizesAreRejected()
    {
        File.WriteAllBytes(Path.Combine(Photos, "zero.png"), PngOf(0, 16));
        File.WriteAllBytes(Path.Combine(Photos, "overflow.png"), PngOf(unchecked((int)0x80000010u), 16));
        var frame = Frame();
        frame.ScanNow();
        Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "zero.png"), CancellationToken.None)).Bytes);
        Assert.Null((await frame.Get(PhotoFrame.IdOf(Photos, "overflow.png"), CancellationToken.None)).Bytes);

        var other = Path.Combine(_dir, "photos-other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "only.png"), Png);
        var path = Path.Combine(_dir, "config.json");
        var store = new ConfigStore(path);
        // the old folder's scan is held just before it publishes until the new folder's scan has finished
        var oldReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = new PhotoFrame(store, NullLogger<PhotoFrame>.Instance)
        {
            CacheDir = Path.Combine(_dir, "cache-race"),
            Scanner = (root, subfolders) =>
            {
                var lib = PhotoFrame.Scan(root, subfolders);
                if (root == Photos) { oldReached.TrySetResult(); oldRelease.Task.Wait(TimeSpan.FromSeconds(10)); }
                return lib;
            },
        };
        _ = racing.List();
        var firstScan = ScanTask(racing);
        await oldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.WriteAllText(path, File.ReadAllText(path).Replace(JsonSerializer.Serialize(Photos), JsonSerializer.Serialize(other)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        racing.SettingsSaved();
        _ = racing.List();
        await ScanTask(racing).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([PhotoFrame.IdOf(other, "only.png")], racing.List().Ids);
        oldRelease.SetResult();
        await firstScan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([PhotoFrame.IdOf(other, "only.png")], racing.List().Ids);
    }

    // a cleared slot means that scan has already finished
    private static Task ScanTask(PhotoFrame frame) =>
        (Task?)typeof(PhotoFrame).GetField("_scan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frame)
        ?? Task.CompletedTask;

    private static PhotoFrame.Library? Published(PhotoFrame frame) =>
        (PhotoFrame.Library?)typeof(PhotoFrame).GetField("_library", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frame);

    /// <summary>A frame whose scans each wait for the test to let them publish, in the order they were started.</summary>
    private (PhotoFrame Frame, List<(bool Subfolders, TaskCompletionSource Reached, TaskCompletionSource Release)> Scans) HeldScans(ConfigStore store)
    {
        var scans = new List<(bool, TaskCompletionSource, TaskCompletionSource)>();
        var frame = new PhotoFrame(store, NullLogger<PhotoFrame>.Instance)
        {
            CacheDir = Path.Combine(_dir, "cache-held"),
            Scanner = (root, subfolders) =>
            {
                var lib = PhotoFrame.Scan(root, subfolders);
                (bool, TaskCompletionSource Reached, TaskCompletionSource Release) mine;
                lock (scans) scans.Add(mine = (subfolders, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));
                mine.Reached.TrySetResult();
                mine.Release.Task.Wait(TimeSpan.FromSeconds(10));
                return lib;
            },
        };
        return (frame, scans);
    }

    private static void SetSubfolders(string path, bool on, int minutes)
    {
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"subfolders\":{(on ? "false" : "true")}", $"\"subfolders\":{(on ? "true" : "false")}"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes));
    }

    private static async Task Reached(List<(bool Subfolders, TaskCompletionSource Reached, TaskCompletionSource Release)> scans, int count)
    {
        for (var i = 0; i < 1000; i++)
        {
            lock (scans) if (scans.Count >= count) break;
            await Task.Delay(10);
        }
        Task reached;
        lock (scans) reached = scans[count - 1].Reached.Task;
        await reached.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnOlderScanForTheSameSelectionCannotPublishOverTheOneThatOwnsTheSlot()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        _ = Frame();
        var path = Path.Combine(_dir, "config.json");
        var (frame, scans) = HeldScans(new ConfigStore(path));
        try
        {
            // recursive, then not, then recursive again (two requests that read the settings around a save)
            _ = frame.List();
            var first = ScanTask(frame);
            await Reached(scans, 1);
            SetSubfolders(path, false, 2);
            _ = frame.List();
            await Reached(scans, 2);
            SetSubfolders(path, true, 4);
            _ = frame.List();
            var third = ScanTask(frame);
            await Reached(scans, 3);
            Assert.NotSame(first, third);

            scans[0].Release.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(Published(frame));
            Assert.True(frame.List().Scanning);
        }
        finally { lock (scans) foreach (var s in scans) s.Release.TrySetResult(); }
    }

    [Fact]
    public async Task TurningSubfoldersOffDropsTheRecursiveScanInFlight()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        Directory.CreateDirectory(Path.Combine(Photos, "trip"));
        File.WriteAllBytes(Path.Combine(Photos, "trip", "b.jpg"), Jpeg);
        _ = Frame();
        var path = Path.Combine(_dir, "config.json");
        var (frame, scans) = HeldScans(new ConfigStore(path));
        try
        {
            _ = frame.List();
            var recursive = ScanTask(frame);
            await Reached(scans, 1);
            SetSubfolders(path, false, 2);
            frame.SettingsSaved();
            scans[0].Release.SetResult();
            await recursive.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(Published(frame));
        }
        finally { lock (scans) foreach (var s in scans) s.Release.TrySetResult(); }
    }
}
