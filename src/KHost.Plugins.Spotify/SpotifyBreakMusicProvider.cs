using KHost.Plugins.Sdk.Models;
using KHost.Plugins.Sdk.Services;
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
    private readonly ILogger<SpotifyBreakMusicProvider> _logger;
    private readonly ISpotifyController _controller;
    private readonly string? _contextUri;
    private readonly bool _shuffle;

    public SpotifyBreakMusicProvider(ILogger<SpotifyBreakMusicProvider> logger, IPluginContext context)
        : this(logger, context, controller: null)
    {
    }

    internal SpotifyBreakMusicProvider(
        ILogger<SpotifyBreakMusicProvider> logger, IPluginContext context, ISpotifyController? controller)
    {
        _logger = logger;

        var settings = context.BindSettings<SpotifySettings>();

        _contextUri = SpotifyUri.Normalize(settings.PlaylistUri);
        _shuffle = settings.Shuffle;

        _controller = controller ?? SpotifyControllerFactory.ForCurrentPlatform(logger, settings.LaunchIfNotRunning);

        if (!string.IsNullOrWhiteSpace(settings.PlaylistUri) && _contextUri is null)
        {
            context.ReportWarning(
                $"'{settings.PlaylistUri}' is not a Spotify playlist, album or artist link, so break "
                + "music will resume whatever Spotify already has loaded instead.");
        }

        if (_controller.Limitation is { } limitation)
            context.ReportWarning(limitation);
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

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => _controller.SkipAsync(cancellationToken);

    /// <summary>
    /// Deliberately nothing. Spotify's level belongs to whoever set it there, and the host has no
    /// business moving a slider the person running the room can see in another app.
    /// </summary>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
