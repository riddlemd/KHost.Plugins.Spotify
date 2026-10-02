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

    private readonly FakePendingFadeLevels _pending = new();
    private readonly ListLogger _log = new();

    private Func<TimeSpan, CancellationToken, Task>? _delay;
    private bool _openThrows;

    private WindowsSpotifyFader Build(IPendingFadeLevels? pending = null) => new(
        _log,
        new FakeSessions(this),
        (pause, token) =>
        {
            _waits.Add(pause);
            return _delay?.Invoke(pause, token) ?? Task.CompletedTask;
        },
        pending ?? _pending);

    private FakeSession Session(string id, float volume, string? levelKey = null)
    {
        var session = new FakeSession(id, volume, levelKey);
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

    // ── the level owed, kept across a Spotify or a KHost restart ────────────────────────

    // Windows keeps whatever level a killed fade reached, so where it was going is saved before
    // anything is lowered.
    [Fact]
    public async Task SilenceAsync_SavesTheLevelBeforeTheFirstStep()
    {
        var spotify = Session("a", 0.49f, "spotify@speakers");
        var writesBeforeSave = -1;
        _pending.OnWrite = () => writesBeforeSave = spotify.Writes.Count;

        await Build().SilenceAsync(Fade);

        Assert.Equal(0, writesBeforeSave);
        Assert.Equal(0.49f, (float)_pending.Levels["spotify@speakers"]);
    }

    [Fact]
    public async Task RestoreAsync_ClearsTheRecordOnceTheLevelIsBack()
    {
        Session("a", 0.49f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Empty(_pending.Levels);
    }

    [Fact]
    public async Task RestoreOnceQuietAsync_ClearsTheRecordOnceTheLevelIsBack()
    {
        Session("a", 0.49f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.RestoreOnceQuietAsync();

        Assert.Empty(_pending.Levels);
    }

    // A restore a newer fade cut short has not put anything back.
    [Fact]
    public async Task ARestoreCancelledPartWay_KeepsTheRecord()
    {
        Session("a", 0.49f);
        var fader = Build();
        using var cancel = new CancellationTokenSource();

        await fader.SilenceAsync(Fade);
        _delay = (_, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fader.RestoreAsync(Fade, cancel.Token));

        Assert.Equal(0.49f, (float)_pending.Levels["a"]);
    }

    // Spotify killed mid-fade comes back as a new session at whatever level Windows kept.
    [Fact]
    public async Task ANewSessionWithALevelOwed_ComesBackToItNotToWhatItReads()
    {
        var first = Session("a#1", 0.37f, "spotify@speakers");
        var fader = Build();
        await fader.SilenceAsync(Fade);

        _sessions.Remove(first);
        var relaunched = Session("a#2", 0.12f, "spotify@speakers");

        Assert.Equal(FadeOutcome.Landed, await fader.RestoreOnceHeardAsync(Fade));

        Assert.Equal(0.37f, relaunched.Volume);
        Assert.Empty(_pending.Levels);
        Assert.Contains(_log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && entry.Message.Contains("0.120") && entry.Message.Contains("0.370"));
    }

    // A KHost restart is a new fader over the same record.
    [Fact]
    public async Task AFreshFaderWithALevelOwed_FadesOutAndComesBackToIt()
    {
        _pending.Levels["spotify@speakers"] = 0.37;
        var spotify = Session("a", 0f, "spotify@speakers");
        var fader = Build();

        await fader.SilenceAsync(TimeSpan.Zero);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.37f, spotify.Volume);
        Assert.Empty(_pending.Levels);
    }

    [Fact]
    public async Task AFreshFaderWithNothingOwed_AdoptsTheLevelItReads()
    {
        var spotify = Session("a", 0.6f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.6f, spotify.Volume);
        Assert.Empty(_pending.Levels);
    }

    // The host's slider move while paused was kept and the record cleared, so a restart after it
    // must not drag Spotify back to the old level.
    [Fact]
    public async Task ARecordClearedByAHostsSliderMove_DoesNotOverrideItAfterARestart()
    {
        var spotify = Session("a", 0.37f);
        var fader = Build();
        await fader.SilenceAsync(Fade);
        spotify.Volume = 0.55f;
        await fader.RestoreAsync(Fade);

        var restarted = Build();
        await restarted.SilenceAsync(Fade);
        await restarted.RestoreAsync(Fade);

        Assert.Equal(0.55f, spotify.Volume);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.2)]
    [InlineData(1.5)]
    public async Task ARecordOutOfRange_IsIgnored(double saved)
    {
        _pending.Levels["a"] = saved;
        var spotify = Session("a", 0.6f);
        var fader = Build();

        await fader.SilenceAsync(Fade);
        await fader.RestoreAsync(Fade);

        Assert.Equal(0.6f, spotify.Volume);
    }

    [Fact]
    public async Task ARecordThatCannotBeWritten_CostsNothingButTheRecord()
    {
        _pending.WriteThrows = true;
        var spotify = Session("a", 0.49f);
        var fader = Build();

        Assert.Equal(FadeOutcome.Landed, await fader.SilenceAsync(Fade));
        Assert.Equal(0f, spotify.Volume);
        Assert.Equal(FadeOutcome.Landed, await fader.RestoreAsync(Fade));
        Assert.Equal(0.49f, spotify.Volume);
        Assert.Contains(_log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Message.Contains("Could not save"));
    }

    // A just-launched Spotify opens its audio a beat after it starts playing.
    [Fact]
    public async Task RestoreOnceHeardAsync_WithALevelOwed_WaitsForSpotifysAudio()
    {
        _pending.Levels["a"] = 0.37;
        var fader = Build();
        FakeSession? spotify = null;

        _delay = (_, _) =>
        {
            if (_waits.Count == 3)
                spotify = Session("a", 0f);

            return Task.CompletedTask;
        };

        Assert.Equal(FadeOutcome.Landed, await fader.RestoreOnceHeardAsync(TimeSpan.Zero));

        Assert.Equal(3, _waits.Count);
        Assert.Equal(0.37f, spotify!.Volume);
    }

    [Fact]
    public async Task RestoreOnceHeardAsync_WithNothingOwed_DoesNotWait()
    {
        Assert.Equal(FadeOutcome.NothingToFade, await Build().RestoreOnceHeardAsync(Fade));
        Assert.Empty(_waits);
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

    private sealed class FakeSession(string id, float volume, string? levelKey = null) : ISpotifyAudioSession
    {
        public string LevelKey { get; } = levelKey ?? id;

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
