using System.Text.RegularExpressions;

namespace KHost.Plugins.Spotify;

/// <summary>
/// Turns what a host actually pastes — the "Copy link to playlist" URL — into the
/// <c>spotify:playlist:id</c> form every backend wants.
/// </summary>
public static partial class SpotifyUri
{
    /// <summary>
    /// Contexts worth playing a break-music bed from. A single track is rejected: it ends after
    /// one song and nothing here reads Spotify back to notice.
    /// </summary>
    private static readonly string[] PlayableTypes = ["playlist", "album", "artist", "collection"];

    /// <summary>
    /// Null when the value is blank or is not a Spotify context this can play. Callers treat null
    /// as "resume whatever Spotify already has loaded".
    /// </summary>
    /// <remarks>
    /// The base62 shape is enforced rather than assumed: this string is interpolated into an
    /// AppleScript literal and a D-Bus argument, so anything that is not [A-Za-z0-9] would be an
    /// injection point.
    /// </remarks>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        var match = UriPattern().Match(trimmed);

        if (!match.Success)
            match = LinkPattern().Match(trimmed);

        if (!match.Success)
            return null;

        var type = match.Groups["type"].Value.ToLowerInvariant();

        if (!PlayableTypes.Contains(type))
            return null;

        return $"spotify:{type}:{match.Groups["id"].Value}";
    }

    [GeneratedRegex(@"^spotify:(?<type>[a-zA-Z]+):(?<id>[A-Za-z0-9]+)$")]
    private static partial Regex UriPattern();

    // The trailing ?si=... share token is dropped: it identifies whoever sent the link, not the playlist.
    [GeneratedRegex(@"^https?://open\.spotify\.com/(?:intl-[a-zA-Z-]+/)?(?<type>[a-zA-Z]+)/(?<id>[A-Za-z0-9]+)(?:[/?#].*)?$")]
    private static partial Regex LinkPattern();
}
