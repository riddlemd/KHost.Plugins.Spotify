using System.Text.RegularExpressions;

namespace KHost.Plugins.Spotify;

/// <summary>Turns the "Copy link to playlist" URL a host actually pastes into the
/// <c>spotify:playlist:id</c> form every backend wants.</summary>
public static partial class SpotifyUri
{
    /// <summary>A single track is rejected: it ends after one song and nothing here reads Spotify
    /// back to notice.</summary>
    private static readonly string[] PlayableTypes = ["playlist", "album", "artist", "collection"];

    /// <summary>Null when blank or not playable; callers read null as resume whatever is loaded.</summary>
    /// <remarks>Base62 only: this feeds an AppleScript literal and a D-Bus argument as-is.</remarks>
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
