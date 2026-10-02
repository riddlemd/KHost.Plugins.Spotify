using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The order is the point: each list is what the room hears, fade and command interleaved.</summary>
public class FadingSpotifyControllerTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(1500);

    private readonly FakeSpotifyController _inner = new();
    private readonly FakeSpotifyFader _fader;
    private readonly IFlashService _flash = Substitute.For<IFlashService>();

    public FadingSpotifyControllerTests() => _fader = new FakeSpotifyFader(_inner.Calls);

    private FadingSpotifyController Build(TimeSpan? fade = null)
        => new(_inner, _fader, fade ?? Fade, _flash);

    // ── what each command does with a fader that works ─────────────────────────────────

    // Down at once to cover the playlist loading, up over the fade once it plays.
    [Fact]
    public async Task StartAsync_SilencesFirstStartsThenComesUpOverTheFade()
    {
        Assert.True(await Build().StartAsync("spotify:playlist:x", shuffle: true));

        Assert.Equal(["silence 0", "start", "restore 1500"], _inner.Calls);
    }

    // Nothing started, so the silence just set would be permanent.
    [Fact]
    public async Task StartAsync_TheBackendRefuses_PutsTheLevelBackAtOnce()
    {
        _inner.CanStart = false;

        Assert.False(await Build().StartAsync(null, shuffle: false));

        Assert.Equal(["silence 0", "start", "restore 0"], _inner.Calls);
    }

    [Fact]
    public async Task PauseAsync_FadesOutThenPauses()
    {
        await Build().PauseAsync();

        Assert.Equal(["silence 1500", "pause"], _inner.Calls);
    }

    // Silenced first so the fade out reads the room: a host who turned Spotify up while it sat
    // paused has that level come back.
    [Fact]
    public async Task ResumeAsync_FadesIn()
    {
        await Build().ResumeAsync();

        Assert.Equal(["silence 0", "resume", "restore 1500"], _inner.Calls);
    }

    // The way back is bookkeeping over a stopped Spotify, so it is instant, but it has to happen
    // or Spotify is left muted for whoever reaches for it next.
    [Fact]
    public async Task StopAsync_FadesOutStopsThenPutsTheLevelBack()
    {
        await Build().StopAsync();

        Assert.Equal(["silence 1500", "stop", "restore 0"], _inner.Calls);
    }

    [Fact]
    public async Task SkipAsync_WhileTheMusicIsUp_IsAPlainSkip()
    {
        var controller = Build();
        await controller.StartAsync(null, shuffle: false);
        _inner.Calls.Clear();

        await controller.SkipAsync();

        Assert.Equal(["skip"], _inner.Calls);
    }

    // The backend's next track resumes a paused client, and the level is still where the fade
    // out left it, so without this the new song plays to a silent room.
    [Fact]
    public async Task SkipAsync_WhileFadedOut_ComesBackUp()
    {
        var controller = Build();
        await controller.PauseAsync();
        _inner.Calls.Clear();

        await controller.SkipAsync();

        Assert.Equal(["skip", "restore 1500"], _inner.Calls);
    }

    [Fact]
    public async Task SkipAsync_AfterAResume_IsAPlainSkipAgain()
    {
        var controller = Build();
        await controller.PauseAsync();
        await controller.ResumeAsync();
        _inner.Calls.Clear();

        await controller.SkipAsync();

        Assert.Equal(["skip"], _inner.Calls);
    }

    // A fade out that never landed left nothing silent to bring back.
    [Fact]
    public async Task SkipAsync_AfterAFadeOutThatFailed_IsAPlainSkip()
    {
        var controller = Build();
        _fader.Outcome = FadeOutcome.Failed;
        await controller.PauseAsync();
        _inner.Calls.Clear();

        await controller.SkipAsync();

        Assert.Equal(["skip"], _inner.Calls);
    }

    // ── a fader that fails, or is not there, costs the fade and nothing else ────────────

    [Fact]
    public async Task StopAsync_TheFadeOutFails_StillStopsAndDoesNotRestoreALevelItNeverLeft()
    {
        _fader.Outcome = FadeOutcome.Failed;

        await Build().StopAsync();

        Assert.Equal(["silence 1500", "stop"], _inner.Calls);
    }

    [Fact]
    public async Task StartAsync_TheSilenceFails_StartsAtFullLevelWithNoFadeIn()
    {
        _fader.Outcome = FadeOutcome.Failed;

        Assert.True(await Build().StartAsync(null, shuffle: false));

        Assert.Equal(["silence 0", "start"], _inner.Calls);
    }

    [Fact]
    public async Task AFailingFader_IsFlashedOnceNotOnEveryCommand()
    {
        _fader.Outcome = FadeOutcome.Failed;

        var controller = Build();
        await controller.StartAsync(null, shuffle: false);
        await controller.PauseAsync();
        await controller.ResumeAsync();

        _flash.Received(1).Show(Arg.Is<string>(text => text.StartsWith("Spotify:")), FlashType.Warning);
    }

    // Once per run of failures: a fade that lands in between means the next failure is news.
    [Fact]
    public async Task AFaderThatRecoversAndFailsAgain_IsFlashedAgain()
    {
        _fader.Outcomes.Enqueue(FadeOutcome.Failed);
        _fader.Outcomes.Enqueue(FadeOutcome.Landed);
        _fader.Outcomes.Enqueue(FadeOutcome.Failed);

        var controller = Build();
        await controller.PauseAsync();
        await controller.PauseAsync();
        await controller.PauseAsync();

        _flash.Received(2).Show(Arg.Any<string>(), FlashType.Warning);
    }

    // A Spotify that is not running, or a fade a newer one replaced, is not something to tell
    // the host about.
    [Theory]
    [InlineData(nameof(FadeOutcome.NothingToFade))]
    [InlineData(nameof(FadeOutcome.Superseded))]
    public async Task AFadeWithNothingToDo_IsNotFlashed(string outcome)
    {
        _fader.Outcome = Enum.Parse<FadeOutcome>(outcome);

        await Build().StartAsync(null, shuffle: false);

        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    [Fact]
    public async Task WithNoFaderOnThisPlatform_EveryCommandGoesStraightThrough()
    {
        _fader.IsAvailable = false;

        var controller = Build();
        await controller.StartAsync("spotify:playlist:x", shuffle: true);
        await controller.PauseAsync();
        await controller.ResumeAsync();
        await controller.SkipAsync();
        await controller.StopAsync();

        Assert.Equal(["start", "pause", "resume", "skip", "stop"], _inner.Calls);
        _flash.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    // Said once at startup instead of flashed at every start.
    [Fact]
    public void WithNoFaderOnThisPlatform_TheLimitationSaysSo()
    {
        _fader.IsAvailable = false;
        _inner.Limitation = "The media keys reach whichever app owns media focus.";

        var limitation = Build().Limitation;

        Assert.StartsWith("The media keys reach whichever app owns media focus.", limitation);
        Assert.Contains("not faded", limitation);
    }

    [Fact]
    public void WithAFader_TheLimitationIsTheBackendsOwn()
    {
        _inner.Limitation = "Spotify cannot be watched on this platform.";

        Assert.Equal("Spotify cannot be watched on this platform.", Build().Limitation);
    }

    // Zero is the host turning fading off: Spotify's level is not touched at all, not even to
    // silence it for an instant.
    [Fact]
    public async Task AFadeOfZero_NeverTouchesTheLevel()
    {
        var controller = Build(TimeSpan.Zero);
        await controller.StartAsync(null, shuffle: false);
        await controller.PauseAsync();
        await controller.StopAsync();

        Assert.Equal(["start", "pause", "stop"], _inner.Calls);
        Assert.Null(controller.Limitation);
    }

    // ── what passes straight through ───────────────────────────────────────────────────

    [Fact]
    public void TheBackendsOwnWatch_ReachesTheSubscriber()
    {
        var controller = Build();
        var raised = 0;
        controller.PlaybackChanged += (_, _) => raised++;

        _inner.RaisePlaybackChanged();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task StartingTheWatch_ReachesTheBackend()
    {
        await Build().StartWatchingAsync();

        Assert.True(_inner.WatchStarted);
    }

    [Fact]
    public async Task State_ComesFromTheBackend()
    {
        _inner.State = new SpotifyState(SpotifyPlayback.Playing, "Blue Monday", "New Order");

        Assert.Equal("Blue Monday", (await Build().GetStateAsync())?.Title);
    }
}
