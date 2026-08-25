namespace KHost.Plugins.Spotify.Control;

public enum SpotifyPlayback
{
    /// <summary>Spotify is not running, or is running with nothing loaded.</summary>
    Stopped,
    Paused,
    Playing,
}

/// <summary>
/// What Spotify is actually doing, read back rather than remembered. Title and artist are null
/// when the backend can see the transport but not the track.
/// </summary>
public sealed record SpotifyState(SpotifyPlayback Playback, string? Title = null, string? Artist = null)
{
    public static readonly SpotifyState Stopped = new(SpotifyPlayback.Stopped);
}
