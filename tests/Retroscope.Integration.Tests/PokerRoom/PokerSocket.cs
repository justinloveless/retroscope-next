using System.Net.WebSockets;
using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>
/// A browser tab on a real WebSocket: it keeps every frame it receives (parsed and raw), tracks the room's epoch and
/// seq like the client does, and numbers its commands per cid.
/// </summary>
internal sealed class PokerSocket : IAsyncDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket;
    private readonly List<(ServerFrame Frame, string Raw)> _frames = [];
    private readonly Task _receiving;
    private long _q;

    private PokerSocket(ClientWebSocket socket, Principal who, ClientId client) {
        _socket = socket;
        Who = who;
        Client = client;
        _receiving = ReceiveLoopAsync();
    }

    public Principal Who { get; }

    public ClientId Client { get; }

    public int? CloseStatus { get; private set; }

    public static Task<PokerSocket> ConnectAsync(PokerHost host, Principal who, ClientId? client = null) {
        var kind = who.Kind == PrincipalKind.Guest ? "guest" : "user";
        return ConnectAsync(
            host.SocketUri,
            new Dictionary<string, string> {
                [PokerHost.CallerHeader] = $"{kind}:{who.Id}:{who.DisplayName}",
                ["Origin"] = PokerHost.Origin,
            },
            who,
            client);
    }

    /// <summary>Opens a socket with <paramref name="headers"/> on the upgrade (a cookie, an Origin) and says hello.</summary>
    public static async Task<PokerSocket> ConnectAsync(
        Uri socketUri,
        IReadOnlyDictionary<string, string> headers,
        Principal who,
        ClientId? client = null) {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        foreach (var (name, value) in headers) {
            socket.Options.SetRequestHeader(name, value);
        }
        await socket.ConnectAsync(socketUri, PokerHost.Token);
        var tab = new PokerSocket(socket, who, client ?? new ClientId(Guid.CreateVersion7()));
        await tab.SendAsync(new HelloFrame(1, tab.Client, []));
        await tab.WaitForAsync<WelcomeFrame>();
        return tab;
    }

    public int Count {
        get {
            lock (_frames) {
                return _frames.Count;
            }
        }
    }

    public IReadOnlyList<string> Raw {
        get {
            lock (_frames) {
                return [.. _frames.Select(x => x.Raw)];
            }
        }
    }

    public IReadOnlyList<ServerFrame> Frames(int from = 0) {
        lock (_frames) {
            return [.. _frames.Skip(from).Select(x => x.Frame)];
        }
    }

    /// <summary>Every tx event received from position <paramref name="from"/> on.</summary>
    public IReadOnlyList<WireEvent> Events(int from = 0) => [.. Frames(from).OfType<TxFrame>().SelectMany(x => x.Ev)];

    /// <summary>The room's epoch and the last seq this tab applied, as a resuming client sends them.</summary>
    public (Epoch Epoch, long Seq) Cursor(RoomKey room) {
        Epoch? epoch = null;
        long seq = 0;
        foreach (var frame in Frames()) {
            switch (frame) {
                case SnapFrame snap when snap.R == room:
                    (epoch, seq) = (snap.E, snap.S);
                    break;
                case ReplayFrame replay when replay.R == room:
                    epoch = replay.E;
                    seq = replay.Txs.Count > 0 ? replay.Txs[^1].S : replay.From - 1;
                    break;
                case TxFrame tx when tx.R == room:
                    seq = tx.S;
                    break;
            }
        }
        return (epoch ?? throw new InvalidOperationException("No snapshot yet."), seq);
    }

    public async Task<ServerFrame> SubscribeAsync(RoomKey room, Epoch? epoch = null, long? seq = null) {
        var from = Count;
        await SendAsync(new SubFrame(room, epoch, seq));
        return await WaitForAsync<ServerFrame>(x => x is SnapFrame or ReplayFrame or KickFrame, from);
    }

    /// <summary>Sends a command with the next <c>q</c> and waits for its ack or nack.</summary>
    public async Task<ServerFrame> CommandAsync(RoomKey room, Command command) {
        var q = Interlocked.Increment(ref _q);
        return await CommandAsync(room, command, q);
    }

    public async Task<ServerFrame> CommandAsync(RoomKey room, Command command, long q) {
        var from = Count;
        await SendAsync(new CmdFrame(room, q, command));
        return await WaitForAsync<ServerFrame>(x => x is AckFrame a && a.Q == q || x is NackFrame n && n.Q == q, from);
    }

    /// <summary>Sends a command that must be acked; returns the events of the tx frames received meanwhile.</summary>
    public async Task<IReadOnlyList<WireEvent>> AckedAsync(RoomKey room, Command command) {
        var from = Count;
        var reply = await CommandAsync(room, command);
        Assert.True(reply is AckFrame { Dup: false }, $"{command.GetType().Name} was answered with {reply}");
        return Events(from);
    }

    public async Task<NackFrame> NackedAsync(RoomKey room, Command command) {
        var reply = await CommandAsync(room, command);
        return Assert.IsType<NackFrame>(reply);
    }

    public Task SendAsync(ClientFrame frame) {
        return _socket.SendAsync(WireWriter.ToUtf8(frame), WebSocketMessageType.Text, endOfMessage: true, PokerHost.Token);
    }

    public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null, int from = 0) where T : ServerFrame {
        var deadline = DateTime.UtcNow + Timeout;
        while (true) {
            var found = Frames(from).OfType<T>().FirstOrDefault(x => match?.Invoke(x) ?? true);
            if (found is not null) {
                return found;
            }
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"No {typeof(T).Name} arrived; received: {string.Join(" | ", Raw.Skip(from))}");
            }
            if (_receiving.IsCompleted) {
                throw new InvalidOperationException($"The socket closed with {CloseStatus} before a {typeof(T).Name} arrived.");
            }
            await Task.Delay(10, PokerHost.Token);
        }
    }

    public async Task<WireEvent> WaitForEventAsync<T>(int from = 0) where T : WireEvent {
        await WaitForAsync<TxFrame>(x => x.Ev.OfType<T>().Any(), from);
        return Events(from).OfType<T>().First();
    }

    public async Task<int> WaitForCloseAsync() {
        await _receiving.WaitAsync(Timeout, PokerHost.Token);
        return CloseStatus ?? throw new InvalidOperationException("The socket ended without a close frame.");
    }

    private async Task ReceiveLoopAsync() {
        var buffer = new byte[256 * 1024];
        try {
            while (true) {
                var length = 0;
                WebSocketReceiveResult result;
                do {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) {
                        CloseStatus = (int?)_socket.CloseStatus;
                        if (_socket.State == WebSocketState.CloseReceived) {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        return;
                    }
                    length += result.Count;
                } while (!result.EndOfMessage);
                var bytes = buffer.AsSpan(0, length);
                var parsed = WireParser.ParseServer(bytes);
                if (!parsed.Ok) {
                    throw new InvalidOperationException($"Unparseable server frame: {parsed.Message}");
                }
                lock (_frames) {
                    _frames.Add((parsed.Frame!, Encoding.UTF8.GetString(bytes)));
                }
            }
        } catch (WebSocketException) {
            // The server aborted the socket.
        }
    }

    public async ValueTask DisposeAsync() {
        if (_socket.State == WebSocketState.Open) {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                await _receiving.WaitAsync(timeout.Token);
            } catch (Exception e) when (e is WebSocketException or OperationCanceledException or TimeoutException) {
                _socket.Abort();
            }
        }
        _socket.Dispose();
    }
}
