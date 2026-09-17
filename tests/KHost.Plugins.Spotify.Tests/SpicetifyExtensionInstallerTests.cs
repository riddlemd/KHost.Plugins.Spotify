using KHost.Plugins.Spotify.Bridge;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

public class SpicetifyExtensionInstallerTests : IDisposable
{
    private const string Cli = "spicetify";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("khost-spicetify-install-");
    private readonly List<string> _ran = [];

    private SpicetifyInstallation Installation => new() { ConfigDirectory = _root.FullName };

    /// <summary>Stands in for Spotify's Resources folder, which is what Spicetify patches.</summary>
    private string SpotifyResources => Path.Combine(_root.FullName, "Spotify", "Contents", "Resources");

    /// <summary>Where applying leaves the extension: the .spa is extracted to a directory first.</summary>
    private string PatchedExtensionPath =>
        Path.Combine(SpotifyResources, "Apps", "xpui", "extensions", "khost-bridge.js");

    private void PretendSpotifyIsPatched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PatchedExtensionPath)!);
        File.WriteAllText(PatchedExtensionPath, "bridge");
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task EnsureInstalledAsync_NothingInstalled_CopiesRegistersAndApplies()
    {
        WriteConfig("extensions = khost-bridge.js");

        var outcome = await Installer().EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli, force: true);

        Assert.Equal(SpicetifyInstallOutcome.Installed, outcome);
        Assert.Equal("bridge", File.ReadAllText(Installation.InstalledExtensionPath));

        // apply first, and then the heavier call, because apply left the extension nowhere near
        // Spotify while still exiting zero.
        Assert.Equal(["config extensions khost-bridge.js", "apply", "backup apply"], _ran);
    }

    /// <summary>Installed and registered is not the same as patched: an update reverts the patch
    /// and leaves both true, so a caller that watched and never saw it attach forces it anyway.</summary>
    [Fact]
    public async Task EnsureInstalledAsync_Forced_AppliesEvenWhenEverythingOnDiskLooksRight()
    {
        var source = WriteSource("bridge");

        WriteInstalled("bridge");
        WriteConfig("extensions = khost-bridge.js");

        var outcome = await Installer().EnsureInstalledAsync(Installation, source, Cli, force: true);

        Assert.Equal(SpicetifyInstallOutcome.Installed, outcome);
        Assert.Equal(["config extensions khost-bridge.js", "apply", "backup apply"], _ran);
    }

    /// <summary>Applying patches Spotify and restarts it, so a settled host must not have it run.</summary>
    [Fact]
    public async Task EnsureInstalledAsync_AlreadyCurrent_RunsNothing()
    {
        var source = WriteSource("bridge");

        WriteInstalled("bridge");
        WriteConfig("extensions = khost-bridge.js");

        var outcome = await Installer().EnsureInstalledAsync(Installation, source, Cli);

        Assert.Equal(SpicetifyInstallOutcome.AlreadyCurrent, outcome);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task EnsureInstalledAsync_StaleCopy_IsOverwritten()
    {
        var source = WriteSource("bridge v2");

        WriteInstalled("bridge v1");
        WriteConfig("extensions = khost-bridge.js");

        await Installer().EnsureInstalledAsync(Installation, source, Cli);

        Assert.Equal("bridge v2", File.ReadAllText(Installation.InstalledExtensionPath));
    }

    /// <summary>A Spotify Spicetify has never patched has no backup to apply over.</summary>
    [Fact]
    public async Task EnsureInstalledAsync_ApplyFailsWithNoBackup_FallsBackToBackupApply()
    {
        WriteConfig("extensions = khost-bridge.js");

        var outcome = await Installer(fail: "apply")
            .EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli, force: true);

        Assert.Equal(SpicetifyInstallOutcome.Installed, outcome);
        Assert.Equal(["config extensions khost-bridge.js", "apply", "backup apply"], _ran);
    }

    [Fact]
    public async Task EnsureInstalledAsync_BothApplyAttemptsFail_ReportsFailure()
    {
        var outcome = await Installer(fail: "apply", failBackup: true)
            .EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli);

        Assert.Equal(SpicetifyInstallOutcome.Failed, outcome);
    }

    [Fact]
    public async Task EnsureInstalledAsync_RegisteringFails_DoesNotApply()
    {
        var outcome = await Installer(fail: "config").EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli);

        Assert.Equal(SpicetifyInstallOutcome.Failed, outcome);
        Assert.Equal(["config extensions khost-bridge.js"], _ran);
    }

    [Fact]
    public async Task EnsureInstalledAsync_SourceFileMissing_ReportsFailureRatherThanThrowing()
    {
        var outcome = await Installer()
            .EnsureInstalledAsync(Installation, Path.Combine(_root.FullName, "absent.js"), Cli);

        Assert.Equal(SpicetifyInstallOutcome.Failed, outcome);
        Assert.Empty(_ran);
    }

    /// <summary>Spicetify exits zero when asked to apply over a Spotify it has never backed up:
    /// it warns and does nothing, so exit code alone is not proof the extension reached Spotify.</summary>
    [Fact]
    public async Task EnsureInstalledAsync_SpicetifyExitsZeroWithoutPatching_IsNotCalledASuccess()
    {
        // Exits zero throughout, and leaves no backup behind: the real CLI's behaviour when it has
        // nothing to patch over.
        WriteConfig("extensions = khost-bridge.js");

        var installer = new SpicetifyExtensionInstaller(NullLogger.Instance, (_, arguments, _) =>
        {
            _ran.Add(string.Join(' ', arguments));

            return Task.FromResult(new ProcessResult(0, "spotify not backed up", string.Empty));
        });

        var outcome = await installer.EnsureInstalledAsync(
            Installation, WriteSource("bridge"), Cli, force: true);

        Assert.Equal(SpicetifyInstallOutcome.Failed, outcome);
    }

    private SpicetifyExtensionInstaller Installer(string? fail = null, bool failBackup = false)
        => new(NullLogger.Instance, (_, arguments, _) =>
        {
            var line = string.Join(' ', arguments);

            _ran.Add(line);

            var failed = fail is not null && line.StartsWith(fail, StringComparison.Ordinal)
                || (failBackup && line.StartsWith("backup", StringComparison.Ordinal));

            // What the real CLI leaves on disk, which is the only proof the installer accepts now:
            // spicetify exiting zero is not the same as Spotify having been patched.
            if (!failed && line.StartsWith("backup", StringComparison.Ordinal))
                PretendSpotifyIsPatched();

            return Task.FromResult(new ProcessResult(failed ? 1 : 0, string.Empty, failed ? "no backup found" : string.Empty));
        });

    private void WriteConfig(string line)
        => File.WriteAllLines(Installation.ConfigFilePath,
            ["[AdditionalOptions]", line, "[Setting]", $"spotify_path = {SpotifyResources}"]);

    private string WriteSource(string content)
    {
        var path = Path.Combine(_root.FullName, "source-khost-bridge.js");

        File.WriteAllText(path, content);

        return path;
    }

    private void WriteInstalled(string content)
    {
        Directory.CreateDirectory(Installation.ExtensionsDirectory);
        File.WriteAllText(Installation.InstalledExtensionPath, content);
    }
}
