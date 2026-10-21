using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Server.Auth;

namespace Retroscope.Server.Data.Repos;

/// <summary>A <c>sessions</c> row with the user facts a request needs.</summary>
public sealed record SessionRow(
    byte[] IdHash,
    AuthMethod Method,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsExpiresAt,
    Principal User,
    bool EmailVerified,
    bool Disabled);

/// <summary>One of the user's sessions, as the account page lists it.</summary>
public sealed record SessionSummary(
    byte[] IdHash,
    AuthMethod Method,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? UserAgent);

public sealed class SessionsRepo(NpgsqlDataSource dataSource) {
    public const int MaxUserAgentLength = 200;

    public static NpgsqlBatchCommand Insert(
        byte[] idHash,
        PrincipalId user,
        AuthMethod method,
        DateTimeOffset now,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absExpiresAt,
        string? userAgent) =>
        new("""
            insert into sessions (id_hash, user_id, auth_method, created_at, last_seen_at, idle_expires_at, abs_expires_at, user_agent)
            values ($1, $2, $3, $4, $4, $5, $6, $7)
            """) {
            Parameters = {
                new() { Value = idHash },
                new() { Value = user.Value },
                new() { Value = AuthMethods.ToDb(method) },
                new() { Value = now },
                new() { Value = idleExpiresAt },
                new() { Value = absExpiresAt },
                new() { Value = (object?)Truncate(userAgent) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            },
        };

    public static NpgsqlBatchCommand DeleteAllOf(PrincipalId user) =>
        new("delete from sessions where user_id = $1 returning id_hash") {
            Parameters = { new() { Value = user.Value } },
        };

    public async Task InsertAsync(NpgsqlBatchCommand insert, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(insert);
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SessionRow?> LoadAsync(byte[] idHash, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select s.id_hash, s.auth_method, s.created_at, s.last_seen_at, s.idle_expires_at, s.abs_expires_at,
                   u.id, {UsersRepo.DisplayNameSql}, u.avatar_blob_id, u.is_admin, u.email_verified_at is not null,
                   u.disabled_at is not null
            from sessions s join users u on u.id = s.user_id
            where s.id_hash = $1
            """);
        command.Parameters.Add(new() { Value = idHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var user = new Principal(
            new PrincipalId(reader.GetGuid(6)),
            PrincipalKind.User,
            reader.GetString(7),
            reader.IsDBNull(8) ? null : new BlobId(reader.GetGuid(8)),
            reader.GetBoolean(9));
        return new SessionRow(
            reader.GetFieldValue<byte[]>(0),
            AuthMethods.FromDb(reader.GetString(1)),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            user,
            reader.GetBoolean(10),
            reader.GetBoolean(11));
    }

    /// <summary>Slides the idle expiry; the absolute expiry never moves.</summary>
    public async Task TouchAsync(byte[] idHash, DateTimeOffset now, DateTimeOffset idleExpiresAt, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "update sessions set last_seen_at = $2, idle_expires_at = least($3, abs_expires_at) where id_hash = $1");
        command.Parameters.Add(new() { Value = idHash });
        command.Parameters.Add(new() { Value = now });
        command.Parameters.Add(new() { Value = idleExpiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select id_hash, auth_method, created_at, last_seen_at, user_agent
            from sessions where user_id = $1
            order by last_seen_at desc, id_hash
            """);
        command.Parameters.Add(new() { Value = user.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<SessionSummary>();
        while (await reader.ReadAsync(cancellationToken)) {
            sessions.Add(new SessionSummary(
                reader.GetFieldValue<byte[]>(0),
                AuthMethods.FromDb(reader.GetString(1)),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return sessions;
    }

    /// <summary>Deletes the given sessions of one user.</summary>
    /// <returns>The hashes that were deleted.</returns>
    public async Task<IReadOnlyList<byte[]>> DeleteAsync(
        PrincipalId user,
        IReadOnlyCollection<byte[]> idHashes,
        CancellationToken cancellationToken) {
        if (idHashes.Count == 0) {
            return [];
        }
        await using var command = dataSource.CreateCommand(
            "delete from sessions where user_id = $1 and id_hash = any($2) returning id_hash");
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = idHashes.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        return await ReadHashesAsync(command, cancellationToken);
    }

    /// <summary>Deletes every session of the user except <paramref name="keep"/>.</summary>
    /// <returns>The hashes that were deleted.</returns>
    public async Task<IReadOnlyList<byte[]>> DeleteAllAsync(PrincipalId user, byte[]? keep, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "delete from sessions where user_id = $1 and ($2::bytea is null or id_hash <> $2) returning id_hash");
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = (object?)keep ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        return await ReadHashesAsync(command, cancellationToken);
    }

    public static async Task<IReadOnlyList<byte[]>> ReadHashesAsync(NpgsqlCommand command, CancellationToken cancellationToken) {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hashes = new List<byte[]>();
        while (await reader.ReadAsync(cancellationToken)) {
            hashes.Add(reader.GetFieldValue<byte[]>(0));
        }
        return hashes;
    }

    private static string? Truncate(string? userAgent) =>
        userAgent is { Length: > MaxUserAgentLength } ? userAgent[..MaxUserAgentLength] : userAgent;
}
