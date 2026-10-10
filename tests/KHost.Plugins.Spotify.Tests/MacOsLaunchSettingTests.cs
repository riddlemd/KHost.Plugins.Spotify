using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The venue can change launch-if-not-running while KHost runs, so the controller asks at
/// the moment Spotify turns out not to be running.</summary>
public class MacOsLaunchSettingTests
{
    private readonly List<string> _ran = [];

    // Everything fails: Spotify is not running, and the launch itself is refused so no real
    // settle delay is paid.
    private MacOsSpotifyController Controller(Func<bool> launch)
        => new(NullLogger.Instance, launch,
            run: (file, _, _) =>
            {
                _ran.Add(file);
                return Task.FromResult(new ProcessResult(1, string.Empty, "no"));
            },
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task StartAsync_LaunchWasTurnedOnAfterConstruction_LaunchesSpotify()
    {
        var launch = false;
        var controller = Controller(() => launch);

        launch = true;
        await controller.StartAsync(null, shuffle: true);

        Assert.Contains("open", _ran);
    }

    [Fact]
    public async Task StartAsync_LaunchWasTurnedOffAfterConstruction_DoesNotLaunchSpotify()
    {
        var launch = true;
        var controller = Controller(() => launch);

        launch = false;
        await controller.StartAsync(null, shuffle: true);

        Assert.DoesNotContain("open", _ran);
    }
}
