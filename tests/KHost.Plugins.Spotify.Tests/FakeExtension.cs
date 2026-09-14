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

    /// <param name="ready">
    /// Whether to say the player is usable, which the real extension says the moment it opens the
    /// socket. False stands in for the extension that loads into a Spotify whose Spicetify never
    /// bound to it: attached, and able to do nothing.
    /// </param>
    public static async Task<FakeExtension> ConnectAsync(int port, bool ready = true)
    {
        var socket = new ClientWebSocket();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/khost"), timeout.Token);

        var extension = new FakeExtension(socket);

        // Sent before the caller gets it, because nothing the bridge does for an extension happens
        // until one has said it can work — the same order the real extension keeps. The caller
        // waits for the bridge to have read it; only the caller can see the bridge.
        await extension.SendAsync(Diagnosis(ready));

        return extension;
    }

    /// <summary>The report the real extension opens with, as the bridge expects to read it.</summary>
    public static string Diagnosis(bool ready)
    {
        const string PlayerThrew = "Cannot read properties of undefined (reading '_volume')";

        return ready
            ? """{"type":"diagnosis","ready":true,"waitedMs":0,"spicetify":true,"player":true,"platformKeys":40,"error":null}"""
            : $$"""{"type":"diagnosis","ready":false,"waitedMs":30000,"spicetify":true,"player":false,"platformKeys":0,"error":"{{PlayerThrew}}"}""";
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
