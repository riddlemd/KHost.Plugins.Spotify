namespace KHost.Plugins.Spotify;

/// <summary>Typed view of the settings declared in manifest.json, kept in sync with it by hand.</summary>
public class SpotifySettings
{
    /// <summary>Blank resumes whatever Spotify already has loaded, for a host who curates the bed
    /// in Spotify itself rather than here.</summary>
    public string PlaylistUri { get; set; } = "";

    public bool Shuffle { get; set; } = true;

    public bool LaunchIfNotRunning { get; set; } = true;

    /// <summary>Harmless with no Spicetify extension installed: the socket sits idle and every
    /// command takes the ordinary path instead of a smooth ramp.</summary>
    public bool SpicetifyBridge { get; set; } = true;

    /// <summary>Loopback only. The extension reads the same number from its own storage key.</summary>
    public int SpicetifyBridgePort { get; set; } = 8974;

    /// <summary>Milliseconds. Zero turns fading off while leaving the bridge itself up.</summary>
    public int FadeMilliseconds { get; set; } = 1500;
}
