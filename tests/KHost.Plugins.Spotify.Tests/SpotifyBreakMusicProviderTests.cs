using KHost.Plugins.Sdk.Services;
using KHost.Plugins.Spotify;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace KHost.Plugins.Spotify.Tests;

public class SpotifyBreakMusicProviderTests
{
    private readonly FakeSpotifyController _controller = new();
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();

    private SpotifyBreakMusicProvider Build(SpotifySettings? settings = null)
    {
        _context.BindSettings<SpotifySettings>().Returns(settings ?? new SpotifySettings());

        return new SpotifyBreakMusicProvider(NullLogger<SpotifyBreakMusicProvider>.Instance, _context, _controller);
    }

    [Fact]
    public void RendersThroughHost_IsFalse_BecauseTheSoundLeavesSpotifysOwnOutput()
        => Assert.False(Build().RendersThroughHost);

    // A property cannot go and ask, so it stays empty until a command has been through.
    [Fact]
    public void CurrentTrack_BeforeAnyCommand_IsNull()
        => Assert.Null(Build().CurrentTrack);

    [Fact]
    public async Task CurrentTrack_AfterStarting_NamesWhatSpotifyReports()
    {
        _controller.State = new SpotifyState(SpotifyPlayback.Paused, "Blue Monday", "New Order");

        var provider = Build();
        await provider.StartAsync();

        Assert.Equal("Blue Monday", provider.CurrentTrack!.Title);
        Assert.Equal("New Order", provider.CurrentTrack.Artist);
    }

    // The host put break music on themselves while waiting for a first singer. Starting again
    // must not send a command — on Windows that is one key, and it would stop the room's music.
    [Fact]
    public async Task StartAsync_SpotifyAlreadyPlaying_LeavesItAloneAndReportsSuccess()
    {
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Blue Monday", "New Order");

        var provider = Build();

        Assert.True(await provider.StartAsync());
        Assert.DoesNotContain("start", _controller.Calls);
        Assert.Equal("Blue Monday", provider.CurrentTrack!.Title);
    }

    [Fact]
    public async Task StartAsync_SpotifyPaused_StartsItAsBefore()
    {
        _controller.State = new SpotifyState(SpotifyPlayback.Paused);

        await Build().StartAsync();

        Assert.Contains("start", _controller.Calls);
    }

    // A backend that cannot see must not be treated as "already playing", or break music never
    // starts at all on that platform.
    [Fact]
    public async Task StartAsync_StateUnreadable_StartsItAnyway()
    {
        _controller.State = null;

        await Build().StartAsync();

        Assert.Contains("start", _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_SpotifyCouldNotBeReached_IsFalse()
    {
        _controller.CanStart = false;

        Assert.False(await Build().StartAsync());
    }

    [Fact]
    public async Task StartAsync_SpotifyStarted_IsTrue()
        => Assert.True(await Build().StartAsync());

    [Fact]
    public async Task StartAsync_APlaylistLinkIsConfigured_PlaysThatContext()
    {
        var provider = Build(new SpotifySettings
        {
            PlaylistUri = "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc",
        });

        await provider.StartAsync();

        Assert.Equal("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", _controller.StartedContextUri);
    }

    [Fact]
    public async Task StartAsync_NoPlaylistConfigured_ResumesWhateverSpotifyHasLoaded()
    {
        await Build().StartAsync();

        Assert.Null(_controller.StartedContextUri);
    }

    [Fact]
    public async Task StartAsync_ShuffleIsOff_DoesNotShuffle()
    {
        await Build(new SpotifySettings { Shuffle = false }).StartAsync();

        Assert.False(_controller.StartedWithShuffle);
    }

    [Fact]
    public async Task StartAsync_APlaylistThatIsNotAPlayableContext_FallsBackToResuming()
    {
        var provider = Build(new SpotifySettings { PlaylistUri = "https://example.com/nope" });

        await provider.StartAsync();

        Assert.Null(_controller.StartedContextUri);
    }

    [Fact]
    public void Constructor_APlaylistThatIsNotAPlayableContext_TellsTheHost()
    {
        Build(new SpotifySettings { PlaylistUri = "https://example.com/nope" });

        _context.Received(1).ReportWarning(Arg.Is<string>(message => message.Contains("https://example.com/nope")));
    }

    [Fact]
    public void Constructor_APlaylistThatPlays_SaysNothingAboutIt()
    {
        Build(new SpotifySettings { PlaylistUri = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M" });

        _context.DidNotReceive().ReportWarning(Arg.Any<string>());
    }

    [Fact]
    public void Constructor_TheBackendCannotDoEverything_TellsTheHostOnce()
    {
        _controller.Limitation = "The media keys reach whichever app owns media focus.";

        Build();

        _context.Received(1).ReportWarning("The media keys reach whichever app owns media focus.");
    }

    [Fact]
    public async Task PauseAsync_PausesSpotify()
    {
        await Build().PauseAsync();

        Assert.Equal(["pause"], _controller.Calls);
    }

    [Fact]
    public async Task ResumeAsync_ResumesSpotify()
    {
        await Build().ResumeAsync();

        Assert.Equal(["resume"], _controller.Calls);
    }

    [Fact]
    public async Task SkipAsync_SkipsToTheNextTrack()
    {
        await Build().SkipAsync();

        Assert.Equal(["skip"], _controller.Calls);
    }

    /// <summary>
    /// Spotify's level is the host's own setting, in an app they can see. KHost pushes the venue
    /// volume at every provider it cannot mix, and this one has to decline it rather than move a
    /// slider out from under them.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.35f)]
    [InlineData(1f)]
    public async Task SetVolumeAsync_AnyLevel_LeavesSpotifyAlone(float volume)
    {
        await Build().SetVolumeAsync(volume);

        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task StopAsync_NoFadeAsked_Stops()
    {
        await Build().StopAsync();

        Assert.Equal(["stop"], _controller.Calls);
    }

    /// <summary>
    /// The fade hint is ignored outright. KHost suspends break music before it loads a song and
    /// waits on it, and the ramp this used to run was a process spawn per step — a two second
    /// fade held the console for nearly five with the singer stood there.
    /// </summary>
    [Fact]
    public async Task StopAsync_AFadeAsked_StopsAtOnceWithoutTouchingTheVolume()
    {
        await Build().StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["stop"], _controller.Calls);
    }

    [Fact]
    public async Task StopAsync_AFadeAsked_DoesNotBlockForTheFadeDuration()
    {
        var started = Stopwatch.GetTimestamp();

        await Build().StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500),
            "stopping waited out the fade it was asked for");
    }
}
