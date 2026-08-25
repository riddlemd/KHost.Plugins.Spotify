using System.Text.RegularExpressions;

namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Picks the two fields worth showing out of what gdbus prints for MPRIS Metadata. Parsed as text
/// rather than with a GVariant library so the plugin keeps shelling out to gdbus and carries no
/// D-Bus dependency into the host's plugin folder.
/// </summary>
public static partial class MprisMetadata
{
    public static string? Title(string? metadata)
        => First(TitlePattern(), metadata);

    /// <summary>Only the first artist: MPRIS types it as a list, and a console row wants one name.</summary>
    public static string? Artist(string? metadata)
        => First(ArtistPattern(), metadata);

    private static string? First(Regex pattern, string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;

        var match = pattern.Match(metadata);

        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    // Non-greedy up to the closing quote, so a title running into the next key does not swallow it.
    [GeneratedRegex(@"'xesam:title':\s*<'(.*?)'>")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"'xesam:artist':\s*<\['(.*?)'")]
    private static partial Regex ArtistPattern();
}
