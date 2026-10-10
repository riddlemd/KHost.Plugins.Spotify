namespace KHost.Plugins.Spotify;

/// <summary>Typed view of the settings declared in manifest.json, kept in sync with it by hand.</summary>
public class SpotifySettings
{
    /// <summary>Blank resumes whatever Spotify already has loaded, for a host who curates the bed
    /// in Spotify itself rather than here.</summary>
    public string PlaylistUri { get; set; } = "";

    public bool Shuffle { get; set; } = true;

    public bool LaunchIfNotRunning { get; set; } = true;

    /// <summary>Works around Spotify ending a track without starting the next: presses play, then
    /// skips, when a track stops at its own end with nobody asking. Needs a backend that is told
    /// when Spotify moves, so it does nothing on Linux.</summary>
    public bool RecoverStalledPlayback { get; set; } = true;
}
