using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

public class SpotifyControllerFactoryTests
{
    // Only the branch for the running OS is reachable here, which is the one that matters: it is the
    // controller a host on this machine actually gets, and a swapped case would hand it the wrong one.
    [Fact]
    public void ForCurrentPlatform_ReturnsTheControllerForThisOS()
    {
        var controller = SpotifyControllerFactory.ForCurrentPlatform(NullLogger.Instance, launchIfNotRunning: false);

        if (OperatingSystem.IsMacOS())
            Assert.IsType<MacOsSpotifyController>(controller);
        else if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsSpotifyController>(controller);
        else if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxSpotifyController>(controller);
        else
            Assert.IsType<UnsupportedSpotifyController>(controller);
    }
}
