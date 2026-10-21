using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

// A Connection over a loopback socket pair with a fake clock, so the hello timer can be fired at the exact moment the
// hello frame is being handled.
[Trait("Category", "Transport")]
public sealed class HelloRaceTests {
    private static readonly TransportOptions Options = new() { HelloTimeout = TimeSpan.FromSeconds(5) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hello_and_the_hello_timer_never_both_win() {
        var welcomed = 0;
        var timedOut = 0;
        for (var i = 0; i < 200; i++) {
            await using var pair = await SocketPair.StartAsync();
            var hello = WireWriter.ToUtf8(new HelloFrame(1, TransportApp.NewClient(), []));

            await Task.WhenAll(
                Task.Run(() => pair.Client.SendAsync(hello, WebSocketMessageType.Text, true, Token), Token),
                Task.Run(() => pair.Time.Advance(Options.HelloTimeout), Token));

            var first = await pair.ReceiveAsync();
            if (first is WelcomeFrame) {
                welcomed++;
                await pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", Token);
                Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.IsType<WebSocketCloseStatus>(await pair.ReceiveAsync()));
            } else {
                timedOut++;
                Assert.Equal((WebSocketCloseStatus)CloseCodes.ProtocolViolation, Assert.IsType<WebSocketCloseStatus>(first));
            }
        }

        Assert.Equal(200, welcomed + timedOut);
    }

    [Fact]
    public async Task A_timer_that_fired_first_closes_4400_and_a_late_hello_gets_no_welcome() {
        await using var pair = await SocketPair.StartAsync();

        pair.Time.Advance(Options.HelloTimeout);
        await pair.Client.SendAsync(
            WireWriter.ToUtf8(new HelloFrame(1, TransportApp.NewClient(), [])),
            WebSocketMessageType.Text,
            true,
            Token);

        Assert.Equal((WebSocketCloseStatus)CloseCodes.ProtocolViolation, Assert.IsType<WebSocketCloseStatus>(await pair.ReceiveAsync()));
        Assert.Null(pair.Connection.Client);
    }

    [Fact]
    public async Task A_hello_that_won_is_not_undone_by_the_timer_firing_later() {
        await using var pair = await SocketPair.StartAsync();
        await pair.Client.SendAsync(
            WireWriter.ToUtf8(new HelloFrame(1, TransportApp.NewClient(), [])),
            WebSocketMessageType.Text,
            true,
            Token);
        Assert.IsType<WelcomeFrame>(await pair.ReceiveAsync());

        pair.Time.Advance(Options.HelloTimeout * 2);
        await pair.Client.SendAsync(WireWriter.ToUtf8(new PingFrame(3)), WebSocketMessageType.Text, true, Token);

        Assert.Equal(3, Assert.IsType<PongFrame>(await pair.ReceiveAsync()).C);
        Assert.True(pair.Connection.IsOpen);
    }

    [Fact]
    public async Task A_close_after_the_connection_ended_arms_no_abort_timer() {
        await using var pair = await SocketPair.StartAsync();
        pair.DropPeer();
        await pair.Running.WaitAsync(TimeSpan.FromSeconds(10), Token);

        pair.Connection.Restart();
        pair.Connection.Close(CloseCodes.Restart, "restart", urgent: false);

        Assert.Equal(0, pair.Time.LiveTimers);
    }

    [Fact]
    public async Task Every_timer_is_disposed_once_the_connection_ended() {
        for (var i = 0; i < 50; i++) {
            await using var pair = await SocketPair.StartAsync();
            var closing = Task.Run(() => pair.Connection.Close(CloseCodes.Restart, "restart", urgent: false), Token);
            pair.DropPeer();

            await Task.WhenAll(closing, pair.Running).WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.Equal(0, pair.Time.LiveTimers);
        }
    }

    /// <summary>A fake clock that counts the timers created and not yet disposed.</summary>
    private sealed class CountingTime : FakeTimeProvider {
        private int _live;

        public int LiveTimers => Volatile.Read(ref _live);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            Interlocked.Increment(ref _live);
            return new Counted(base.CreateTimer(callback, state, dueTime, period), this);
        }

        private sealed class Counted(ITimer inner, CountingTime owner) : ITimer {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose() {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                    Interlocked.Decrement(ref owner._live);
                }
                inner.Dispose();
            }

            public ValueTask DisposeAsync() {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SocketPair : IAsyncDisposable {
        private readonly TcpClient _serverTcp;
        private readonly TcpClient _clientTcp;
        private readonly byte[] _buffer = new byte[64 * 1024];

        private SocketPair(TcpClient serverTcp, TcpClient clientTcp, CountingTime time) {
            _serverTcp = serverTcp;
            _clientTcp = clientTcp;
            Time = time;
            var server = WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions {
                IsServer = true,
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            Client = WebSocket.CreateFromStream(clientTcp.GetStream(), new WebSocketCreationOptions {
                IsServer = false,
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            var principal = new Principal(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "Uma", null, false);
            Connection = new Connection(
                server,
                new Caller(principal, true),
                "203.0.113.1",
                Options,
                new ConnectionRegistry(Options),
                new RecordingGateway(),
                time,
                NullLogger.Instance);
            Running = Connection.RunAsync(CancellationToken.None);
        }

        public CountingTime Time { get; }

        public WebSocket Client { get; }

        public Connection Connection { get; }

        public Task Running { get; }

        public static async Task<SocketPair> StartAsync() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try {
                var client = new TcpClient();
                var accepting = listener.AcceptTcpClientAsync(Token).AsTask();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, Token);
                return new SocketPair(await accepting, client, new CountingTime());
            } finally {
                listener.Stop();
            }
        }

        /// <summary>The peer vanishes without a close frame: the server's receive fails and the connection ends.</summary>
        public void DropPeer() {
            _clientTcp.Client.LingerState = new LingerOption(true, 0);
            _clientTcp.Dispose();
        }

        /// <summary>The next server frame, or the close status once the server closed.</summary>
        public async Task<object> ReceiveAsync() {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var length = 0;
            while (true) {
                var result = await Client.ReceiveAsync(_buffer.AsMemory(length), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) {
                    return Client.CloseStatus!.Value;
                }
                length += result.Count;
                if (result.EndOfMessage) {
                    var parsed = WireParser.ParseServer(_buffer.AsSpan(0, length));
                    Assert.True(parsed.Ok, $"{parsed.Error}: {parsed.Message}");
                    return parsed.Frame!;
                }
            }
        }

        public async ValueTask DisposeAsync() {
            Client.Abort();
            _clientTcp.Dispose();
            await Running.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _serverTcp.Dispose();
        }
    }
}
