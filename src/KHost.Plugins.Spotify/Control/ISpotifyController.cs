namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Drives the Spotify desktop app on this machine. Transport only: nothing is read back out of
/// Spotify, and nothing sets its level — the room's Spotify volume is set in Spotify.
/// </summary>
public interface ISpotifyController
{
    /// <summary>What this backend cannot do, for the Plugins page to say once at startup.</summary>
    string? Limitation { get; }

    /// <summary>
    /// Begins playback, loading <paramref name="contextUri"/> first when one is given. False when
    /// Spotify could not be reached at all; a host who then presses play hears nothing, and the
    /// console needs to know rather than sit showing Playing.
    /// </summary>
    Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SkipAsync(CancellationToken cancellationToken = default);
}
