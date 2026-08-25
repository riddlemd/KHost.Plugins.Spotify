using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// Records the calls in order. Hand-rolled rather than substituted so a test can assert what was
/// sent to Spotify and, just as importantly, that nothing else was.
/// </summary>
public sealed class FakeSpotifyController : ISpotifyController
{
    public List<string> Calls { get; } = [];

    public string? Limitation { get; set; }

    public bool CanStart { get; set; } = true;

    public string? StartedContextUri { get; private set; }
    public bool StartedWithShuffle { get; private set; }

    /// <summary>What the backend reports. Null stands for a backend that cannot see.</summary>
    public SpotifyState? State { get; set; } = SpotifyState.Stopped;

    /// <summary>
    /// Reads to hand out before falling back to <see cref="State"/>. Lets a test stand a transport
    /// up that answers with the old track first, the way a real one does until a skip lands.
    /// </summary>
    public Queue<SpotifyState?> QueuedStates { get; } = new();

    public Task<SpotifyState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("state");

        return Task.FromResult(QueuedStates.Count > 0 ? QueuedStates.Dequeue() : State);
    }

    public Task<bool> StartAsync(string? contextUri, bool shuffle, CancellationToken cancellationToken = default)
    {
        StartedContextUri = contextUri;
        StartedWithShuffle = shuffle;

        Calls.Add("start");

        return Task.FromResult(CanStart);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("pause");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("resume");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("stop");
        return Task.CompletedTask;
    }

    public Task SkipAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("skip");
        return Task.CompletedTask;
    }
}
