using PhoneBox.Agent;
using Xunit;

/// <summary>What the agent accepts as the phone app's icon: a small PNG, and nothing else, whatever the phone sends.</summary>
public sealed class NotificationIconTests
{
    private static byte[] Png(int w, int h, int length = 33)
    {
        var png = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        png[16] = (byte)(w >> 24); png[17] = (byte)(w >> 16); png[18] = (byte)(w >> 8); png[19] = (byte)w;
        png[20] = (byte)(h >> 24); png[21] = (byte)(h >> 16); png[22] = (byte)(h >> 8); png[23] = (byte)h;
        return png;
    }

    [Fact] public void ASmallPngIsAccepted() => Assert.NotNull(NotificationIcon.TryDecode(Convert.ToBase64String(Png(96, 96))));
    [Fact] public void The256PixelLimitIsInclusive() => Assert.NotNull(NotificationIcon.TryDecode(Convert.ToBase64String(Png(256, 256))));
    [Fact] public void ABiggerPictureIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(Png(257, 96))));
    [Fact] public void ZeroSizeIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(Png(0, 96))));
    [Fact] public void ANegativeSizeIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(Png(unchecked((int)0x80000000), 96))));
    [Fact] public void ABigFileIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(Png(96, 96, NotificationIcon.MaxBytes + 1))));
    [Fact] public void AFileOfExactlyTheLimitIsAccepted() => Assert.NotNull(NotificationIcon.TryDecode(Convert.ToBase64String(Png(96, 96, NotificationIcon.MaxBytes))));
    [Fact] public void NotAPngIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(new byte[100])));
    [Fact] public void NotBase64IsIgnored() => Assert.Null(NotificationIcon.TryDecode("this is not base64!!"));
    [Fact] public void EmptyAndNullAreIgnored() { Assert.Null(NotificationIcon.TryDecode("")); Assert.Null(NotificationIcon.TryDecode(null)); }
    [Fact] public void AHugeStringIsRefusedBeforeItIsDecoded() => Assert.Null(NotificationIcon.TryDecode(new string('A', 1_000_000)));
    [Fact] public void ATruncatedPngIsIgnored() => Assert.Null(NotificationIcon.TryDecode(Convert.ToBase64String(Png(96, 96)[..20])));
}
