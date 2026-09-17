using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Drives Spotify.app through its AppleScript dictionary, which has the discrete play,
/// pause and next commands this plugin needs, no toggle to keep track of.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsSpotifyController : ISpotifyController, IDisposable
{
    /// <summary>osascript's code for an Apple event the user has not granted Automation access to.</summary>
    private const string NotAuthorized = "-1743";

    /// <summary>Spotify accepts Apple events some way into launching, and the first command after
    /// a cold start is otherwise dropped. Only paid once, when Spotify was not already running.</summary>
    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly bool _launchIfNotRunning;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MacOsSpotifyController(ILogger logger, bool launchIfNotRunning)
        : this(logger, launchIfNotRunning,
            (file, arguments, token) => ProcessRunner.RunAsync(file, arguments, token),
            Task.Delay)
    {
    }

    /// <summary>The seams the watch below is tested through: without them a test either spawns
    /// osascript or waits out a real interval, asserting change detection by hand.</summary>
    internal MacOsSpotifyController(
        ILogger logger,
        bool launchIfNotRunning,
        Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _launchIfNotRunning = launchIfNotRunning;
        _run = run;
        _delay = delay;
    }

    public string? Limitation => null;

    /// <summary>Each ask is an osascript process, so this is a compromise, not a target: a track
    /// turning over is noticed within a few seconds, without a spawn every second all shift.</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

    /// <summary>Nothing is loaded, so a change can only come from someone reaching for Spotify's
    /// own window. Worth noticing, not worth the same cadence.</summary>
    private static readonly TimeSpan IdleWatchInterval = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _watchStopping = new();
    private Task? _watch;

    /// <summary>What the last poll saw. Compared rather than remembered as a flag: raising on
    /// every poll would have the host re-read Spotify three times a second for nothing new.</summary>
    private string? _lastSeen;

    /// <inheritdoc />
    public event EventHandler? PlaybackChanged;

    /// <summary>Polls, since macOS offers this process nothing to subscribe to: reaching Spotify's
    /// own distributed notification needs Objective-C interop this plugin does not have.</summary>
    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        _watch ??= Task.Run(() => WatchAsync(_watchStopping.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        // The first read is the baseline, not news: the provider has already asked by the time
        // this starts, and raising here would republish what the host just read.
        _lastSeen = await SignatureAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(
                    _lastSeen is null or "stopped" ? IdleWatchInterval : WatchInterval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var seen = await SignatureAsync(cancellationToken);

                if (seen == _lastSeen)
                    continue;

                _lastSeen = seen;

                PlaybackChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Spotify quitting, or Automation access withdrawn mid-shift. The host can still
                // ask before every decision; only the live display is lost, so this keeps trying.
                _logger.LogDebug(ex, "Could not read Spotify while watching it");
            }
        }
    }

    /// <summary>What is playing, as one string to compare. The track alone is not enough: pausing
    /// leaves it unchanged, exactly the case this watch exists for.</summary>
    private async Task<string?> SignatureAsync(CancellationToken cancellationToken)
    {
        if (await GetStateAsync(cancellationToken) is not { } state)
            return null;

        return state.Playback == SpotifyPlayback.Stopped
            ? "stopped"
            : $"{state.Playback}\u0000{state.Title}\u0000{state.Artist}";
    }

    public void Dispose()
    {
        _watchStopping.Cancel();
        _watchStopping.Dispose();
    }

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

    /// <summary>Stopped, not null, when Spotify is not running: the guard declining to start it is
    /// a definite answer, unlike osascript failing to run at all.</summary>
    public async Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _run("osascript", ["-e", MacOsScripts.State()], cancellationToken);

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

    /// <summary>Pause is as far as the dictionary goes: it exposes no stop command.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Pause(), cancellationToken);

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => RunAsync(MacOsScripts.Skip(), cancellationToken);

    /// <summary>False when Spotify was not running, or could not be reached at all.</summary>
    private async Task<bool> RunAsync(string script, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _run("osascript", ["-e", script], cancellationToken);

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
            var result = await _run("open", ["-gj", "-a", "Spotify"], cancellationToken);

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
