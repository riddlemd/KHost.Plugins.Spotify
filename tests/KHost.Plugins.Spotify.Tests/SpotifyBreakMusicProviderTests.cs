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

    // Skipping is the one command that changes what is playing. Without reading back, the console
    // re-renders on the host's announcement and names the track that was just skipped past.
    [Fact]
    public async Task SkipAsync_Always_NamesTheTrackSkippedTo()
    {
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Just like Heaven", "The Cure");

        var provider = Build();
        await provider.StartAsync();

        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Walk Like an Egyptian", "The Bangles");
        await provider.SkipAsync();

        Assert.Equal("Walk Like an Egyptian", provider.CurrentTrack!.Title);
        Assert.Equal("The Bangles", provider.CurrentTrack.Artist);
    }

    // A media key is asked for and answered later: the transport keeps reporting the old track for
    // a moment after the skip lands. Taking the first read would name the track skipped past.
    [Fact]
    public async Task SkipAsync_TransportStillReportsTheOldTrack_WaitsForItToTurnOver()
    {
        var old = new SpotifyState(SpotifyPlayback.Playing, "Just like Heaven", "The Cure");
        _controller.State = old;

        var provider = Build();
        await provider.StartAsync();

        // Two more reads of the old track before it turns over.
        _controller.QueuedStates.Enqueue(old);
        _controller.QueuedStates.Enqueue(old);
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Walk Like an Egyptian", "The Bangles");

        await provider.SkipAsync();

        Assert.Equal("Walk Like an Egyptian", provider.CurrentTrack!.Title);
    }

    // A skip Spotify refuses never turns the track over, and the console cannot be held waiting
    // for one that is not coming.
    [Fact]
    public async Task SkipAsync_TrackNeverTurnsOver_GivesUpAndKeepsWhatIsPlaying()
    {
        var same = new SpotifyState(SpotifyPlayback.Playing, "Just like Heaven", "The Cure");
        _controller.State = same;

        var provider = Build();
        await provider.StartAsync();
        await provider.SkipAsync();

        Assert.Equal("Just like Heaven", provider.CurrentTrack!.Title);
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

    // Reads bracket the skip: one for what it is leaving, one for what it landed on. The point of
    // asserting the whole list is that nothing else — no play, pause or stop — goes with it.
    [Fact]
    public async Task SkipAsync_SkipsToTheNextTrackAndReadsBackWhatItLandedOn()
    {
        await Build().SkipAsync();

        Assert.Equal(["state", "skip", "state"], _controller.Calls);
    }

    // Spotify moves on by itself as tracks end, so the console's own idea of what is playing may
    // already be behind. Settling against it would stop on the track the skip was leaving.
    [Fact]
    public async Task SkipAsync_ConsoleIsBehindWhatSpotifyMovedToAlone_StillNamesWhatItSkippedTo()
    {
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Call Me", "Blondie");

        var provider = Build();
        await provider.StartAsync();

        // Spotify moved on with nothing asked of it; the console still shows Call Me.
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "How Soon Is Now?", "The Smiths");
        _controller.QueuedStates.Enqueue(_controller.State);          // the pre-skip read
        _controller.QueuedStates.Enqueue(_controller.State);          // still turning over
        _controller.State = new SpotifyState(SpotifyPlayback.Playing, "Manic Monday", "The Bangles");

        await provider.SkipAsync();

        Assert.Equal("Manic Monday", provider.CurrentTrack!.Title);
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
