namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Stands in on an OS with no backend. Every command is accepted and does nothing, and
/// <see cref="StartAsync"/> is false so the console shows the bed as stopped rather than as
/// playing something nobody can hear.
/// </summary>
public sealed class UnsupportedSpotifyController : ISpotifyController
{
    public UnsupportedSpotifyController(string platform) => Limitation =
        $"There is no Spotify backend for {platform}, so this provider cannot play anything.";

    public string? Limitation { get; }

    /// <summary>Never raised: there is no backend here to watch.</summary>
    public event EventHandler? PlaybackChanged { add { } remove { } }

    public Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>Stopped rather than null: nothing can play here, which is a definite answer.</summary>
    public Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SpotifyState?>(SpotifyState.Stopped);

    public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SkipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
