namespace KHost.Plugins.Spotify;

/// <summary>Typed view of the settings declared in manifest.json — keep the two in sync.</summary>
public class SpotifySettings
{
    /// <summary>
    /// What to put on between singers, as a Spotify link or URI. Blank resumes whatever Spotify
    /// already has loaded, which is what a host who curates the bed in Spotify itself wants.
    /// </summary>
    public string PlaylistUri { get; set; } = "";

    public bool Shuffle { get; set; } = true;

    public bool LaunchIfNotRunning { get; set; } = true;
}
