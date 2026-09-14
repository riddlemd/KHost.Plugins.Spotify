using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Bridge;

public enum SpicetifyInstallOutcome
{
    /// <summary>The shipped extension is already in place and registered; nothing was run.</summary>
    AlreadyCurrent,
    Installed,
    Failed,
}

/// <summary>
/// Puts the bridge extension into a host's Spicetify so fading works without them being asked to
/// copy a file. Only ever runs when something is actually out of date — applying patches Spotify
/// and restarts it, which is not something to do on every console start.
/// </summary>
public sealed class SpicetifyExtensionInstaller(
    ILogger logger,
    Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
{
    public SpicetifyExtensionInstaller(ILogger logger)
        : this(logger, (file, arguments, token) => ProcessRunner.RunAsync(file, arguments, token))
    {
    }

    /// <param name="force">
    /// Applies even when everything on disk looks right. The currency check asks whether the file
    /// is there and registered, which is not the same question as whether the running Spotify is
    /// actually patched — an update reverts the patch and leaves both of those true. A caller that
    /// has watched for the extension and seen it never attach knows better than the check does.
    /// </param>
    public async Task<SpicetifyInstallOutcome> EnsureInstalledAsync(
        SpicetifyInstallation installation, string sourcePath, string cliPath,
        bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && installation.IsExtensionCurrent(sourcePath))
            return SpicetifyInstallOutcome.AlreadyCurrent;

        try
        {
            Directory.CreateDirectory(installation.ExtensionsDirectory);
            File.Copy(sourcePath, installation.InstalledExtensionPath, overwrite: true);

            var configured = await run(
                cliPath, ["config", "extensions", SpicetifyInstallation.ExtensionFileName], cancellationToken);

            if (!configured.Succeeded)
                return Failed("registering the extension", configured.Message);

            var applied = await run(cliPath, ["apply"], cancellationToken);

            // Checked on the disk rather than taken from the exit code, which `apply` returns as
            // zero even when it patched nothing at all. `backup apply` is the heavier call that
            // takes its own copy of Spotify first, and is what actually works on a Spotify this
            // Spicetify has not patched before — or has not patched since it last updated.
            if (!applied.Succeeded || !installation.IsSpotifyPatched())
                applied = await run(cliPath, ["backup", "apply"], cancellationToken);

            if (!applied.Succeeded)
                return Failed("applying the change to Spotify", applied.Message);

            // Claimed only where the extension is actually inside Spotify. Spicetify exiting zero
            // is not the same as Spotify having been patched, and saying so anyway is what sent
            // the last diagnosis looking in the wrong place for an afternoon.
            if (!installation.IsSpotifyPatched())
            {
                return Failed("applying the change to Spotify",
                    "Spicetify reported success but the extension is not in Spotify, so nothing was "
                    + "patched. Run 'spicetify backup apply' yourself and check what it says — a "
                    + "Spicetify older than the installed Spotify is the usual reason.");
            }

            logger.LogInformation("Installed the KHost bridge extension into Spicetify; Spotify was restarted to pick it up");

            return SpicetifyInstallOutcome.Installed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Failed("writing the extension", ex.Message);
        }
    }

    private SpicetifyInstallOutcome Failed(string step, string message)
    {
        logger.LogWarning(
            "Spicetify is installed but {Step} failed, so break music will not fade: {Message}", step, message);

        return SpicetifyInstallOutcome.Failed;
    }
}
