using System.Collections.Concurrent;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>Unsequenced frames a room kind hands a subscriber right after its snapshot or replay (cache fills).</summary>
public interface IResumeFill {
    RoomKind Kind { get; }

    IReadOnlyList<ServerFrame> AfterResume(RoomKey room);
}

/// <summary>
/// Hands transport traffic to the actor runtime (U14's <see cref="IRoomGateway"/>, for every room kind). Each
/// subscription is an <see cref="IRoomPeer"/> over the connection, and everything a connection sends to one room
/// reaches the registry in the order it arrived, even while an actor is starting.
/// </summary>
/// <remarks>
/// A command the room refused with <c>server_busy</c> may lose its nack with the socket, and the client then resends
/// it with the same <c>q</c>. If a later command of the same cid had committed meanwhile, the cursor would answer that
/// resend <c>dup</c> and the intent would be lost. So after a refusal every later command of the room on this
/// connection is refused the same way until the client subscribes to the room again, which it does once it has read
/// the nack: the cursor never passes a refused <c>q</c>.
/// </remarks>
public sealed class RoomGateway : IRoomGateway {
    private readonly ActorRegistry _registry;
    private readonly HashSet<RoomKind> _kinds;
    private readonly Dictionary<RoomKind, IResumeFill[]> _fills;
    private readonly ILogger<RoomGateway> _logger;
    private readonly ConcurrentDictionary<RoomKey, ConcurrentDictionary<ConnectionId, Peer>> _rooms = new();

    public RoomGateway(
        ActorRegistry registry,
        IEnumerable<RoomType> types,
        IEnumerable<IResumeFill> fills,
        ILogger<RoomGateway> logger) {
        _registry = registry;
        _kinds = [.. types.Select(x => x.Kind)];
        _fills = fills.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.ToArray());
        _logger = logger;
    }

    public void Subscribe(IClientConnection connection, RoomKey room, Epoch? epoch, long? seq) {
        if (!_kinds.Contains(room.Kind)) {
            connection.Kick(room, KickCode.NotFound);
            return;
        }
        var peers = _rooms.GetOrAdd(room, static _ => new());
        var peer = peers.GetOrAdd(
            connection.Viewer.Conn,
            static (_, x) => new Peer(x.Gateway, x.Connection, x.Room),
            (Gateway: this, Connection: connection, Room: room));
        peer.Then(() => {
            peer.Refusal = null;
            return SubscribeAsync(peer, epoch, seq);
        });
    }

    public void Unsubscribe(IClientConnection connection, RoomKey room) {
        Forget(room, connection.Viewer.Conn);
        _registry.Unsubscribe(room, connection.Viewer.Conn);
    }

    public void Command(IClientConnection connection, CmdFrame command) {
        if (!_rooms.TryGetValue(command.R, out var peers) || !peers.TryGetValue(connection.Viewer.Conn, out var peer)) {
            connection.Send(new NackFrame(command.R, command.Q, NackCode.NotSubscribed));
            return;
        }
        peer.Then(() => CommandAsync(peer, command));
    }

    public void Disconnected(IClientConnection connection, IReadOnlyCollection<RoomKey> rooms) {
        foreach (var room in rooms) {
            Forget(room, connection.Viewer.Conn);
            _registry.ConnectionClosed(room, connection.Viewer.Conn);
        }
    }

    /// <summary>Sends an unsequenced frame to every subscriber that already holds the room's snapshot.</summary>
    public void Broadcast(RoomKey room, ServerFrame frame) {
        if (!_rooms.TryGetValue(room, out var peers)) {
            return;
        }
        byte[]? bytes = null;
        foreach (var peer in peers.Values) {
            if (peer.Live) {
                peer.Connection.Send(bytes ??= WireWriter.ToUtf8(frame));
            }
        }
    }

    private async Task SubscribeAsync(Peer peer, Epoch? epoch, long? seq) {
        try {
            var result = await _registry.SubscribeAsync(peer.Room, peer, peer.Connection.Viewer, epoch, seq, CancellationToken.None);
            if (result == PostResult.Accepted) {
                return;
            }
        } catch (Exception e) {
            GatewayLog.Failed(_logger, e, peer.Room, "sub");
        }
        // A room too busy for a new subscription, or one that could not be reached: the client reconnects and resumes.
        Forget(peer.Room, peer.Id);
        peer.Connection.Restart();
    }

    private async Task CommandAsync(Peer peer, CmdFrame command) {
        if (peer.Refusal is { } code) {
            peer.Connection.Send(new NackFrame(peer.Room, command.Q, code));
            return;
        }
        try {
            if (await _registry.CommandAsync(peer.Room, peer, peer.Connection.Viewer, command, CancellationToken.None) == PostResult.Busy) {
                peer.Refusal = NackCode.ServerBusy;
            }
        } catch (Exception e) {
            GatewayLog.Failed(_logger, e, peer.Room, command.K);
            peer.Connection.Restart();
        }
    }

    private void Forget(RoomKey room, ConnectionId connection) {
        if (_rooms.TryGetValue(room, out var peers)) {
            peers.TryRemove(connection, out _);
            if (peers.IsEmpty) {
                _rooms.TryRemove(new KeyValuePair<RoomKey, ConcurrentDictionary<ConnectionId, Peer>>(room, peers));
            }
        }
    }

    private IReadOnlyList<ServerFrame> FillsAfterResume(RoomKey room) {
        return _fills.TryGetValue(room.Kind, out var fills) ? [.. fills.SelectMany(x => x.AfterResume(room))] : [];
    }

    private sealed class Peer(RoomGateway gateway, IClientConnection connection, RoomKey room) : IRoomPeer {
        private readonly Lock _lock = new();
        private Task _tail = Task.CompletedTask;
        private volatile bool _live;

        public ConnectionId Id => Connection.Viewer.Conn;

        public IClientConnection Connection { get; } = connection;

        public RoomKey Room { get; } = room;

        /// <summary>The peer has its snapshot or replay, so unsequenced fills may reach it.</summary>
        public bool Live => _live;

        /// <summary>The retryable code every command is refused with until the next <c>sub</c>; touched only by steps.</summary>
        public NackCode? Refusal { get; set; }

        /// <summary>
        /// Runs <paramref name="step"/> after the steps before it. A step whose predecessors are done runs at once on
        /// the receive loop, so a live room is reached without a hop through the thread pool.
        /// </summary>
        public void Then(Func<Task> step) {
            lock (_lock) {
                _tail = _tail.IsCompleted
                    ? step()
                    : _tail.ContinueWith(_ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        public bool Send(byte[] frame) => Connection.Send(frame);

        public bool Kick(RoomKey room, KickCode code) {
            _live = false;
            gateway.Forget(Room, Id);
            Connection.Kick(Room, code);
            return true;
        }

        public bool SendSync(byte[] frame) {
            if (!Connection.Send(frame)) {
                return false;
            }
            _live = true;
            foreach (var fill in gateway.FillsAfterResume(Room)) {
                Connection.Send(fill);
            }
            return true;
        }

        public void Restart() {
            _live = false;
            Connection.Restart();
        }
    }
}

internal static partial class GatewayLog {
    [LoggerMessage(Level = LogLevel.Error, Message = "Room {Room}: handing {Kind} to the actor failed; the connection restarts")]
    public static partial void Failed(ILogger logger, Exception exception, RoomKey room, string kind);
}
