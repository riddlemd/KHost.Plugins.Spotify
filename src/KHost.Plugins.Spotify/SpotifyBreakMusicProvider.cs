using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify;

/// <summary>Break music out of the Spotify desktop app on this machine. The host carries none of
/// this audio, so nothing here routes it to a screen or Cast. Spotify's level is only ever faded
/// away from and back to wherever the host set it.</summary>
public sealed class SpotifyBreakMusicProvider : IBreakMusicProvider
{
    /// <summary>Up to a second and a half, which is longer than Spotify has needed to turn a
    /// track over here and short enough that a refused skip does not hold the console.</summary>
    private static readonly TimeSpan SkipSettleInterval = TimeSpan.FromMilliseconds(150);
    private const int SkipSettleAttempts = 10;

    private readonly ILogger<SpotifyBreakMusicProvider> _logger;
    private readonly IMessageBroker? _broker;
    private readonly ISpotifyController _controller;
    private readonly string? _contextUri;
    private readonly bool _shuffle;

    public SpotifyBreakMusicProvider(
        ILogger<SpotifyBreakMusicProvider> logger, IPluginContext context, IMessageBroker broker,
        IFlashService flash)
        : this(logger, context, controller: null, broker, flash)
    {
    }

    /// <param name="controller">A stand-in for the platform backend; null picks the one for this OS.</param>
    /// <param name="fader">Null picks this OS's own when the backend is too, and fades nothing
    /// around a stand-in backend.</param>
    internal SpotifyBreakMusicProvider(
        ILogger<SpotifyBreakMusicProvider> logger,
        IPluginContext context,
        ISpotifyController? controller,
        IMessageBroker? broker = null,
        IFlashService? flash = null,
        ISpotifyFader? fader = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _broker = broker;

        var settings = context.BindSettings<SpotifySettings>();

        _contextUri = SpotifyUri.Normalize(settings.PlaylistUri);
        _shuffle = settings.Shuffle;

        var platform = controller ?? SpotifyControllerFactory.ForCurrentPlatform(logger, settings.LaunchIfNotRunning);

        if (controller is null)
            fader ??= SpotifyControllerFactory.FaderForCurrentPlatform(logger);

        if (fader is not null)
        {
            platform = new FadingSpotifyController(
                platform, fader, TimeSpan.FromMilliseconds(Math.Max(0, settings.FadeMilliseconds)), flash);
        }

        if (settings.RecoverStalledPlayback)
            platform = new StallRecoveringSpotifyController(platform, time ?? TimeProvider.System, logger);

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

    /// <summary>A property cannot go and ask, so this is refreshed by the transport calls rather
    /// than polled, naming what is on without putting a timer on Spotify for a whole shift.</summary>
    public BreakMusicTrack? CurrentTrack { get; private set; }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        // Asked before anything is sent: a host who put Spotify on themselves while waiting for a
        // first singer is already doing what was wanted, and the console should say so.
        var before = await _controller.GetStateAsync(cancellationToken);

        if (before?.Playback == SpotifyPlayback.Playing)
        {
            _logger.LogInformation("Spotify was already playing; leaving it as the host set it");
            CurrentTrack = ToTrack(before);

            return true;
        }

        if (!await _controller.StartAsync(_contextUri, _shuffle, cancellationToken))
        {
            _logger.LogWarning("Spotify refused to start break music");

            throw new KHostException(
                "Spotify: couldn't start break music. Check that Spotify is installed and reachable, "
                + "then try again.",
                "Check that Spotify is installed and reachable, then try again.",
                "KH-SPOTIFY-START-FAILED");
        }

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

    /// <summary>Starts the playlist instead when Spotify has no track: a freshly launched client
    /// reports itself paused with nothing loaded, and accepts a resume that then plays nothing.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var state = await _controller.GetStateAsync(cancellationToken);

        // Unreadable is not empty: a backend that cannot see still has only the resume to try.
        if (state is not null && string.IsNullOrWhiteSpace(state.Title))
        {
            await StartAsync(cancellationToken);
            return;
        }

        await _controller.ResumeAsync(cancellationToken);
    }

    /// <summary><paramref name="fadeDuration"/> is ignored: the fade is this plugin's own setting,
    /// and the level is put back once stopped so Spotify is never left muted.</summary>
    public Task StopAsync(TimeSpan? fadeDuration = null, CancellationToken cancellationToken = default)
        => _controller.StopAsync(cancellationToken);

    /// <summary>Skipping is the one command that changes what is playing, so this reads the track
    /// back afterwards instead of trusting the console's last render of it.</summary>
    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        // Read rather than taken from CurrentTrack: Spotify moves on by itself as tracks end, so
        // a settle compared against the last shown track would stop on the one being left.
        var before = (await _controller.GetStateAsync(cancellationToken))?.Title;

        await _controller.SkipAsync(cancellationToken);

        CurrentTrack = ToTrack(await ReadSettledStateAsync(before, cancellationToken));
    }

    /// <summary>Polled rather than slept a fixed delay, so a quick machine is not held to the worst
    /// case; giving up returns the last read rather than the stale title being skipped past.</summary>
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

    /// <summary>Deliberately nothing: Spotify's level belongs to whoever set it there, not to a
    /// slider the host can move behind the room's back.</summary>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

}
