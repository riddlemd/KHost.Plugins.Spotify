using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Drives Spotify over MPRIS, the desktop-media bus every Linux player answers on. Discrete Play,
/// Pause, Stop and Next, so nothing here has to know what Spotify is currently doing.
/// </summary>
/// <remarks>
/// gdbus is shelled out to rather than taking a D-Bus client dependency: a plugin's dependencies
/// are copied into the host's plugin folder, and glib ships gdbus on any desktop that has Spotify.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxSpotifyController : ISpotifyController
{
    private const string Destination = "org.mpris.MediaPlayer2.spotify";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly bool _launchIfNotRunning;

    public LinuxSpotifyController(ILogger logger, bool launchIfNotRunning)
    {
        _logger = logger;
        _launchIfNotRunning = launchIfNotRunning;
    }

    public string? Limitation => null;

    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        if (!await PlayAsync(contextUri, shuffle, cancellationToken))
        {
            if (!_launchIfNotRunning)
            {
                _logger.LogInformation("Spotify is not running and this plugin is set not to launch it");
                return false;
            }

            if (!await LaunchAsync(cancellationToken))
                return false;

            return await PlayAsync(contextUri, shuffle, cancellationToken);
        }

        return true;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => CallAsync($"{PlayerInterface}.Pause", [], cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => CallAsync($"{PlayerInterface}.Play", [], cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => CallAsync($"{PlayerInterface}.Stop", [], cancellationToken);

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => CallAsync($"{PlayerInterface}.Next", [], cancellationToken);

    private async Task<bool> PlayAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken)
    {
        if (shuffle && !await SetShuffleAsync(cancellationToken))
            return false;

        return contextUri is null
            ? await CallAsync($"{PlayerInterface}.Play", [], cancellationToken)
            : await CallAsync($"{PlayerInterface}.OpenUri", [contextUri], cancellationToken);
    }

    private Task<bool> SetShuffleAsync(CancellationToken cancellationToken)
        => CallAsync("org.freedesktop.DBus.Properties.Set", [PlayerInterface, "Shuffle", "<true>"], cancellationToken);

    private async Task<bool> CallAsync(string method, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        List<string> gdbusArguments =
        [
            "call", "--session",
            "--dest", Destination,
            "--object-path", ObjectPath,
            "--method", method,
            .. arguments,
        ];

        try
        {
            var result = await ProcessRunner.RunAsync("gdbus", gdbusArguments, cancellationToken);

            if (!result.Succeeded)
            {
                _logger.LogWarning("Spotify rejected {Method}: {Message}", method, result.Message);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Spotify did not answer {Method} in time", method);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach Spotify over MPRIS");
            return false;
        }
    }

    /// <summary>
    /// Started and left running, not awaited: the Spotify binary does not exit, so waiting on it
    /// would spend the whole command timeout every launch.
    /// </summary>
    private async Task<bool> LaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("spotify") { UseShellExecute = false });

            if (process is null)
            {
                _logger.LogWarning("Could not launch Spotify: no 'spotify' on PATH");
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
