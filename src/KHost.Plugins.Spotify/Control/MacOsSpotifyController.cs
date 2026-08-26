using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Drives Spotify.app through its AppleScript dictionary, which has the discrete play, pause and
/// next commands this plugin needs — no toggle to keep track of.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsSpotifyController : ISpotifyController
{
    /// <summary>osascript's code for an Apple event the user has not granted Automation access to.</summary>
    private const string NotAuthorized = "-1743";

    /// <summary>
    /// Spotify accepts Apple events some way into launching, and the first command after a cold
    /// start is otherwise dropped. Only paid once, when Spotify was not already running.
    /// </summary>
    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly bool _launchIfNotRunning;

    public MacOsSpotifyController(ILogger logger, bool launchIfNotRunning)
    {
        _logger = logger;
        _launchIfNotRunning = launchIfNotRunning;
    }

    public string? Limitation => null;

    /// <summary>
    /// Never raised. Spotify posts com.spotify.client.PlaybackStateChanged as a distributed
    /// notification, which is what this would listen to; until then the host asks rather than
    /// being told, and only misses a change the host made in Spotify's own window.
    /// </summary>
    public event EventHandler? PlaybackChanged { add { } remove { } }

    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        var script = MacOsScripts.Play(contextUri, shuffle);

        if (await RunAsync(script, cancellationToken))
            return true;

        if (!_launchIfNotRunning)
        {
            _logger.LogInformation("Spotify is not running and this plugin is set not to launch it");
            return false;
        }

        if (!await LaunchAsync(cancellationToken))
            return false;

        return await RunAsync(script, cancellationToken);
    }

    /// <summary>
    /// Stopped, not null, when Spotify is not running: the guard declining to start it is a
    /// definite answer, unlike osascript failing to run at all.
    /// </summary>
    public async Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("osascript", ["-e", MacOsScripts.State()], cancellationToken);

            if (!result.Succeeded)
                return null;

            return MacOsScripts.ParseState(result.StandardOutput) ?? SpotifyState.Stopped;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Spotify's state");
            return null;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Pause(), cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Play(contextUri: null, shuffle: false), cancellationToken);

    /// <summary>Pause is as far as the dictionary goes — it exposes no stop command.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Pause(), cancellationToken);

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Skip(), cancellationToken);

    /// <summary>False when Spotify was not running, or could not be reached at all.</summary>
    private async Task<bool> RunAsync(string script, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("osascript", ["-e", script], cancellationToken);

            if (result.Succeeded)
                return MacOsScripts.ReachedSpotify(result.StandardOutput);

            if (result.Message.Contains(NotAuthorized, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "macOS has not granted this app permission to control Spotify. Approve it under "
                    + "System Settings > Privacy & Security > Automation.");
            }
            else
            {
                _logger.LogWarning("Spotify rejected a command: {Message}", result.Message);
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Spotify did not answer in time");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach Spotify");
            return false;
        }
    }

    private async Task<bool> LaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            // -g and -j keep it behind the console: this is an appliance, and Spotify stealing the
            // window mid-shift is worse than no break music.
            var result = await ProcessRunner.RunAsync("open", ["-gj", "-a", "Spotify"], cancellationToken);

            if (!result.Succeeded)
            {
                _logger.LogWarning("Could not launch Spotify: {Message}", result.Message);
                return false;
            }

            await Task.Delay(LaunchSettle, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not launch Spotify");
            return false;
        }
    }
}
