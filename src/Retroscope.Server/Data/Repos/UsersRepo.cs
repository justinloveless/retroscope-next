using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Server.Endpoints;

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

/// <summary>A user to create; <see cref="PasswordHash"/> is null for password-less accounts.</summary>
public sealed record NewUser(
    PrincipalId Id,
    string Email,
    string? PasswordHash,
    string FullName,
    DateTimeOffset? EmailVerifiedAt,
    DateTimeOffset Now);

/// <param name="OpenActions">The team's action items not yet done.</param>
public sealed record UserTeam(TeamId Id, string Name, TeamRole Role, int OpenActions);

/// <param name="LastTeamId">The team the user visited last among their current teams, or null.</param>
public sealed record UserTeams(IReadOnlyList<UserTeam> Teams, TeamId? LastTeamId);

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

    // One principal and user insert for every way an account is created, over the parameters AddNewUser binds:
    // $1 id, $2 email, $3 email_verified_at, $4 password_hash, $5 full_name, $6 now. InsertUser reads `principal`.
    private const string InsertPrincipal = "insert into principals (id, kind, created_at) select $1, 'user', $6";

    private const string InsertUser = """
        insert into users (id, email, email_verified_at, password_hash, full_name, mentions_seen_at, created_at, updated_at)
        select id, $2, $3, $4, $5, $6, $6, $6 from principal
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

    /// <summary>Inserts the principal and the user in one statement.</summary>
    /// <returns>False when the email is already taken.</returns>
    public async Task<bool> CreateAsync(NewUser user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            with principal as (
              {InsertPrincipal}
              returning id
            )
            {InsertUser}
            """);
        AddNewUser(command, user);
        try {
            await command.ExecuteNonQueryAsync(cancellationToken);
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "users_email_uq") {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Signup: creates an unverified user when the address is free, else finds its owner. Both outcomes run this one
    /// statement and write an audit row (<c>auth.signup</c> or <c>auth.signup.taken</c>), so both commit a write and
    /// wait for the same WAL flush: a read-only taken path answered measurably faster (no account oracle).
    /// </summary>
    /// <returns>Whether the user was created, else the id of the account that holds the address.</returns>
    public async Task<(bool Created, PrincipalId? Existing)> SignupAsync(
        NewUser user,
        string emailHmac,
        string ipPrefix,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            with taken as (
              select id from users where email = $2
            ), principal as (
              {InsertPrincipal}
              where not exists (select 1 from taken)
              returning id
            ), created as (
              {InsertUser}
              returning id
            ), audited as (
              insert into audit_log (at, actor_id, action, target, meta)
              select $6, (select id from created),
                     case when exists (select 1 from created) then 'auth.signup' else 'auth.signup.taken' end,
                     coalesce((select id from created), (select id from taken))::text,
                     jsonb_build_object('email_hmac', $7::text, 'ip', $8::text)
            )
            select (select id from created), (select id from taken)
            """);
        AddNewUser(command, user);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = emailHmac });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ipPrefix });
        try {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.IsDBNull(0)) {
                return (true, null);
            }
            return (false, reader.IsDBNull(1) ? null : new PrincipalId(reader.GetGuid(1)));
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "users_email_uq") {
            // A concurrent signup took the address between the lookup and the insert; only that race pays the lookup.
            return (false, await FindIdAsync(user.Email, cancellationToken));
        }
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

    /// <summary>
    /// Stores <paramref name="image"/> as the user's avatar and deletes the avatar it replaces, in one transaction. When
    /// no user matches, or <paramref name="onlyIfNone"/> is set and the user already has one, nothing is written.
    /// </summary>
    /// <returns>True when the avatar changed.</returns>
    public async Task<bool> SetAvatarAsync(
        PrincipalId id,
        SniffedImage image,
        bool onlyIfNone,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        var blob = new BlobId(Guid.NewGuid());
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) {
            BatchCommands = { BlobsRepo.InsertAvatar(blob, id, image), PointAvatar(id, blob, onlyIfNone, now) },
        };
        if (!(bool)(await batch.ExecuteScalarAsync(cancellationToken))!) {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Verifies the email of an unverified account and audits <c>auth.verify</c> with <paramref name="proof"/>, in one
    /// statement that reads the row itself. False, and nothing written, when the account was already verified.
    /// </summary>
    public async Task<bool> VerifyAsync(PrincipalId id, string proof, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            with verified as (
              update users set email_verified_at = $2, updated_at = $2
              where id = $1 and email_verified_at is null
              returning id
            )
            insert into audit_log (at, actor_id, action, target, meta)
            select $2, id, 'auth.verify', id::text, jsonb_build_object('proof', $3::text) from verified
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id.Value });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = proof });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Removes the user's avatar and deletes its blob; false when no user matches.</summary>
    public async Task<bool> ClearAvatarAsync(PrincipalId id, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(PointAvatar(id, null, onlyIfNone: false, now));
        return (bool)(await batch.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The user's teams, by name ignoring case, each with its open action-item count (one grouped count over the
    /// open-items index), and the team the user visited last among them (UX18 decisions 8 and 10). One round trip.
    /// </summary>
    public async Task<UserTeams> TeamsAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new("""
            select t.id, t.name, m.role, coalesce(o.open, 0)
            from team_members m
            join teams t on t.id = m.team_id and t.deleted_at is null
            left join (
              select a.team_id, count(*)::int as open
              from team_action_items a
              join team_members mine on mine.team_id = a.team_id and mine.user_id = $1
              where a.done_at is null
              group by a.team_id
            ) o on o.team_id = t.id
            where m.user_id = $1
            order by lower(t.name), t.id
            """) {
            Parameters = { new() { Value = id.Value } },
        });
        batch.BatchCommands.Add(new("""
            select a.entity_id
            from recent_activity a
            join team_members m on m.team_id = a.entity_id and m.user_id = a.user_id
            join teams t on t.id = a.entity_id and t.deleted_at is null
            where a.user_id = $1 and a.entity_kind = 'team'
            order by a.last_at desc, a.entity_id
            limit 1
            """) {
            Parameters = { new() { Value = id.Value } },
        });
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var teams = new List<UserTeam>();
        while (await reader.ReadAsync(cancellationToken)) {
            teams.Add(new UserTeam(new TeamId(reader.GetGuid(0)), reader.GetString(1), ParseRole(reader.GetString(2)), reader.GetInt32(3)));
        }
        await reader.NextResultAsync(cancellationToken);
        TeamId? last = await reader.ReadAsync(cancellationToken) ? new TeamId(reader.GetGuid(0)) : null;
        return new UserTeams(teams, last);
    }

    /// <summary>
    /// Where a signed-in <c>/</c> leads: the team visited last among the user's current teams, else the first of them
    /// in <c>me.teams</c> order (by name, ignoring case); null when the user is in no team.
    /// </summary>
    public async Task<TeamId?> HomeTeamAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select t.id
            from team_members m
            join teams t on t.id = m.team_id and t.deleted_at is null
            left join recent_activity a on a.user_id = m.user_id and a.entity_kind = 'team' and a.entity_id = t.id
            where m.user_id = $1
            order by a.last_at desc nulls last, lower(t.name), t.id
            limit 1
            """);
        command.Parameters.Add(new() { Value = id.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid team ? new TeamId(team) : null;
    }

    /// <summary>
    /// Mentions newer than <c>mentions_seen_at</c> on boards the user can still read: the board and its team are not
    /// deleted, and the user is a member of the board's team. The same rule as <c>Policy.Can(Subscribe)</c> for a
    /// board (UX18).
    /// </summary>
    public async Task<int> UnreadMentionsAsync(PrincipalId id, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select count(*)::int
            from mentions m
            join users u on u.id = m.user_id
            join retro_boards b on b.id = m.board_id and b.deleted_at is null
            join teams t on t.id = b.team_id and t.deleted_at is null
            join team_members tm on tm.team_id = b.team_id and tm.user_id = m.user_id
            where m.user_id = $1
              and m.created_at > u.mentions_seen_at
            """);
        command.Parameters.Add(new() { Value = id.Value });
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Concurrent writers each delete exactly the avatar they replaced: PostgreSQL 18's old.avatar_blob_id is the row's
    // value under the update's own row lock.
    private static NpgsqlBatchCommand PointAvatar(PrincipalId id, BlobId? blob, bool onlyIfNone, DateTimeOffset now) =>
        new("""
            with updated as (
              update users set avatar_blob_id = $2, updated_at = $3
              where id = $1 and (not $4 or avatar_blob_id is null)
              returning old.avatar_blob_id as previous
            ), deleted as (
              delete from blobs where purpose = 'avatar' and id = (select previous from updated)
            )
            select exists (select 1 from updated)
            """) {
            Parameters = {
                new NpgsqlParameter<Guid> { TypedValue = id.Value },
                new NpgsqlParameter<Guid?> { TypedValue = blob?.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new NpgsqlParameter<DateTimeOffset> { TypedValue = now },
                new NpgsqlParameter<bool> { TypedValue = onlyIfNone },
            },
        };

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

    /// <summary>Marks the email verified (keeping an earlier verification time) without claiming the account.</summary>
    public static NpgsqlBatchCommand MarkVerified(PrincipalId id, DateTimeOffset now) =>
        new("update users set email_verified_at = coalesce(email_verified_at, $2), updated_at = $2 where id = $1") {
            Parameters = { new() { Value = id.Value }, new() { Value = now } },
        };

    /// <summary>Removes every external identity of the user, returning one <c>provider</c> row per identity.</summary>
    public static NpgsqlBatchCommand UnlinkIdentities(PrincipalId id) =>
        new("delete from external_identities where user_id = $1 returning provider") {
            Parameters = { new() { Value = id.Value } },
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

    private static void AddNewUser(NpgsqlCommand command, NewUser user) {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = user.Id.Value });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = user.Email });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = user.EmailVerifiedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = user.PasswordHash, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = user.FullName });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = user.Now });
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
