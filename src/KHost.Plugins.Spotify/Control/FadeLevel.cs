using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Control;

/// <summary>The level a fade comes back to, and the level this end last wrote. A reading that is
/// not one we wrote is the host's hand on the slider, and the host's level always wins.</summary>
internal sealed class FadeLevel(double tolerance)
{
    private double? _low;
    private double _high;

    /// <summary>What the next restore comes back to; null until a fade out has read the room.</summary>
    public double? Target { get; private set; }

    /// <summary>What this end last wrote, as a range: a ramp cut off part way leaves a level
    /// somewhere between where it started and where it was heading, and that is still ours.</summary>
    public (double Low, double High)? Written => _low is { } low ? (low, _high) : null;

    public bool IsOurs(double reading)
        => _low is { } low && reading >= low - tolerance && reading <= _high + tolerance;

    /// <summary>Taken at the start of a fade out. Still at a level we wrote (silent, say) keeps
    /// the target; anything else is the room's level now, and is what comes back.</summary>
    public void NoteFadeOutFrom(double reading)
    {
        // Nothing written yet reads as not ours, so the first fade out always adopts.
        if (!IsOurs(reading))
            Target = reading;
    }

    /// <summary>The host moved Spotify while it was silent: that level is the room's, and is left alone.</summary>
    public void AdoptHostLevel(double reading)
    {
        Target = reading;
        NoteLanded(reading);
    }

    /// <summary>A level a fade before this process, or before this Spotify, never put back. Every
    /// reading counts as ours until it lands, so the record wins over whatever Spotify was left at.</summary>
    public void AwaitRestore(double target, double ceiling)
    {
        Target = target;
        _low = 0;
        _high = ceiling;
    }

    public void NoteLanded(double reading)
    {
        _low = reading;
        _high = reading;
    }

    /// <param name="from">Where the ramp started, when that was read before it was cut off.</param>
    public void NoteInterrupted(double towards, double? from = null)
    {
        var known = from ?? _low;

        if (known is not { } start)
            return;

        var low = Math.Min(start, towards);
        var high = Math.Max(start, towards);

        if (_low is { } writtenLow)
        {
            low = Math.Min(low, writtenLow);
            high = Math.Max(high, _high);
        }

        _low = low;
        _high = high;
    }
}

/// <summary>One fade at a time, and a newer one supersedes the older rather than queueing behind
/// it: two ramps writing in turn is what makes a fade stutter.</summary>
internal abstract class SupersedingFader : ISpotifyFader
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _current;

    protected SupersedingFader(ILogger logger, IPendingFadeLevels? pending)
    {
        Logger = logger;
        Pending = pending ?? new NoPendingFadeLevels();
    }

    protected ILogger Logger { get; }

    private IPendingFadeLevels Pending { get; }

    public abstract bool IsAvailable { get; }

    public Task<FadeOutcome> SilenceAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => RunAsync(silence: true, duration, cancellationToken);

    public Task<FadeOutcome> RestoreAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => RunAsync(silence: false, duration, cancellationToken);

    public Task<FadeOutcome> RestoreOnceQuietAsync(CancellationToken cancellationToken = default)
        => RunAsync(silence: false, TimeSpan.Zero, cancellationToken, wait: WaitForQuietAsync);

    public Task<FadeOutcome> RestoreOnceHeardAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => RunAsync(silence: false, duration, cancellationToken, wait: WaitForSoundAsync);

    /// <summary>Throws <see cref="OperationCanceledException"/> when superseded or cancelled.</summary>
    protected abstract Task<FadeOutcome> FadeAsync(bool silence, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Returns once Spotify has stopped sending sound. Waited out inside the gate, so a
    /// newer fade supersedes the wait as well as the restore behind it.</summary>
    protected virtual Task WaitForQuietAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Returns once Spotify has a level to set, or straight away where it always has one.</summary>
    protected virtual Task WaitForSoundAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A record that cannot be read is no record: the fade goes on adopting what it finds.</summary>
    protected IReadOnlyDictionary<string, double> ReadPending()
    {
        try
        {
            return Pending.Read();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read the Spotify level saved before an earlier fade");
            return new Dictionary<string, double>();
        }
    }

    /// <summary>Sets <paramref name="saved"/> and drops <paramref name="cleared"/>, writing only when
    /// that changes the record, since a write sits in front of every fade out.</summary>
    /// <remarks>A failed write costs only the protection against a restart: the fade still runs.</remarks>
    protected void UpdatePending(IReadOnlyDictionary<string, double> saved, IEnumerable<string> cleared)
    {
        try
        {
            var record = Pending.Read().ToDictionary(StringComparer.Ordinal);
            var changed = false;

            foreach (var key in cleared)
                changed |= record.Remove(key);

            foreach (var (key, level) in saved)
            {
                if (!record.TryGetValue(key, out var existing) || existing != level)
                {
                    record[key] = level;
                    changed = true;
                }
            }

            if (changed)
                Pending.Write(record);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not save Spotify's level before a fade; a restart mid-fade may leave it low");
        }
    }

    private async Task<FadeOutcome> RunAsync(
        bool silence, TimeSpan duration, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? wait = null)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (Interlocked.Exchange(ref _current, mine) is { } older)
        {
            try { older.Cancel(); } catch (ObjectDisposedException) { /* finished meanwhile */ }
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            mine.Token.ThrowIfCancellationRequested();

            if (wait is not null)
                await wait(mine.Token);

            return await FadeAsync(silence, duration < TimeSpan.Zero ? TimeSpan.Zero : duration, mine.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The newer fade owns the level now, so this one claims nothing.
            return FadeOutcome.Superseded;
        }
        finally
        {
            _gate.Release();
            Interlocked.CompareExchange(ref _current, null, mine);
            mine.Dispose();
        }
    }
}
