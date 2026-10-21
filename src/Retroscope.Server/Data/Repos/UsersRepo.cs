using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>What login needs about an account; <see cref="PasswordHash"/> is null for OIDC-only and claimed accounts.</summary>
public sealed record UserCredentials(PrincipalId Id, string? PasswordHash, bool Disabled);

public sealed record UserProfile(
    PrincipalId Id,
    string Email,
    bool EmailVerified,
    string FullName,
    string? Nickname,
    string DisplayName,
    BlobId? AvatarBlobId,
    string Theme,
    bool IsAdmin,
    bool HasPassword);

public sealed record UserTeam(TeamId Id, string Name, TeamRole Role);

/// <summary>A profile change; a null member is left as it is, an empty <see cref="Nickname"/> clears it.</summary>
public sealed record ProfilePatch(string? FullName, string? Nickname, string? Theme);

public sealed class UsersRepo(NpgsqlDataSource dataSource) {
    /// <summary>
    /// The name other people see, over a <c>users u</c> row: nickname, then full name, then "Player" (the original's
    /// roster fallback). Never the email (gaps: display-name leakage).
    /// </summary>
    public const string DisplayNameSql = "coalesce(u.nickname, nullif(u.full_name, ''), 'Player')";

    private const string ProfileColumns = $"""
        u.id, u.email, u.email_verified_at is not null, u.full_name, u.nickname, {DisplayNameSql}, u.avatar_blob_id,
        u.theme, u.is_admin, u.password_hash is not null
        """;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public Task<UserCredentials?> FindCredentialsAsync(string email, CancellationToken cancellationToken) =>
        FindCredentialsAsync("email = $1", email, cancellationToken);

    public Task<UserCredentials?> FindCredentialsAsync(PrincipalId id, CancellationToken cancellationToken) =>
        FindCredentialsAsync("id = $1", id.Value, cancellationToken);

    private async Task<UserCredentials?> FindCredentialsAsync(string where, object key, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            $"select id, password_hash, disabled_at is not null from users where {where}");
        command.Parameters.Add(new() { Value = key });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new UserCredentials(
            new PrincipalId(reader.GetGuid(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2));
    }

    public async Task<PrincipalId?> FindIdAsync(string email, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("select id from users where email = $1");
        command.Parameters.Add(new() { Value = email });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? new PrincipalId(id) : null;
    }

    /// <summary>Inserts the principal and the user in one transaction.</summary>
    /// <returns>False when the email is already taken.</returns>
    public async Task<bool> CreateAsync(
        PrincipalId id,
        string email,
        string? passwordHash,
        string fullName,
        DateTimeOffset? emailVerifiedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) {
            BatchCommands = {
                new("insert into principals (id, kind, created_at) values ($1, 'user', $2)") {
                    Parameters = { new() { Value = id.Value }, new() { Value = now } },
                },
                new("""
                    insert into users (id, email, email_verified_at, password_hash, full_name, mentions_seen_at, created_at, updated_at)
                    values ($1, $2, $3, $4, $5, $6, $6, $6)
                    """) {
                    Parameters = {
                        new() { Value = id.Value },
                        new() { Value = email },
                        new() { Value = (object?)emailVerifiedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                        new() { Value = (object?)passwordHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
                        new() { Value = fullName },
                        new() { Value = now },
                    },
                },
            },
        };
        try {
            await batch.ExecuteNonQueryAsync(cancellationToken);
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "users_email_uq") {
            return false;
        }
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async Task<UserProfile?> GetProfileAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"select {ProfileColumns} from users u where u.id = $1");
        command.Parameters.Add(new() { Value = id.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    /// <returns>The profile after the change; null when the user does not exist.</returns>
    public async Task<UserProfile?> UpdateProfileAsync(
        PrincipalId id,
        ProfilePatch patch,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            update users u set
              full_name = coalesce($2, u.full_name),
              nickname = case when $3::text is null then u.nickname else nullif($3, '') end,
              theme = coalesce($4, u.theme),
              updated_at = $5
            where u.id = $1
            returning {ProfileColumns}
            """);
        command.Parameters.Add(new() { Value = id.Value });
        command.Parameters.Add(new() { Value = (object?)patch.FullName ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)patch.Nickname ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)patch.Theme ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task<IReadOnlyList<UserTeam>> TeamsAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select t.id, t.name, m.role
            from team_members m join teams t on t.id = m.team_id
            where m.user_id = $1 and t.deleted_at is null
            order by t.name, t.id
            """);
        command.Parameters.Add(new() { Value = id.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var teams = new List<UserTeam>();
        while (await reader.ReadAsync(cancellationToken)) {
            teams.Add(new UserTeam(new TeamId(reader.GetGuid(0)), reader.GetString(1), ParseRole(reader.GetString(2))));
        }
        return teams;
    }

    /// <summary>
    /// Mentions newer than <c>mentions_seen_at</c> on boards the user can still read: the board and its team are not
    /// deleted, and the user is a member, or the board is <c>link</c>, or it is <c>password</c> and the user holds a
    /// current grant (or created the quick board). The same rules as <c>Policy.Can(Subscribe)</c> for a board.
    /// </summary>
    public async Task<int> UnreadMentionsAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select count(*)::int
            from mentions m
            join users u on u.id = m.user_id
            join retro_boards b on b.id = m.board_id and b.deleted_at is null
            left join teams t on t.id = b.team_id
            left join team_members tm on tm.team_id = b.team_id and tm.user_id = m.user_id
            where m.user_id = $1
              and m.created_at > u.mentions_seen_at
              and (b.team_id is null or t.deleted_at is null)
              and (tm.user_id is not null
                or b.visibility = 'link'
                or (b.visibility = 'password'
                    and ((b.team_id is null and b.created_by = m.user_id)
                      or exists (select 1 from board_access_grants g
                                 where g.board_id = b.id and g.principal_id = m.user_id
                                   and g.password_epoch = b.password_set_at))))
            """);
        command.Parameters.Add(new() { Value = id.Value });
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static NpgsqlBatchCommand SetPasswordHash(PrincipalId id, string? passwordHash, DateTimeOffset now) =>
        new("update users set password_hash = $2, updated_at = $3 where id = $1") {
            Parameters = {
                new() { Value = id.Value },
                new() { Value = (object?)passwordHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
                new() { Value = now },
            },
        };

    /// <summary>
    /// The claim (§e): an unverified account becomes verified and loses its password. Affects no row when the account is
    /// already verified, so a claim can never strip a verified owner's password.
    /// </summary>
    public static NpgsqlBatchCommand Claim(PrincipalId id, DateTimeOffset now) =>
        new("""
            update users set email_verified_at = $2, password_hash = null, updated_at = $2
            where id = $1 and email_verified_at is null
            """) {
            Parameters = { new() { Value = id.Value }, new() { Value = now } },
        };

    /// <summary>An <c>audit_log</c> row; <paramref name="meta"/> holds ids, codes and HMACs, never content or secrets.</summary>
    public static NpgsqlBatchCommand Audit(
        string action,
        PrincipalId? actor,
        string? target,
        IReadOnlyDictionary<string, string> meta,
        DateTimeOffset now) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            foreach (var (name, value) in meta) {
                writer.WriteString(name, value);
            }
            writer.WriteEndObject();
        }
        return new("insert into audit_log (at, actor_id, action, target, meta) values ($1, $2, $3, $4, $5)") {
            Parameters = {
                new() { Value = now },
                new() { Value = (object?)actor?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new() { Value = action },
                new() { Value = (object?)target ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
                new() { Value = Encoding.UTF8.GetString(buffer.ToArray()), NpgsqlDbType = NpgsqlDbType.Jsonb },
            },
        };
    }

    public async Task WriteAsync(IEnumerable<NpgsqlBatchCommand> commands, CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var command in commands) {
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static UserProfile ReadProfile(NpgsqlDataReader reader) => new(
        new PrincipalId(reader.GetGuid(0)),
        reader.GetString(1),
        reader.GetBoolean(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : new BlobId(reader.GetGuid(6)),
        reader.GetString(7),
        reader.GetBoolean(8),
        reader.GetBoolean(9));

    private static TeamRole ParseRole(string role) => role switch {
        "owner" => TeamRole.Owner,
        "admin" => TeamRole.Admin,
        _ => TeamRole.Member,
    };
}
