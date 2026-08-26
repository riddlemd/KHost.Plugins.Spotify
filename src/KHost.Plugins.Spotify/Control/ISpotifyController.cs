namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Drives the Spotify desktop app on this machine. Nothing here sets its level — the room's
/// Spotify volume is set in Spotify.
/// </summary>
public interface ISpotifyController
{
    /// <summary>What this backend cannot do, for the Plugins page to say once at startup.</summary>
    string? Limitation { get; }

    /// <summary>
    /// Raised when Spotify moved without being asked — the host pressed pause in its own window
    /// or on the keyboard, or a track ended. Never raised by a backend that cannot watch, so a
    /// subscriber has to treat silence as "no news", not as "nothing changed".
    /// </summary>
    event EventHandler? PlaybackChanged;

    /// <summary>
    /// Begins watching, where the platform offers a way to be told. Defaulted to nothing so a
    /// backend that can only be asked need not pretend otherwise, and so the Windows one still
    /// compiles for the target framework built without the media session projection.
    /// </summary>
    Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// What Spotify is doing right now, or null when this backend cannot see. Asked rather than
    /// remembered: a host who put break music on themselves before the first singer was ready
    /// would otherwise have the next command toggle them off.
    /// </summary>
    Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default);

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
