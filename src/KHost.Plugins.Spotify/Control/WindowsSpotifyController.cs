using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Drives Spotify with the keyboard's media keys, which is the only transport Windows offers
/// without reading anything back out of the app.
/// </summary>
/// <remarks>
/// The keys are global, so they reach whichever app currently owns media focus — normally
/// Spotify, but not guaranteed on a machine running another player. Windows also has a
/// play/pause toggle and no discrete play or pause, so a command has to know which way the
/// toggle will land: <see cref="GetStateAsync"/> reads that from the system media session
/// rather than remembering what was last sent, which is what keeps a host who pressed play in
/// Spotify's own window from being toggled back off.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpotifyController : ISpotifyController
{
    private const byte MediaNextTrack = 0xB0;
    private const byte MediaStop = 0xB2;
    private const byte MediaPlayPause = 0xB3;
    private const uint KeyEventKeyUp = 0x0002;

    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(5);

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
        "On Windows the media keys reach whichever app currently owns media focus, which is "
        + "Spotify only while nothing else is playing.";

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
    {
        Send(MediaStop);
        return Task.CompletedTask;
    }

    public Task SkipAsync(CancellationToken cancellationToken = default)
    {
        Send(MediaNextTrack);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Decides whether the one key Windows offers would land the right way up. Unknown state
    /// sends it: a backend that cannot see is no worse off than the old remembered flag, and
    /// doing nothing would leave a host pressing play to silence.
    /// </summary>
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

        if (ShouldSendToggle(state, target))
            Send(MediaPlayPause);
        else
            _logger.LogDebug("Spotify is already {Target}; leaving the media key alone", target);
    }

#if WINDOWS_MEDIA_SESSION
    /// <summary>
    /// Held for the life of this controller: the session raises nothing once it is collected, and
    /// a watch that stops after the first garbage collection is worse than no watch at all.
    /// </summary>
    private Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private Windows.Media.Control.GlobalSystemMediaTransportControlsSession? _watchedSession;

    /// <summary>
    /// Subscribes to Spotify's own row on the system media transport, so a host pressing pause in
    /// Spotify's window or on the keyboard reaches the console without anything polling all shift.
    /// Sessions come and go with the app, so the manager is watched too and the session re-bound.
    /// </summary>
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

            session.PlaybackInfoChanged += (_, _) => PlaybackChanged?.Invoke(this, EventArgs.Empty);
            session.MediaPropertiesChanged += (_, _) => PlaybackChanged?.Invoke(this, EventArgs.Empty);

            // Spotify appearing at all is itself news: it may have come up already playing.
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not bind to Spotify's media session");
        }
    }

    /// <summary>
    /// Reads Spotify's own row on the system media transport — the same one the volume flyout
    /// shows. Filtered by session id rather than taking the current session, so another player
    /// holding media focus reports as itself instead of being mistaken for Spotify.
    /// </summary>
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

            return new SpotifyState(playback, properties?.Title, properties?.Artist);
        }
        catch (Exception ex)
        {
            // Null, not Stopped: "cannot see" and "is not playing" lead to different decisions.
            _logger.LogDebug(ex, "Could not read Spotify's media session");
            return null;
        }
    }
#else
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
