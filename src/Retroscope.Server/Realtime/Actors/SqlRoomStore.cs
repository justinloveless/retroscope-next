using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// The Postgres store of a room kind: the subclass loads the room and plans SQL per event; this class runs the
/// plan and the cursor upsert behind the epoch fence (§c.7 step 3).
/// </summary>
public abstract class SqlRoomStore<TState, TEvent>(NpgsqlDataSource dataSource, Leader leader, CursorRepo cursors)
    : IRoomStore<TState, TEvent> where TState : class {
    protected NpgsqlDataSource DataSource => dataSource;

    public abstract Task<TState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The statements for <paramref name="events"/>, planned against the state before they apply.</summary>
    protected abstract IEnumerable<NpgsqlBatchCommand> Plan(RoomKey room, TState state, IReadOnlyList<TEvent> events);

    public Task<ClientCursor> LoadCursorAsync(
        RoomKey room,
        ClientId client,
        PrincipalId principal,
        CancellationToken cancellationToken) {
        return cursors.LoadAsync(room, client, principal, cancellationToken);
    }

    public async Task CommitAsync(
        RoomKey room,
        TState state,
        IReadOnlyList<TEvent> events,
        CursorAdvance? cursor,
        CancellationToken cancellationToken) {
        var commands = Plan(room, state, events).ToList();
        if (cursor is { } advance) {
            commands.Add(CursorRepo.Upsert(room, advance.Client, advance.Principal, advance.Q));
        }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try {
            await leader.CommitFencedAsync(connection, commands, cancellationToken);
        } catch (PostgresException e) when (!Leader.IsFencingLost(e) && !IsOutcomeUnknown(e)) {
            throw new CommitRejectedException(e.SqlState, e);
        }
    }

    /// <summary>
    /// A connection exception (class 08) or operator intervention (class 57, an admin shutdown, say) can cut a
    /// transaction off around its COMMIT, so the server's answer does not prove it rolled back. Such a failure
    /// stays unclassified and poisons the actor instead of nacking an intent that may have committed.
    /// </summary>
    private static bool IsOutcomeUnknown(PostgresException exception) =>
        exception.SqlState.StartsWith("08", StringComparison.Ordinal) || exception.SqlState.StartsWith("57", StringComparison.Ordinal);
}
