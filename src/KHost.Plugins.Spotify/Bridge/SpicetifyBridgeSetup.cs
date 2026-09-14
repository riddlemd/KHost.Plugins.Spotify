using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// Gets the bridge extension into the Spotify on this machine, and says so when it will not go.
/// </summary>
/// <remarks>
/// A type of its own rather than a corner of the break music provider, because the whole of it
/// turns on one rule — <b>applying restarts Spotify, so it only ever happens while the room is not
/// listening</b> — and a rule whose breach costs the room its music has to be assertable without a
/// Spotify to break. Every collaborator is a delegate for that reason.
///
/// The check that guards installing asks whether the file is on disk and registered in Spicetify's
/// config. Neither is the same question as whether Spotify is patched: an update reverts the patch
/// and leaves both true, so the installer decides there is nothing to do and the extension never
/// attaches again. <see cref="SpicetifyInstallation.IsSpotifyPatched"/> is the question that
/// matters, and it reads Spotify's own resources rather than Spicetify's config — so it answers
/// whether or not Spotify is running.
///
/// The extension connecting is then the confirmation, not the diagnosis. It cannot be the
/// diagnosis: nothing attaches to a Spotify that is not running, and the console starting before
/// Spotify is the normal way round.
/// </remarks>
internal sealed class SpicetifyBridgeSetup
{
    /// <summary>
    /// How long the extension is given to connect before its absence is taken as an answer.
    /// Generous: applying restarts Spotify, and Spotify takes its time coming back.
    /// </summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The grace has to outlast the extension's own wait, or it asks for a verdict the extension
    /// has not reached and falls through to blaming the patch — which is the wrong answer and the
    /// one that restarts Spotify to prove it.
    /// </summary>
    internal static bool GracePeriodOutlastsTheExtensionsWait
        => GracePeriod.TotalMilliseconds > SpicetifyDiagnosis.VerdictAfterMilliseconds;

    /// <summary>
    /// Long enough that a Spotify restarting on its own is not mistaken for one that has gone.
    /// </summary>
    internal static readonly TimeSpan LossPollInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly IPluginContext _context;
    private readonly Func<bool> _isExtensionReady;
    private readonly Func<SpicetifyDiagnosis?> _diagnose;
    private readonly Func<Task<bool>> _isSpotifyPlaying;
    private readonly Func<SpicetifyInstallation?> _discover;
    private readonly Func<SpicetifyInstallation, bool, Task<SpicetifyInstallOutcome>> _install;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal SpicetifyBridgeSetup(
        ILogger logger,
        IPluginContext context,
        Func<bool> isExtensionReady,
        Func<SpicetifyDiagnosis?> diagnose,
        Func<Task<bool>> isSpotifyPlaying,
        Func<SpicetifyInstallation?> discover,
        Func<SpicetifyInstallation, bool, Task<SpicetifyInstallOutcome>> install,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _context = context;
        _isExtensionReady = isExtensionReady;
        _diagnose = diagnose;
        _isSpotifyPlaying = isSpotifyPlaying;
        _discover = discover;
        _install = install;
        _delay = delay;
    }

    /// <summary>The one wired to the real disk, the real Spicetify CLI and the real clock.</summary>
    public static SpicetifyBridgeSetup ForThisMachine(
        ILogger logger,
        IPluginContext context,
        SpicetifyBridge bridge,
        Func<Task<bool>> isSpotifyPlaying,
        string cliPath,
        string shippedExtensionPath)
        => new(
            logger,
            context,
            () => bridge.IsReady,
            () => bridge.LastDiagnosis,
            isSpotifyPlaying,
            SpicetifyInstallation.Discover,
            (installation, force) => new SpicetifyExtensionInstaller(logger)
                .EnsureInstalledAsync(installation, shippedExtensionPath, cliPath, force),
            Task.Delay);

    /// <summary>
    /// Runs the whole of the way up: patch if it needs patching, then confirm it took, then hand
    /// over to the watch for the rest of the shift. Never throws — the bridge only ever added
    /// fading on top of a backend that works without it, so anything wrong here costs a fade.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var applied = false;

            // Everything that patches is behind this one question, asked before the grace rather
            // than after it. The console comes up before Spotify does, so waiting first would
            // spend the grace watching for an extension that has nothing to attach to — and then
            // read its absence as a Spotify needing to be patched.
            //
            // That is the bug this ordering exists for. Break music started during the grace, out
            // of a Spotify the console had just launched; the extension had had no chance to
            // attach, and applying killed the track the room was listening to.
            if (await IsPlayingAsync())
            {
                // Installing applies too, when the shipped extension is newer than the one on
                // disk. A bridge a version behind still fades; a restart mid-song does not.
                _logger.LogInformation(
                    "Spotify is already playing, so nothing here patches it — that would restart Spotify");
            }
            else
            {
                await InstallAsync();

                // Discovered after installing, which is what creates the directory on a machine
                // whose Spicetify has never carried an extension.
                if (_discover() is { } installation && !installation.IsSpotifyPatched())
                {
                    if (await ApplyAsync(installation) is SpicetifyInstallOutcome.Failed)
                        return;

                    applied = true;
                }
            }

            await _delay(GracePeriod, cancellationToken);

            if (_isExtensionReady())
            {
                // It attached and can drive Spotify, so anything that goes from here is a live
                // problem rather than a setup one, and is only ever reported.
                WatchForLoss(cancellationToken);
                return;
            }

            // Attached and unable to work is not a patching problem at all, and the extension has
            // already said so over the socket. Reported as it described itself, because the host
            // cannot see inside Spotify and every guess it makes from out here is one somebody
            // then has to disprove.
            if (_diagnose() is { Ready: false, IsVerdict: true } diagnosis)
            {
                ReportTheExtensionCannotRun(diagnosis);
                return;
            }

            // Nothing attached at all, and the disk says which of two quite different things that
            // is. Patching cannot fix a patch that is already there, and reading the silence as a
            // missing one sends the next person to the wrong half of the search.
            if (applied || _discover()?.IsSpotifyPatched() == true)
            {
                ReportTheExtensionCannotRun(applied);
                return;
            }

            // The same rule the watch keeps for the rest of the shift: from here it only ever says
            // so. Restarting Spotify to recover a fade is a worse outcome than a missing fade.
            if (await IsPlayingAsync())
            {
                _context.ReportWarning(
                    "Break music will not fade: Spotify is not patched with the KHost bridge, and "
                    + "patching it restarts Spotify, which would stop what is playing now. Close "
                    + "Spotify and restart KHost to have it patched. Break music still plays, at "
                    + "full level throughout.");

                return;
            }

            _logger.LogInformation(
                "The Spicetify extension has not attached, so Spotify is not patched with it — applying again");

            if (_discover() is not { } discovered)
                return;

            if (await ApplyAsync(discovered) is SpicetifyInstallOutcome.Failed)
                return;

            // Given the grace again: applying restarts Spotify, and the extension cannot connect
            // until it is back up.
            await _delay(GracePeriod, cancellationToken);

            if (!_isExtensionReady())
            {
                ReportTheExtensionCannotRun(applied: true);
                return;
            }

            WatchForLoss(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Nothing here is worth a word on the way out.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not settle the Spicetify bridge");
        }
    }

    /// <summary>
    /// Says so when the extension goes and stays gone. Never re-applies: a Spotify update during a
    /// shift is exactly when this fires, and restarting Spotify to fix a fade would take the room's
    /// music with it.
    /// </summary>
    /// <remarks>
    /// Waits before speaking, because a detach is also what a Spotify restart looks like from here
    /// and one that comes back is not worth a word. Said once per loss rather than once per poll.
    /// </remarks>
    internal Task WatchForLoss(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        bool reported = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(LossPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isExtensionReady())
            {
                reported = false;
                continue;
            }

            if (reported)
                continue;

            reported = true;

            _logger.LogWarning("The Spicetify extension has detached and not come back");

            _context.ReportWarning(
                "Spotify is no longer patched with the KHost bridge — usually a Spotify update "
                + "undoing it. Break music still plays, but it starts and stops at full level "
                + "instead of fading. Restarting KHost puts it back.");
        }
    });

    /// <summary>
    /// The extension is inside Spotify and still nothing attached, which patching cannot fix —
    /// applying again writes the same file to the same place and restarts Spotify for nothing.
    /// </summary>
    /// <remarks>
    /// What is left is the extension being unable to run. Spicetify's own API never comes up on a
    /// Spotify newer than the Spicetify that patched it: <c>Spicetify.Platform</c> stays an empty
    /// object — measured at a hundred seconds on the machine this was found on — so every
    /// extension sits in the wait its first line does, having loaded perfectly well. From out here
    /// that is indistinguishable from an unpatched Spotify, which is why the disk is asked.
    /// </remarks>
    private void ReportTheExtensionCannotRun(bool applied) => _context.ReportWarning(
        (applied
            ? "Break music will not fade: KHost patched Spotify with the KHost bridge and the "
            : "Break music will not fade: Spotify carries the KHost bridge but the ")
        + "extension never attached. That is usually a Spicetify too old for the installed "
        + "Spotify — it patches without error, and then its own API never starts, so no extension "
        + "runs. Update Spicetify and run 'spicetify backup apply'. Break music still plays, at "
        + "full level throughout.");

    /// <summary>
    /// The extension is attached and has said it cannot drive Spotify. Its own words are used,
    /// since it is the only thing that can see, and the fix named is the host's to carry out.
    /// </summary>
    /// <remarks>
    /// Nothing here is platform-specific and nothing here may become so: the fault, the wording
    /// and the remedy all come from inside Spotify's own client, which is the same client on every
    /// operating system this plugin runs on.
    /// </remarks>
    private void ReportTheExtensionCannotRun(SpicetifyDiagnosis diagnosis)
    {
        _logger.LogWarning("The Spicetify bridge cannot drive Spotify: {Diagnosis}", diagnosis.Describe());

        _context.ReportWarning(
            $"Break music will not fade: {diagnosis.Describe()}. "
            + (diagnosis.SpicetifyApiNeverStarted
                ? "That is a Spicetify older than the installed Spotify — it patches without "
                  + "error and then never binds, so no extension can run. Update Spicetify, then "
                  + "run 'spicetify backup apply'. If it says a backup already exists, reinstall "
                  + "Spotify first so there is a clean copy to patch."
                : "Updating Spicetify and running 'spicetify backup apply' is what usually fixes "
                  + "it.")
            + " Break music still plays, at full level throughout.");
    }

    /// <summary>
    /// Whether the room can hear Spotify right now, which is the one thing that makes restarting
    /// it expensive.
    /// </summary>
    /// <remarks>
    /// Unknown is taken as playing. Being wrong that way costs a fade; being wrong the other way
    /// stops the room's music.
    /// </remarks>
    private async Task<bool> IsPlayingAsync()
    {
        try
        {
            return await _isSpotifyPlaying();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read whether Spotify is playing");
            return true;
        }
    }

    /// <summary>
    /// Puts the shipped file in place, applying only if it is not already current. Nothing here is
    /// fatal: a Spicetify that cannot be written to costs a fade, not break music.
    /// </summary>
    private async Task InstallAsync()
    {
        if (_discover() is not { } installation)
        {
            _logger.LogInformation(
                "Spicetify is installed but has never been run, so there is nothing to install the bridge into");

            return;
        }

        await _install(installation, false);
    }

    /// <summary>
    /// Patches Spotify, forcing past the currency check — the caller has established that the
    /// running Spotify is not patched whatever the config says. Only ever reached where a restart
    /// of Spotify costs the room nothing.
    /// </summary>
    private async Task<SpicetifyInstallOutcome> ApplyAsync(SpicetifyInstallation installation)
    {
        var outcome = await _install(installation, true);

        if (outcome == SpicetifyInstallOutcome.Failed)
        {
            _context.ReportWarning(
                "Break music cannot fade: Spotify is not patched with the KHost bridge and KHost "
                + "could not patch it. Run 'spicetify apply' yourself and restart KHost. Break "
                + "music still plays; it starts and stops at full level.");
        }

        return outcome;
    }
}
