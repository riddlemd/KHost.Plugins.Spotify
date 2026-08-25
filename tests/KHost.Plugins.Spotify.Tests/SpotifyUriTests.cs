using KHost.Plugins.Spotify;

namespace KHost.Plugins.Spotify.Tests;

public class SpotifyUriTests
{
    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:album:1DFixLWuPkv3KT3TnV35m3", "spotify:album:1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("spotify:artist:0gxyHStUsqpMadRV0Di1Qt", "spotify:artist:0gxyHStUsqpMadRV0Di1Qt")]
    public void Normalize_UriForAPlayableContext_IsReturnedUnchanged(string value, string expected)
        => Assert.Equal(expected, SpotifyUri.Normalize(value));

    [Fact]
    public void Normalize_UriTypeInAnotherCase_IsLowercased()
        => Assert.Equal("spotify:playlist:abc123", SpotifyUri.Normalize("spotify:PLAYLIST:abc123"));

    [Fact]
    public void Normalize_SurroundingWhitespace_IsTrimmed()
        => Assert.Equal("spotify:playlist:abc123", SpotifyUri.Normalize("  spotify:playlist:abc123\n"));

    [Fact]
    public void Normalize_ShareLink_BecomesAUri()
        => Assert.Equal(
            "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
            SpotifyUri.Normalize("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"));

    [Fact]
    public void Normalize_ShareLinkWithTrackingToken_DropsTheToken()
        => Assert.Equal(
            "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
            SpotifyUri.Normalize("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=8f2a1b7c4d"));

    [Fact]
    public void Normalize_LocalisedShareLink_BecomesAUri()
        => Assert.Equal(
            "spotify:album:1DFixLWuPkv3KT3TnV35m3",
            SpotifyUri.Normalize("https://open.spotify.com/intl-de/album/1DFixLWuPkv3KT3TnV35m3"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NothingConfigured_IsNull(string? value)
        => Assert.Null(SpotifyUri.Normalize(value));

    /// <summary>A bed that ends after one song is not a bed, so a track URI is refused.</summary>
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ")]
    public void Normalize_ContextThatCannotCarryABed_IsNull(string value)
        => Assert.Null(SpotifyUri.Normalize(value));

    /// <summary>
    /// The id is interpolated into an AppleScript literal and a D-Bus argument, so anything
    /// outside base62 has to be refused rather than escaped.
    /// </summary>
    [Theory]
    [InlineData("spotify:playlist:abc\" & (do shell script \"rm -rf ~\") & \"")]
    [InlineData("spotify:playlist:abc 123")]
    [InlineData("spotify:playlist:abc;rm -rf /")]
    [InlineData("spotify:playlist:")]
    [InlineData("https://open.spotify.com/playlist/abc\"def")]
    [InlineData("https://evil.example.com/playlist/abc123")]
    [InlineData("not a uri at all")]
    public void Normalize_ValueThatIsNotAPlainBase62Context_IsNull(string value)
        => Assert.Null(SpotifyUri.Normalize(value));
}
