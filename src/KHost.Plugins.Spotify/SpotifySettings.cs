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

    /// <summary>
    /// Listens for the Spicetify extension that ships beside this plugin, which is the only way
    /// Spotify's own volume can be ramped smoothly. Harmless with nothing installed: the socket
    /// sits idle and every command takes the ordinary path.
    /// </summary>
    public bool SpicetifyBridge { get; set; } = true;

    /// <summary>Loopback only. The extension reads the same number from its own storage key.</summary>
    public int SpicetifyBridgePort { get; set; } = 8974;

    /// <summary>How long a fade takes, in milliseconds. Zero turns fading off while leaving the bridge up.</summary>
    public int FadeMilliseconds { get; set; } = 1500;
}
