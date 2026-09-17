using KHost.Abstractions.Services;
using KHost.Plugins.Spotify.Bridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>Applying restarts Spotify, and every rule here follows from that: it happens only
/// while the room is not listening, and only where the disk says Spotify is not patched.</summary>
public class SpicetifyBridgeSetupTests : IDisposable
{
    private const string Install = "install";
    private const string Apply = "apply";
    private const string Wait = "wait";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("khost-bridge-setup-test-");
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();
    /// <summary>Ordered, not a call count: when patching happens relative to the grace is the
    /// whole of the fix, and a count cannot tell a patch before the wait from one after.</summary>
    private readonly List<string> _log = [];

    /// <summary>Stops the loss watch, which the run leaves polling behind it.</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Whether an attached extension can actually drive Spotify, read fresh every check.</summary>
    private bool _ready;

    /// <summary>What an attached extension said about itself; null while none has said anything.</summary>
    private SpicetifyDiagnosis? _diagnosis;

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

    /// <summary>Unpatched and playing, so patching is both needed and unaffordable. The room
    /// keeps its music and the host is told why the fade is missing.</summary>
    [Fact]
    public async Task RunAsync_SpotifyIsPlayingAndNotPatched_TouchesNothingOnDisk()
    {
        PatchIsMissing();
        _isPlaying = () => Task.FromResult(true);

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("would stop what is playing")));
    }

    /// <summary>The reported failure, in order: nothing playing at startup, Spotify launched by
    /// a break during the grace, and the extension not attached because it only just started.</summary>
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

    /// <summary>Before the grace, not after it: waiting first spends it watching for an extension
    /// with nothing to attach to, then reads that absence as a Spotify needing patching.</summary>
    [Fact]
    public async Task RunAsync_SpotifyIsNotPatched_AppliesBeforeItWaitsAtAll()
    {
        PatchIsMissing();

        await Build(onWait: () => _ready = true).RunAsync(_stopping.Token);

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

    /// <summary>Spicetify's config says the extension is installed and Spotify's own resources
    /// say it is not: the case the forced apply exists for, invisible to the currency check.</summary>
    [Fact]
    public async Task RunAsync_ConfigLooksRightButSpotifyIsNotPatched_AppliesAnyway()
    {
        PatchIsMissing();
        RegisterExtensionInConfig();

        await Build(onWait: () => _ready = true).RunAsync(_stopping.Token);

        Assert.Contains(Apply, _log);
    }

    [Fact]
    public async Task RunAsync_AlreadyPatchedAndTheExtensionAttaches_NeverApplies()
    {
        PatchIsPresent();

        await Build(onWait: () => _ready = true).RunAsync(_stopping.Token);

        Assert.Equal([Install, Wait], _log);
        _context.DidNotReceive().ReportWarning(Arg.Any<string>());
    }

    /// <summary>The extension is inside Spotify and still nothing attached. Applying again would
    /// restart Spotify for nothing, so it says what is actually left: the extension cannot run.</summary>
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

    /// <summary>Unknown is taken as playing. Being wrong that way costs a fade; being wrong the
    /// other way stops the room's music.</summary>
    [Fact]
    public async Task RunAsync_ReadingSpotifyThrows_IsTakenAsPlayingRatherThanPatching()
    {
        PatchIsMissing();
        _isPlaying = () => throw new InvalidOperationException("osascript is not available");

        await Build().RunAsync(_stopping.Token);

        Assert.DoesNotContain(Apply, _log);
    }

    /// <summary>Playing at startup, so nothing was patched then, and stopped by the grace, which
    /// makes patching affordable after all: the one path that reaches a second apply.</summary>
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

    /// <summary>Attached and unable to work is its own answer, not a patching problem: the
    /// extension already said why over the socket, so the host repeats that instead of guessing.</summary>
    [Fact]
    public async Task RunAsync_TheExtensionAttachesAndCannotRun_ExplainsThatRatherThanPatching()
    {
        PatchIsPresent();
        _diagnosis = new SpicetifyDiagnosis(
            Ready: false, WaitedMilliseconds: 30000, HasSpicetify: true, HasPlayer: false,
            PlatformKeys: 0, Error: "Cannot read properties of undefined (reading '_volume')");

        await Build().RunAsync(_stopping.Token);

        Assert.Equal([Install, Wait], _log);
        _context.Received(1).ReportWarning(Arg.Is<string>(m =>
            m.Contains("Spicetify's own API never started")
            && m.Contains("Update Spicetify")
            && m.Contains("reinstall Spotify first")));
    }

    /// <summary>A fault the extension does not recognise must not be answered with a confident
    /// wrong remedy: it still says update Spicetify, without claiming to know that is the cause.</summary>
    [Fact]
    public async Task RunAsync_TheExtensionCannotRunForSomeOtherReason_DoesNotBlameTheVersion()
    {
        PatchIsPresent();
        _diagnosis = new SpicetifyDiagnosis(
            Ready: false, WaitedMilliseconds: 30000, HasSpicetify: true, HasPlayer: true,
            PlatformKeys: 40, Error: null);

        await Build().RunAsync(_stopping.Token);

        _context.Received(1).ReportWarning(Arg.Is<string>(m =>
            m.Contains("never became usable") && !m.Contains("older than the installed Spotify")));
    }

    /// <summary>A report the extension has not stood behind yet is not a fault to repeat: it
    /// connects before it knows anything, so its opening report says not-ready with nothing failed.</summary>
    [Fact]
    public async Task RunAsync_TheExtensionHasNotReachedAVerdict_DoesNotRepeatItAsOne()
    {
        PatchIsPresent();
        _diagnosis = new SpicetifyDiagnosis(
            Ready: false, WaitedMilliseconds: 0, HasSpicetify: true, HasPlayer: false,
            PlatformKeys: 0, Error: "not ready yet");

        await Build().RunAsync(_stopping.Token);

        _context.DidNotReceive().ReportWarning(Arg.Is<string>(m => m.Contains("still waiting for the player")));
        _context.DidNotReceive().ReportWarning(Arg.Is<string>(m => m.Contains("Spicetify's own API never started")));
    }

    /// <summary>A ready extension is the working case and says nothing at all.</summary>
    [Fact]
    public async Task RunAsync_TheExtensionAttachesAndIsReady_SaysNothing()
    {
        PatchIsPresent();
        _diagnosis = new SpicetifyDiagnosis(true, 0, true, true, 40, null);

        await Build(onWait: () => _ready = true).RunAsync(_stopping.Token);

        _context.DidNotReceive().ReportWarning(Arg.Any<string>());
    }

    private SpicetifyBridgeSetup Build(Action? onWait = null)
    {
        var installation = new SpicetifyInstallation { ConfigDirectory = _root.FullName };

        return new SpicetifyBridgeSetup(
            NullLogger.Instance,
            _context,
            () => _ready,
            () => _diagnosis,
            () => _isPlaying(),
            () => installation,
            (_, force) =>
            {
                _log.Add(force ? Apply : Install);
                return Task.FromResult(force ? _outcome : SpicetifyInstallOutcome.Installed);
            },
            (duration, token) =>
            {
                // The loss watch polls for the rest of the shift; returning from its wait would
                // spin it against this log while the assertions read it, so it is held until Dispose.
                if (duration == SpicetifyBridgeSetup.LossPollInterval)
                    return Task.Delay(Timeout.Infinite, token);

                _log.Add(Wait);
                onWait?.Invoke();

                return Task.CompletedTask;
            });
    }

    /// <summary>Spicetify records where Spotify's resources are; the extension inside them is the
    /// patch being live. Both halves are written, so the check reads a real installation.</summary>
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
