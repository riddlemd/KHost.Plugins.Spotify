using System.Net;
using System.Net.Sockets;
using System.Text;
using KHost.Plugins.Spotify.Bridge;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// The decorator's whole point is that it adds and never subtracts: a host with no extension
/// installed — which is nearly all of them — has to get exactly the behaviour they had before.
/// Driven against a real socket rather than a substituted bridge, because the thing worth proving
/// is that a browser's frames and ours meet in the middle.
/// </summary>
public class BridgedSpotifyControllerTests : IDisposable
{
    private readonly FakeSpotifyController _inner = new();
    private readonly SpicetifyBridge _bridge;
    private readonly int _port;
    private readonly BridgedSpotifyController _controller;

    public BridgedSpotifyControllerTests()
    {
        _port = FreePort();
        _bridge = new SpicetifyBridge(NullLogger.Instance, _port);
        _bridge.Start();

        _controller = new BridgedSpotifyController(_inner, _bridge, TimeSpan.FromMilliseconds(40));
    }

    // ── with nothing attached, which is the ordinary case ──────────────────────────────

    [Fact]
    public async Task WithNoExtension_EveryCommandStillReachesTheBackend()
    {
        await _controller.StartAsync("spotify:playlist:x", shuffle: true);
        await _controller.PauseAsync();
        await _controller.ResumeAsync();
        await _controller.SkipAsync();
        await _controller.StopAsync();

        Assert.Equal(["start", "pause", "resume", "skip", "stop"], _inner.Calls.Where(c => c != "state"));
    }

    [Fact]
    public async Task WithNoExtension_SettingTheVolumeReportsItCouldNot()
    {
        // The venue volume is what the host tried to apply; false is how the provider learns that
        // Spotify's level is still whatever the person in the room set it to.
        Assert.False(await _controller.SetVolumeAsync(0.5f));
    }

    [Fact]
    public async Task WithNoExtension_TheBackendsOwnLimitationIsStillReported()
    {
        _inner.Limitation = "Spotify cannot be watched on this platform.";

        Assert.Equal("Spotify cannot be watched on this platform.", _controller.Limitation);
    }

    [Fact]
    public async Task WithNoExtension_StateComesFromTheBackend()
    {
        _inner.State = new SpotifyState(SpotifyPlayback.Playing, "Blue Monday", "New Order");

        var state = await _controller.GetStateAsync();

        Assert.Equal("Blue Monday", state?.Title);
        Assert.Contains("state", _inner.Calls);
    }

    // ── with an extension attached ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnAttachedExtension_PausesAndFadesAsOneCommand()
    {
        await using var extension = await AttachAsync();

        var pausing = _controller.PauseAsync();
        var command = await extension.NextAsync();

        // One command carries both, so there is no window between the ramp ending and the pause
        // arriving — and the backend is not asked to pause a client the extension already did.
        Assert.Contains("\"type\":\"pauseWithFadeOut\"", command);

        // No level travels with it either way: out is always to silence, and the level to come
        // back to is the extension's to remember.
        Assert.DoesNotContain("\"to\"", command);

        await extension.SendAsync("""{"type":"faded","to":0,"paused":true}""");
        await pausing;

        Assert.DoesNotContain("pause", _inner.Calls);
    }

    [Fact]
    public async Task ThePauseWaitsForTheRampToFinish_NotJustForTheCommandToGoOut()
    {
        await using var extension = await AttachAsync();

        var pausing = _controller.PauseAsync();
        await extension.NextAsync();

        // Sending is not ramping: the extension takes the whole duration, and a caller that only
        // waits for the bytes to leave hands the room to a singer over music still at full volume.
        await Task.Delay(150);
        Assert.False(pausing.IsCompleted);

        await extension.SendAsync("""{"type":"faded","to":0,"paused":true}""");
        await pausing;

        Assert.True(pausing.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AFadeThatIsNeverAcknowledged_GivesUpRatherThanHoldingTheConsole()
    {
        await using var extension = await AttachAsync();

        // An extension that has stopped answering must not wedge the gap between singers, so the
        // wait is bounded and the pause goes through anyway.
        await _controller.PauseAsync();

        Assert.Contains("pause", _inner.Calls);
    }

    [Fact]
    public async Task AnExtensionThatVanishesMidFade_FallsStraightBackToTheBackend()
    {
        var extension = await AttachAsync();

        var pausing = _controller.PauseAsync();
        await extension.NextAsync();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await extension.DisposeAsync();
        await pausing;
        watch.Stop();

        // Spotify restarting mid-show is the case: the socket goes and the pause still has to land.
        Assert.Contains("pause", _inner.Calls);

        // And it lands on the socket closing rather than on waiting the whole grace out — between
        // singers, a two second stall is the thing the fade was supposed to avoid.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AnAttachedExtension_ClearsTheBackendsNoFadingLimitation()
    {
        _inner.Limitation = "Fading is not supported on this platform.";

        await using var extension = await AttachAsync();

        // The limitation exists to tell a host what they cannot do; with the extension attached
        // they can, and repeating it on the Plugins page would be false.
        Assert.Null(_controller.Limitation);
    }

    [Fact]
    public async Task AnAttachedExtension_IsWhereTheStateComesFrom()
    {
        await using var extension = await AttachAsync();

        await extension.SendAsync("""{"type":"state","playing":true,"title":"Regulate","artist":"Warren G","volume":0.4}""");
        await WaitForAsync(() => _bridge.LastState is not null);

        _inner.Calls.Clear();
        var state = await _controller.GetStateAsync();

        // Read from inside the client, so it costs no process and is not however old the last one was.
        Assert.Equal("Regulate", state?.Title);
        Assert.DoesNotContain("state", _inner.Calls);
    }

    [Fact]
    public async Task AnAttachedExtension_StartsSilentAndComesUp()
    {
        await using var extension = await AttachAsync();

        var starting = _controller.StartAsync(null, shuffle: false);

        Assert.Contains("\"type\":\"volume\"", await extension.NextAsync());   // silence first
        await extension.SendAsync("""{"type":"faded","to":0}""");

        Assert.Contains("\"type\":\"fade\"", await extension.NextAsync());     // then up
        await extension.SendAsync("""{"type":"faded","to":1}""");

        await starting;
    }

    [Fact]
    public async Task ResumingAsksTheExtensionToComeBackUpOnItsOwn()
    {
        await using var extension = await AttachAsync();

        var resuming = _controller.ResumeAsync();
        var command = await extension.NextAsync();

        // No level is sent with it: the extension holds what it faded away from, because Spotify
        // rounds what it reports and a level read back and restored walks down a point per fade.
        Assert.Contains("\"type\":\"playWithFadeIn\"", command);
        Assert.DoesNotContain("\"to\"", command);

        await extension.SendAsync("""{"type":"faded","to":0.62,"playing":true}""");
        await resuming;

        Assert.DoesNotContain("resume", _inner.Calls);
    }

    [Fact]
    public async Task AnAttachedExtensionThatRefusesTheCommand_StillGetsThePauseThroughTheBackend()
    {
        await using var extension = await AttachAsync();

        // Bounded here as well as in the bridge: without its timeout this hangs rather than fails,
        // and a test that never finishes reads as a build that stopped.
        var pausing = _controller.PauseAsync();

        Assert.Same(pausing, await Task.WhenAny(pausing, Task.Delay(TimeSpan.FromSeconds(20))));
        await pausing;

        // The ramp was asked for and never acknowledged, so the bridge reports it could not and
        // the ordinary backend does what it always did.
        Assert.Contains("pause", _inner.Calls);
    }

    [Fact]
    public async Task ATrackCalledFadedDoesNotCountAsAnAcknowledgement()
    {
        await using var extension = await AttachAsync();

        var pausing = _controller.PauseAsync();
        await extension.NextAsync();

        // Alan Walker is real, and so is a host who puts him on between singers. Matched on the
        // message's own type rather than on the text anywhere in it, or the ramp ends the moment
        // the extension reports what is playing.
        await extension.SendAsync("""{"type":"state","playing":true,"title":"faded","volume":0.4}""");
        await Task.Delay(150);

        Assert.False(pausing.IsCompleted);

        await extension.SendAsync("""{"type":"faded","to":0,"paused":true}""");
        await pausing;
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<FakeExtension> AttachAsync()
    {
        var extension = await FakeExtension.ConnectAsync(_port);
        await WaitForAsync(() => _bridge.IsConnected);
        return extension;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "the bridge never reached the expected state");
    }

    public void Dispose() => _bridge.Dispose();
}
