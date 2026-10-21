using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

// R6: liveness is the server's protocol PING/PONG. A browser answers PINGs without running JS, so a throttled tab
// that sends nothing stays connected; a peer that stops answering is aborted.
[Trait("Category", "Transport")]
public sealed class LivenessTests {
    // Many PINGs per test, but a timeout a starved test process can still answer within: what is measured is surviving
    // more than two whole timeouts on PONGs alone, and being aborted once the PONGs stop.
    private static readonly TransportOptions Fast = new() {
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveTimeout = TimeSpan.FromMilliseconds(1500),
    };

    [Fact]
    public async Task A_silent_client_keeps_working_across_many_ping_intervals() {
        await using var app = await TransportApp.StartAsync(Fast);
        await using var client = await app.HelloAsync();
        var pending = client.ReceiveAsync(Wait.Default);
        await Task.Delay((2 * Fast.KeepAliveTimeout) + (5 * Fast.KeepAliveInterval), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, app.Registry.Count);
        Assert.Empty(app.Gateway.Of("gone"));

        await client.SendAsync(new PingFrame(42));

        Assert.Equal(42, Assert.IsType<PongFrame>(await pending).C);
    }

    [Fact]
    public async Task A_peer_that_stops_answering_pings_is_aborted() {
        await using var app = await TransportApp.StartAsync(Fast);
        using var dead = await DeadPeer.ConnectAsync(app);
        await app.Gateway.WaitForAsync("sub");
        Assert.Equal(1, app.Registry.Count);

        var gone = await app.Gateway.WaitForAsync("gone");

        Assert.Equal(0, app.Registry.Count);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<Retroscope.Core.Ids.RoomKey>>(gone.Detail));
    }

    // A raw TCP peer: it completes the upgrade, says hello, subscribes, and then never reads again, so the server's
    // PINGs go unanswered (a crashed browser, a dead NAT mapping).
    private sealed class DeadPeer : IDisposable {
        private readonly TcpClient _tcp;

        private DeadPeer(TcpClient tcp) {
            _tcp = tcp;
        }

        public static async Task<DeadPeer> ConnectAsync(TransportApp app) {
            var token = TestContext.Current.CancellationToken;
            var tcp = new TcpClient();
            await tcp.ConnectAsync(app.SocketUri.Host, app.SocketUri.Port, token);
            var stream = tcp.GetStream();
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = $"GET {WsEndpoint.Path} HTTP/1.1\r\nHost: {app.SocketUri.Authority}\r\nUpgrade: websocket\r\n"
                + $"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
                + $"Origin: {TransportApp.Origin}\r\n{TransportApp.CallerHeader}: user\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
            var response = await ReadHeadersAsync(stream, token);
            Assert.StartsWith("HTTP/1.1 101", response, StringComparison.Ordinal);

            var hello = new HelloFrame(1, TransportApp.NewClient(), [new SubCursor(TransportApp.NewRoom())]);
            await stream.WriteAsync(MaskedTextFrame(WireWriter.ToUtf8(hello)), token);
            return new DeadPeer(tcp);
        }

        public void Dispose() => _tcp.Dispose();

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken token) {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 4 || !(bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')) {
                if (await stream.ReadAsync(one, token) == 0) {
                    break;
                }
                bytes.Add(one[0]);
            }
            return Encoding.ASCII.GetString([.. bytes]);
        }

        private static byte[] MaskedTextFrame(byte[] payload) {
            var header = payload.Length < 126
                ? new byte[] { 0x81, (byte)(0x80 | payload.Length) }
                : [0x81, 0x80 | 126, (byte)(payload.Length >> 8), (byte)payload.Length];
            var mask = RandomNumberGenerator.GetBytes(4);
            var frame = new byte[header.Length + 4 + payload.Length];
            header.CopyTo(frame, 0);
            mask.CopyTo(frame, header.Length);
            for (var i = 0; i < payload.Length; i++) {
                frame[header.Length + 4 + i] = (byte)(payload[i] ^ mask[i % 4]);
            }
            return frame;
        }
    }
}
