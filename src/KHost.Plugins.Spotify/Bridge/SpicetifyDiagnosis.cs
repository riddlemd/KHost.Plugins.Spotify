using System.Text.Json;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// Why the attached extension can or cannot drive Spotify, as the extension itself sees it.
/// </summary>
/// <remarks>
/// The extension is the only code on the inside of the client, and it opens the socket before it
/// checks whether it can work — so this arrives even from an extension that can do nothing else.
/// That is what makes the explanation identical on macOS, Windows and Linux: nothing here is
/// inferred from the host's own platform, because the host cannot see any of it.
/// </remarks>
public sealed record SpicetifyDiagnosis(
    bool Ready,
    int WaitedMilliseconds,
    bool HasSpicetify,
    bool HasPlayer,
    int PlatformKeys,
    string? Error)
{
    /// <summary>
    /// How long the extension waits for the player before its report is an answer rather than a
    /// snapshot. It opens its socket immediately, so the first report it sends is nearly always
    /// "not ready" — at that moment nothing has failed, and reading it as a fault accuses a
    /// Spicetify that is merely still starting.
    /// </summary>
    /// <remarks>Shorter than <see cref="SpicetifyBridgeSetup.GracePeriod"/>, which is when the
    /// host asks; <c>GracePeriodOutlastsTheExtensionsWait</c> holds the two together.</remarks>
    public const int VerdictAfterMilliseconds = 15000;

    /// <summary>
    /// Whether this report settles anything. Working is always an answer; not working is only an
    /// answer once the extension has waited the player out.
    /// </summary>
    public bool IsVerdict => Ready || WaitedMilliseconds >= VerdictAfterMilliseconds;

    /// <summary>
    /// Spicetify patched Spotify and then never bound to it: extensions load, and
    /// <c>Spicetify.Platform</c> stays an empty object for as long as anything waits on it. The
    /// signature of a Spicetify older than the Spotify it patched, and the one fault here that no
    /// amount of re-patching fixes — measured at a hundred seconds on the machine it was found on.
    /// </summary>
    public bool SpicetifyApiNeverStarted => IsVerdict && !Ready && HasSpicetify && PlatformKeys <= 0;

    /// <summary>
    /// One line naming what was seen, for the warning a host reads. Deliberately concrete: "the
    /// extension is attached and Spicetify's API never started" sends somebody to the right place,
    /// where "break music will not fade" sends them to KHost's own settings.
    /// </summary>
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

    /// <summary>Null for anything that is not a diagnosis — the socket carries several kinds.</summary>
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
