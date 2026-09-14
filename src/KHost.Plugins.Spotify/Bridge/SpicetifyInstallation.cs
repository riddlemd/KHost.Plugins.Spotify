namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// Where Spicetify lives on this machine and whether the bridge extension is already part of it.
/// Path and file work only — the CLI is spawned to apply a change, never to find out whether one
/// is needed, so a host who is already set up pays nothing on every start.
/// </summary>
public sealed class SpicetifyInstallation
{
    public const string ExtensionFileName = "khost-bridge.js";

    private const string ConfigFileName = "config-xpui.ini";
    private const string ExtensionsFolderName = "Extensions";

    public required string ConfigDirectory { get; init; }

    public string ExtensionsDirectory => Path.Combine(ConfigDirectory, ExtensionsFolderName);

    public string InstalledExtensionPath => Path.Combine(ExtensionsDirectory, ExtensionFileName);

    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>
    /// Whether the Spotify on this machine is actually carrying the extension — the only question
    /// worth asking, and the one nothing else here answers.
    /// </summary>
    /// <remarks>
    /// Not the exit code: <c>spicetify apply</c> over a Spotify it cannot patch warns and exits
    /// zero, which is how an installer came to log that Spotify had been restarted to pick the
    /// extension up while leaving it nowhere near Spotify. Not the config line either, nor the
    /// file in Spicetify's own Extensions folder: both stay true after a Spotify update reverts
    /// the patch, which is exactly the case this has to catch.
    ///
    /// Applying replaces <c>Apps/xpui.spa</c> with an extracted <c>Apps/xpui/</c> directory and
    /// writes the extension inside it, so the file being there is the patch being live.
    /// </remarks>
    public bool IsSpotifyPatched()
    {
        if (SpotifyResourcesDirectory is not { } resources)
            return false;

        try
        {
            return File.Exists(Path.Combine(resources, "Apps", "xpui", "extensions", ExtensionFileName));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Spotify's Resources folder, as Spicetify's own config records it. Read from there rather
    /// than guessed, because a host may have Spotify somewhere this plugin would never look.
    /// </summary>
    public string? SpotifyResourcesDirectory => ReadConfigValue("spotify_path");

    private string? ReadConfigValue(string key)
    {
        if (!File.Exists(ConfigFilePath))
            return null;

        try
        {
            foreach (var line in File.ReadAllLines(ConfigFilePath))
            {
                var trimmed = line.TrimStart();

                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var separator = trimmed.IndexOf('=');

                if (separator < 0)
                    continue;

                var value = trimmed[(separator + 1)..].Trim();

                return value.Length > 0 ? value : null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static string CliFileName => OperatingSystem.IsWindows() ? "spicetify.exe" : "spicetify";

    /// <summary>The CLI, or null when Spicetify is not installed for this user.</summary>
    public static string? FindCli() => FindCli(SearchDirectories(), CliFileName);

    internal static string? FindCli(IEnumerable<string> directories, string fileName)
    {
        // A PATH entry can name anything at all; a junk one reads as "no file there" and the
        // search carries on past it.
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static SpicetifyInstallation? Discover() => Discover(ConfigDirectories());

    internal static SpicetifyInstallation? Discover(IEnumerable<string> candidates)
    {
        foreach (var directory in candidates)
        {
            if (Directory.Exists(directory))
                return new SpicetifyInstallation { ConfigDirectory = directory };
        }

        return null;
    }

    /// <summary>
    /// True only when the file on disk is the one this build ships and the config already lists it.
    /// Both halves matter: a stale copy fades against an older protocol, and a copy Spicetify was
    /// never told about is not loaded at all.
    /// </summary>
    public bool IsExtensionCurrent(string sourcePath)
    {
        try
        {
            return File.Exists(InstalledExtensionPath)
                && File.ReadAllBytes(InstalledExtensionPath).AsSpan().SequenceEqual(File.ReadAllBytes(sourcePath))
                && IsExtensionRegistered();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether config-xpui.ini's <c>extensions</c> line names the bridge.</summary>
    public bool IsExtensionRegistered()
    {
        if (!File.Exists(ConfigFilePath))
            return false;

        try
        {
            foreach (var line in File.ReadAllLines(ConfigFilePath))
            {
                var separator = line.IndexOf('=');

                if (separator < 0 || !line[..separator].Trim().Equals("extensions", StringComparison.OrdinalIgnoreCase))
                    continue;

                return line[(separator + 1)..]
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(entry => entry.Equals(ExtensionFileName, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (IOException)
        {
        }

        return false;
    }

    private static IEnumerable<string> ConfigDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "spicetify");

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "spicetify");

        if (string.IsNullOrEmpty(home))
            yield break;

        yield return Path.Combine(home, ".config", "spicetify");

        // Where installs before the XDG move put it, and still what those hosts have.
        yield return Path.Combine(home, ".spicetify");
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return directory;

        // The installer's own location, which a shell that never re-read its profile will not have.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".spicetify");
    }
}
