using System.Text.Json;
using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// What the extension reports from inside the client. Read straight from the player rather than
/// asked for, so unlike the platform backends this costs no process and arrives as it happens.
/// </summary>
public sealed record SpicetifyState(SpotifyPlayback Playback, string? Title, string? Artist, float Volume)
{
    public SpotifyState ToSpotifyState() => new(Playback, Title, Artist);

    /// <summary>
    /// The message's own type, or null when it is not a JSON object with one. Read rather than
    /// matched against the text: a report naming a track called "faded" contains everything a
    /// substring check for an acknowledgement would look for.
    /// </summary>
    public static string? TypeOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Null for anything that is not a state report — the extension also sends acknowledgements,
    /// and a malformed line from a client that can be replaced by anyone with the port is not
    /// worth throwing over.
    /// </summary>
    public static SpicetifyState? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "state") return null;

            var playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;

            return new SpicetifyState(
                playing ? SpotifyPlayback.Playing : SpotifyPlayback.Paused,
                NullIfEmpty(Read(root, "title")),
                NullIfEmpty(Read(root, "artist")),
                root.TryGetProperty("volume", out var v) && v.TryGetSingle(out var volume) ? volume : 1f);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
