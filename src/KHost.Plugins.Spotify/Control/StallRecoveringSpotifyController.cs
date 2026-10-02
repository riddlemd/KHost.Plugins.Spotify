using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Works around Spotify ending a track without starting the next: playback stops
/// outright, or the next track loads and sits at 0:00. Pressing play, then skipping, is what a
/// host would do by hand.</summary>
/// <remarks>Judged on each <see cref="ISpotifyController.PlaybackChanged"/>, so a backend with no
/// watch is never nudged. The queue is out of sight from outside the client, so a playlist that
/// simply ran out reads the same as a stall at its last track.</remarks>
internal sealed class StallRecoveringSpotifyController : ISpotifyController
{
    /// <summary>How close to the end counts as "the track ran out" rather than a pause near it.</summary>
    internal const long StallEndMs = 2000;

    /// <summary>A track loaded but sitting at zero is the other face of the same bug.</summary>
    internal const long StallStartMs = 250;

    internal static readonly TimeSpan StallConfirm = TimeSpan.FromSeconds(1);

    /// <summary>A workaround for somebody else's bug, so it is bounded: past this it lets the
    /// room fall silent rather than fighting whatever is really wrong.</summary>
    internal const int MaxNudges = 2;

    internal static readonly TimeSpan NudgeWindow = TimeSpan.FromSeconds(60);

    private readonly ISpotifyController _inner;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly List<DateTimeOffset> _nudges = [];

    /// <summary>Set by the host's own pause or stop, before its fade, and cleared once Spotify is
    /// seen playing: resuming a pause the host asked for is worse than the bug this recovers from.</summary>
    private volatile bool _pausedByHost;

    private bool _seenATrack;
    private string? _lastTitle;
    private DateTimeOffset _songChangedAt = DateTimeOffset.MinValue;

    private int _checking;
    private Task _lastCheck = Task.CompletedTask;

    public StallRecoveringSpotifyController(ISpotifyController inner, TimeProvider time, ILogger logger)
    {
        _inner = inner;
        _time = time;
        _logger = logger;

        _inner.PlaybackChanged += (_, _) =>
        {
            PlaybackChanged?.Invoke(this, EventArgs.Empty);

            // One at a time, and never during a nudge: the nudge's own play raises a change too.
            if (Interlocked.CompareExchange(ref _checking, 1, 0) == 0)
                _lastCheck = CheckAsync();
        };
    }

    public string? Limitation => _inner.Limitation;

    public event EventHandler? PlaybackChanged;

    /// <summary>The check the last change started, for a test to wait on.</summary>
    internal Task LastCheck => _lastCheck;

    /// <summary>Whether a stop at this playhead is Spotify's fault rather than somebody's hand.</summary>
    internal static bool LooksLikeAStall(long? progressMs, long? durationMs, TimeSpan sinceSongChange)
    {
        if (progressMs is not { } progress || durationMs is not { } duration || duration <= 0)
            return false;

        if (duration - progress <= StallEndMs)
            return true;

        // Only just after a track change: a host who pauses a track they have just started is at
        // the beginning of it too, and that is theirs to do.
        return progress <= StallStartMs && sinceSongChange <= StallConfirm;
    }

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
        => _inner.StartWatchingAsync(cancellationToken);

    public Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
        => _inner.GetStateAsync(cancellationToken);

    public Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        _pausedByHost = false;
        return _inner.StartAsync(contextUri, shuffle, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        // Before the fade, not after: a track that ends during it must not read as a stall.
        _pausedByHost = true;
        return _inner.PauseAsync(cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        _pausedByHost = false;
        return _inner.ResumeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _pausedByHost = true;
        return _inner.StopAsync(cancellationToken);
    }

    public Task SkipAsync(CancellationToken cancellationToken = default)
        => _inner.SkipAsync(cancellationToken);

    private async Task CheckAsync()
    {
        try
        {
            if (await _inner.GetStateAsync() is not { } state)
                return;

            var now = _time.GetUtcNow();

            if (state.Title != _lastTitle)
            {
                // The first track seen is a baseline, not a change.
                if (_seenATrack)
                    _songChangedAt = now;

                _seenATrack = true;
                _lastTitle = state.Title;
            }

            if (state.Playback == SpotifyPlayback.Playing)
            {
                // Whatever started it, the room is playing again and the host's pause is over.
                _pausedByHost = false;
                return;
            }

            if (_pausedByHost || !LooksLikeAStall(state.ProgressMs, state.DurationMs, now - _songChangedAt))
                return;

            if (!MayNudge(now))
            {
                _logger.LogInformation(
                    "Spotify looks stalled on {Title}, but it has been nudged {Count} times in the last {Window}; leaving it",
                    state.Title, MaxNudges, NudgeWindow);
                return;
            }

            await NudgeAsync(state, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recover Spotify from a stall");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Bounded per window rather than per track: a fault that returns every time would
    /// otherwise be nudged all night, and the log would say it recovered each time.</summary>
    private bool MayNudge(DateTimeOffset now)
    {
        _nudges.RemoveAll(at => now - at >= NudgeWindow);

        return _nudges.Count < MaxNudges;
    }

    private async Task NudgeAsync(SpotifyState state, DateTimeOffset now)
    {
        _nudges.Add(now);

        _logger.LogInformation(
            "Spotify stopped at {Progress} of {Duration} ms on {Title} with nobody asking; pressing play",
            state.ProgressMs, state.DurationMs, state.Title);

        // Play first: for the sits-at-0:00 face of the bug it is the whole fix, and skipping
        // straight to next would lose a track nobody has heard.
        await _inner.ResumeAsync();
        await Task.Delay(StallConfirm, _time);

        if ((await _inner.GetStateAsync())?.Playback == SpotifyPlayback.Playing)
        {
            _logger.LogInformation("Spotify recovered from the stall by playing");
            return;
        }

        if (_pausedByHost)
            return;

        _logger.LogInformation("Play did not take; skipping to the next track");

        await _inner.SkipAsync();
        await Task.Delay(StallConfirm, _time);

        if ((await _inner.GetStateAsync())?.Playback == SpotifyPlayback.Playing)
            _logger.LogInformation("Spotify recovered from the stall by skipping");
        else
            _logger.LogWarning("Spotify did not recover from the stall; leaving it stopped");
    }
}
