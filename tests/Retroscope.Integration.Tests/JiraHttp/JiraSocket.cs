using System.Net.WebSockets;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>A browser tab on a real WebSocket that keeps every frame it receives.</summary>
internal sealed class JiraSocket : IAsyncDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket;
    private readonly List<ServerFrame> _frames = [];
    private readonly Task _receiving;
    private long _q;

    private JiraSocket(ClientWebSocket socket) {
        _socket = socket;
        _receiving = ReceiveLoopAsync();
    }

    public int Count {
        get {
            lock (_frames) {
                return _frames.Count;
            }
        }
    }

    public static async Task<JiraSocket> ConnectAsync(JiraHttpHost host, Principal who) {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.SetRequestHeader(JiraHttpHost.CallerHeader, $"user:{who.Id}:{who.DisplayName}");
        socket.Options.SetRequestHeader("Origin", JiraHttpHost.Origin);
        await socket.ConnectAsync(host.SocketUri, JiraHttpHost.Token);
        var tab = new JiraSocket(socket);
        await tab.SendAsync(new HelloFrame(1, new ClientId(Guid.CreateVersion7()), []));
        await tab.WaitForAsync<WelcomeFrame>();
        return tab;
    }

    public IReadOnlyList<ServerFrame> Frames(int from = 0) {
        lock (_frames) {
            return [.. _frames.Skip(from)];
        }
    }

    public IReadOnlyList<WireEvent> Events(int from = 0) => [.. Frames(from).OfType<TxFrame>().SelectMany(x => x.Ev)];

    public async Task SubscribeAsync(RoomKey room) {
        var from = Count;
        await SendAsync(new SubFrame(room, null, null));
        var reply = await WaitForAsync<ServerFrame>(x => x is SnapFrame or ReplayFrame or KickFrame, from);
        Assert.IsType<SnapFrame>(reply);
    }

    public async Task<ServerFrame> CommandAsync(RoomKey room, Command command) {
        var q = Interlocked.Increment(ref _q);
        var from = Count;
        await SendAsync(new CmdFrame(room, q, command));
        return await WaitForAsync<ServerFrame>(x => x is AckFrame a && a.Q == q || x is NackFrame n && n.Q == q, from);
    }

    public Task SendAsync(ClientFrame frame) =>
        _socket.SendAsync(WireWriter.ToUtf8(frame), WebSocketMessageType.Text, endOfMessage: true, JiraHttpHost.Token);

    public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null, int from = 0) where T : ServerFrame {
        var deadline = DateTime.UtcNow + Timeout;
        while (true) {
            var found = Frames(from).OfType<T>().FirstOrDefault(x => match?.Invoke(x) ?? true);
            if (found is not null) {
                return found;
            }
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"No {typeof(T).Name} arrived; received: {string.Join(" | ", Frames(from))}");
            }
            if (_receiving.IsCompleted) {
                throw new InvalidOperationException($"The socket closed before a {typeof(T).Name} arrived.");
            }
            await Task.Delay(10, JiraHttpHost.Token);
        }
    }

    /// <summary>The first tx event of type <typeparamref name="T"/> matching <paramref name="match"/>.</summary>
    public async Task<T> WaitForEventAsync<T>(Func<T, bool> match, int from = 0) where T : WireEvent {
        await WaitForAsync<TxFrame>(x => x.Ev.OfType<T>().Any(match), from);
        return Events(from).OfType<T>().First(match);
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
                        if (_socket.State == WebSocketState.CloseReceived) {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        return;
                    }
                    length += result.Count;
                } while (!result.EndOfMessage);
                var parsed = WireParser.ParseServer(buffer.AsSpan(0, length));
                if (!parsed.Ok) {
                    throw new InvalidOperationException($"Unparseable server frame: {parsed.Message}");
                }
                lock (_frames) {
                    _frames.Add(parsed.Frame!);
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
