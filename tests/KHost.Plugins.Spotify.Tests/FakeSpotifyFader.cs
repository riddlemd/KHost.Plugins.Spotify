using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>Records fades into the same list as the backend's commands, so a test asserts the
/// order the room hears them in, not two lists that cannot be interleaved.</summary>
internal sealed class FakeSpotifyFader(List<string> calls) : ISpotifyFader
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>Handed out in order, then <see cref="Outcome"/> once they run out.</summary>
    public Queue<FadeOutcome> Outcomes { get; } = new();

    public FadeOutcome Outcome { get; set; } = FadeOutcome.Landed;

    public Task<FadeOutcome> SilenceAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        calls.Add($"silence {duration.TotalMilliseconds:0}");
        return Task.FromResult(Next());
    }

    public Task<FadeOutcome> RestoreAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        calls.Add($"restore {duration.TotalMilliseconds:0}");
        return Task.FromResult(Next());
    }

    private FadeOutcome Next() => Outcomes.Count > 0 ? Outcomes.Dequeue() : Outcome;
}
