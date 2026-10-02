using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Drives Spotify through its own row on the system media transport, falling back to the
/// keyboard's media keys only while Spotify has no row.</summary>
/// <remarks>A media key lands on whichever app owns media focus, so it is the last resort: KHost's
/// own screen or a browser tab takes focus the moment it plays anything.</remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpotifyController : ISpotifyController
{
    private const byte MediaNextTrack = 0xB0;
    private const byte MediaStop = 0xB2;
    private const byte MediaPlayPause = 0xB3;
    private const uint KeyEventKeyUp = 0x0002;

    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(5);

    /// <summary>Wide enough to swallow a pair raised a millisecond apart, short enough that the
    /// console still follows the room.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    private int _raiseGeneration;

    /// <summary>Spotify's own session id on the system media transport.</summary>
    internal const string SessionAppId = "Spotify.exe";

    private readonly ILogger _logger;
    private readonly bool _launchIfNotRunning;

    public WindowsSpotifyController(ILogger logger, bool launchIfNotRunning)
    {
        _logger = logger;
        _launchIfNotRunning = launchIfNotRunning;
    }

    public event EventHandler? PlaybackChanged;

    public string? Limitation =>
        "On Windows Spotify is controlled through its media session, which appears once it has "
        + "played something. Until then the media keys are used, and they reach whichever app "
        + "currently owns media focus.";

    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        // Shuffle is Spotify's own sticky setting and there is no key for it. Left as the host set
        // it in Spotify rather than silently claiming to have changed it.
        if (shuffle)
            _logger.LogInformation("Shuffle is left to Spotify's own setting on Windows");

        if (contextUri is not null)
        {
            if (!Launch(contextUri))
                return false;

            await Task.Delay(LaunchSettle, cancellationToken);

            // The URI loads the playlist; whether it also starts is Spotify's call, so settle and
            // ask rather than sending a toggle that might stop what it just started.
            await ToggleToAsync(SpotifyPlayback.Playing, cancellationToken);

            return true;
        }

        if (!IsRunning())
        {
            if (!_launchIfNotRunning)
            {
                _logger.LogInformation("Spotify is not running and this plugin is set not to launch it");
                return false;
            }

            if (!Launch("spotify:"))
                return false;

            await Task.Delay(LaunchSettle, cancellationToken);
        }

        await ToggleToAsync(SpotifyPlayback.Playing, cancellationToken);

        return true;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => ToggleToAsync(SpotifyPlayback.Paused, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => ToggleToAsync(SpotifyPlayback.Playing, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => CommandAsync(SessionCommand.Stop, MediaStop, cancellationToken);

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => CommandAsync(SessionCommand.Next, MediaNextTrack, cancellationToken);

    /// <summary>Decides whether the one key Windows offers would land the right way up. Unknown
    /// state sends it: a backend that cannot see is no worse off than doing nothing.</summary>
    internal static bool ShouldSendToggle(SpotifyState? state, SpotifyPlayback target)
    {
        if (state is null)
            return true;

        // Stopped has nothing to resume, but the key is still the only way to try.
        if (target == SpotifyPlayback.Playing)
            return state.Playback != SpotifyPlayback.Playing;

        return state.Playback == SpotifyPlayback.Playing;
    }

    private async Task ToggleToAsync(SpotifyPlayback target, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);

        if (!ShouldSendToggle(state, target))
        {
            _logger.LogDebug("Spotify is already {Target}; leaving it alone", target);
            return;
        }

        await CommandAsync(
            target == SpotifyPlayback.Playing ? SessionCommand.Play : SessionCommand.Pause,
            MediaPlayPause,
            cancellationToken);
    }

    private enum SessionCommand { Play, Pause, Stop, Next }

    /// <summary>Sends the command to Spotify's session; the key goes out only when there is none,
    /// since a refusal from the session means Spotify itself declined and the key would reach it
    /// too — or, worse, whatever else holds media focus.</summary>
    private async Task CommandAsync(SessionCommand command, byte fallbackKey, CancellationToken cancellationToken)
    {
        var accepted = await TrySessionCommandAsync(command, cancellationToken);

        if (accepted is null)
        {
            _logger.LogDebug("Spotify has no media session yet; sending the media key for {Command}", command);
            Send(fallbackKey);
        }
        else if (accepted == false)
        {
            _logger.LogInformation("Spotify declined {Command}; it may have nothing loaded to play", command);
        }
    }

#if WINDOWS_MEDIA_SESSION
    /// <summary>Held for the life of this controller: the session raises nothing once it is
    /// collected, and a watch that stops after garbage collection is worse than no watch.</summary>
    private Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private Windows.Media.Control.GlobalSystemMediaTransportControlsSession? _watchedSession;

    /// <summary>Subscribes to Spotify's own row on the system media transport, so a host pressing
    /// pause in Spotify's window reaches the console without polling all shift.</summary>
    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _sessionManager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().AsTask(cancellationToken);

            _sessionManager.SessionsChanged += (_, _) => RebindSession();

            RebindSession();
        }
        catch (Exception ex)
        {
            // Without a watch the host still asks before every decision; only the live display is lost.
            _logger.LogDebug(ex, "Could not watch Spotify's media session");
        }
    }

    private void RebindSession()
    {
        try
        {
            var session = _sessionManager?.GetSessions()
                .FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, SessionAppId, StringComparison.OrdinalIgnoreCase));

            if (ReferenceEquals(session, _watchedSession))
                return;

            _watchedSession = session;

            if (session is null)
                return;

            session.PlaybackInfoChanged += (_, _) => RaiseCoalesced();
            session.MediaPropertiesChanged += (_, _) => RaiseCoalesced();

            // Spotify appearing at all is itself news: it may have come up already playing.
            RaiseCoalesced();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not bind to Spotify's media session");
        }
    }

    /// <summary>Both events above fire per turn; only the last raise in a window gets through.</summary>
    /// <remarks>Trailing edge: the second event carries the settled track name.</remarks>
    private void RaiseCoalesced()
    {
        var generation = Interlocked.Increment(ref _raiseGeneration);

        _ = Task.Delay(CoalesceWindow).ContinueWith(
            _ =>
            {
                if (Volatile.Read(ref _raiseGeneration) == generation)
                    PlaybackChanged?.Invoke(this, EventArgs.Empty);
            },
            TaskScheduler.Default);
    }

    /// <summary>Reads Spotify's own row on the system media transport, the same one the volume
    /// flyout shows, filtered by session id so another focused player is not mistaken for it.</summary>
    public async Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().AsTask(cancellationToken);

            var session = manager.GetSessions()
                .FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, SessionAppId, StringComparison.OrdinalIgnoreCase));

            // No row at all means Spotify has never played this session, which is stopped.
            if (session is null)
                return SpotifyState.Stopped;

            var playback = session.GetPlaybackInfo().PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => SpotifyPlayback.Playing,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => SpotifyPlayback.Paused,
                _ => SpotifyPlayback.Stopped,
            };

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);

            long? progressMs = null;
            long? durationMs = null;

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                // Position is as of the session's last update, which is exact while paused: the
                // only time the playhead is asked for, to tell a stall from a pause.
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;

                if (duration > TimeSpan.Zero)
                {
                    progressMs = (long)(timeline.Position - timeline.StartTime).TotalMilliseconds;
                    durationMs = (long)duration.TotalMilliseconds;
                }
            }

            return new SpotifyState(playback, properties?.Title, properties?.Artist, progressMs, durationMs);
        }
        catch (Exception ex)
        {
            // Null, not Stopped: "cannot see" and "is not playing" lead to different decisions.
            _logger.LogDebug(ex, "Could not read Spotify's media session");
            return null;
        }
    }

    /// <summary>True or false for Spotify's own answer; null when it has no session to ask.</summary>
    private async Task<bool?> TrySessionCommandAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().AsTask(cancellationToken);

            var session = manager.GetSessions()
                .FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, SessionAppId, StringComparison.OrdinalIgnoreCase));

            if (session is null)
                return null;

            var operation = command switch
            {
                SessionCommand.Play => session.TryPlayAsync(),
                SessionCommand.Pause => session.TryPauseAsync(),
                SessionCommand.Stop => session.TryStopAsync(),
                _ => session.TrySkipNextAsync(),
            };

            return await operation.AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not reach Spotify's media session");
            return null;
        }
    }
#else
    private Task<bool?> TrySessionCommandAsync(SessionCommand command, CancellationToken cancellationToken)
        => Task.FromResult<bool?>(null);

    /// <summary>Built without the Windows media session projection, so nothing can be read back.</summary>
    public Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SpotifyState?>(null);
#endif

    private static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Spotify");

        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private bool Launch(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not launch Spotify. Is the desktop app installed?");
            return false;
        }
    }

    private static void Send(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    // DllImport rather than LibraryImport: the signature is entirely blittable, so the generated
    // marshaller would buy nothing and only forces AllowUnsafeBlocks on the whole assembly.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
