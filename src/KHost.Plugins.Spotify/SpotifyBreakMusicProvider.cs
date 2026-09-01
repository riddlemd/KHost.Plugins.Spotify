using KHost.Plugins.Sdk.Messaging;
using KHost.Plugins.Sdk.Messaging.Messages;
using KHost.Plugins.Sdk.Models;
using KHost.Plugins.Sdk.Services;
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
