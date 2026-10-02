using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Control;

/// <summary>One of Spotify's audio sessions in the Windows mixer. Disposing releases the COM
/// objects behind it.</summary>
internal interface ISpotifyAudioSession : IDisposable
{
    /// <summary>Stable for the life of the session, so a level found on one fade is matched to
    /// the same session on the next.</summary>
    string Id { get; }

    /// <summary>What Windows remembers this session's level against: the same for the next Spotify
    /// on the same output, where <see cref="Id"/> is not.</summary>
    string LevelKey { get; }

    /// <summary>The mixer's 0 to 1 level for this session alone, independent of the device's.</summary>
    float Volume { get; set; }

    /// <summary>Whether Spotify is still sending this session sound; it goes inactive a beat after a
    /// pause or stop, once its last buffer has gone out.</summary>
    bool IsActive { get; }
}

/// <summary>Spotify's sessions on every active output, found afresh for each fade.</summary>
internal interface ISpotifyAudioSessions
{
    IReadOnlyList<ISpotifyAudioSession> Open();
}

/// <summary>Fades Spotify's own rows in the Windows volume mixer, stepping in-process.</summary>
/// <remarks>Spotify runs several processes and may hold a session on more than one device, so
/// every session is faded and each comes back to its own level.</remarks>
internal sealed class WindowsSpotifyFader : SupersedingFader
{
    private const int Steps = 30;

    private static readonly TimeSpan QuietPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>Measured at ~250ms after a stop. A Spotify that never goes quiet must not leave the
    /// level down for ever, so the wait is capped.</summary>
    private static readonly TimeSpan QuietLimit = TimeSpan.FromSeconds(2);

    /// <summary>How long a just-started Spotify gets to open its audio. Past it the music plays at
    /// whatever level Windows kept, and the next fade puts the saved one back.</summary>
    private static readonly TimeSpan SoundLimit = TimeSpan.FromSeconds(5);

    /// <summary>Loose enough for float noise, tight enough that a host's nudge of the mixer reads as theirs.</summary>
    private const double Tolerance = 0.005;

    private readonly ISpotifyAudioSessions _sessions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, FadeLevel> _levels = new(StringComparer.Ordinal);

    internal WindowsSpotifyFader(
        ILogger logger,
        ISpotifyAudioSessions sessions,
        Func<TimeSpan, CancellationToken, Task> delay,
        IPendingFadeLevels? pending = null)
        : base(logger, pending)
    {
        _sessions = sessions;
        _delay = delay;
    }

    public override bool IsAvailable => true;

    protected override async Task<FadeOutcome> FadeAsync(
        bool silence, TimeSpan duration, CancellationToken cancellationToken)
    {
        IReadOnlyList<ISpotifyAudioSession> sessions;

        try
        {
            sessions = _sessions.Open();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not find Spotify in the Windows volume mixer");
            return FadeOutcome.Failed;
        }

        try
        {
            var ramps = new List<(ISpotifyAudioSession Session, FadeLevel Level, float From, float To)>();
            var adopted = new List<string>();
            IReadOnlyDictionary<string, double>? pending = null;

            foreach (var session in sessions)
            {
                var level = Level(session, ref pending);
                var from = session.Volume;

                if (silence)
                {
                    level.NoteFadeOutFrom(from);
                    ramps.Add((session, level, from, 0f));
                }
                else if (level.Target is not { } target)
                {
                    // A session this end never silenced: nothing of ours to put back.
                }
                else if (!level.IsOurs(from))
                {
                    Logger.LogInformation(
                        "Spotify was set to {Level:P0} in the mixer while silent; keeping that", from);
                    level.AdoptHostLevel(from);
                    adopted.Add(session.LevelKey);
                }
                else
                {
                    ramps.Add((session, level, from, (float)target));
                }
            }

            // No session is a Spotify that has not opened its audio yet, not a fault.
            if (ramps.Count == 0 && adopted.Count == 0)
                return FadeOutcome.NothingToFade;

            // Before the first step: a fade killed part way must still know where it was going.
            if (silence)
            {
                UpdatePending(
                    ramps.Where(ramp => ramp.Level.Target is { } target && PendingFadeLevelFile.IsRestorable(target))
                        .ToDictionary(ramp => ramp.Session.LevelKey, ramp => ramp.Level.Target!.Value),
                    ramps.Where(ramp => ramp.Level.Target is not { } target || !PendingFadeLevelFile.IsRestorable(target))
                        .Select(ramp => ramp.Session.LevelKey));
            }

            try
            {
                await RampAsync(ramps, duration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                foreach (var ramp in ramps)
                    ramp.Level.NoteInterrupted(ramp.To, ramp.From);

                throw;
            }

            foreach (var ramp in ramps)
                ramp.Level.NoteLanded(ramp.Session.Volume);

            // Back where the host left it, or theirs to keep: nothing is owed any more.
            if (!silence)
                UpdatePending(new Dictionary<string, double>(), ramps.Select(ramp => ramp.Session.LevelKey).Concat(adopted));

            return FadeOutcome.Landed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not fade Spotify in the Windows volume mixer");
            return FadeOutcome.Failed;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    protected override async Task WaitForQuietAsync(CancellationToken cancellationToken)
    {
        for (var waited = TimeSpan.Zero; waited < QuietLimit; waited += QuietPoll)
        {
            if (!AnySessionActive())
                return;

            await _delay(QuietPoll, cancellationToken);
        }

        Logger.LogDebug("Spotify still had sound after {Limit}; putting its level back anyway", QuietLimit);
    }

    /// <summary>Waited for only while a level is owed: with nothing saved there is nothing a
    /// session appearing could change.</summary>
    protected override async Task WaitForSoundAsync(CancellationToken cancellationToken)
    {
        if (ReadPending().Count == 0)
            return;

        for (var waited = TimeSpan.Zero; waited < SoundLimit; waited += QuietPoll)
        {
            if (AnySession(_ => true))
                return;

            await _delay(QuietPoll, cancellationToken);
        }

        Logger.LogDebug("Spotify opened no audio within {Limit}; its saved level waits for the next fade", SoundLimit);
    }

    /// <summary>A mixer that cannot be read counts as quiet: the restore behind it reports the fault.</summary>
    private bool AnySessionActive() => AnySession(session => session.IsActive);

    private bool AnySession(Func<ISpotifyAudioSession, bool> match)
    {
        IReadOnlyList<ISpotifyAudioSession> sessions;

        try
        {
            sessions = _sessions.Open();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return sessions.Any(match);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    /// <summary>A session seen for the first time takes a level an earlier fade still owes it over
    /// its own reading, which is that fade's leftovers rather than the host's choice.</summary>
    private FadeLevel Level(ISpotifyAudioSession session, ref IReadOnlyDictionary<string, double>? pending)
    {
        if (_levels.TryGetValue(session.Id, out var level))
            return level;

        _levels[session.Id] = level = new FadeLevel(Tolerance);

        pending ??= ReadPending();

        if (pending.TryGetValue(session.LevelKey, out var saved))
        {
            Logger.LogInformation(
                "Spotify came back at {Reading:0.000} after a fade that never finished; restoring {Saved:0.000}, the level saved before that fade",
                session.Volume, saved);

            level.AwaitRestore(saved, ceiling: 1);
        }

        return level;
    }

    /// <summary>The last write is the target itself, not the last step's arithmetic, so the level
    /// put back is the exact float that was found.</summary>
    private async Task RampAsync(
        List<(ISpotifyAudioSession Session, FadeLevel Level, float From, float To)> ramps,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (ramps.Count == 0)
            return;

        var steps = duration > TimeSpan.Zero ? Steps : 1;
        var pause = duration / steps;

        for (var step = 1; step <= steps; step++)
        {
            if (step > 1)
                await _delay(pause, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (session, _, from, to) in ramps)
                session.Volume = step == steps ? to : from + (to - from) * step / steps;
        }
    }
}
