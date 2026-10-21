using System.Buffers;
using System.Net.WebSockets;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Realtime.Transport;

/// <summary>
/// One accepted socket. The receive loop parses client frames and routes them to the <see cref="IRoomGateway"/>; the
/// send loop drains the <see cref="OutboundQueue"/> in order, so an ack always follows the tx frame queued before it.
/// </summary>
public sealed class Connection : IClientConnection {
    private const int HelloPending = 0;
    private const int HelloAccepted = 1;
    private const int HelloExpired = 2;

    private readonly WebSocket _socket;
    private readonly TransportOptions _options;
    private readonly ConnectionRegistry _registry;
    private readonly IRoomGateway _gateway;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly OutboundQueue _outbound;
    private readonly ConnRateLimit _rate;
    private readonly HashSet<RoomKey> _rooms = [];
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _timerLock = new();
    private Viewer? _viewer;
    private int _closeRequested;
    private int _hello = HelloPending;
    private volatile bool _discardOutbound;
    private ITimer? _helloTimer;
    private ITimer? _abortTimer;
    private bool _timersDisposed;

    internal Connection(
        WebSocket socket,
        Caller caller,
        string ipKey,
        TransportOptions options,
        ConnectionRegistry registry,
        IRoomGateway gateway,
        TimeProvider time,
        ILogger logger) {
        _socket = socket;
        _options = options;
        _registry = registry;
        _gateway = gateway;
        _time = time;
        _logger = logger;
        _outbound = new OutboundQueue(options.MaxOutboundBytes);
        _rate = new ConnRateLimit(time, options.FramesPerSecond, options.FrameBurst, options.AbuseWindow);
        Id = new ConnectionId(Guid.CreateVersion7());
        Principal = caller.Principal;
        AuthSessionId = caller.AuthSessionId;
        IpKey = ipKey;
    }

    public ConnectionId Id { get; }

    public Principal Principal { get; }

    public Guid? AuthSessionId { get; }

    /// <summary>The per-IP limit key (<see cref="ClientIp.LimitKey(HttpContext)"/>).</summary>
    public string IpKey { get; }

    /// <summary>The tab's client id, once <c>hello</c> has bound it.</summary>
    public ClientId? Client => Volatile.Read(ref _viewer)?.Client;

    public Viewer Viewer => Volatile.Read(ref _viewer) ?? throw new InvalidOperationException("The connection has not sent hello yet.");

    public bool IsOpen => Volatile.Read(ref _closeRequested) == 0;

    public long PendingBytes => _outbound.PendingBytes;

    /// <summary>Completes when both loops have ended and the socket is disposed.</summary>
    internal Task Finished => _finished.Task;

    public bool Send(ServerFrame frame) => Enqueue(Outbound.Frame(WireWriter.ToUtf8(frame)));

    public bool Send(ReadOnlyMemory<byte> utf8) => Enqueue(Outbound.Frame(utf8));

    public bool Send(IReadOnlyList<ReadOnlyMemory<byte>> segments) => Enqueue(Outbound.Frame(segments));

    public void Kick(RoomKey room, KickCode code) {
        lock (_rooms) {
            _rooms.Remove(room);
        }
        Send(new KickFrame(room, code));
    }

    public void Restart() {
        if (Send(new ByeFrame(ByeCode.Restart, _registry.DrawRetryMs()))) {
            Close(CloseCodes.Restart, "restart", urgent: false);
        }
    }

    /// <summary>
    /// Closes the socket with <paramref name="code"/>. An urgent close drops whatever is still queued; otherwise the
    /// close frame follows it. Either way the socket is aborted if the close handshake has not finished in time.
    /// </summary>
    public void Close(int code, string reason, bool urgent) {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0) {
            return;
        }
        if (urgent) {
            _discardOutbound = true;
        }
        _outbound.Close((WebSocketCloseStatus)code, reason);
        // Close may run on any thread, also while RunAsync is disposing the timers at the end of the connection.
        lock (_timerLock) {
            if (!_timersDisposed) {
                _abortTimer = _time.CreateTimer(
                    static state => ((Connection)state!)._socket.Abort(),
                    this,
                    _options.CloseTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal async Task RunAsync(CancellationToken aborted) {
        lock (_timerLock) {
            _helloTimer = _time.CreateTimer(
                static state => ((Connection)state!).HelloTimedOut(),
                this,
                _options.HelloTimeout,
                Timeout.InfiniteTimeSpan);
        }
        var sending = SendLoopAsync();
        var peerClosed = false;
        try {
            await ReceiveLoopAsync(aborted);
            peerClosed = true;
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) {
            // Aborted: keep-alive timeout, close timeout, or the peer vanished.
        } catch (Exception exception) {
            TransportLog.ConnectionFailed(_logger, exception, Id);
        } finally {
            _discardOutbound = true;
            _outbound.Complete();
            if (!peerClosed) {
                _socket.Abort();
            }
        }
        // After the peer's close frame the send loop still answers it; the abort timer bounds that.
        await sending;
        lock (_timerLock) {
            _timersDisposed = true;
            _helloTimer?.Dispose();
            _abortTimer?.Dispose();
        }
        _socket.Dispose();
        _finished.TrySetResult();
    }

    /// <summary>The rooms still subscribed, for <see cref="IRoomGateway.Disconnected"/>.</summary>
    internal RoomKey[] Rooms() {
        lock (_rooms) {
            return [.. _rooms];
        }
    }

    private bool Enqueue(Outbound item) {
        if (!IsOpen) {
            return false;
        }
        if (_outbound.TryEnqueue(item)) {
            return true;
        }
        if (IsOpen) {
            TransportLog.SlowConsumer(_logger, Id, _outbound.PendingBytes);
            Close(CloseCodes.SlowConsumer, "slow consumer", urgent: true);
        }
        return false;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken) {
        var buffer = new byte[_options.ReceiveBufferBytes];
        byte[]? message = null;
        var length = 0;
        try {
            while (true) {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) {
                    Close((int)WebSocketCloseStatus.NormalClosure, "", urgent: true);
                    return;
                }
                if (!IsOpen) {
                    continue;
                }
                if (result.MessageType == WebSocketMessageType.Binary) {
                    Violation("binary frame");
                    continue;
                }
                if (message is null && result.EndOfMessage) {
                    Handle(buffer.AsSpan(0, result.Count));
                    continue;
                }
                if (length + result.Count > WireParser.MaxFrameBytes) {
                    Close(CloseCodes.FrameTooLarge, "frame too large", urgent: true);
                    continue;
                }
                message ??= ArrayPool<byte>.Shared.Rent(WireParser.MaxFrameBytes);
                buffer.AsSpan(0, result.Count).CopyTo(message.AsSpan(length));
                length += result.Count;
                if (result.EndOfMessage) {
                    Handle(message.AsSpan(0, length));
                    length = 0;
                }
            }
        } finally {
            if (message is not null) {
                ArrayPool<byte>.Shared.Return(message);
            }
        }
    }

    private async Task SendLoopAsync() {
        var reader = _outbound.Reader;
        try {
            while (await reader.WaitToReadAsync()) {
                while (reader.TryRead(out var item)) {
                    if (item.CloseStatus is { } status) {
                        await _socket.CloseOutputAsync(status, item.CloseReason, CancellationToken.None);
                        return;
                    }
                    if (!_discardOutbound) {
                        await SendAsync(item);
                    }
                    _outbound.Sent(item);
                }
            }
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) {
            // The socket was aborted under the send; the receive loop ends the connection.
        }
    }

    private async ValueTask SendAsync(Outbound item) {
        if (item.Segments is not { } segments) {
            await _socket.SendAsync(item.Single, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            return;
        }
        for (var i = 0; i < segments.Count; i++) {
            var last = i == segments.Count - 1;
            await _socket.SendAsync(segments[i], WebSocketMessageType.Text, last, CancellationToken.None);
        }
    }

    private void Handle(ReadOnlySpan<byte> utf8) {
        var parsed = WireParser.ParseClient(utf8);
        if (Volatile.Read(ref _viewer) is null) {
            if (parsed.Frame is HelloFrame hello) {
                Hello(hello);
            } else {
                Refuse(parsed, nackable: false);
            }
            return;
        }
        var verdict = _rate.Take();
        if (verdict == RateVerdict.Abusive) {
            TransportLog.RateAbuse(_logger, Id);
            Close(CloseCodes.RateLimited, "rate limited", urgent: true);
            return;
        }
        if (verdict == RateVerdict.Limited) {
            if (CommandOf(parsed) is ({ } room, { } q)) {
                Send(new NackFrame(room, q, NackCode.RateLimited));
            }
            return;
        }
        switch (parsed.Frame) {
            case null:
                Refuse(parsed, nackable: true);
                break;
            case HelloFrame:
                Violation("second hello");
                break;
            case SubFrame sub:
                Subscribe(sub.R, sub.E, sub.S);
                break;
            case UnsubFrame unsub:
                Unsubscribe(unsub.R);
                break;
            case CmdFrame command:
                Command(command);
                break;
            case PingFrame ping:
                Send(new PongFrame(ping.C, _time.GetUtcNow().ToUnixTimeMilliseconds()));
                break;
        }
    }

    /// <summary>
    /// The hello timer and <see cref="Hello"/> race for <see cref="_hello"/>: a timer that wins closes 4400 and no
    /// welcome follows; a hello that wins sends welcome and the timer does nothing, even if it already fired.
    /// </summary>
    private void HelloTimedOut() {
        if (Interlocked.CompareExchange(ref _hello, HelloExpired, HelloPending) == HelloPending) {
            Close(CloseCodes.ProtocolViolation, "no hello", urgent: true);
        }
    }

    private void Hello(HelloFrame hello) {
        if (Interlocked.CompareExchange(ref _hello, HelloAccepted, HelloPending) != HelloPending) {
            return;
        }
        lock (_timerLock) {
            _helloTimer?.Dispose();
        }
        if (hello.V != 1) {
            Violation("unsupported protocol version");
            return;
        }
        if (!_registry.TryBindClient(this, hello.Cid)) {
            Close(CloseCodes.CidInUse, "cid_in_use", urgent: true);
            return;
        }
        Volatile.Write(ref _viewer, new Viewer(Principal, hello.Cid, Id));
        var me = new PrincipalInfo(Principal.Id, Principal.Kind, Principal.DisplayName, Principal.AvatarBlobId);
        Send(new WelcomeFrame(Id, me, _time.GetUtcNow().ToUnixTimeMilliseconds()));
        foreach (var sub in hello.Subs) {
            Subscribe(sub.R, sub.E, sub.S);
        }
    }

    private void Subscribe(RoomKey room, Epoch? epoch, long? seq) {
        bool admitted;
        lock (_rooms) {
            admitted = _rooms.Contains(room) || _rooms.Count < _options.MaxRoomsPerConnection;
            if (admitted) {
                _rooms.Add(room);
            }
        }
        if (!admitted) {
            Send(new KickFrame(room, KickCode.TooManyRooms));
            return;
        }
        _gateway.Subscribe(this, room, epoch, seq);
    }

    private void Unsubscribe(RoomKey room) {
        bool removed;
        lock (_rooms) {
            removed = _rooms.Remove(room);
        }
        if (removed) {
            _gateway.Unsubscribe(this, room);
        }
    }

    private void Command(CmdFrame command) {
        bool subscribed;
        lock (_rooms) {
            subscribed = _rooms.Contains(command.R);
        }
        if (subscribed) {
            _gateway.Command(this, command);
        } else {
            Send(new NackFrame(command.R, command.Q, NackCode.NotSubscribed));
        }
    }

    // A command whose envelope parsed but whose kind or payload did not (version skew, a bad payload) is nacked, so
    // one bad command does not cost the client its socket. Anything else is a protocol violation.
    private void Refuse(ParseResult<ClientFrame> parsed, bool nackable) {
        if (parsed.Error == FrameError.TooLarge) {
            Close(CloseCodes.FrameTooLarge, "frame too large", urgent: true);
        } else if (nackable
            && parsed.Error is FrameError.UnknownKind or FrameError.WrongRoom or FrameError.InvalidData
            && CommandOf(parsed) is ({ } room, { } q)) {
            Send(new NackFrame(room, q, NackCode.Invalid, parsed.Message));
        } else {
            Violation(parsed.Ok ? "expected hello first" : parsed.Message ?? "malformed frame");
        }
    }

    private void Violation(string problem) {
        TransportLog.ProtocolViolation(_logger, Id, problem);
        Close(CloseCodes.ProtocolViolation, "protocol violation", urgent: true);
    }

    private static (RoomKey? Room, long? Q) CommandOf(ParseResult<ClientFrame> parsed) {
        return parsed.Frame is CmdFrame command ? (command.R, command.Q) : (parsed.Room, parsed.Q);
    }
}
