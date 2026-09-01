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

    public async Task<SpicetifyInstallOutcome> EnsureInstalledAsync(
        SpicetifyInstallation installation, string sourcePath, string cliPath, CancellationToken cancellationToken = default)
    {
        if (installation.IsExtensionCurrent(sourcePath))
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

            // A Spotify Spicetify has never patched has no backup to apply over, and says so
            // rather than making one — that first run is the only time this second call is right.
            if (!applied.Succeeded)
                applied = await run(cliPath, ["backup", "apply"], cancellationToken);

            if (!applied.Succeeded)
                return Failed("applying the change to Spotify", applied.Message);

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
