namespace KHost.Plugins.Spotify.Control;

/// <summary>How a fade ended. Only <see cref="Failed"/> is worth telling a host about: a Spotify
/// that is not running, or not yet making a sound, has no level to fade.</summary>
internal enum FadeOutcome
{
    Landed,
    NothingToFade,
    Superseded,
    Failed,
}

/// <summary>Moves Spotify's own level from outside it, and only ever back to where it was found:
/// the room's Spotify volume is set in Spotify, so a fade never chooses a level of its own.</summary>
internal interface ISpotifyFader
{
    /// <summary>False where this platform has no way to reach Spotify's level at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Takes Spotify to silence over <paramref name="duration"/>, remembering the level it
    /// left unless that level is one this fader wrote.</summary>
    Task<FadeOutcome> SilenceAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Brings Spotify back to the level the last silence left, over <paramref name="duration"/>.
    /// A level the host set while it was silent is kept, not overwritten.</summary>
    Task<FadeOutcome> RestoreAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Puts the level straight back once Spotify has stopped making sound.</summary>
    /// <remarks>A stop command returns before Spotify's last buffer has played, and that buffer
    /// put back at full level is heard as a burst at the end of the fade.</remarks>
    Task<FadeOutcome> RestoreOnceQuietAsync(CancellationToken cancellationToken = default);

    /// <summary>Brings back a level an unfinished fade left behind, once Spotify has opened its audio.</summary>
    /// <remarks>For a command that found nothing to silence: a Spotify relaunched after being killed
    /// mid-fade comes back at the faded level, and nothing else would raise it.</remarks>
    Task<FadeOutcome> RestoreOnceHeardAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

/// <summary>Where Spotify's level cannot be reached: every command goes through unfaded.</summary>
internal sealed class UnavailableSpotifyFader : ISpotifyFader
{
    public bool IsAvailable => false;

    public Task<FadeOutcome> SilenceAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => Task.FromResult(FadeOutcome.Failed);

    public Task<FadeOutcome> RestoreAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => Task.FromResult(FadeOutcome.Failed);

    public Task<FadeOutcome> RestoreOnceQuietAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(FadeOutcome.Failed);

    public Task<FadeOutcome> RestoreOnceHeardAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => Task.FromResult(FadeOutcome.Failed);
}
