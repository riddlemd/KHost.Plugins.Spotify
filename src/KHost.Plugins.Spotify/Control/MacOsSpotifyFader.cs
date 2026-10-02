using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Fades Spotify.app's <c>sound volume</c> through AppleScript, one osascript run per fade.</summary>
/// <remarks>A process per step measured nearly five seconds for a short fade; one run doing the
/// whole ramp with <c>delay</c> takes the fade's own length plus about a quarter second.</remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacOsSpotifyFader : SupersedingFader
{
    /// <summary>ProcessRunner cuts every run off at ten seconds, and a fade killed part way leaves
    /// the room at whatever level it had reached.</summary>
    private static readonly TimeSpan LongestFade = TimeSpan.FromSeconds(8);

    /// <summary>Spotify.app has one level, so the record holds one.</summary>
    internal const string LevelKey = "spotify";

    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> _run;

    /// <summary>Spotify's own 0 to 100 integer scale, so a level is ours only if it reads back exactly.</summary>
    private readonly FadeLevel _level = new(tolerance: 0);

    private bool _attached;

    public MacOsSpotifyFader(ILogger logger, IPendingFadeLevels? pending = null)
        : this(logger, (file, arguments, token) => ProcessRunner.RunAsync(file, arguments, token), pending)
    {
    }

    internal MacOsSpotifyFader(
        ILogger logger,
        Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run,
        IPendingFadeLevels? pending = null)
        : base(logger, pending)
    {
        _run = run;
    }

    public override bool IsAvailable => true;

    protected override async Task<FadeOutcome> FadeAsync(
        bool silence, TimeSpan duration, CancellationToken cancellationToken)
    {
        int target;
        (int Low, int High)? onlyFrom = null;

        Attach();

        if (silence)
        {
            target = 0;

            // The level is only read inside the fade's own run, so what is known now is saved
            // first and corrected once the run reports where it started.
            if (_level.Target is { } known)
                SavePending(known);
        }
        else
        {
            if (_level.Target is not { } restoreTo || _level.Written is not { } written)
                return FadeOutcome.NothingToFade;

            target = (int)restoreTo;
            onlyFrom = ((int)written.Low, (int)written.High);
        }

        var script = MacOsScripts.Fade(target, duration > LongestFade ? LongestFade : duration, onlyFrom);

        ProcessResult result;

        try
        {
            result = await _run("osascript", ["-e", script], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run was killed part way, so the level is somewhere between the two ends.
            _level.NoteInterrupted(target);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not fade Spotify");
            return FadeOutcome.Failed;
        }

        if (!result.Succeeded)
        {
            Logger.LogWarning("Spotify refused a fade: {Message}", result.Message);
            return FadeOutcome.Failed;
        }

        if (!MacOsScripts.ReachedSpotify(result.StandardOutput))
            return FadeOutcome.NothingToFade;

        if (MacOsScripts.ParseFade(result.StandardOutput) is not { } levels)
        {
            Logger.LogWarning("Could not read the fade's result: {Output}", result.StandardOutput.Trim());
            return FadeOutcome.Failed;
        }

        if (silence)
        {
            _level.NoteFadeOutFrom(levels.Start);
            SavePending(_level.Target ?? 0);
        }
        else if (!_level.IsOurs(levels.Start))
        {
            Logger.LogInformation(
                "Spotify was set to {Level} while silent; keeping that rather than restoring {Target}",
                levels.Start, target);

            _level.AdoptHostLevel(levels.Start);
            UpdatePending(new Dictionary<string, double>(), [LevelKey]);
            return FadeOutcome.Landed;
        }

        _level.NoteLanded(levels.Final);

        if (!silence)
            UpdatePending(new Dictionary<string, double>(), [LevelKey]);

        return FadeOutcome.Landed;
    }

    /// <summary>The first fade in this process takes a level an earlier one still owes Spotify,
    /// which keeps its own volume across a restart, over whatever it now reads.</summary>
    private void Attach()
    {
        if (_attached)
            return;

        _attached = true;

        if (ReadPending().TryGetValue(LevelKey, out var saved))
        {
            var level = (int)Math.Round(saved * 100);

            Logger.LogInformation(
                "Restoring Spotify to {Saved}, the level saved before a fade that never finished", level);

            _level.AwaitRestore(level, ceiling: 100);
        }
    }

    /// <summary>On Spotify's 0 to 100 scale; a 0 is no level to come back to, and drops the record.</summary>
    private void SavePending(double level)
    {
        if (PendingFadeLevelFile.IsRestorable(level / 100))
            UpdatePending(new Dictionary<string, double> { [LevelKey] = level / 100 }, []);
        else
            UpdatePending(new Dictionary<string, double>(), [LevelKey]);
    }
}
