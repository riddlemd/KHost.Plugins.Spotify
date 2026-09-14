using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.Spotify.Bridge;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify;

/// <summary>
/// Break music out of the Spotify desktop app on this machine. The host carries none of this
/// audio — it leaves Spotify's own output — so there is nothing to route to a screen or a Cast
/// device, and nothing here touches the level: that one is set in Spotify, by whoever set it.
/// </summary>
public sealed class SpotifyBreakMusicProvider : IBreakMusicProvider
{
    /// <summary>Up to a second and a half, which is longer than Spotify has needed to turn a
    /// track over here and short enough that a refused skip does not hold the console.</summary>
    private static readonly TimeSpan SkipSettleInterval = TimeSpan.FromMilliseconds(150);
    private const int SkipSettleAttempts = 10;

    private readonly ILogger<SpotifyBreakMusicProvider> _logger;
    private readonly IMessageBroker? _broker;
    private readonly ISpotifyController _controller;
    private readonly SpicetifyBridge? _bridge;
    private readonly string? _contextUri;
    private readonly bool _shuffle;

    public SpotifyBreakMusicProvider(
        ILogger<SpotifyBreakMusicProvider> logger, IPluginContext context, IMessageBroker broker)
        : this(logger, context, controller: null, broker)
    {
    }

    internal SpotifyBreakMusicProvider(
        ILogger<SpotifyBreakMusicProvider> logger,
        IPluginContext context,
        ISpotifyController? controller,
        IMessageBroker? broker = null)
    {
        _logger = logger;
        _broker = broker;

        var settings = context.BindSettings<SpotifySettings>();

        _contextUri = SpotifyUri.Normalize(settings.PlaylistUri);
        _shuffle = settings.Shuffle;

        var platform = controller ?? SpotifyControllerFactory.ForCurrentPlatform(logger, settings.LaunchIfNotRunning);

        if (settings.SpicetifyBridge && controller is null)
        {
            _bridge = new SpicetifyBridge(logger, settings.SpicetifyBridgePort);
            _bridge.Start();

            platform = new BridgedSpotifyController(
                platform, _bridge, TimeSpan.FromMilliseconds(Math.Max(0, settings.FadeMilliseconds)));

            if (SpicetifyInstallation.FindCli() is { } cli)
            {
                // Off the constructor: this patches Spotify and restarts it on the first run, which
                // is far too slow to hold up the host starting. It settles long before a break.
                _ = Task.Run(() => InstallExtensionAsync(cli));

                // And then watches whether any of it took. Applying is only ever done here, on the
                // way up: it restarts Spotify, which mid-shift is the room's music stopping.
                _ = Task.Run(() => SettleBridgeAsync(cli, context));
            }
            else
            {
                context.ReportWarning(
                    "Break music fades in and out only on a machine with Spicetify installed — it is "
                    + "what lets KHost reach Spotify's own volume. Without it break music still plays, "
                    + "but it starts and stops at full level. Install Spicetify from spicetify.app and "
                    + "restart KHost; the rest is set up for you.");
            }
        }

        _controller = platform;

        if (!string.IsNullOrWhiteSpace(settings.PlaylistUri) && _contextUri is null)
        {
            context.ReportWarning(
                $"'{settings.PlaylistUri}' is not a Spotify playlist, album or artist link, so break "
                + "music will resume whatever Spotify already has loaded instead.");
        }

        if (_controller.Limitation is { } limitation)
            context.ReportWarning(limitation);

        // Relayed onto the broker, which is how the SDK says a provider reports moving on its own.
        // The host re-reads on it, so this carries no payload of its own.
        _controller.PlaybackChanged += (_, _) => _broker?.Announce(new BreakMusicTrackChanged(SourceName));

        // Fire and forget: the console must not wait on another app to finish starting, and a
        // watch that never binds only costs the live display, not the host's ability to ask.
        _ = _controller.StartWatchingAsync();
    }

    /// <summary>Whatever Spotify says, so the host need not have started it to know about it.</summary>
    public async Task<BreakMusicPlayback?> ReadPlaybackAsync(CancellationToken cancellationToken = default)
    {
        var state = await _controller.GetStateAsync(cancellationToken);

        if (state is null)
            return null;

        CurrentTrack = ToTrack(state) ?? CurrentTrack;

        return state.Playback switch
        {
            SpotifyPlayback.Playing => BreakMusicPlayback.Playing,
            SpotifyPlayback.Paused => BreakMusicPlayback.Paused,
            _ => BreakMusicPlayback.Stopped,
        };
    }

    public string DisplayName => "Spotify";

    public string SourceName => nameof(SpotifyBreakMusicProvider);

    public bool RendersThroughHost => false;

    /// <summary>
    /// The last track a command saw. A property cannot go and ask, so this is refreshed by the
    /// transport calls rather than polled — the console names what is on without this provider
    /// putting a timer on Spotify for a whole shift.
    /// </summary>
    public BreakMusicTrack? CurrentTrack { get; private set; }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        // Asked before anything is sent: a host who put Spotify on themselves while waiting for a
        // first singer is already doing what was wanted, and the console should say so rather than
        // the controller deciding on its own.
        var before = await _controller.GetStateAsync(cancellationToken);

        if (before?.Playback == SpotifyPlayback.Playing)
        {
            _logger.LogInformation("Spotify was already playing; leaving it as the host set it");
            CurrentTrack = ToTrack(before);

            return true;
        }

        if (!await _controller.StartAsync(_contextUri, _shuffle, cancellationToken))
            return false;

        CurrentTrack = ToTrack(await _controller.GetStateAsync(cancellationToken));

        _logger.LogInformation("Break music playing from Spotify");

        return true;
    }

    internal static BreakMusicTrack? ToTrack(SpotifyState? state)
        => string.IsNullOrWhiteSpace(state?.Title)
            ? null
            : new BreakMusicTrack { Title = state.Title, Artist = state.Artist ?? string.Empty };

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _controller.PauseAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => _controller.ResumeAsync(cancellationToken);

    /// <summary>
    /// <paramref name="fadeDuration"/> is ignored. Fading meant ramping Spotify's own volume,
    /// which is the host's setting to keep — and each step was a process spawn, so a two second
    /// fade blocked the console for nearly five before the next song could load.
    /// </summary>
    public Task StopAsync(TimeSpan? fadeDuration = null, CancellationToken cancellationToken = default)
        => _controller.StopAsync(cancellationToken);

    /// <summary>
    /// Reads the track back afterwards: skipping is the one command that changes what is playing,
    /// and the console re-renders on the host's own announcement — reading nothing here leaves it
    /// naming the track that was skipped past.
    /// </summary>
    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        // Read rather than taken from CurrentTrack: Spotify moves on by itself as tracks end, so
        // what the console last showed may already be behind, and a settle that compared against
        // it would stop on the track this skip was leaving.
        var before = (await _controller.GetStateAsync(cancellationToken))?.Title;

        await _controller.SkipAsync(cancellationToken);

        CurrentTrack = ToTrack(await ReadSettledStateAsync(before, cancellationToken));
    }

    /// <summary>
    /// Skipping is asked for and answered later, so a read taken straight afterwards still names
    /// the track being skipped past. Polled rather than slept on a fixed delay: a machine that
    /// turns the track over quickly is not made to wait for the worst case, and one that does not
    /// still lands on the right name. A read with no track to name is taken as it comes — there is
    /// nothing to wait for — and giving up returns the last read rather than the stale one.
    /// </summary>
    private async Task<SpotifyState?> ReadSettledStateAsync(string? previousTitle, CancellationToken cancellationToken)
    {
        SpotifyState? state = null;

        for (var attempt = 0; attempt < SkipSettleAttempts; attempt++)
        {
            await Task.Delay(SkipSettleInterval, cancellationToken);

            state = await _controller.GetStateAsync(cancellationToken);

            if (state?.Title is not { Length: > 0 } title || title != previousTitle)
                return state;
        }

        return state;
    }

    /// <summary>
    /// Deliberately nothing. Spotify's level belongs to whoever set it there, and the host has no
    /// business moving a slider the person running the room can see in another app.
    /// </summary>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// How long the extension is given to connect before its absence is taken as an answer.
    /// Generous: the install above may be restarting Spotify, and Spotify takes its time.
    /// </summary>
    private static readonly TimeSpan BridgeGracePeriod = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Whether the bridge actually took, and what to do about it — which is different on the way
    /// up from during a shift.
    /// </summary>
    /// <remarks>
    /// The check that guards installing asks whether the file is on disk and registered in
    /// Spicetify's config. Neither is the same question as whether the Spotify now running is
    /// patched: an update reverts the patch and leaves both true, so the installer decided there
    /// was nothing to do and the extension never attached again. Watching for the connection is
    /// the only thing here that can tell the difference, because the extension connecting is the
    /// one fact that requires the patch to be live.
    ///
    /// Startup applies, once. After that it only says so: applying restarts Spotify, and doing
    /// that to a room mid-song to recover a fade is a worse outcome than the fade being missing.
    /// </remarks>
    private async Task SettleBridgeAsync(string cliPath, IPluginContext context)
    {
        if (_bridge is not { } bridge)
            return;

        try
        {
            await Task.Delay(BridgeGracePeriod);

            if (bridge.IsConnected)
            {
                // It attached, so anything that drops it from here is a live problem rather than a
                // setup one, and is only ever reported.
                WatchForBridgeLoss(bridge, context);
                return;
            }

            _logger.LogInformation(
                "The Spicetify extension has not attached, so Spotify is not patched with it — applying again");

            if (SpicetifyInstallation.Discover() is not { } installation)
                return;

            var outcome = await new SpicetifyExtensionInstaller(_logger)
                .EnsureInstalledAsync(installation, ShippedExtensionPath, cliPath, force: true);

            if (outcome == SpicetifyInstallOutcome.Failed)
            {
                context.ReportWarning(
                    "Break music cannot fade: Spotify is not patched with the KHost bridge and KHost "
                    + "could not patch it. Run 'spicetify apply' yourself and restart KHost. Break "
                    + "music still plays; it starts and stops at full level.");

                return;
            }

            // Given the same grace again: applying restarts Spotify, and the extension cannot
            // connect until it is back up.
            await Task.Delay(BridgeGracePeriod);

            if (!bridge.IsConnected)
            {
                // Said as what it is. Calling this a loss would be wrong — nothing ever attached —
                // and wrong in the direction that sends the next person looking at Spotify's
                // update history rather than at whether Spicetify ever patched it.
                context.ReportWarning(
                    "Break music will not fade: KHost applied the Spicetify bridge to Spotify and "
                    + "the extension still did not connect. Check 'spicetify backup apply' from a "
                    + "terminal — it reports the reason, which is usually a Spicetify too old for "
                    + "this Spotify. Break music still plays, at full level throughout.");

                return;
            }

            WatchForBridgeLoss(bridge, context);
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
    private void WatchForBridgeLoss(SpicetifyBridge bridge, IPluginContext context) => _ = Task.Run(async () =>
    {
        bool reported = false;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));

            if (bridge.IsConnected)
            {
                reported = false;
                continue;
            }

            if (reported)
                continue;

            reported = true;

            _logger.LogWarning("The Spicetify extension has detached and not come back");

            context.ReportWarning(
                "Spotify is no longer patched with the KHost bridge — usually a Spotify update "
                + "undoing it. Break music still plays, but it starts and stops at full level "
                + "instead of fading. Restarting KHost puts it back.");
        }
    });

    /// <summary>
    /// Nothing here is fatal: the bridge only ever added fading on top of a platform backend that
    /// works without it, so a Spicetify that cannot be written to costs a fade, not break music.
    /// </summary>
    private async Task InstallExtensionAsync(string cliPath)
    {
        try
        {
            if (SpicetifyInstallation.Discover() is not { } installation)
            {
                _logger.LogInformation("Spicetify is installed but has never been run, so there is nothing to install the bridge into");
                return;
            }

            await new SpicetifyExtensionInstaller(_logger).EnsureInstalledAsync(installation, ShippedExtensionPath, cliPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install the KHost bridge extension into Spicetify");
        }
    }

    /// <summary>The copy beside this assembly, not the host's base directory — a plugin runs out of
    /// its own folder under plugins/.</summary>
    private static string ShippedExtensionPath => Path.Combine(
        Path.GetDirectoryName(typeof(SpotifyBreakMusicProvider).Assembly.Location) ?? string.Empty,
        "extension",
        SpicetifyInstallation.ExtensionFileName);
}
