using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

public sealed record GuestRow(PrincipalId Id, string DisplayName, DateTimeOffset ExpiresAt);

public sealed class GuestsRepo(NpgsqlDataSource dataSource) {
    /// <summary>Inserts the guest principal and its token in one transaction.</summary>
    public async Task CreateAsync(
        PrincipalId id,
        string displayName,
        byte[] tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) {
            BatchCommands = {
                new("insert into principals (id, kind, display_name, created_at) values ($1, 'guest', $2, $3)") {
                    Parameters = { new() { Value = id.Value }, new() { Value = displayName }, new() { Value = now } },
                },
                new("insert into guest_tokens (token_hash, principal_id, expires_at) values ($1, $2, $3)") {
                    Parameters = { new() { Value = tokenHash }, new() { Value = id.Value }, new() { Value = expiresAt } },
                },
            },
        };
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task<GuestRow?> LoadAsync(byte[] tokenHash, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select p.id, p.display_name, g.expires_at
            from guest_tokens g join principals p on p.id = g.principal_id
            where g.token_hash = $1 and p.kind = 'guest'
            """);
        command.Parameters.Add(new() { Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new GuestRow(new PrincipalId(reader.GetGuid(0)), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task SlideAsync(byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "update guest_tokens set expires_at = greatest(expires_at, $2) where token_hash = $1");
        command.Parameters.Add(new() { Value = tokenHash });
        command.Parameters.Add(new() { Value = expiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RenameAsync(PrincipalId id, string displayName, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "update principals set display_name = $2 where id = $1 and kind = 'guest'");
        command.Parameters.Add(new() { Value = id.Value });
        command.Parameters.Add(new() { Value = displayName });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
