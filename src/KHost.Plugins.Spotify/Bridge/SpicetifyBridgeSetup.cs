using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>Gets the bridge extension into Spotify, and reports when it will not go.</summary>
/// <remarks>Applying restarts Spotify, so this only ever patches while the room isn't listening.</remarks>
internal sealed class SpicetifyBridgeSetup
{
    /// <summary>Grace before a missing extension counts as an answer; applying restarts Spotify.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(25);

    /// <summary>Must outlast the extension's wait, or an unreached verdict is blamed on the patch.</summary>
    internal static bool GracePeriodOutlastsTheExtensionsWait
        => GracePeriod.TotalMilliseconds > SpicetifyDiagnosis.VerdictAfterMilliseconds;

    /// <summary>Long enough that a self-restarting Spotify isn't mistaken for one that has gone.</summary>
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

    /// <summary>Patches if needed, then hands off to the watch. Never throws; costs at worst
    /// a fade.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var applied = false;

            // Checked before the grace: waiting first would read a not-yet-attached extension as unpatched.
            if (await IsPlayingAsync())
            {
                // Installing also applies when the shipped extension is newer; a version behind still fades.
                _logger.LogInformation(
                    "Spotify is already playing, so nothing here patches it — that would restart Spotify");
            }
            else
            {
                await InstallAsync();

                // Discovered after installing: that's what creates it when Spicetify never had an extension.
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
                // Working now, so problems from here on are live ones, not setup ones; only ever reported.
                WatchForLoss(cancellationToken);
                return;
            }

            // Attached but unable to work isn't a patching problem; the extension already said so itself.
            if (_diagnose() is { Ready: false, IsVerdict: true } diagnosis)
            {
                ReportTheExtensionCannotRun(diagnosis);
                return;
            }

            // Nothing attached: the disk says whether that's an already-applied patch or a missing one.
            if (applied || _discover()?.IsSpotifyPatched() == true)
            {
                ReportTheExtensionCannotRun(applied);
                return;
            }

            // Same rule as the watch from here on: restarting Spotify to recover a fade is worse than none.
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

            // Given the grace again; applying just restarted Spotify, and it needs time to come back.
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

    /// <summary>Reports when the extension goes and stays gone; re-applying would restart it.</summary>
    /// <remarks>Waits before speaking; a detach also looks like an ordinary Spotify restart.</remarks>
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

    /// <summary>The extension is present but unattached, so applying again fixes nothing.</summary>
    /// <remarks><c>Spicetify.Platform</c> stays empty on a newer Spotify.</remarks>
    private void ReportTheExtensionCannotRun(bool applied) => _context.ReportWarning(
        (applied
            ? "Break music will not fade: KHost patched Spotify with the KHost bridge and the "
            : "Break music will not fade: Spotify carries the KHost bridge but the ")
        + "extension never attached. That is usually a Spicetify too old for the installed "
        + "Spotify — it patches without error, and then its own API never starts, so no extension "
        + "runs. Update Spicetify and run 'spicetify backup apply'. Break music still plays, at "
        + "full level throughout.");

    /// <summary>Reports the extension's own diagnosis from inside Spotify.</summary>
    /// <remarks>Stays platform-agnostic: the fault and remedy come from Spotify's own client.</remarks>
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

    /// <summary>Whether the room can hear Spotify now; that is what makes restarting it costly.</summary>
    /// <remarks>Unknown counts as playing: wrong costs a fade, wrong the other way stops the music.</remarks>
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

    /// <summary>Installs the shipped file if not current; a failure here costs only a fade.</summary>
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

    /// <summary>Forces the patch past the currency check; reached only where a restart is free.</summary>
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
