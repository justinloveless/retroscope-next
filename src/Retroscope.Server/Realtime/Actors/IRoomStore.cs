using Retroscope.Core.Ids;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>The cursor write of a client command's transaction (§c.8): exactly-once per <c>(cid, q)</c>.</summary>
public readonly record struct CursorAdvance(ClientId Client, PrincipalId Principal, long Q);

/// <summary>
/// Loads a room and persists its committed events. <see cref="SqlRoomStore{TState,TEvent}"/> is the Postgres
/// implementation; tests and the simulation use in-memory ones.
/// </summary>
public interface IRoomStore<TState, TEvent> where TState : class {
    /// <summary>The room's state, or null when it does not exist (deleted, purged, never created).</summary>
    Task<TState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ClientCursor> LoadCursorAsync(RoomKey room, ClientId client, PrincipalId principal, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="events"/> (and the cursor) in one fenced transaction, against the state before they
    /// apply. Failures are classified for the actor (R2):
    /// <see cref="CommitRejectedException"/> means nothing committed (a constraint, a statement error);
    /// <see cref="Data.FencingLostException"/> means another instance leads; anything else (a broken connection,
    /// SQLSTATE class 08 or 57) is an infrastructure failure or an unknown outcome.
    /// </summary>
    Task CommitAsync(
        RoomKey room,
        TState state,
        IReadOnlyList<TEvent> events,
        CursorAdvance? cursor,
        CancellationToken cancellationToken);
}

/// <summary>The database refused the transaction and rolled it back: a bug, nacked <c>internal</c>.</summary>
public sealed class CommitRejectedException(string? sqlState, Exception innerException)
    : Exception($"The room transaction was rejected (SQLSTATE {sqlState}).", innerException) {
    public string? SqlState { get; } = sqlState;
}
