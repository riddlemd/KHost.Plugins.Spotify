using System.Collections.Concurrent;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// macOS offers this process nothing to subscribe to, so the only way it learns a track turned
/// over is by asking on a clock. This event used to discard its subscribers outright — a track
/// change reached nothing, and the console's panel and the screen's card both sat on whatever had
/// been playing when the host last pressed something. It read as a host bug for as long as it
/// existed.
/// </summary>
public class MacOsSpotifyWatchTests
{
    private readonly ConcurrentQueue<string> _replies = new();
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _raised;
    private int _asks;

    /// <summary>One line of what osascript prints: state, then the track, tab separated.</summary>
    private static string Line(string state, string title, string artist = "Tom Petty")
        => $"{state}\t{title}\t{artist}";

    private MacOsSpotifyController Controller()
        => new(NullLogger.Instance, launchIfNotRunning: false,
            run: (_, _, _) =>
            {
                Interlocked.Increment(ref _asks);
                _asked.TrySetResult();

                // The last reply stands once a test stops scripting them, so the loop settles
                // instead of running off the end of what it was given.
                var reply = _replies.TryDequeue(out var next) ? next : _lastReply;
                _lastReply = reply;

                return Task.FromResult(new ProcessResult(0, reply, string.Empty));
            },
            delay: async (_, token) =>
            {
                var inFlight = Interlocked.Increment(ref _waiting);
                InterlockedMax(ref _mostWaitingAtOnce, inFlight);

                try
                {
                    await Task.Delay(1, token);
                }
                finally
                {
                    Interlocked.Decrement(ref _waiting);
                }
            });

    private int _waiting;
    private int _mostWaitingAtOnce;

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                return;
    }

    private string _lastReply = Line("stopped", "");

    private void Script(params string[] replies)
    {
        foreach (var reply in replies)
            _replies.Enqueue(reply);
    }

    private async Task WaitAsync(Func<bool> settled, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (settled())
                return;

            await Task.Delay(5);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// The reported bug: break music moved to the next song and nothing was told.
    /// </summary>
    /// <remarks>
    /// Counted exactly, not "at least once", and the new track is left playing afterwards. One
    /// change is one announcement: a watch that forgets what it last saw announces the same track
    /// on every poll for as long as it plays, and a watch whose comparison is the wrong way round
    /// announces the polls where nothing happened. Both pass an "at least once" assertion, and
    /// both were live until this counted.
    /// </remarks>
    [Fact]
    public async Task ATrackTurningOver_IsAnnouncedOnceAndNotAgainWhileItPlays()
    {
        Script(
            Line("playing", "Where Is My Mind?"),
            Line("playing", "Where Is My Mind?"),
            Line("playing", "Free Fallin'"));

        using var controller = Controller();
        controller.PlaybackChanged += (_, _) => Interlocked.Increment(ref _raised);

        await controller.StartWatchingAsync();

        await WaitAsync(() => Volatile.Read(ref _raised) >= 1, "a track change to be announced");

        // The last scripted reply stands, so the new track keeps playing from here.
        var asksWhenAnnounced = Volatile.Read(ref _asks);
        await WaitAsync(() => Volatile.Read(ref _asks) >= asksWhenAnnounced + 5, "several more polls");

        Assert.Equal(1, Volatile.Read(ref _raised));
    }

    /// <summary>
    /// The host reaching for Spotify's own window is the other half of what this watch is for, and
    /// the track name does not move when they do.
    /// </summary>
    [Fact]
    public async Task PausingInSpotifysOwnWindow_RaisesPlaybackChanged()
    {
        Script(
            Line("playing", "Where Is My Mind?"),
            Line("paused", "Where Is My Mind?"));

        using var controller = Controller();
        controller.PlaybackChanged += (_, _) => Interlocked.Increment(ref _raised);

        await controller.StartWatchingAsync();

        await WaitAsync(() => Volatile.Read(ref _raised) >= 1, "a pause to be announced");
    }

    /// <summary>
    /// Nothing moved, so nothing is announced. Raising every poll would have the host re-read
    /// Spotify and redraw two screens several times a minute for a track sitting still.
    /// </summary>
    [Fact]
    public async Task NothingMoving_AnnouncesNothingHoweverOftenItAsks()
    {
        Script(Line("playing", "Where Is My Mind?"));

        using var controller = Controller();
        controller.PlaybackChanged += (_, _) => Interlocked.Increment(ref _raised);

        await controller.StartWatchingAsync();
        await WaitAsync(() => Volatile.Read(ref _asks) >= 5, "the watch to have asked a few times");

        Assert.Equal(0, Volatile.Read(ref _raised));
    }

    /// <summary>
    /// The provider has already read Spotify by the time this starts, so the first sample is a
    /// baseline. Announcing it would republish what the host just read.
    /// </summary>
    [Fact]
    public async Task TheFirstReading_IsABaselineRatherThanNews()
    {
        Script(Line("playing", "Where Is My Mind?"));

        using var controller = Controller();
        controller.PlaybackChanged += (_, _) => Interlocked.Increment(ref _raised);

        await controller.StartWatchingAsync();
        await WaitAsync(() => Volatile.Read(ref _asks) >= 3, "the watch to be running");

        Assert.Equal(0, Volatile.Read(ref _raised));
    }

    /// <summary>Starting twice must not put two loops on Spotify.</summary>
    [Fact]
    public async Task StartingTheWatchTwice_AsksOnOneClock()
    {
        Script(Line("playing", "Where Is My Mind?"));

        using var controller = Controller();

        await controller.StartWatchingAsync();
        await controller.StartWatchingAsync();

        await WaitAsync(() => Volatile.Read(ref _asks) >= 4, "the watch to be running");

        // A second loop would have its own wait in flight alongside the first. Counting overlaps
        // rather than asks, because a rate is a race and this is not.
        Assert.Equal(1, Volatile.Read(ref _mostWaitingAtOnce));
    }

    /// <summary>Disposing stops asking: a controller left polling outlives the venue it was for.</summary>
    [Fact]
    public async Task Disposed_StopsAsking()
    {
        Script(Line("playing", "Where Is My Mind?"));

        var controller = Controller();
        await controller.StartWatchingAsync();
        await WaitAsync(() => Volatile.Read(ref _asks) >= 3, "the watch to be running");

        controller.Dispose();
        await Task.Delay(40);

        var settled = Volatile.Read(ref _asks);
        await Task.Delay(60);

        Assert.Equal(settled, Volatile.Read(ref _asks));
    }
}
