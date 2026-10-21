using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

public enum CursorOwner : byte {
    /// <summary>No command from this client has committed in the room yet.</summary>
    None,
    Caller,
    /// <summary>The cid is bound to another principal (S16): the command is refused with <c>forbidden</c>.</summary>
    OtherPrincipal,
}

/// <param name="LastQ">The highest committed <c>q</c>; 0 unless <see cref="Owner"/> is <see cref="CursorOwner.Caller"/>.</param>
public readonly record struct ClientCursor(CursorOwner Owner, long LastQ);

/// <summary>
/// Durable per-tab command cursors (§c.8): the cursor is written in the same transaction as the change it
/// acknowledges, so a resent command that already committed is acked as a duplicate, even across a restart.
/// </summary>
public sealed class CursorRepo(NpgsqlDataSource dataSource) {
    public async Task<ClientCursor> LoadAsync(
        RoomKey room,
        ClientId client,
        PrincipalId principal,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "select principal_id, last_cseq from client_cursors where room_key = $1 and client_id = $2");
        command.Parameters.Add(new() { Value = room.ToString() });
        command.Parameters.Add(new() { Value = client.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return new ClientCursor(CursorOwner.None, 0);
        }
        return reader.GetGuid(0) == principal.Value
            ? new ClientCursor(CursorOwner.Caller, reader.GetInt64(1))
            : new ClientCursor(CursorOwner.OtherPrincipal, 0);
    }

    /// <summary>
    /// The cursor statement of a command's persist batch. It never moves a cursor backwards (R28) and never
    /// rebinds a cid that belongs to another principal.
    /// </summary>
    public static NpgsqlBatchCommand Upsert(RoomKey room, ClientId client, PrincipalId principal, long q) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(q);
        return new("""
            insert into client_cursors (room_key, client_id, principal_id, last_cseq) values ($1, $2, $3, $4)
            on conflict (room_key, client_id) do update
              set last_cseq = greatest(client_cursors.last_cseq, excluded.last_cseq),
                  updated_on = excluded.updated_on
              where client_cursors.principal_id = excluded.principal_id
            """) {
            Parameters = {
                new() { Value = room.ToString() },
                new() { Value = client.Value },
                new() { Value = principal.Value },
                new() { Value = q },
            },
        };
    }
}
