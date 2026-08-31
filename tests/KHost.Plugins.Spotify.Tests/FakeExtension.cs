using System.Net.WebSockets;
using System.Text;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// Stands in for the Spicetify extension, using the framework's own client so the bridge is held
/// to what a browser actually sends — masked frames, a real handshake — rather than to a
/// convenient reading of the protocol it was written against.
/// </summary>
public sealed class FakeExtension : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;

    private FakeExtension(ClientWebSocket socket) => _socket = socket;

    public static async Task<FakeExtension> ConnectAsync(int port)
    {
        var socket = new ClientWebSocket();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/khost"), timeout.Token);

        return new FakeExtension(socket);
    }

    public Task SendAsync(string json) => _socket.SendAsync(
        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>
    /// The next command the bridge sends. Bounded, because a test that hangs waiting for a message
    /// the bridge never sent reads as a build that stopped rather than as a failure.
    /// </summary>
    public async Task<string> NextAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[8 * 1024];

        var received = await _socket.ReceiveAsync(buffer, timeout.Token);

        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch { /* the bridge went first */ }

        _socket.Dispose();
    }
}
