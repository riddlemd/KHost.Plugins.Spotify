namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// The AppleScript each command sends. Split out from the backend so the script a setting
/// produces can be asserted without Spotify, or a Mac, being involved.
/// </summary>
public static class MacOsScripts
{
    /// <summary>
    /// Guarded rather than told directly: naming an application inside a <c>tell</c> launches it,
    /// so an unguarded pause would start Spotify in order to pause it.
    /// </summary>
    private const string NotRunning = "notrunning";

    public static string Play(string? contextUri, bool shuffle)
    {
        var body = shuffle
            ? "set shuffling to true\n\t\t"
            : string.Empty;

        // play track takes a context URI directly; a bare play resumes whatever is already loaded.
        body += contextUri is null
            ? "play"
            : $"play track \"{contextUri}\"";

        return Guarded(body);
    }

    public static string Pause() => Guarded("pause");

    public static string Skip() => Guarded("next track");

    /// <summary>True when the script ran against a live Spotify rather than declining to start one.</summary>
    public static bool ReachedSpotify(string standardOutput)
        => !standardOutput.Trim().Equals(NotRunning, StringComparison.OrdinalIgnoreCase);

    private static string Guarded(string body) =>
        $"""
        if application "Spotify" is running then
        	tell application "Spotify"
        		{body}
        	end tell
        	return "ok"
        else
        	return "{NotRunning}"
        end if
        """;
}
