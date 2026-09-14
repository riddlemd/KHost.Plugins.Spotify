using KHost.Abstractions.Services;
using KHost.Plugins.Spotify.Bridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// Applying restarts Spotify, and every rule here follows from that: it happens only while the
/// room is not listening, and only where the disk says Spotify is not carrying the extension.
/// </summary>
/// <remarks>
/// Getting it wrong stopped a track mid-play. The setup waited out its grace, read an extension
/// that had had nothing to attach to as a Spotify needing patching, and applied over the break
/// music that had started during the wait — which is
/// <see cref="RunAsync_BreakMusicStartsDuringTheGrace_DoesNotApplyOverIt"/>.
///
/// Asserted as an ordered log rather than as call counts: when patching happens relative to the
/// grace is the whole of the fix, and a count cannot tell a patch before the wait from one after.
/// </remarks>
public class SpicetifyBridgeSetupTests : IDisposable
{
    private const string Install = "install";
    private const string Apply = "apply";
    private const string Wait = "wait";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("khost-bridge-setup-test-");
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();
    private readonly List<string> _log = [];

    /// <summary>Stops the loss watch, which the run leaves polling behind it.</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>What the extension's socket reports, read fresh on every check.</summary>
    private bool _attached;

    private Func<Task<bool>> _isPlaying = () => Task.FromResult(false);
    private SpicetifyInstallOutcome _outcome = SpicetifyInstallOutcome.Installed;

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();

        try { _root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Unpatched and playing, so patching is both needed and unaffordable. The room keeps its
    /// music and the host is told why the fade is missing.
    /// </summary>
    [Fact]
    public async Task RunAsync_SpotifyIsPlayingAndNotPatched_TouchesNothingOnDisk()
    {
        PatchIsMissing();
        _isPlaying = () => Task.FromResult(true);

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("would stop what is playing")));
    }

    /// <summary>
    /// The reported failure, in the order it happened: nothing playing when the console came up,
    /// Spotify launched by a break during the grace, and the extension not attached because the
    /// Spotify carrying it had only just started.
    /// </summary>
    [Fact]
    public async Task RunAsync_BreakMusicStartsDuringTheGrace_DoesNotApplyOverIt()
    {
        PatchIsPresent();

        var playing = false;
        _isPlaying = () => Task.FromResult(playing);

        // Turns on as the grace elapses, which is when a host pressing play would turn it on.
        await Build(onWait: () => playing = true).RunAsync(_stopping.Token);

        Assert.Equal([Install, Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("Spotify carries the KHost bridge")));
    }

    /// <summary>
    /// Before the grace, not after it. Waiting first spends it watching for an extension that has
    /// nothing to attach to — the console starts before Spotify does — and then reads that absence
    /// as a Spotify needing to be patched.
    /// </summary>
    [Fact]
    public async Task RunAsync_SpotifyIsNotPatched_AppliesBeforeItWaitsAtAll()
    {
        PatchIsMissing();

        await Build(onWait: () => _attached = true).RunAsync(_stopping.Token);

        Assert.Equal([Install, Apply, Wait], _log);
    }

    /// <summary>Having applied once and seen nothing attach, applying again learns nothing.</summary>
    [Fact]
    public async Task RunAsync_AppliedAndTheExtensionStillDoesNotAttach_DoesNotApplyASecondTime()
    {
        PatchIsMissing();

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Install, Apply, Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("KHost patched Spotify")));
    }

    /// <summary>
    /// Spicetify's config says the extension is installed and Spotify's own resources say it is
    /// not — the case the forced apply exists for, and the one the currency check cannot see.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConfigLooksRightButSpotifyIsNotPatched_AppliesAnyway()
    {
        PatchIsMissing();
        RegisterExtensionInConfig();

        await Build(onWait: () => _attached = true).RunAsync(_stopping.Token);

        Assert.Contains(Apply, _log);
    }

    [Fact]
    public async Task RunAsync_AlreadyPatchedAndTheExtensionAttaches_NeverApplies()
    {
        PatchIsPresent();

        await Build(onWait: () => _attached = true).RunAsync(_stopping.Token);

        Assert.Equal([Install, Wait], _log);
        _context.DidNotReceive().ReportWarning(Arg.Any<string>());
    }

    /// <summary>
    /// The extension is inside Spotify and still nothing attached. Applying again would write the
    /// same file to the same place and restart Spotify for nothing, so it says what is actually
    /// left: the extension cannot run. Found on a machine whose Spicetify was older than its
    /// Spotify — Spicetify.Platform stayed an empty object for a hundred seconds, so every
    /// extension sat in the wait its first line does, and the old message blamed the patch.
    /// </summary>
    [Fact]
    public async Task RunAsync_PatchedYetNothingAttaches_SaysTheExtensionCannotRunRatherThanApplying()
    {
        PatchIsPresent();

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Install, Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("Spotify carries the KHost bridge")));
    }

    [Fact]
    public async Task RunAsync_ApplyingFails_SaysSoAndStops()
    {
        PatchIsMissing();
        _outcome = SpicetifyInstallOutcome.Failed;

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Install, Apply], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("could not patch it")));
    }

    /// <summary>
    /// Unknown is taken as playing. Being wrong that way costs a fade; being wrong the other way
    /// stops the room's music.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReadingSpotifyThrows_IsTakenAsPlayingRatherThanPatching()
    {
        PatchIsMissing();
        _isPlaying = () => throw new InvalidOperationException("osascript is not available");

        await Build().RunAsync(_stopping.Token);

        Assert.DoesNotContain(Apply, _log);
    }

    /// <summary>
    /// Playing when the console came up, so nothing was patched then, and stopped by the time the
    /// grace ran out — which makes patching affordable after all. The one path that reaches the
    /// second apply: every other way to arrive here has already patched or already returned.
    /// </summary>
    [Fact]
    public async Task RunAsync_PlayingAtStartThenStopsByTheGrace_PatchesAfterAll()
    {
        PatchIsMissing();

        var playing = true;
        _isPlaying = () => Task.FromResult(playing);

        await Build(onWait: () => playing = false).RunAsync(_stopping.Token);

        Assert.Equal([Wait, Apply, Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("KHost patched Spotify")));
    }

    private SpicetifyBridgeSetup Build(Action? onWait = null)
    {
        var installation = new SpicetifyInstallation { ConfigDirectory = _root.FullName };

        return new SpicetifyBridgeSetup(
            NullLogger.Instance,
            _context,
            () => _attached,
            () => _isPlaying(),
            () => installation,
            (_, force) =>
            {
                _log.Add(force ? Apply : Install);
                return Task.FromResult(force ? _outcome : SpicetifyInstallOutcome.Installed);
            },
            (duration, token) =>
            {
                // The loss watch polls for the rest of the shift, and returning from its wait
                // would spin it against this log while the assertions read it. Held instead, until
                // Dispose lets it go.
                if (duration == SpicetifyBridgeSetup.LossPollInterval)
                    return Task.Delay(Timeout.Infinite, token);

                _log.Add(Wait);
                onWait?.Invoke();

                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Spicetify records where Spotify's resources are; the extension inside them is the patch
    /// being live. Both halves are written, so the check reads a real installation.
    /// </summary>
    private void PatchIsPresent()
    {
        var resources = _root.CreateSubdirectory("spotify");
        var extensions = resources.CreateSubdirectory(Path.Combine("Apps", "xpui", "extensions"));

        File.WriteAllText(
            Path.Combine(extensions.FullName, SpicetifyInstallation.ExtensionFileName), "bridge");

        WriteConfig(resources.FullName);
    }

    /// <summary>Spotify is where the config says, and the extension is not in it.</summary>
    private void PatchIsMissing() => WriteConfig(_root.CreateSubdirectory("spotify").FullName);

    /// <summary>What a Spotify update leaves behind: the config still naming an extension that
    /// is no longer inside Spotify.</summary>
    private void RegisterExtensionInConfig() => AppendConfig(
        $"extensions            = {SpicetifyInstallation.ExtensionFileName}");

    private void WriteConfig(string spotifyPath)
        => File.WriteAllLines(
            ConfigPath, ["[AdditionalOptions]", $"spotify_path          = {spotifyPath}"]);

    private void AppendConfig(string line) => File.AppendAllLines(ConfigPath, [line]);

    private string ConfigPath => Path.Combine(_root.FullName, "config-xpui.ini");
}
