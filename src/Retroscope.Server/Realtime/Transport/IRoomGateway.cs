using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Transport;

/// <summary>
/// Where the transport hands room traffic: the actor runtime (U13) implements it and registers it in
/// <c>ActorsModule.AddServices</c>. Every method is called from a connection's receive loop and must not block it
/// (R26): a full actor queue answers with a <c>server_busy</c> nack through the connection instead.
/// </summary>
public interface IRoomGateway {
    /// <summary>A <c>sub</c>, or one entry of <c>hello.subs</c>. Idempotent; a refusal is a <c>kick</c>.</summary>
    void Subscribe(IClientConnection connection, RoomKey room, Epoch? epoch, long? seq);

    void Unsubscribe(IClientConnection connection, RoomKey room);

    /// <summary>A command for a room the connection has subscribed to.</summary>
    void Command(IClientConnection connection, CmdFrame command);

    /// <summary>The socket is gone; <paramref name="rooms"/> are the rooms it was still subscribed to.</summary>
    void Disconnected(IClientConnection connection, IReadOnlyCollection<RoomKey> rooms);
}

/// <summary>A live socket as actors see it. Every member is thread-safe and never blocks.</summary>
public interface IClientConnection {
    /// <summary>Identity, tab and socket. Set by <c>hello</c>, before the gateway sees the connection.</summary>
    Viewer Viewer { get; }

    /// <summary>The sign-in session behind a user's cookie; null for guests.</summary>
    Guid? AuthSessionId { get; }

    /// <summary>Queues a frame behind everything sent before it. False when the socket is closing or just overflowed (4008).</summary>
    bool Send(ServerFrame frame);

    /// <summary>Queues one already serialized frame; the memory must not change afterwards.</summary>
    bool Send(ReadOnlyMemory<byte> utf8);

    /// <summary>Queues one frame made of several segments, e.g. a shared snapshot document and a per-viewer overlay (R11).</summary>
    bool Send(IReadOnlyList<ReadOnlyMemory<byte>> segments);

    /// <summary>Drops the room from this connection and tells the client why.</summary>
    void Kick(RoomKey room, KickCode code);

    /// <summary>Sends <c>bye{restart, retryMs}</c> and closes with 1012 (fencing loss, outcome-unknown commit, shutdown).</summary>
    void Restart();
}

/// <summary>Serves no rooms: used until the actor runtime registers the real gateway.</summary>
internal sealed class UnavailableRoomGateway : IRoomGateway {
    public void Subscribe(IClientConnection connection, RoomKey room, Epoch? epoch, long? seq) {
        connection.Kick(room, KickCode.NotFound);
    }

    public void Unsubscribe(IClientConnection connection, RoomKey room) {
    }

    public void Command(IClientConnection connection, CmdFrame command) {
        connection.Send(new NackFrame(command.R, command.Q, NackCode.NotSubscribed));
    }

    public void Disconnected(IClientConnection connection, IReadOnlyCollection<RoomKey> rooms) {
    }
}
