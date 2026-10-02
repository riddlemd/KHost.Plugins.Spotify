using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace KHost.Plugins.Spotify.Control;

public static class SpotifyControllerFactory
{
    public static ISpotifyController ForCurrentPlatform(ILogger logger, bool launchIfNotRunning)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOsSpotifyController(logger, launchIfNotRunning);

        if (OperatingSystem.IsWindows())
            return new WindowsSpotifyController(logger, launchIfNotRunning);

        if (OperatingSystem.IsLinux())
            return new LinuxSpotifyController(logger, launchIfNotRunning);

        return new UnsupportedSpotifyController(RuntimeInformation.OSDescription);
    }

    /// <summary>What reaches Spotify's level here. Linux is left unfaded: MPRIS volume is the
    /// player's own slider, and moving it is moving the host's level.</summary>
    internal static ISpotifyFader FaderForCurrentPlatform(ILogger logger)
    {
        var pending = PendingFadeLevelFile.ForThisUser();

        if (OperatingSystem.IsMacOS())
            return new MacOsSpotifyFader(logger, pending);

        if (OperatingSystem.IsWindows())
            return new WindowsSpotifyFader(logger, new CoreAudioSpotifySessions(), Task.Delay, pending);

        return new UnavailableSpotifyFader();
    }
}
