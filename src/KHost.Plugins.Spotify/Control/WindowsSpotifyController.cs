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
/// Two consequences a host will meet. The keys are global, so they reach whichever app currently
/// owns media focus — normally Spotify, but not guaranteed on a machine running another player.
/// And Windows has a play/pause toggle and no discrete play or pause, so this tracks what it last
/// commanded; a host who pauses in Spotify's own window puts that record out of step until the
/// next start.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpotifyController : ISpotifyController
{
    private const byte MediaNextTrack = 0xB0;
    private const byte MediaStop = 0xB2;
    private const byte MediaPlayPause = 0xB3;
    private const uint KeyEventKeyUp = 0x0002;

    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly bool _launchIfNotRunning;
    private readonly Lock _gate = new();

    private bool _playing;

    public WindowsSpotifyController(ILogger logger, bool launchIfNotRunning)
    {
        _logger = logger;
        _launchIfNotRunning = launchIfNotRunning;
    }

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

            // The URI loads the playlist; whether it also starts is Spotify's call, so this is the
            // one place the toggle is sent blind.
            Send(MediaPlayPause);

            lock (_gate) _playing = true;

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

        Toggle(toPlaying: true);

        return true;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Toggle(toPlaying: false);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Toggle(toPlaying: true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Send(MediaStop);

        lock (_gate) _playing = false;

        return Task.CompletedTask;
    }

    public Task SkipAsync(CancellationToken cancellationToken = default)
    {
        Send(MediaNextTrack);
        return Task.CompletedTask;
    }

    /// <summary>Sends the toggle only when the record says it would land the right way up.</summary>
    private void Toggle(bool toPlaying)
    {
        lock (_gate)
        {
            if (_playing == toPlaying)
                return;

            _playing = toPlaying;
        }

        Send(MediaPlayPause);
    }

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
