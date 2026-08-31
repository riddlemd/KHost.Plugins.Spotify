using System.Net;
using System.Reflection;
using System.Text;
using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// The handshake and framing are hand-rolled because HttpListener's WebSocket support is
/// Windows-only, so the platforms this bridge exists for get no help from the framework. Asserted
/// against the RFC's own worked example rather than against itself.
/// </summary>
public class WebSocketFrameTests
{
    private static readonly Type Frame =
        typeof(SpicetifyBridge).Assembly.GetType("KHost.Plugins.Spotify.Bridge.WebSocketFrame")!;

    private static string AcceptFor(string key)
        => (string)Frame.GetMethod("AcceptFor", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [key])!;

    private static byte[] Encode(string payload, byte opcode = 0x1)
        => (byte[])Frame.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [payload, opcode])!;

    private static async Task<(byte Opcode, string Payload)?> ReadAsync(Stream stream)
    {
        var task = (Task)Frame.GetMethod("ReadAsync", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [stream, CancellationToken.None])!;

        await task;

        return ((byte, string)?)task.GetType().GetProperty("Result")!.GetValue(task);
    }

    [Fact]
    public void AcceptFor_MatchesTheWorkedExampleInRfc6455()
    {
        // RFC 6455 section 1.3. A client that does not see this exact value back refuses the
        // connection, so getting the salt or the hash wrong fails silently as "extension never
        // attached" rather than as anything that names itself.
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", AcceptFor("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Fact]
    public async Task Encode_AndRead_RoundTripAText()
    {
        var stream = new MemoryStream(Encode("""{"type":"fade","to":0}"""));

        var frame = await ReadAsync(stream);

        Assert.Equal(0x1, frame!.Value.Opcode);
        Assert.Equal("""{"type":"fade","to":0}""", frame.Value.Payload);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(125)]      // the last length that fits in the header byte
    [InlineData(126)]      // the first that needs the two-byte extension
    [InlineData(ushort.MaxValue)]   // the largest the header can declare
    public async Task Encode_CarriesEveryLengthTheReaderWillTake(int length)
    {
        var payload = new string('x', length);

        var frame = await ReadAsync(new MemoryStream(Encode(payload)));

        Assert.Equal(payload, frame!.Value.Payload);
    }

    [Fact]
    public void Encode_RefusesWhatTheReaderWouldRefuse()
    {
        // The two agree on one ceiling, so a frame that could never be read is never written.
        // Unwrapped because the call goes through reflection, which boxes what it threw.
        var thrown = Assert.Throws<TargetInvocationException>(() => Encode(new string('x', ushort.MaxValue + 1)));

        Assert.IsType<ArgumentOutOfRangeException>(thrown.InnerException);
    }

    [Fact]
    public async Task Read_UnmasksWhatAClientSent()
    {
        // Everything from a browser is masked; read raw it is noise rather than JSON.
        var body = Encoding.UTF8.GetBytes("""{"type":"state"}""");
        var mask = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
        var masked = body.Select((b, i) => (byte)(b ^ mask[i % 4])).ToArray();

        var stream = new MemoryStream([(byte)0x81, (byte)(0x80 | body.Length), .. mask, .. masked]);

        var frame = await ReadAsync(stream);

        Assert.Equal("""{"type":"state"}""", frame!.Value.Payload);
    }

    [Fact]
    public async Task Read_RefusesAFrameBiggerThanAnythingTheExtensionSends()
    {
        // The port is loopback but anything on the machine can reach it, and a declared length is
        // an allocation someone else chose.
        var header = new byte[] { 0x81, 127 };
        var length = BitConverter.GetBytes(1024UL * 1024).Reverse().ToArray();

        Assert.Null(await ReadAsync(new MemoryStream([.. header, .. length])));
    }

    [Fact]
    public async Task Read_ReportsTheCloseRatherThanWaiting()
    {
        var frame = await ReadAsync(new MemoryStream(Encode(string.Empty, 0x8)));

        Assert.Equal(0x8, frame!.Value.Opcode);
    }

    [Fact]
    public async Task Read_IsNullWhenThePeerVanishedMidFrame()
    {
        // A half-written frame is what a Spotify that quit looks like from here.
        Assert.Null(await ReadAsync(new MemoryStream([0x81, 20, 0x01])));
    }
}

/// <summary>
/// The guard cannot be reached through the socket — the listener binds to loopback, so nothing
/// else can arrive to be refused — which is exactly why it is asserted directly.
/// </summary>
public class BridgeOriginTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void AConnectionFromThisMachineIsAccepted(string address)
        => Assert.True(SpicetifyBridge.IsFromThisMachine(new IPEndPoint(IPAddress.Parse(address), 51000)));

    [Theory]
    [InlineData("192.168.1.50")]   // the room's wifi
    [InlineData("10.14.0.2")]      // a VPN interface
    [InlineData("0.0.0.0")]
    public void AConnectionFromAnywhereElseIsRefused(string address)
        => Assert.False(SpicetifyBridge.IsFromThisMachine(new IPEndPoint(IPAddress.Parse(address), 51000)));

    [Fact]
    public void AConnectionWithNoAddressAtAllIsRefused()
        => Assert.False(SpicetifyBridge.IsFromThisMachine(null));
}
