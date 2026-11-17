using System.Text;
using PhoneBox.Agent;
using Xunit;

public sealed class ShortcutIconUploadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-icon-uploads-" + Guid.NewGuid().ToString("N"));
    private readonly ShortcutIconUploads _uploads;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];
    private static readonly byte[] Webp = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P'];
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>");

    public ShortcutIconUploadsTests() => _uploads = new ShortcutIconUploads(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void NothingUploadedReadsAsNull() => Assert.Null(_uploads.Read("terminal"));

    [Fact]
    public void ExistsAgreesWithRead()
    {
        Assert.False(_uploads.Exists("terminal"));
        Assert.Null(_uploads.Save("terminal", "image/png", Png));
        Assert.True(_uploads.Exists("terminal"));
    }

    [Fact]
    public void RevisionChangesWhenTheUploadIsReplaced()
    {
        Assert.Null(_uploads.Revision("terminal"));
        Assert.Null(_uploads.Save("terminal", "image/png", Png));
        var first = _uploads.Revision("terminal");
        Assert.NotNull(first);

        Assert.Null(_uploads.Save("terminal", "image/svg+xml", Svg));
        Assert.NotEqual(first, _uploads.Revision("terminal"));

        _uploads.Remove("terminal");
        Assert.Null(_uploads.Revision("terminal"));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("image/svg+xml")]
    public void AValidUploadOfEachSupportedTypeCanBeReadBack(string contentType)
    {
        var bytes = contentType switch { "image/png" => Png, "image/webp" => Webp, _ => Svg };
        Assert.Null(_uploads.Save("terminal", contentType, bytes));
        var (readBytes, readType) = _uploads.Read("terminal")!.Value;
        Assert.Equal(bytes, readBytes);
        Assert.Equal(contentType, readType);
    }

    [Fact]
    public void AContentTypeWithParametersIsStillRecognised()
    {
        Assert.Null(_uploads.Save("terminal", "image/png; charset=binary", Png));
        Assert.NotNull(_uploads.Read("terminal"));
    }

    [Fact]
    public void UnsupportedContentTypesAreRejected()
    {
        Assert.Equal("Only PNG, WEBP or SVG icons are supported", _uploads.Save("terminal", "image/gif", [1, 2, 3]));
        Assert.Equal("Only PNG, WEBP or SVG icons are supported", _uploads.Save("terminal", null, [1, 2, 3]));
    }

    [Fact]
    public void ContentThatDoesNotMatchItsDeclaredTypeIsRejected()
    {
        Assert.Contains("PNG", _uploads.Save("terminal", "image/png", Encoding.UTF8.GetBytes("not a png")));
        Assert.Contains("SVG", _uploads.Save("terminal", "image/svg+xml", Png));
        Assert.Null(_uploads.Read("terminal")); // the rejected upload left no file behind
    }

    [Fact]
    public void APngWithACorruptedSignatureIsRejected() =>
        Assert.Contains("PNG", _uploads.Save("terminal", "image/png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]));

    [Fact]
    public void AnEmptyFileIsRejected() => Assert.Equal("The file is empty", _uploads.Save("terminal", "image/png", []));

    [Fact]
    public void TooLargeAnUploadIsRejected() => Assert.Equal($"The icon is larger than {ShortcutIconUploads.MaxBytes / 1024} KB", _uploads.Save("terminal", "image/png", null));

    [Fact]
    public void UploadingAgainInADifferentFormatReplacesTheOldOne()
    {
        _uploads.Save("terminal", "image/png", Png);
        _uploads.Save("terminal", "image/svg+xml", Svg);
        var (bytes, contentType) = _uploads.Read("terminal")!.Value;
        Assert.Equal("image/svg+xml", contentType);
        Assert.Equal(Svg, bytes);
    }

    [Fact]
    public void RemoveDeletesWhicheverFormatWasSaved()
    {
        _uploads.Save("terminal", "image/webp", Webp);
        _uploads.Remove("terminal");
        Assert.Null(_uploads.Read("terminal"));
    }

    [Fact]
    public void RemoveOfANeverUploadedIconIsHarmless() => _uploads.Remove("never-uploaded");

    [Fact]
    public void CleanupDropsUploadsForShortcutsThatNoLongerExist()
    {
        _uploads.Save("keep-me", "image/png", Png);
        _uploads.Save("drop-me", "image/png", Png);
        _uploads.Cleanup(() => ["keep-me"]);
        Assert.NotNull(_uploads.Read("keep-me"));
        Assert.Null(_uploads.Read("drop-me"));
    }

    [Fact]
    public void CleanupOnAFolderThatWasNeverCreatedDoesNothing() => new ShortcutIconUploads(Path.Combine(_dir, "never-created")).Cleanup(() => ["anything"]);

    [Fact]
    public void AnIdWithPathCharactersCannotEscapeTheIconsFolder()
    {
        Assert.Null(_uploads.Save("../escape", "image/png", Png));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "escape.png"))); // did not escape upward
        Assert.NotNull(_uploads.Read("../escape")); // written safely under _dir instead, as a sanitised filename
    }
}
