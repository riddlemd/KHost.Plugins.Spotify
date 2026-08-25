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
}
