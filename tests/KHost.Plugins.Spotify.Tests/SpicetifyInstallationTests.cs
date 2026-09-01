using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Tests;

public class SpicetifyInstallationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("khost-spicetify-test-");

    private SpicetifyInstallation Installation => new() { ConfigDirectory = _root.FullName };

    public void Dispose()
    {
        try { _root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FindCli_NotOnAnySearchPath_ReturnsNull()
        => Assert.Null(SpicetifyInstallation.FindCli([_root.FullName], "spicetify"));

    [Fact]
    public void FindCli_OnALaterSearchPath_StillFindsIt()
    {
        var second = _root.CreateSubdirectory("second");

        File.WriteAllText(Path.Combine(second.FullName, "spicetify"), "#!/bin/sh");

        Assert.Equal(
            Path.Combine(second.FullName, "spicetify"),
            SpicetifyInstallation.FindCli([_root.FullName, second.FullName], "spicetify"));
    }

    /// <summary>A PATH entry can hold characters no path may, and one must not hide the rest.</summary>
    [Fact]
    public void FindCli_EntryThatIsNotAValidPath_KeepsLooking()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "spicetify"), "#!/bin/sh");

        Assert.Equal(
            Path.Combine(_root.FullName, "spicetify"),
            SpicetifyInstallation.FindCli(["\0bad", _root.FullName], "spicetify"));
    }

    [Fact]
    public void Discover_NoCandidateExists_ReturnsNull()
        => Assert.Null(SpicetifyInstallation.Discover([Path.Combine(_root.FullName, "absent")]));

    [Fact]
    public void Discover_TakesTheFirstDirectoryThatExists()
    {
        var found = SpicetifyInstallation.Discover([Path.Combine(_root.FullName, "absent"), _root.FullName]);

        Assert.Equal(_root.FullName, found?.ConfigDirectory);
    }

    [Fact]
    public void IsExtensionRegistered_ConfigNamesIt_IsTrue()
    {
        WriteConfig("extensions            = other.js|khost-bridge.js");

        Assert.True(Installation.IsExtensionRegistered());
    }

    [Fact]
    public void IsExtensionRegistered_ConfigNamesOnlyOthers_IsFalse()
    {
        WriteConfig("extensions            = other.js");

        Assert.False(Installation.IsExtensionRegistered());
    }

    /// <summary>A key that merely ends in "extensions" is a different setting, not this one.</summary>
    [Fact]
    public void IsExtensionRegistered_AKeyEndingInExtensionsNamesIt_IsFalse()
    {
        WriteConfig("custom_extensions     = khost-bridge.js");

        Assert.False(Installation.IsExtensionRegistered());
    }

    [Fact]
    public void IsExtensionRegistered_AnUnrelatedKeyNamesIt_IsFalse()
    {
        WriteConfig("custom_apps           = khost-bridge.js");

        Assert.False(Installation.IsExtensionRegistered());
    }

    [Fact]
    public void IsExtensionRegistered_NoConfigFile_IsFalse()
        => Assert.False(Installation.IsExtensionRegistered());

    [Fact]
    public void IsExtensionCurrent_SameBytesAndRegistered_IsTrue()
    {
        var source = WriteSource("bridge v2");

        WriteInstalled("bridge v2");
        WriteConfig("extensions = khost-bridge.js");

        Assert.True(Installation.IsExtensionCurrent(source));
    }

    /// <summary>A stale copy speaks an older protocol to the socket, so it counts as not installed.</summary>
    [Fact]
    public void IsExtensionCurrent_InstalledCopyIsStale_IsFalse()
    {
        var source = WriteSource("bridge v2");

        WriteInstalled("bridge v1");
        WriteConfig("extensions = khost-bridge.js");

        Assert.False(Installation.IsExtensionCurrent(source));
    }

    /// <summary>A file Spicetify was never told about is on disk but never loaded.</summary>
    [Fact]
    public void IsExtensionCurrent_RightBytesButNotRegistered_IsFalse()
    {
        var source = WriteSource("bridge v2");

        WriteInstalled("bridge v2");
        WriteConfig("extensions = other.js");

        Assert.False(Installation.IsExtensionCurrent(source));
    }

    [Fact]
    public void IsExtensionCurrent_NothingInstalled_IsFalse()
        => Assert.False(Installation.IsExtensionCurrent(WriteSource("bridge v2")));

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
