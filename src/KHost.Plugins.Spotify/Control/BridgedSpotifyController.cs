using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Control;

/// <summary>
/// Adds what the Spicetify extension can do to whatever the platform backend already did, and
/// takes nothing away when no extension is attached — which is most of the time, since installing
/// one is a deliberate act.
/// </summary>
/// <remarks>
/// A decorator rather than a replacement because the extension only exists once Spotify is
/// running, and something has to be able to start Spotify. Every command still reaches the
/// platform backend; the bridge only adds the fade in front of it.
/// </remarks>
public sealed class BridgedSpotifyController : ISpotifyController
{
    private readonly ISpotifyController _inner;
    private readonly SpicetifyBridge _bridge;
    private readonly TimeSpan _fade;

    /// <summary>
    /// Whether a fade out left Spotify's level at zero. Held because the level alone cannot say
    /// so — a host who pulled the slider down themselves is not something to fade back up from.
    /// </summary>
    private bool _silenced;

    public BridgedSpotifyController(ISpotifyController inner, SpicetifyBridge bridge, TimeSpan fade)
    {
        _inner = inner;
        _bridge = bridge;
        _fade = fade;

        _bridge.StateReceived += (_, state) => PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Only the backend's own limits are reported while an extension is attached, since the one it
    /// answers for — no fading — is the thing the extension is there to fix.
    /// </summary>
    public string? Limitation => _bridge.IsConnected ? null : _inner.Limitation;

    public event EventHandler? PlaybackChanged;

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
        => _inner.StartWatchingAsync(cancellationToken);

    /// <summary>
    /// The extension's report is preferred: it comes from inside the client, so it is current
    /// rather than however long ago the last process answered.
    /// </summary>
    public async Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsConnected && _bridge.LastState is { } state)
            return state.ToSpotifyState();

        return await _inner.GetStateAsync(cancellationToken);
    }

    /// <summary>
    /// The backend still starts it — only it can load a playlist, or launch Spotify — and the
    /// extension brings it up from silence once something is playing.
    /// </summary>
    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        // Waited for rather than fired off: a playlist that loads while the level is still up is
        // heard as a burst of it before the fade in has begun.
        var silenced = _bridge.IsConnected && await _bridge.SilenceAsync(TimeSpan.Zero, cancellationToken);

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

    /// <summary>
    /// One command, not a fade followed by a pause: the extension does both without a socket in
    /// the middle, and the gap between them is a gap the room would hear.
    /// </summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsConnected && await _bridge.PauseWithFadeOutAsync(_fade, cancellationToken))
        {
            _silenced = true;
            return;
        }

        await _inner.PauseAsync(cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_bridge.IsConnected && await _bridge.PlayWithFadeInAsync(_fade, cancellationToken))
        {
            _silenced = false;
            return;
        }

        await _inner.ResumeAsync(cancellationToken);
    }

    /// <summary>
    /// Stopping is the backend's — the extension can pause a client but not end a session — so the
    /// fade is asked for on its own and the room's own level put back while it is silent, rather
    /// than Spotify being left muted for whoever reaches for it next.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var faded = _bridge.IsConnected && await _bridge.SilenceAsync(_fade, cancellationToken);

        await _inner.StopAsync(cancellationToken);

        if (faded && await _bridge.RestoreAsync(TimeSpan.Zero, cancellationToken))
            _silenced = false;
    }

    /// <summary>
    /// Not faded while the music is up: a skip is meant to be heard as one, and the next track
    /// starts at level. Skipping out of a fade out is the exception — the backend's next track
    /// resumes a paused client, so without coming back up the new song plays to an empty room.
    /// </summary>
    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        await _inner.SkipAsync(cancellationToken);

        if (!_silenced || !_bridge.IsConnected) return;

        // The same command a resume uses: it is already the one that starts from silence and comes
        // back to the level the fade out was taken from, which is not a level this end holds.
        if (await _bridge.PlayWithFadeInAsync(_fade, cancellationToken))
            _silenced = false;
    }

    /// <summary>
    /// The venue's level, which the backend alone cannot apply. The extension takes it as the level
    /// every later fade returns to, so this is also how the room's level is set rather than guessed.
    /// </summary>
    public async Task<bool> SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        var level = Math.Clamp(volume, 0f, 1f);

        if (!_bridge.IsConnected || !await _bridge.SetVolumeAsync(level, cancellationToken))
            return false;

        _silenced = level <= 0f;

        return true;
    }

}
