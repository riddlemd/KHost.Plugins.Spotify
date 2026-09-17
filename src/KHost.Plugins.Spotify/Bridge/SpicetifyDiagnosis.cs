using System.Text.Json;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>Why the extension can or cannot drive Spotify; sent even when it cannot do anything.</summary>
/// <remarks>Identical on every OS: nothing here is inferred from the host's own platform.</remarks>
public sealed record SpicetifyDiagnosis(
    bool Ready,
    int WaitedMilliseconds,
    bool HasSpicetify,
    bool HasPlayer,
    int PlatformKeys,
    string? Error)
{
    /// <summary>How long before "not ready" reads as a verdict.</summary> <remarks>Shorter than
    /// <see cref="SpicetifyBridgeSetup.GracePeriod"/>, which a test holds in step.</remarks>
    public const int VerdictAfterMilliseconds = 15000;

    /// <summary>Working is always an answer; not working is only one once the extension has
    /// waited the player out.</summary>
    public bool IsVerdict => Ready || WaitedMilliseconds >= VerdictAfterMilliseconds;

    /// <summary>Spicetify patched Spotify and never bound to it: <c>Spicetify.Platform</c> stays
    /// empty. Signals a Spicetify older than Spotify; re-patching never fixes it.</summary>
    public bool SpicetifyApiNeverStarted => IsVerdict && !Ready && HasSpicetify && PlatformKeys <= 0;

    /// <summary>Deliberately concrete: naming what was seen sends a host to the right place, where
    /// "break music will not fade" would send them to KHost's own settings instead.</summary>
    public string Describe()
    {
        if (Ready) return "the extension is attached and driving Spotify";

        var waited = $"after {WaitedMilliseconds / 1000}s";

        // Not an accusation yet. The extension connects before it knows anything, so its first
        // report always looks like a failure and is not one.
        if (!IsVerdict) return $"the extension is attached and still waiting for the player {waited}";

        if (!HasSpicetify)
            return $"the extension loaded but Spicetify itself was not there {waited}";

        if (SpicetifyApiNeverStarted)
        {
            return $"the extension loaded and Spicetify's own API never started {waited} "
                + $"(Spicetify.Platform was still empty{(Error is null ? "" : $"; the player threw \"{Error}\"")})";
        }

        return $"the extension loaded and the player never became usable {waited}"
            + (Error is null ? "" : $" (it threw \"{Error}\")");
    }

    /// <summary>Null for anything that is not a diagnosis: the socket carries several kinds.</summary>
    public static SpicetifyDiagnosis? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "diagnosis") return null;

            return new SpicetifyDiagnosis(
                Flag(root, "ready"),
                Number(root, "waitedMs"),
                Flag(root, "spicetify"),
                Flag(root, "player"),
                Number(root, "platformKeys"),
                Text(root, "error"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
}
