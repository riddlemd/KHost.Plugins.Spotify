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
    /// Long enough that a Spotify restarting on its own is not mistaken for one that has gone.
    /// </summary>
    internal static readonly TimeSpan LossPollInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly IPluginContext _context;
    private readonly Func<bool> _isExtensionAttached;
    private readonly Func<Task<bool>> _isSpotifyPlaying;
    private readonly Func<SpicetifyInstallation?> _discover;
    private readonly Func<SpicetifyInstallation, bool, Task<SpicetifyInstallOutcome>> _install;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal SpicetifyBridgeSetup(
        ILogger logger,
        IPluginContext context,
        Func<bool> isExtensionAttached,
        Func<Task<bool>> isSpotifyPlaying,
        Func<SpicetifyInstallation?> discover,
        Func<SpicetifyInstallation, bool, Task<SpicetifyInstallOutcome>> install,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _context = context;
        _isExtensionAttached = isExtensionAttached;
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
            () => bridge.IsConnected,
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

            if (_isExtensionAttached())
            {
                // It attached, so anything that drops it from here is a live problem rather than a
                // setup one, and is only ever reported.
                WatchForLoss(cancellationToken);
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

            // Only where the patch looked present and turned out not to be. Having already applied
            // above and still seen nothing attach, applying again would restart Spotify a second
            // time to learn what it has just been told.
            if (!applied)
            {
                _logger.LogInformation(
                    "The Spicetify extension has not attached, so Spotify is not patched with it — applying again");

                if (_discover() is not { } discovered)
                    return;

                if (await ApplyAsync(discovered) is SpicetifyInstallOutcome.Failed)
                    return;

                // Given the grace again: applying restarts Spotify, and the extension cannot
                // connect until it is back up.
                await _delay(GracePeriod, cancellationToken);
            }

            if (!_isExtensionAttached())
            {
                // Said as what it is. Calling this a loss would be wrong — nothing ever attached —
                // and wrong in the direction that sends the next person looking at Spotify's
                // update history rather than at whether Spicetify ever patched it.
                _context.ReportWarning(
                    "Break music will not fade: KHost applied the Spicetify bridge to Spotify and "
                    + "the extension still did not connect. Check 'spicetify backup apply' from a "
                    + "terminal — it reports the reason, which is usually a Spicetify too old for "
                    + "this Spotify. Break music still plays, at full level throughout.");

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

            if (_isExtensionAttached())
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
