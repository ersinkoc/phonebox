using PhoneBox.Agent;
using Xunit;

// The player name Now playing shows (and the lyrics widget's per-player offset matches on): well known players get a
// friendly name, anything else is the app's ID without its last dotted part.
public sealed class MediaAppNameTests
{
    [Theory]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "Apple Music")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("org.videolan.vlc", "VLC")]
    [InlineData("SomeCompany.SomePlayer!App", "SomeCompany")]
    public void KnownPlayersGetTheirNameAndOthersTheirId(string id, string expected)
        => Assert.Equal(expected, MediaAppName.Friendly(id));
}
