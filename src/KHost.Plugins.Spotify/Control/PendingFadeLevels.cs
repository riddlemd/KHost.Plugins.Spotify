using System.Text.Json;

namespace KHost.Plugins.Spotify.Control;

/// <summary>The levels a fade still owes Spotify, kept where neither a Spotify nor a KHost restart
/// loses them: Windows and Spotify both remember a faded level, and a relaunch otherwise reads that
/// 0 as the level the host chose.</summary>
/// <remarks>Keyed by whatever the platform keeps a level against, so each of Spotify's outputs
/// comes back to its own. Implementations may throw; the faders log it and fade on.</remarks>
internal interface IPendingFadeLevels
{
    /// <summary>Only levels a restore can use: a saved 0, or anything outside 0 to 1, is dropped.</summary>
    IReadOnlyDictionary<string, double> Read();

    /// <summary>Replaces the whole record; an empty one leaves nothing behind.</summary>
    void Write(IReadOnlyDictionary<string, double> levels);
}

/// <summary>Remembers nothing: a fader built without a record.</summary>
internal sealed class NoPendingFadeLevels : IPendingFadeLevels
{
    public IReadOnlyDictionary<string, double> Read() => new Dictionary<string, double>();

    public void Write(IReadOnlyDictionary<string, double> levels)
    {
    }
}

/// <summary>A small JSON file in the user's local app data, not the plugin folder (an update
/// replaces that) and not the host's settings (a plugin cannot write them).</summary>
/// <remarks>Per user rather than per host install, which is right: the level being protected is
/// Spotify's on this account, and every KHost on it fades the same Spotify.</remarks>
internal sealed class PendingFadeLevelFile(string path) : IPendingFadeLevels
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>Null where the OS names no local app data folder, as a stripped-down Linux may not.</summary>
    public static PendingFadeLevelFile? ForThisUser()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        return string.IsNullOrWhiteSpace(root)
            ? null
            : new PendingFadeLevelFile(System.IO.Path.Combine(root, "KHost.Plugins.Spotify", "pending-fade-levels.json"));
    }

    /// <summary>A file that does not parse is treated as no record: a guess at a level is worse
    /// than adopting the one Spotify reports.</summary>
    public IReadOnlyDictionary<string, double> Read()
    {
        if (!File.Exists(Path))
            return new Dictionary<string, double>();

        Record? record;

        try
        {
            record = JsonSerializer.Deserialize<Record>(File.ReadAllText(Path));
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>();
        }

        return (record?.Levels ?? [])
            .Where(entry => IsRestorable(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>Written beside the record and moved over it, so a crash mid-write leaves the old
    /// record or the new one, never half of either.</summary>
    public void Write(IReadOnlyDictionary<string, double> levels)
    {
        if (levels.Count == 0)
        {
            File.Delete(Path);
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Record { Levels = levels.ToDictionary() }, Options));
        File.Move(temporary, Path, overwrite: true);
    }

    internal static bool IsRestorable(double level) => double.IsFinite(level) && level > 0 && level <= 1;

    private sealed class Record
    {
        public Dictionary<string, double>? Levels { get; set; }
    }
}
