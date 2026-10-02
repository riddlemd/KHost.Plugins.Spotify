using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Control;

/// <summary>One of Spotify's audio sessions in the Windows mixer. Disposing releases the COM
/// objects behind it.</summary>
internal interface ISpotifyAudioSession : IDisposable
{
    /// <summary>Stable for the life of the session, so a level found on one fade is matched to
    /// the same session on the next.</summary>
    string Id { get; }

    /// <summary>The mixer's 0 to 1 level for this session alone, independent of the device's.</summary>
    float Volume { get; set; }
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

    /// <summary>Loose enough for float noise, tight enough that a host's nudge of the mixer reads as theirs.</summary>
    private const double Tolerance = 0.005;

    private readonly ILogger _logger;
    private readonly ISpotifyAudioSessions _sessions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, FadeLevel> _levels = new(StringComparer.Ordinal);

    internal WindowsSpotifyFader(
        ILogger logger, ISpotifyAudioSessions sessions, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
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
            _logger.LogWarning(ex, "Could not find Spotify in the Windows volume mixer");
            return FadeOutcome.Failed;
        }

        try
        {
            var ramps = new List<(ISpotifyAudioSession Session, FadeLevel Level, float From, float To)>();
            var adopted = false;

            foreach (var session in sessions)
            {
                var level = Level(session.Id);
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
                    _logger.LogInformation(
                        "Spotify was set to {Level:P0} in the mixer while silent; keeping that", from);
                    level.AdoptHostLevel(from);
                    adopted = true;
                }
                else
                {
                    ramps.Add((session, level, from, (float)target));
                }
            }

            // No session is a Spotify that has not opened its audio yet, not a fault.
            if (ramps.Count == 0 && !adopted)
                return FadeOutcome.NothingToFade;

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

            return FadeOutcome.Landed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not fade Spotify in the Windows volume mixer");
            return FadeOutcome.Failed;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    private FadeLevel Level(string id)
    {
        if (!_levels.TryGetValue(id, out var level))
            _levels[id] = level = new FadeLevel(Tolerance);

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
