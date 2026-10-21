using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>
/// A keyset position in a list ordered by <c>(created_at, id)</c>, opaque to clients (§d: <c>?cursor=</c>). The time
/// is kept in microseconds, the precision of <c>timestamptz</c>, so the next page starts exactly after the last row.
/// </summary>
public readonly record struct ListCursor(DateTimeOffset At, Guid Id) {
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static int Limit(int? requested) => Math.Clamp(requested ?? DefaultLimit, 1, MaxLimit);

    public static bool TryParse(string? text, out ListCursor? cursor) {
        cursor = null;
        if (string.IsNullOrEmpty(text)) {
            return true;
        }
        if (text.Length > 128 || !Base64Url.IsValid(text)) {
            return false;
        }
        var parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(text)).Split('.');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var micros)
            || micros > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() * 1000
            || !Guid.TryParseExact(parts[1], "N", out var id)) {
            return false;
        }
        cursor = new ListCursor(DateTimeOffset.UnixEpoch.AddTicks(micros * 10), id);
        return true;
    }

    public override string ToString() {
        var micros = (At - DateTimeOffset.UnixEpoch).Ticks / 10;
        return Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"{micros.ToString(CultureInfo.InvariantCulture)}.{Id:N}"));
    }

    /// <summary>Adds <c>at</c> and <c>id</c> parameters (both null on the first page) for <c>(created_at, id) &lt; (at, id)</c>.</summary>
    public static void AddParameters(NpgsqlCommand command, ListCursor? cursor) {
        command.Parameters.Add(new() { Value = (object?)cursor?.At ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new() { Value = (object?)cursor?.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    }
}

/// <summary>One page and where the next one starts (null on the last page).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, ListCursor? Next);

public static class Page {
    /// <summary>From <paramref name="limit"/> + 1 rows: the extra one only proves that a next page exists.</summary>
    public static Page<T> Of<T>(List<(T Item, ListCursor Key)> rows, int limit) {
        return rows.Count > limit
            ? new Page<T>([.. rows.Take(limit).Select(x => x.Item)], rows[limit - 1].Key)
            : new Page<T>([.. rows.Select(x => x.Item)], null);
    }
}

/// <summary>Team membership as the rows hold it; the team and board actors commit membership before announcing it.</summary>
public static class TeamRoles {
    /// <returns>Null when the user is not a member or the team is soft-deleted.</returns>
    public static async Task<TeamRole?> RoleAsync(
        NpgsqlDataSource dataSource,
        TeamId team,
        PrincipalId user,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select m.role from team_members m join teams t on t.id = m.team_id
            where m.team_id = $1 and m.user_id = $2 and t.deleted_at is null
            """);
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = user.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is string role ? Parse(role) : null;
    }

    public static TeamRole Parse(string role) => role switch {
        "owner" => TeamRole.Owner,
        "admin" => TeamRole.Admin,
        _ => TeamRole.Member,
    };
}

public sealed record BoardListItem(
    BoardId Id,
    string Code,
    string Title,
    BoardVisibility Visibility,
    bool Archived,
    long? ArchivedAt,
    long CreatedAt);

/// <param name="MaxVotes">Null means unlimited.</param>
public sealed record BoardSettings(
    bool AllowAnonymous,
    bool VotingEnabled,
    int? MaxVotes,
    bool ShowAuthorNames,
    bool StagesEnabled,
    bool EnforceReadiness,
    bool AllowSelfVotes,
    string VoteEmoji);

public sealed record NewColumn(string Title, string Color, bool ActionItems);

public sealed record BoardTemplate(BoardSettings Settings, IReadOnlyList<NewColumn> Columns);

public sealed record NewBoard(
    BoardId Id,
    RoomCode Code,
    TeamId? Team,
    string Title,
    PrincipalId CreatedBy,
    BoardVisibility Visibility,
    string? PasswordHash,
    BoardTemplate Template);

/// <summary>A board as its management routes see it, with the caller's role and grant read in the same query.</summary>
public sealed record ManagedBoard(ResourceFacts Facts, IAccessView Access, string Title, string? PasswordHash);

/// <summary>Team board lists, board creation (create in place, §c.2) and the columns HTTP owns on a board.</summary>
public sealed class RetroListsRepo(NpgsqlDataSource dataSource) {
    public const string UniqueViolation = "23505";

    /// <summary>The original's defaults when a board has no template (<c>create_columns_from_template</c>).</summary>
    public static readonly IReadOnlyList<NewColumn> DefaultColumns = [
        new("Good", "green", false),
        new("Bad", "red", false),
        new("Kudos", "blue", false),
        new("Action Items", "yellow", true),
    ];

    /// <summary>The schema defaults; quick boards get 3 votes per user (parity: <c>useRoomAccess</c>).</summary>
    public static readonly BoardSettings DefaultSettings = new(true, true, null, true, false, false, true, "👍");

    /// <returns>Null when <paramref name="template"/> is given but is not one of the team's.</returns>
    public async Task<BoardTemplate?> TemplateAsync(TeamId team, Guid? template, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new("""
            select id, allow_anonymous, voting_enabled, max_votes_per_user, show_author_names, stages_enabled,
                   enforce_readiness, allow_self_votes, vote_emoji
            from board_templates
            where team_id = $1 and (case when $2::uuid is null then is_default else id = $2 end)
            """) {
            Parameters = {
                new() { Value = team.Value },
                new() { Value = (object?)template ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
            },
        });
        batch.BatchCommands.Add(new("""
            select c.title, c.color, c.is_action_items
            from template_columns c join board_templates t on t.id = c.template_id
            where t.team_id = $1 and (case when $2::uuid is null then t.is_default else t.id = $2 end)
            order by c.sort_key
            """) {
            Parameters = {
                new() { Value = team.Value },
                new() { Value = (object?)template ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
            },
        });
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return template is null ? new BoardTemplate(DefaultSettings, DefaultColumns) : null;
        }
        var settings = new BoardSettings(
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetInt16(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetString(8));
        await reader.NextResultAsync(cancellationToken);
        var columns = new List<NewColumn>();
        while (await reader.ReadAsync(cancellationToken)) {
            columns.Add(new NewColumn(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        return new BoardTemplate(settings, columns.Count > 0 ? columns : DefaultColumns);
    }

    /// <summary>
    /// Inserts the board and its columns in one transaction. False, writing nothing, when the code is taken in any
    /// letter case: lookups ignore case (§d), so two codes differing only in case would shadow each other.
    /// </summary>
    public async Task<bool> CreateBoardAsync(NewBoard board, CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = board.Template.Settings;
        await using (var insert = new NpgsqlCommand("""
            insert into retro_boards (id, code, team_id, title, created_by, visibility, password_hash, password_set_at,
                                      allow_anonymous, voting_enabled, max_votes_per_user, show_author_names,
                                      stages_enabled, enforce_readiness, allow_self_votes, vote_emoji)
            select $1, $2, $3, $4, $5, $6, $7, case when $7::text is null then null else now() end,
                   $8, $9, $10, $11, $12, $13, $14, $15
            where not exists (select 1 from retro_boards where upper(code) = upper($2))
            """, connection, transaction)) {
            insert.Parameters.Add(new() { Value = board.Id.Value });
            insert.Parameters.Add(new() { Value = board.Code.Value });
            insert.Parameters.Add(Uuid(board.Team?.Value));
            insert.Parameters.Add(new() { Value = board.Title });
            insert.Parameters.Add(new() { Value = board.CreatedBy.Value });
            insert.Parameters.Add(new() { Value = VisibilityText(board.Visibility) });
            insert.Parameters.Add(new() { Value = (object?)board.PasswordHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            insert.Parameters.Add(new() { Value = settings.AllowAnonymous });
            insert.Parameters.Add(new() { Value = settings.VotingEnabled });
            insert.Parameters.Add(new() { Value = settings.MaxVotes is { } max ? (short)max : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Smallint });
            insert.Parameters.Add(new() { Value = settings.ShowAuthorNames });
            insert.Parameters.Add(new() { Value = settings.StagesEnabled });
            insert.Parameters.Add(new() { Value = settings.EnforceReadiness });
            insert.Parameters.Add(new() { Value = settings.AllowSelfVotes });
            insert.Parameters.Add(new() { Value = settings.VoteEmoji });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) {
                return false;
            }
        }
        var columns = board.Template.Columns;
        await using (var insert = new NpgsqlCommand("""
            insert into retro_columns (id, board_id, title, color, sort_key, is_action_items)
            select x.id, $1, x.title, x.color, x.ordinality - 1, x.action
            from unnest($2::uuid[], $3::text[], $4::text[], $5::boolean[]) with ordinality as x(id, title, color, action, ordinality)
            """, connection, transaction)) {
            insert.Parameters.Add(new() { Value = board.Id.Value });
            insert.Parameters.Add(new() { Value = columns.Select(_ => Guid.CreateVersion7()).ToArray() });
            insert.Parameters.Add(new() { Value = columns.Select(x => x.Title).ToArray() });
            insert.Parameters.Add(new() { Value = columns.Select(x => x.Color).ToArray() });
            insert.Parameters.Add(new() { Value = columns.Select(x => x.ActionItems).ToArray() });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<Page<BoardListItem>> ListBoardsAsync(
        TeamId team,
        bool archived,
        ListCursor? cursor,
        int limit,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select id, code, title, visibility, archived_at, created_at
            from retro_boards
            where team_id = $1 and deleted_at is null and (archived_at is not null) = $2
              and ($3::timestamptz is null or (created_at, id) < ($3, $4))
            order by created_at desc, id desc
            limit $5
            """);
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = archived });
        ListCursor.AddParameters(command, cursor);
        command.Parameters.Add(new() { Value = limit + 1 });
        var rows = new List<(BoardListItem, ListCursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var id = reader.GetGuid(0);
            var archivedAt = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(5);
            rows.Add((
                new BoardListItem(
                    new BoardId(id),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseVisibility(reader.GetString(3)),
                    archivedAt is not null,
                    archivedAt?.ToUnixTimeMilliseconds(),
                    createdAt.ToUnixTimeMilliseconds()),
                new ListCursor(createdAt, id)));
        }
        return Page.Of(rows, limit);
    }

    /// <returns>Null when the board does not exist (deleted boards are returned with <c>Deleted</c> set).</returns>
    public async Task<ManagedBoard?> BoardAsync(BoardId board, PrincipalId caller, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select b.team_id, b.created_by, b.visibility, b.password_set_at, b.archived_at is not null,
                   b.deleted_at is not null, coalesce(t.deleted_at is not null, false), m.role, g.password_epoch,
                   b.title, b.password_hash
            from retro_boards b
            left join teams t on t.id = b.team_id
            left join team_members m on m.team_id = b.team_id and m.user_id = $2
            left join board_access_grants g on g.board_id = b.id and g.principal_id = $2
            where b.id = $1
            """);
        command.Parameters.Add(new() { Value = board.Value });
        command.Parameters.Add(new() { Value = caller.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var team = reader.IsDBNull(0) ? (TeamId?)null : new TeamId(reader.GetGuid(0));
        var facts = new ResourceFacts(
            RoomKey.Board(board),
            Team: team,
            CreatedBy: reader.IsDBNull(1) ? null : new PrincipalId(reader.GetGuid(1)),
            Visibility: ParseVisibility(reader.GetString(2)),
            PasswordSetAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            Archived: reader.GetBoolean(4),
            Deleted: reader.GetBoolean(5),
            TeamDeleted: reader.GetBoolean(6));
        var access = new CallerAccess(
            team,
            board,
            caller,
            reader.IsDBNull(7) ? null : TeamRoles.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
        return new ManagedBoard(facts, access, reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    /// <summary>
    /// Writes the visibility and password together, as the §b CHECKs require, and drops grants for any other password
    /// in the same transaction. <paramref name="newPasswordHash"/> null with <c>password</c> visibility keeps the
    /// current password and its grants.
    /// </summary>
    /// <returns>The board's <c>password_set_at</c> as stored (the epoch grants are compared with); false when the board is gone.</returns>
    public async Task<(bool Found, DateTimeOffset? PasswordSetAt)> SetAccessAsync(
        BoardId board,
        BoardVisibility visibility,
        string? newPasswordHash,
        CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset? setAt;
        await using (var update = new NpgsqlCommand("""
            update retro_boards set
              visibility = $2,
              password_hash = case when $2 <> 'password' then null else coalesce($3, password_hash) end,
              password_set_at = case when $2 <> 'password' then null when $3::text is null then password_set_at else now() end,
              updated_at = now()
            where id = $1 and deleted_at is null
            returning password_set_at
            """, connection, transaction)) {
            update.Parameters.Add(new() { Value = board.Value });
            update.Parameters.Add(new() { Value = VisibilityText(visibility) });
            update.Parameters.Add(new() { Value = (object?)newPasswordHash ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) {
                return (false, null);
            }
            setAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
        }
        await using (var prune = new NpgsqlCommand(
            "delete from board_access_grants where board_id = $1 and password_epoch is distinct from $2",
            connection,
            transaction)) {
            prune.Parameters.Add(new() { Value = board.Value });
            prune.Parameters.Add(new() { Value = (object?)setAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, setAt);
    }

    /// <summary>
    /// Records an unlock for the password the caller just proved. Nothing is written when the password changed in the
    /// meantime (<paramref name="epoch"/> is no longer the board's), so a grant can never outlive a password change.
    /// </summary>
    public async Task<bool> GrantAsync(BoardId board, PrincipalId principal, DateTimeOffset epoch, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            insert into board_access_grants (board_id, principal_id, password_epoch)
            select id, $2, password_set_at from retro_boards
            where id = $1 and deleted_at is null and visibility = 'password' and password_set_at = $3
            on conflict (board_id, principal_id) do update set password_epoch = excluded.password_epoch, granted_at = now()
            """);
        command.Parameters.Add(new() { Value = board.Value });
        command.Parameters.Add(new() { Value = principal.Value });
        command.Parameters.Add(new() { Value = epoch });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SoftDeleteAsync(BoardId board, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "update retro_boards set deleted_at = now(), updated_at = now() where id = $1 and deleted_at is null");
        command.Parameters.Add(new() { Value = board.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static bool IsCodeCollision(PostgresException exception) {
        return exception is { SqlState: UniqueViolation, ConstraintName: "retro_boards_code_uq" or "poker_sessions_code_uq" };
    }

    public static string VisibilityText(BoardVisibility visibility) => visibility switch {
        BoardVisibility.Link => "link",
        BoardVisibility.Team => "team",
        _ => "password",
    };

    public static BoardVisibility ParseVisibility(string text) => text switch {
        "link" => BoardVisibility.Link,
        "team" => BoardVisibility.Team,
        _ => BoardVisibility.Password,
    };

    internal static NpgsqlParameter Uuid(Guid? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    /// <summary>The one caller's role and grant as the rows held them at read time.</summary>
    private sealed class CallerAccess(
        TeamId? boardTeam,
        BoardId readBoard,
        PrincipalId caller,
        TeamRole? role,
        DateTimeOffset? grantEpoch) : IAccessView {
        public TeamRole? RoleIn(TeamId team, PrincipalId principal) =>
            team == boardTeam && principal == caller ? role : null;

        public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) =>
            board == readBoard && principal == caller && grantEpoch is not null && grantEpoch == passwordSetAt;
    }
}
