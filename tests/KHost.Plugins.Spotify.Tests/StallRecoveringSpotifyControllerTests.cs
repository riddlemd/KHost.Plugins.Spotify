using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>Spotify sometimes ends a track without starting the next: playback stops outright, or
/// the next loads and sits at 0:00. Both arrive as a stop nobody asked for.</summary>
public class StallRuleTests
{
    private static readonly TimeSpan LongAgo = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(178000)]
    [InlineData(179500)]
    [InlineData(180000)]
    public void LooksLikeAStall_StoppedAtTheEnd_IsOne(long progressMs)
        => Assert.True(StallRecoveringSpotifyController.LooksLikeAStall(progressMs, 180000, LongAgo));

    [Fact]
    public void LooksLikeAStall_JustShortOfTheEnd_IsSomebodyPausing()
        => Assert.False(StallRecoveringSpotifyController.LooksLikeAStall(177999, 180000, LongAgo));

    [Fact]
    public void LooksLikeAStall_MidTrack_IsSomebodyPressingPause()
        => Assert.False(StallRecoveringSpotifyController.LooksLikeAStall(90000, 180000, LongAgo));

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public void LooksLikeAStall_AtTheStartJustAfterATrackChange_IsOne(long progressMs)
        => Assert.True(StallRecoveringSpotifyController.LooksLikeAStall(progressMs, 180000, TimeSpan.FromSeconds(1)));

    [Fact]
    public void LooksLikeAStall_PastTheStartJustAfterATrackChange_IsNotOne()
        => Assert.False(StallRecoveringSpotifyController.LooksLikeAStall(251, 180000, TimeSpan.Zero));

    // A host who pauses a track they have just started is at the beginning of it too.
    [Fact]
    public void LooksLikeAStall_AtTheStartLongAfterATrackChange_IsSomebodyPausing()
        => Assert.False(StallRecoveringSpotifyController.LooksLikeAStall(0, 180000, TimeSpan.FromMilliseconds(1001)));

    [Theory]
    [InlineData(null, 180000L)]
    [InlineData(179000L, null)]
    [InlineData(0L, 0L)]
    public void LooksLikeAStall_WithNoPlayhead_HasNothingToGoOn(long? progressMs, long? durationMs)
        => Assert.False(StallRecoveringSpotifyController.LooksLikeAStall(progressMs, durationMs, TimeSpan.Zero));
}

public class StallRecoveringSpotifyControllerTests
{
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(1);

    private readonly FakeSpotifyController _inner = new();
    private readonly TimerCountingTimeProvider _time = new();
    private readonly StallRecoveringSpotifyController _controller;
    private int _waitsReleased;

    public StallRecoveringSpotifyControllerTests()
        => _controller = new StallRecoveringSpotifyController(_inner, _time, NullLogger.Instance);

    private static SpotifyState Paused(long progressMs, string title = "Free Fallin'")
        => new(SpotifyPlayback.Paused, title, "Tom Petty", progressMs, 180000);

    private static SpotifyState Playing(string title = "Free Fallin'")
        => new(SpotifyPlayback.Playing, title, "Tom Petty", 1000, 180000);

    /// <summary>Puts Spotify where a stall leaves it and raises what the backend's watch would.</summary>
    private void Stall(long progressMs = 179000, string title = "Free Fallin'")
    {
        _inner.State = Paused(progressMs, title);
        _inner.RaisePlaybackChanged();
    }

    private IEnumerable<string> Commands => _inner.Calls.Where(call => call != "state");

    /// <summary>Lets the second's wait run out once the nudge is waiting on it.</summary>
    private async Task ConfirmAsync(int commandsSoFar)
    {
        for (var i = 0; i < 200 && Commands.Count() < commandsSoFar; i++)
            await Task.Delay(10);

        await NextWaitAsync();
        _time.Advance(Confirm);
    }

    // The nudge records its command before its wait's timer exists, so advancing on seeing the
    // command can land in between and leave that wait pending forever.
    private async Task NextWaitAsync()
    {
        for (var i = 0; i < 1000 && _time.TimersCreated <= _waitsReleased; i++)
            await Task.Delay(10);

        Assert.Equal(_waitsReleased + 1, _time.TimersCreated);
        _waitsReleased++;
    }

    private async Task SettleAsync()
        => Assert.Same(_controller.LastCheck, await Task.WhenAny(_controller.LastCheck, Task.Delay(TimeSpan.FromSeconds(10))));

    [Fact]
    public async Task ATrackThatEndsWithoutAdvancing_IsPlayedAgain()
    {
        Stall();

        // Play is taken, so nothing more is needed.
        _inner.State = Playing();
        await ConfirmAsync(1);
        await SettleAsync();

        Assert.Equal(["resume"], Commands);
    }

    [Fact]
    public async Task PlayNotTaking_FallsThroughToSkipping()
    {
        Stall();
        await ConfirmAsync(1);
        await ConfirmAsync(2);
        await SettleAsync();

        Assert.Equal(["resume", "skip"], Commands);
    }

    // Waited for, not assumed: a play that has not landed in a second is the wedged case.
    [Fact]
    public async Task TheNudge_WaitsASecondBeforeJudgingPlay()
    {
        Stall();
        await NextWaitAsync();

        _time.Advance(Confirm - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        Assert.Equal(["resume"], Commands);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await ConfirmAsync(2);
        await SettleAsync();
    }

    [Fact]
    public async Task ANextTrackSittingAtZero_JustAfterASongChange_IsPlayed()
    {
        _inner.State = Playing("Free Fallin'");
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        Stall(progressMs: 0, title: "I Won't Back Down");

        _inner.State = Playing("I Won't Back Down");
        await ConfirmAsync(1);
        await SettleAsync();

        Assert.Equal(["resume"], Commands);
    }

    // No track change behind it, so this is a host pausing a track they just started.
    [Fact]
    public async Task APauseAtTheStart_WithNoSongChange_IsLeftAlone()
    {
        _inner.State = Playing();
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        Stall(progressMs: 0);
        await SettleAsync();

        Assert.Empty(Commands);
    }

    // The same track a second after it changed is no longer "just after" anything.
    [Fact]
    public async Task APauseAtTheStart_LongAfterTheSongChange_IsLeftAlone()
    {
        _inner.State = Playing("Free Fallin'");
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        _inner.State = Playing("I Won't Back Down");
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        _time.Advance(TimeSpan.FromSeconds(5));
        Stall(progressMs: 0, title: "I Won't Back Down");
        await SettleAsync();

        Assert.Empty(Commands);
    }

    [Fact]
    public async Task APauseMidTrack_IsLeftAlone()
    {
        Stall(progressMs: 90000);
        await SettleAsync();

        Assert.Empty(Commands);
    }

    // Exactly where a stall would look like one, so only the host's own pause distinguishes it.
    [Fact]
    public async Task APauseTheHostAskedFor_IsNeverUndone()
    {
        await _controller.PauseAsync();

        Stall();
        await SettleAsync();

        Assert.Equal(["pause"], Commands);
    }

    [Fact]
    public async Task AStopTheHostAskedFor_IsNeverUndone()
    {
        await _controller.StopAsync();

        Stall();
        await SettleAsync();

        Assert.Equal(["stop"], Commands);
    }

    [Fact]
    public async Task TheHostResumingAgain_ArmsItAgain()
    {
        await _controller.PauseAsync();
        await _controller.ResumeAsync();

        Stall();
        _inner.State = Playing();
        await ConfirmAsync(3);
        await SettleAsync();

        Assert.Equal(["pause", "resume", "resume"], Commands);
    }

    [Fact]
    public async Task TheHostStartingAgain_ArmsItAgain()
    {
        await _controller.StopAsync();
        await _controller.StartAsync(null, shuffle: false);

        Stall();
        _inner.State = Playing();
        await ConfirmAsync(3);
        await SettleAsync();

        Assert.Equal(["stop", "start", "resume"], Commands);
    }

    // Whatever started it (a hand on Spotify's own play button, say), the host's pause is over.
    [Fact]
    public async Task PlaybackSeenAfterTheHostsPause_ArmsItAgain()
    {
        await _controller.PauseAsync();

        _inner.State = Playing();
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        Stall();
        _inner.State = Playing();
        await ConfirmAsync(2);
        await SettleAsync();

        Assert.Equal(["pause", "resume"], Commands);
    }

    // Bounded per window: a fault that returns every track must not be nudged all night.
    [Fact]
    public async Task AClientTooWedgedToRecover_IsNudgedTwiceAMinuteThenLetAlone()
    {
        for (var nudge = 1; nudge <= 2; nudge++)
        {
            Stall();
            await ConfirmAsync(nudge * 2 - 1);
            await ConfirmAsync(nudge * 2);
            await SettleAsync();
        }

        Stall();
        await SettleAsync();

        Assert.Equal(["resume", "skip", "resume", "skip"], Commands);

        // Exactly a minute after the first nudge, that one has left the window and one more is allowed.
        _time.Advance(TimeSpan.FromSeconds(56));
        Stall();
        _inner.State = Playing();
        await ConfirmAsync(5);
        await SettleAsync();

        Assert.Equal(["resume", "skip", "resume", "skip", "resume"], Commands);
    }

    [Fact]
    public async Task SpotifyPlaying_IsNeverAStall()
    {
        _inner.State = new SpotifyState(SpotifyPlayback.Playing, "Free Fallin'", "Tom Petty", 179000, 180000);
        _inner.RaisePlaybackChanged();
        await SettleAsync();

        Assert.Empty(Commands);
    }

    [Fact]
    public void TheBackendsOwnWatch_StillReachesTheSubscriber()
    {
        var raised = 0;
        _controller.PlaybackChanged += (_, _) => raised++;

        _inner.RaisePlaybackChanged();

        Assert.Equal(1, raised);
    }
}

internal sealed class TimerCountingTimeProvider : FakeTimeProvider
{
    private int _timersCreated;

    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timersCreated);
        return timer;
    }
}
