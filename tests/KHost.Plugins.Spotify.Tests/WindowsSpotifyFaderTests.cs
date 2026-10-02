using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The ramp, adopt and restore rules, over stand-in mixer sessions: Core Audio itself is
/// only reachable on Windows.</summary>
public class WindowsSpotifyFaderTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(1500);

    private readonly List<FakeSession> _sessions = [];
    private readonly List<TimeSpan> _waits = [];

    private Func<TimeSpan, CancellationToken, Task>? _delay;
    private bool _openThrows;

    private WindowsSpotifyFader Build() => new(
        NullLogger.Instance,
        new FakeSessions(this),
        (pause, token) =>
        {
            _waits.Add(pause);
            return _delay?.Invoke(pause, token) ?? Task.CompletedTask;
        });

    private FakeSession Session(string id, float volume)
    {
        var session = new FakeSession(id, volume);
        _sessions.Add(session);
        return session;
    }

    [Fact]
    public async Task SilenceAsync_RampsDownInStepsOverTheFade()
    {
        // 0.49f is one a step's float arithmetic misses, so only an exact last write lands on 0.
        var spotify = Session("a", 0.49f);

        Assert.Equal(FadeOutcome.Landed, await Build().SilenceAsync(Fade));

        Assert.Equal(30, spotify.Writes.Count);
        Assert.Equal(0f, spotify.Writes[^1]);
        Assert.True(spotify.Writes.SequenceEqual(spotify.Writes.OrderByDescending(level => level)), "the ramp went up part way");
        Assert.Equal(29, _waits.Count);
        Assert.Equal(Fade / 30, _waits[0]);
    }

    [Fact]
    public async Task SilenceAsync_WithNoLength_IsOneWriteAndNoWait()
    {
        var spotify = Session("a", 0.6f);

        await Build().SilenceAsync(TimeSpan.Zero);

        Assert.Equal([0f], spotify.Writes);
        Assert.Empty(_waits);
    }

    // The exact float found, not a step's arithmetic landing near it.
    [Fact]
    public async Task RestoreAsync_ComesBackToExactlyTheLevelFound()
    {
        var spotify = Session("a", 0.49f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        Assert.Equal(FadeOutcome.Landed, await fader.RestoreAsync(Fade));

        Assert.Equal(0.49f, spotify.Volume);
        Assert.True(spotify.Writes.Skip(30).SequenceEqual(spotify.Writes.Skip(30).OrderBy(level => level)), "the ramp went down part way");
    }

    // Spotify can hold a session on more than one output, each at its own level.
    [Fact]
    public async Task EverySpotifySession_ComesBackToItsOwnLevel()
    {
        var speakers = Session("speakers", 0.8f);
        var headphones = Session("headphones", 0.3f);
        var fader = Build();

        await fader.SilenceAsync(Fade);

        Assert.Equal(0f, speakers.Volume);
        Assert.Equal(0f, headphones.Volume);

        await fader.RestoreAsync(Fade);

        Assert.Equal(0.8f, speakers.Volume);
        Assert.Equal(0.3f, headphones.Volume);
    }

    // Still silent from the last fade out: a second one reads 0, which is ours, not the room's.
    [Fact]
    public async Task ASecondSilence_DoesNotTakeSilenceAsTheLevelToComeBackTo()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.5f, spotify.Volume);
    }

    [Fact]
    public async Task AHostWhoMovedTheMixerWhilePlaying_IsFollowed()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        spotify.Volume = 0.65f;

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.65f, spotify.Volume);
    }

    [Fact]
    public async Task AHostWhoMovedTheMixerWhileSilent_IsLeftAlone()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();

        await fader.SilenceAsync(Fade);

        spotify.Volume = 0.8f;
        spotify.Writes.Clear();
        _waits.Clear();

        Assert.Equal(FadeOutcome.Landed, await fader.RestoreAsync(Fade));
        Assert.Empty(spotify.Writes);
        Assert.Empty(_waits);

        // And it is the level every later fade comes back to.
        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.8f, spotify.Volume);
    }

    // A ramp cut off part way leaves a level between its ends. That is still ours: adopting it
    // would bring every later fade back to half volume.
    [Fact]
    public async Task AFadeCancelledPartWay_IsNotTakenForTheHostsLevel()
    {
        var spotify = Session("a", 0.6f);
        var fader = Build();
        using var cancel = new CancellationTokenSource();

        _delay = (_, token) =>
        {
            if (_waits.Count == 15)
                cancel.Cancel();

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fader.SilenceAsync(Fade, cancel.Token));
        _delay = null;

        Assert.InRange(spotify.Volume, 0.01f, 0.59f);

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.6f, spotify.Volume);
    }

    // A Spotify that has not opened its audio yet is not a fault worth telling anyone about.
    [Fact]
    public async Task NoSpotifySession_HasNothingToFade()
        => Assert.Equal(FadeOutcome.NothingToFade, await Build().SilenceAsync(Fade));

    [Fact]
    public async Task RestoreAsync_WithNothingSilenced_WritesNothing()
    {
        var spotify = Session("a", 0.5f);

        Assert.Equal(FadeOutcome.NothingToFade, await Build().RestoreAsync(Fade));
        Assert.Empty(spotify.Writes);
    }

    [Fact]
    public async Task AMixerThatCannotBeRead_HasFailed()
    {
        _openThrows = true;

        Assert.Equal(FadeOutcome.Failed, await Build().SilenceAsync(Fade));
    }

    [Fact]
    public async Task EverySessionOpened_IsReleasedAfterTheFade()
    {
        var spotify = Session("a", 0.5f);

        await Build().SilenceAsync(Fade);

        Assert.Equal(1, spotify.Disposed);
    }

    // Spotify's last buffer still plays after a stop; put back at full level it is a burst.
    [Fact]
    public async Task RestoreOnceQuietAsync_WaitsForSpotifyToGoQuietThenPutsTheLevelBackAtOnce()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        spotify.Writes.Clear();
        _waits.Clear();
        spotify.ActiveReads = 3;

        Assert.Equal(FadeOutcome.Landed, await fader.RestoreOnceQuietAsync());

        Assert.Equal([0.5f], spotify.Writes);
        Assert.False(spotify.WroteWhileActive);
        Assert.Equal(3, _waits.Count);
    }

    [Fact]
    public async Task RestoreOnceQuietAsync_ASpotifyThatNeverGoesQuiet_IsPutBackAfterTwoSeconds()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        _waits.Clear();
        spotify.ActiveForever = true;

        Assert.Equal(FadeOutcome.Landed, await fader.RestoreOnceQuietAsync());

        Assert.Equal(0.5f, spotify.Volume);
        Assert.Equal(TimeSpan.FromSeconds(2), _waits.Aggregate(TimeSpan.Zero, (total, wait) => total + wait));
    }

    // The wait sits inside the gate, so a fade asked for meanwhile cuts it short, and the level
    // it was waiting to put back never lands over the newer fade.
    [Fact]
    public async Task RestoreOnceQuietAsync_ANewerFadeDuringTheWait_SupersedesIt()
    {
        var spotify = Session("a", 0.5f);
        var fader = Build();
        var waiting = new TaskCompletionSource();

        await fader.SilenceAsync(Fade);
        spotify.ActiveForever = true;

        _delay = (_, token) =>
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        };

        var restore = fader.RestoreOnceQuietAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _delay = null;

        // Bounded, since a wait the newer fade cannot cut short holds the gate for good.
        await fader.SilenceAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FadeOutcome.Superseded, await restore.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0f, spotify.Volume);
    }

    private sealed class FakeSessions(WindowsSpotifyFaderTests test) : ISpotifyAudioSessions
    {
        public IReadOnlyList<ISpotifyAudioSession> Open()
        {
            if (test._openThrows)
                throw new InvalidOperationException("Core Audio is not there");

            return test._sessions.ToList();
        }
    }

    private sealed class FakeSession(string id, float volume) : ISpotifyAudioSession
    {
        private float _volume = volume;

        public List<float> Writes { get; } = [];

        public int Disposed { get; private set; }

        /// <summary>How many more reads of <see cref="IsActive"/> answer true.</summary>
        public int ActiveReads { get; set; }

        public bool ActiveForever { get; set; }

        public bool WroteWhileActive { get; private set; }

        public string Id { get; } = id;

        public float Volume
        {
            get => _volume;
            set
            {
                Writes.Add(value);
                WroteWhileActive |= ActiveForever || ActiveReads > 0;
                _volume = value;
            }
        }

        public bool IsActive
        {
            get
            {
                if (ActiveForever)
                    return true;

                if (ActiveReads == 0)
                    return false;

                ActiveReads--;
                return true;
            }
        }

        public void Dispose() => Disposed++;
    }
}
