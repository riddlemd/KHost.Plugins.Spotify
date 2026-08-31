using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KHost.Plugins.Spotify.Bridge;

/// <summary>
/// Just enough of RFC 6455 to carry JSON lines between the plugin and the Spicetify extension.
/// Hand-rolled rather than taken from HttpListener, whose WebSocket support is Windows-only and
/// would leave the backend this exists for — macOS and Linux — without a bridge at all.
/// </summary>
internal static class WebSocketFrame
{
    /// <summary>The constant RFC 6455 mixes into the client's key to prove the handshake was read.</summary>
    private const string HandshakeSalt = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// Both ends of this file agree on one ceiling. Reading is where it matters — the port is
    /// loopback but anything on the machine can reach it, and a declared length is an allocation
    /// somebody else chose. It is the largest a three-byte header can express, and one more
    /// overflows the length cast silently into a frame that declares nothing.
    /// </summary>
    internal const int MaxPayload = ushort.MaxValue;

    internal const byte Text = 0x1;
    internal const byte Close = 0x8;
    internal const byte Ping = 0x9;
    internal const byte Pong = 0xA;

    /// <summary>
    /// The value the client's <c>Sec-WebSocket-Key</c> has to be answered with. A client that does
    /// not see its own key come back refuses the connection, so this is not decorative.
    /// </summary>
    public static string AcceptFor(string clientKey)
        => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + HandshakeSalt)));

    /// <summary>
    /// Reads one header line at a time rather than taking the whole request: a browser sends the
    /// handshake and then says nothing until it has something to say, so reading to the end blocks.
    /// </summary>
    public static async Task<Dictionary<string, string>> ReadHeadersAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var line = new StringBuilder();
        var one = new byte[1];

        while (headers.Count < 64)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0) break;

            if (one[0] != (byte)'\n')
            {
                if (one[0] != (byte)'\r') line.Append((char)one[0]);
                continue;
            }

            if (line.Length == 0) break;

            var text = line.ToString();
            line.Clear();

            var colon = text.IndexOf(':');
            if (colon > 0) headers[text[..colon].Trim()] = text[(colon + 1)..].Trim();
        }

        return headers;
    }

    public static byte[] Encode(string payload, byte opcode = Text)
    {
        var body = Encoding.UTF8.GetBytes(payload);

        // Commands are a line of JSON, so this is a bug on our side rather than something a peer
        // can provoke — and a frame the other end would refuse to read is worse sent than thrown.
        if (body.Length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Frames are capped at {MaxPayload} bytes.");

        var header = new List<byte> { (byte)(0x80 | opcode) };

        // The server never masks. One length byte up to 125, three beyond it.
        if (body.Length < 126)
        {
            header.Add((byte)body.Length);
        }
        else
        {
            header.Add(126);
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)body.Length);
            header.AddRange(length);
        }

        return [.. header, .. body];
    }

    /// <summary>Null when the peer closed. Control frames come back with their opcode for the caller to answer.</summary>
    public static async Task<(byte Opcode, string Payload)?> ReadAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await FillAsync(stream, header, cancellationToken)) return null;

        var opcode = (byte)(header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            if (!await FillAsync(stream, extended, cancellationToken)) return null;
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            if (!await FillAsync(stream, extended, cancellationToken)) return null;
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
        }

        if (length is < 0 or > MaxPayload) return null;

        var mask = new byte[4];
        if (masked && !await FillAsync(stream, mask, cancellationToken)) return null;

        var body = new byte[length];
        if (length > 0 && !await FillAsync(stream, body, cancellationToken)) return null;

        if (masked)
            for (var i = 0; i < body.Length; i++)
                body[i] ^= mask[i % 4];

        return (opcode, Encoding.UTF8.GetString(body));
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0) return false;
            filled += read;
        }

        return true;
    }
}
