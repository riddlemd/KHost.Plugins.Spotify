using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

public class ShouldSendToggleTests
{
    // The whole reason state is read back. Windows has one key for both directions, so a host who
    // put Spotify on before the first singer was ready used to get it toggled straight off.
    [Fact]
    public void ShouldSendToggle_AlreadyPlayingAndAskedToPlay_SendsNothing()
    {
        var state = new SpotifyState(SpotifyPlayback.Playing, "Blue Monday", "New Order");

        Assert.False(WindowsSpotifyController.ShouldSendToggle(state, SpotifyPlayback.Playing));
    }

    // Pausing what is already stopped would start it — the toggle only goes one way.
    [Fact]
    public void ShouldSendToggle_StoppedAndAskedToPause_SendsNothing()
    {
        Assert.False(WindowsSpotifyController.ShouldSendToggle(SpotifyState.Stopped, SpotifyPlayback.Paused));
    }

    [Fact]
    public void ShouldSendToggle_AlreadyPausedAndAskedToPause_SendsNothing()
    {
        var state = new SpotifyState(SpotifyPlayback.Paused);

        Assert.False(WindowsSpotifyController.ShouldSendToggle(state, SpotifyPlayback.Paused));
    }

    [Theory]
    [InlineData(SpotifyPlayback.Paused, SpotifyPlayback.Playing)]
    [InlineData(SpotifyPlayback.Stopped, SpotifyPlayback.Playing)]
    [InlineData(SpotifyPlayback.Playing, SpotifyPlayback.Paused)]
    public void ShouldSendToggle_StateIsNotWhatWasAsked_SendsTheKey(SpotifyPlayback current, SpotifyPlayback target)
    {
        Assert.True(WindowsSpotifyController.ShouldSendToggle(new SpotifyState(current), target));
    }

    // A backend that cannot see is no worse off than the remembered flag it replaced, and doing
    // nothing would leave a host pressing play to silence.
    [Fact]
    public void ShouldSendToggle_StateUnreadable_SendsTheKeyAnyway()
    {
        Assert.True(WindowsSpotifyController.ShouldSendToggle(null, SpotifyPlayback.Playing));
    }
}

public class MacOsStateScriptTests
{
    [Fact]
    public void ParseState_Playing_ReadsTransportTrackAndArtist()
    {
        var state = MacOsScripts.ParseState("playing\tBlue Monday\tNew Order\n");

        Assert.Equal(SpotifyPlayback.Playing, state!.Playback);
        Assert.Equal("Blue Monday", state.Title);
        Assert.Equal("New Order", state.Artist);
    }

    // Tab separated for exactly this: a comma-joined list would split the title in the wrong place.
    [Fact]
    public void ParseState_TitleContainingAComma_KeepsItWhole()
    {
        var state = MacOsScripts.ParseState("playing\tHello, Goodbye\tThe Beatles");

        Assert.Equal("Hello, Goodbye", state!.Title);
        Assert.Equal("The Beatles", state.Artist);
    }

    [Fact]
    public void ParseState_Paused_ReadsAsPaused()
    {
        Assert.Equal(SpotifyPlayback.Paused, MacOsScripts.ParseState("paused\tX\tY")!.Playback);
    }

    // The guard declining to launch Spotify is not a transport state, and must not read as one.
    [Fact]
    public void ParseState_SpotifyNotRunning_IsNull()
    {
        Assert.Null(MacOsScripts.ParseState("notrunning\n"));
    }

    [Fact]
    public void ParseState_NoTrackLoaded_ReportsTransportWithoutATrack()
    {
        var state = MacOsScripts.ParseState("stopped\t\t");

        Assert.Equal(SpotifyPlayback.Stopped, state!.Playback);
        Assert.Null(state.Title);
        Assert.Null(state.Artist);
    }
}

public class MprisMetadataTests
{
    private const string Sample =
        "({'mpris:trackid': <'spotify:track:abc'>, 'xesam:title': <'Blue Monday'>, "
        + "'xesam:artist': <['New Order', 'Someone Else']>, 'mpris:length': <int64 450000000>},)";

    [Fact]
    public void Title_RealGdbusOutput_IsRead()
    {
        Assert.Equal("Blue Monday", MprisMetadata.Title(Sample));
    }

    // MPRIS types artist as a list; a console row wants one name, so the first is taken.
    [Fact]
    public void Artist_RealGdbusOutput_TakesTheFirst()
    {
        Assert.Equal("New Order", MprisMetadata.Artist(Sample));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("()")]
    public void Title_NothingUsable_IsNull(string? metadata)
    {
        Assert.Null(MprisMetadata.Title(metadata));
    }
}
