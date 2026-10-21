using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

public enum PostResult : byte {
    Accepted,

    /// <summary>The bounded external queue is full; a command was already nacked <c>server_busy</c> (R26).</summary>
    Busy,

    /// <summary>The actor retired; post to the next incarnation (<see cref="ActorRegistry"/> does that).</summary>
    Closed,
}

/// <summary>A room actor as the transport and HTTP handlers see it. Every method is a non-blocking enqueue.</summary>
public interface IRoomActor {
    RoomKey Key { get; }

    /// <summary>
    /// Subscribes (or re-subscribes) the connection. With an epoch it is a resume and uses the internal queue, so a
    /// reconnect storm is never refused; without one it is a new subscription on the bounded external queue.
    /// </summary>
    PostResult Subscribe(IRoomPeer peer, Viewer viewer, Epoch? epoch = null, long? seq = null);

    /// <summary>A client command; when the external queue is full the peer gets <c>nack server_busy</c>.</summary>
    PostResult Command(IRoomPeer peer, Viewer viewer, CmdFrame frame);

    PostResult Unsubscribe(ConnectionId connection);

    PostResult ConnectionClosed(ConnectionId connection);

    /// <summary>Re-checks every subscriber's access and kicks those who lost it (§c.2).</summary>
    PostResult AccessChanged();

    /// <summary>A profile or guest rename: <c>presence.update</c> when the principal is present.</summary>
    PostResult PrincipalUpdated(PrincipalInfo principal);
}

/// <summary>The typed side of an actor: internal inputs, HTTP asks and queries.</summary>
public interface IRoomActor<TState, in TInput> : IRoomActor where TState : class where TInput : class {
    /// <summary>An actor-to-actor post: enqueued on the unbounded internal queue, never awaited.</summary>
    PostResult Post(TInput input);

    /// <summary>Decides <paramref name="input"/> as an internal transition with an HTTP caller and waits for the outcome.</summary>
    Task<AskResult> AskAsync(TInput input, Viewer? viewer, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="query"/> on the actor's thread against its current state.</summary>
    Task<T> QueryAsync<T>(Func<RoomView<TState>, ValueTask<T>> query, CancellationToken cancellationToken);
}

public enum AskStatus : byte {
    Accepted,
    NoOp,
    Rejected,
    NotFound,

    /// <summary>The transaction failed for infrastructure reasons, or the actor is stopping; retry later.</summary>
    Unavailable,
}

/// <param name="Seq">The room's seq after the input (unchanged for a no-op).</param>
public sealed record AskResult(
    AskStatus Status,
    long Seq = 0,
    NackCode? Code = null,
    string? Detail = null,
    JsonElement? Data = null);

/// <summary>Thrown by a query the actor could not run: the room could not be loaded or the actor stopped.</summary>
public sealed class RoomUnavailableException(RoomKey room, Exception? innerException = null)
    : Exception($"Room {room} is unavailable.", innerException);
