using KHost.Abstractions.Models;
using KHost.Abstractions.Services;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Fades the platform backend's commands through Spotify's own level, taking nothing
/// away when the level cannot be reached: every command still goes through, just unfaded.</summary>
internal sealed class FadingSpotifyController : ISpotifyController
{
    private readonly ISpotifyController _inner;
    private readonly ISpotifyFader _fader;
    private readonly TimeSpan _fade;
    private readonly IFlashService? _flash;

    /// <summary>Whether the last fade this end made left Spotify at silence.</summary>
    private volatile bool _silenced;

    /// <summary>Once per run of failures: told once that fades are not landing, not again on
    /// every command until one does.</summary>
    private bool _failureFlashed;

    public FadingSpotifyController(
        ISpotifyController inner, ISpotifyFader fader, TimeSpan fade, IFlashService? flash = null)
    {
        _inner = inner;
        _fader = fader;
        _fade = fade < TimeSpan.Zero ? TimeSpan.Zero : fade;
        _flash = flash;

        _inner.PlaybackChanged += (_, _) => PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A platform that cannot fade at all is said once at startup, rather than flashed
    /// at the host on every start.</summary>
    public string? Limitation => _fader.IsAvailable || _fade == TimeSpan.Zero
        ? _inner.Limitation
        : string.Join(" ", new[]
        {
            _inner.Limitation,
            "Break music is not faded on this platform: it starts and stops at full volume.",
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public event EventHandler? PlaybackChanged;

    /// <summary>Zero is the host turning fades off, which is not a failure to report.</summary>
    private bool CanFade => _fader.IsAvailable && _fade > TimeSpan.Zero;

    /// <summary>Also brings back a level an unfinished fade still owes: a KHost closed or killed
    /// mid-fade leaves Spotify low, and it may play on at that level before any command arrives.</summary>
    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.StartWatchingAsync(cancellationToken);
        }
        finally
        {
            // Not flashed: nobody asked for anything yet, and the first command reports a fault.
            if (CanFade && await _fader.RestoreOnceHeardAsync(_fade, cancellationToken) == FadeOutcome.Landed)
                _silenced = false;
        }
    }

    public Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
        => _inner.GetStateAsync(cancellationToken);

    /// <summary>Silenced before the backend starts it, and brought up once it plays.</summary>
    public async Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        // Waited for rather than fired off: a playlist that loads while the level is still up is
        // heard as a burst of it before the fade in has begun.
        var silence = await FadeOutAsync(TimeSpan.Zero, cancellationToken);

        if (!await _inner.StartAsync(contextUri, shuffle, cancellationToken))
        {
            // Nothing started, so the silence just set would be permanent.
            if (silence == FadeOutcome.Landed)
                await RestoreAsync(TimeSpan.Zero, cancellationToken);

            return false;
        }

        await ComeBackUpAsync(silence, cancellationToken);

        return true;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await SilenceAsync(_fade, cancellationToken);
        await _inner.PauseAsync(cancellationToken);
    }

    /// <summary>Silenced first even when already silent: the fade out reads the room, so a host
    /// who turned Spotify up while it sat paused has that level come back, not the old one.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var silence = await FadeOutAsync(TimeSpan.Zero, cancellationToken);

        await _inner.ResumeAsync(cancellationToken);

        await ComeBackUpAsync(silence, cancellationToken);
    }

    /// <summary>The level is put back once stopped, or Spotify is left muted for whoever reaches
    /// for it next.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var faded = await SilenceAsync(_fade, cancellationToken);

        await _inner.StopAsync(cancellationToken);

        if (faded && CanFade && Note(await _fader.RestoreOnceQuietAsync(cancellationToken)))
            _silenced = false;
    }

    /// <summary>Not faded while the music is up: a skip is meant to be heard as one. Out of a fade
    /// out the backend resumes a paused client, which would otherwise play to a silent room.</summary>
    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        await _inner.SkipAsync(cancellationToken);

        if (_silenced)
            await RestoreAsync(_fade, cancellationToken);
    }

    private async Task<bool> SilenceAsync(TimeSpan duration, CancellationToken cancellationToken)
        => await FadeOutAsync(duration, cancellationToken) == FadeOutcome.Landed;

    /// <summary>Null where nothing is faded at all.</summary>
    private async Task<FadeOutcome?> FadeOutAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!CanFade)
            return null;

        var outcome = await _fader.SilenceAsync(duration, cancellationToken);

        if (Note(outcome))
            _silenced = true;

        return outcome;
    }

    /// <summary>Nothing to fade means Spotify had no audio open, which is how a relaunched one looks:
    /// it may come back at a level an unfinished fade left, so that is waited for and put back.</summary>
    private async Task ComeBackUpAsync(FadeOutcome? silence, CancellationToken cancellationToken)
    {
        if (silence == FadeOutcome.Landed)
        {
            await RestoreAsync(_fade, cancellationToken);
        }
        else if (silence == FadeOutcome.NothingToFade
            && Note(await _fader.RestoreOnceHeardAsync(_fade, cancellationToken)))
        {
            _silenced = false;
        }
    }

    private async Task RestoreAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!CanFade)
            return;

        if (Note(await _fader.RestoreAsync(duration, cancellationToken)))
            _silenced = false;
    }

    private bool Note(FadeOutcome outcome)
    {
        if (outcome == FadeOutcome.Landed)
        {
            _failureFlashed = false;
            return true;
        }

        if (outcome == FadeOutcome.Failed && !_failureFlashed)
        {
            _failureFlashed = true;
            _flash?.Show(
                "Spotify: break music could not be faded, so it is starting and stopping at full volume. "
                + "The log says why.",
                FlashType.Warning);
        }

        return false;
    }
}
