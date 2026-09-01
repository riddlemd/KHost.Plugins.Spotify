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

    public void Dispose()
    {
        try { _root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task EnsureInstalledAsync_NothingInstalled_CopiesRegistersAndApplies()
    {
        var outcome = await Installer().EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli);

        Assert.Equal(SpicetifyInstallOutcome.Installed, outcome);
        Assert.Equal("bridge", File.ReadAllText(Installation.InstalledExtensionPath));
        Assert.Equal(["config extensions khost-bridge.js", "apply"], _ran);
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
        var outcome = await Installer(fail: "apply").EnsureInstalledAsync(Installation, WriteSource("bridge"), Cli);

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

    private SpicetifyExtensionInstaller Installer(string? fail = null, bool failBackup = false)
        => new(NullLogger.Instance, (_, arguments, _) =>
        {
            var line = string.Join(' ', arguments);

            _ran.Add(line);

            var failed = fail is not null && line.StartsWith(fail, StringComparison.Ordinal)
                || (failBackup && line.StartsWith("backup", StringComparison.Ordinal));

            return Task.FromResult(new ProcessResult(failed ? 1 : 0, string.Empty, failed ? "no backup found" : string.Empty));
        });

    private void WriteConfig(string line)
        => File.WriteAllLines(Installation.ConfigFilePath, ["[AdditionalOptions]", line]);

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
