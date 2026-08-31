using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// The plugin's end of the Spicetify extension. Spotify's own transport surface offers no volume
/// on Windows and only a process spawn per step on macOS, so the one thing this exists to carry is
/// a fade: the extension runs inside the client and can ramp the volume in JavaScript, for free.
/// </summary>
/// <remarks>
/// The extension dials in rather than the plugin dialling out — nothing outside the Spotify client
/// can reach into it, and a browser context cannot listen. Loopback only: a bridge reachable off
/// the machine would let anything on the room's wifi drive the host's music.
/// </remarks>
public sealed class SpicetifyBridge : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _port;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private Stream? _client;
    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>
    /// Completed when the extension says the ramp finished. Held here rather than correlated by id
    /// because a newer fade supersedes an older one at the other end too — there is only ever one.
    /// </summary>
    private TaskCompletionSource<bool>? _fading;

    public SpicetifyBridge(ILogger logger, int port)
    {
        _logger = logger;
        _port = port;
    }

    /// <summary>Raised when the extension reports Spotify moved, so the provider need not poll for it.</summary>
    public event EventHandler<SpicetifyState>? StateReceived;

    /// <summary>Whether an extension is attached right now. False is the ordinary case: most hosts install nothing.</summary>
    public bool IsConnected => _client is not null;

    public SpicetifyState? LastState { get; private set; }

    /// <summary>
    /// Begins listening. Never throws: a port already taken is a bridge that does not run, not a
    /// plugin that fails to load, and break music still works through the ordinary backend.
    /// </summary>
    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Spicetify bridge could not listen on 127.0.0.1:{Port}: {Reason}", _port, ex.Message);
            return;
        }

        _logger.LogInformation("Spicetify bridge listening on 127.0.0.1:{Port}", _port);
        _ = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <summary>
    /// Ramps the volume and then pauses, as one command. Paired here rather than by the caller
    /// because the gap between a fade finishing and a pause arriving is a gap the room hears, and
    /// the extension is the only end that has neither a socket nor a process in the middle.
    /// </summary>
    /// <remarks>
    /// The level being faded away from is remembered at the far end, so
    /// <see cref="PlayWithFadeInAsync"/> comes back to it. Kept there rather than here because
    /// Spotify rounds what it reports, and a level read back and restored walks down a point per
    /// fade.
    /// </remarks>
    public Task<bool> PauseWithFadeOutAsync(TimeSpan over, CancellationToken cancellationToken = default)
        => RampAsync(new { type = "pauseWithFadeOut", ms = (int)over.TotalMilliseconds }, over, cancellationToken);

    /// <summary>
    /// Starts playing silent and comes up to the level the fade out was taken from. Neither end of
    /// the pair carries a level: out is always to silence, and in is always back to what silence
    /// was faded away from.
    /// </summary>
    public Task<bool> PlayWithFadeInAsync(TimeSpan over, CancellationToken cancellationToken = default)
        => RampAsync(new { type = "playWithFadeIn", ms = (int)over.TotalMilliseconds }, over, cancellationToken);

    /// <summary>
    /// A ramp with nothing on the end of it, for the two moves that have no paired command: coming
    /// up from silence after the backend has started a playlist, and going down before it ends a
    /// session. Neither is something the extension can do on its own.
    /// </summary>
    public Task<bool> FadeAsync(float to, TimeSpan over, CancellationToken cancellationToken = default)
        => RampAsync(new { type = "fade", to, ms = (int)over.TotalMilliseconds }, over, cancellationToken);

    /// <summary>
    /// Sends a command that takes time at the far end and waits for it to say so. The await covers
    /// the whole ramp rather than the send, because the caller's next move is the thing the ramp
    /// exists to happen before.
    /// </summary>
    private async Task<bool> RampAsync(object command, TimeSpan over, CancellationToken cancellationToken)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fading = finished;

        if (!await SendAsync(command, cancellationToken)) return false;

        // Bounded by the ramp the extension was asked for, plus room for a client that is busy. One
        // that never reports back must not hold the console between singers.
        var timeout = Task.Delay(over + TimeSpan.FromSeconds(2), cancellationToken);

        return await Task.WhenAny(finished.Task, timeout) == finished.Task && finished.Task.Result;
    }

    public Task<bool> SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => SendAsync(new { type = "volume", to = volume }, cancellationToken);

    private async Task<bool> SendAsync(object message, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null) return false;

        await _writing.WaitAsync(cancellationToken);
        try
        {
            await client.WriteAsync(WebSocketFrame.Encode(JsonSerializer.Serialize(message)), cancellationToken);
            await client.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // A client that has gone is not an error the console should hear about: the caller
            // reads false and falls back to the ordinary backend.
            _logger.LogDebug(ex, "Spicetify bridge lost the extension mid-send");
            Drop(client);
            return false;
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => ServeAsync(connection, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Spicetify bridge stopped accepting");
                return;
            }
        }
    }

    private async Task ServeAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using var _ = connection;

        if (!IsFromThisMachine(connection.Client.RemoteEndPoint))
        {
            _logger.LogWarning("Spicetify bridge refused a connection from {Remote}", connection.Client.RemoteEndPoint);
            return;
        }

        var stream = connection.GetStream();

        var headers = await WebSocketFrame.ReadHeadersAsync(stream, cancellationToken);

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await RefuseAsync(stream, cancellationToken);
            return;
        }

        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {WebSocketFrame.AcceptFor(key)}\r\n\r\n";

        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(handshake), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // One extension at a time: there is one Spotify. A second connection replaces the first,
        // which is what a reconnect after a Spotify restart looks like from here.
        _client = stream;
        _logger.LogInformation("Spicetify extension attached");

        try
        {
            await ReadLoopAsync(stream, cancellationToken);
        }
        finally
        {
            Drop(stream);
            _logger.LogInformation("Spicetify extension detached");
        }
    }

    /// <summary>
    /// Belt and braces over binding to loopback: the bind is what keeps anything else out today,
    /// and this is what keeps it out if the bind is ever widened. What arrives here can start and
    /// stop the room's music.
    /// </summary>
    internal static bool IsFromThisMachine(EndPoint? remote)
        => remote is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address);

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await WebSocketFrame.ReadAsync(stream, cancellationToken);
            if (frame is not { } received) return;

            if (received.Opcode == WebSocketFrame.Close) return;

            if (received.Opcode == WebSocketFrame.Ping)
            {
                await stream.WriteAsync(WebSocketFrame.Encode(received.Payload, WebSocketFrame.Pong), cancellationToken);
                continue;
            }

            if (received.Opcode != WebSocketFrame.Text) continue;

            switch (SpicetifyState.TypeOf(received.Payload))
            {
                case "faded":
                    _fading?.TrySetResult(true);
                    break;

                case "state" when SpicetifyState.Parse(received.Payload) is { } state:
                    LastState = state;
                    StateReceived?.Invoke(this, state);
                    break;
            }
        }
    }

    private static async Task RefuseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var body = "KHost Spicetify bridge. This endpoint speaks WebSocket.";
        var response =
            "HTTP/1.1 400 Bad Request\r\nContent-Type: text/plain\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";

        try
        {
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch { /* the caller has gone; nothing to say to it */ }
    }

    /// <summary>Only clears the current client, so a replaced connection's teardown cannot unhook its successor.</summary>
    private void Drop(Stream stream)
    {
        if (!ReferenceEquals(_client, stream)) return;

        _client = null;

        // Whoever is waiting on a fade is waiting on a client that has gone; false sends them to
        // the platform backend rather than to the timeout.
        _fading?.TrySetResult(false);
    }

    public void Dispose()
    {
        _stopping.Cancel();

        try { _listener?.Stop(); } catch { /* already down */ }

        _listener = null;
        _client = null;
        _stopping.Dispose();
        _writing.Dispose();
    }
}
