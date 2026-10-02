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

    private readonly ILogger _logger;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> _run;

    /// <summary>Spotify's own 0 to 100 integer scale, so a level is ours only if it reads back exactly.</summary>
    private readonly FadeLevel _level = new(tolerance: 0);

    public MacOsSpotifyFader(ILogger logger)
        : this(logger, (file, arguments, token) => ProcessRunner.RunAsync(file, arguments, token))
    {
    }

    internal MacOsSpotifyFader(
        ILogger logger, Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
    {
        _logger = logger;
        _run = run;
    }

    public override bool IsAvailable => true;

    protected override async Task<FadeOutcome> FadeAsync(
        bool silence, TimeSpan duration, CancellationToken cancellationToken)
    {
        int target;
        (int Low, int High)? onlyFrom = null;

        if (silence)
        {
            target = 0;
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
            _logger.LogWarning(ex, "Could not fade Spotify");
            return FadeOutcome.Failed;
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning("Spotify refused a fade: {Message}", result.Message);
            return FadeOutcome.Failed;
        }

        if (!MacOsScripts.ReachedSpotify(result.StandardOutput))
            return FadeOutcome.NothingToFade;

        if (MacOsScripts.ParseFade(result.StandardOutput) is not { } levels)
        {
            _logger.LogWarning("Could not read the fade's result: {Output}", result.StandardOutput.Trim());
            return FadeOutcome.Failed;
        }

        if (silence)
        {
            _level.NoteFadeOutFrom(levels.Start);
        }
        else if (!_level.IsOurs(levels.Start))
        {
            _logger.LogInformation(
                "Spotify was set to {Level} while silent; keeping that rather than restoring {Target}",
                levels.Start, target);

            _level.AdoptHostLevel(levels.Start);
            return FadeOutcome.Landed;
        }

        _level.NoteLanded(levels.Final);

        return FadeOutcome.Landed;
    }
}
