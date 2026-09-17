using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Adds what Spicetify can do atop the platform backend, taking nothing away unattached.</summary>
/// <remarks>A decorator: the extension exists only once Spotify runs, so something else starts it.</remarks>
public sealed class BridgedSpotifyController : ISpotifyController
{
    private readonly ISpotifyController _inner;
    private readonly SpicetifyBridge _bridge;
    private readonly TimeSpan _fade;

    /// <summary>Anything at or under this is silence. Loose, since Spotify rounds what it reports.</summary>
    private const float Silence = 0.005f;

    /// <summary>Believed from what this end last asked for, corrected by what the extension
    /// reports: a host who moved the volume in Spotify's own window told nobody here.</summary>
    private volatile bool _silenced;

    public BridgedSpotifyController(ISpotifyController inner, SpicetifyBridge bridge, TimeSpan fade)
    {
        _inner = inner;
        _bridge = bridge;
        _fade = fade;

        _bridge.StateReceived += (_, state) =>
        {
            // Read from inside the client, so it outranks anything believed here, including a
            // room this end never silenced (a restart over already-quiet break music).
            _silenced = state.Volume <= Silence;

            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        };

        // Passed through: without this, an unpatched Spotify (an update can silently undo it)
        // left the console never told a track turned over, since only the bridge raised this.
        _inner.PlaybackChanged += (_, _) =>
        {
            // The bridge outranks it when ready, to avoid two announcements for one change. Checked
            // on readiness, not attachment, or a stalled extension silences a backend that still works.
            if (!_bridge.IsReady)
                PlaybackChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Only the backend's own limits are reported while attached: the one it answers
    /// for, no fading, is the thing the extension is there to fix.</summary>
    public string? Limitation => _bridge.IsReady ? null : _inner.Limitation;

    public event EventHandler? PlaybackChanged;

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
        => _inner.StartWatchingAsync(cancellationToken);

    /// <summary>The extension's report is preferred: it comes from inside the client, so it is
    /// current rather than however long ago the last process answered.</summary>
    public async Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsReady && _bridge.LastState is { } state)
            return state.ToSpotifyState();

        return await _inner.GetStateAsync(cancellationToken);
    }

    /// <summary>The backend still starts it, since only it can load a playlist or launch Spotify,
    /// and the extension brings it up from silence once something is playing.</summary>
    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        // Waited for rather than fired off: a playlist that loads while the level is still up is
        // heard as a burst of it before the fade in has begun.
        var silenced = _bridge.IsReady && await _bridge.SilenceAsync(TimeSpan.Zero, cancellationToken);

        _silenced = silenced;

        if (!await _inner.StartAsync(contextUri, shuffle, cancellationToken))
        {
            // Nothing started, so the silence just set would be permanent.
            if (silenced) await _bridge.RestoreAsync(TimeSpan.Zero, cancellationToken);
            _silenced = false;
            return false;
        }

        if (silenced && await _bridge.RestoreAsync(_fade, cancellationToken))
            _silenced = false;

        return true;
    }

    /// <summary>One command, not a fade followed by a pause: the extension does both without a
    /// socket in the middle, and the gap between them is a gap the room would hear.</summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsReady && await _bridge.PauseWithFadeOutAsync(_fade, cancellationToken))
        {
            _silenced = true;
            return;
        }

        await _inner.PauseAsync(cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsReady && await _bridge.PlayWithFadeInAsync(_fade, cancellationToken))
        {
            _silenced = false;
            return;
        }

        await _inner.ResumeAsync(cancellationToken);
    }

    /// <summary>Stopping is the backend's: the extension can pause a client but not end a
    /// session, so the level is put back while silent rather than leaving Spotify muted.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var faded = _bridge.IsReady && await _bridge.SilenceAsync(_fade, cancellationToken);

        await _inner.StopAsync(cancellationToken);

        if (faded && await _bridge.RestoreAsync(TimeSpan.Zero, cancellationToken))
            _silenced = false;
    }

    /// <summary>Not faded while the music is up: a skip is meant to be heard as one, and the next
    /// track starts at level, except out of a fade out, where the backend resumes a paused client.</summary>
    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        await _inner.SkipAsync(cancellationToken);

        if (!_silenced || !_bridge.IsReady) return;

        // The same command a resume uses: it already starts from silence and comes back
        // to the level the fade out was taken from, a level this end does not hold.
        if (await _bridge.PlayWithFadeInAsync(_fade, cancellationToken))
            _silenced = false;
    }

    /// <summary>The venue's level, which the backend alone cannot apply. The extension takes it
    /// as the level every later fade returns to, so this is how the room's level is set.</summary>
    public async Task<bool> SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        var level = Math.Clamp(volume, 0f, 1f);

        if (!_bridge.IsReady || !await _bridge.SetVolumeAsync(level, cancellationToken))
            return false;

        _silenced = level <= Silence;

        return true;
    }

}
