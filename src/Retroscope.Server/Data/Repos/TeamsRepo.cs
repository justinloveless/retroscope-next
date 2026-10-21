using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>A team as the HTTP routes see it; <see cref="Deleted"/> teams answer like missing ones.</summary>
public sealed record TeamRow(TeamId Id, string Name, string? Description, bool Deleted);

public sealed record TeamListRow(TeamId Id, string Name, string? Description, TeamRole Role, bool Favorite);

public sealed record TeamJiraRow(
    string SiteHost,
    string? ProjectKey,
    long? BoardId,
    string? TicketPrefix,
    string Status,
    bool MembersCanEdit);

public sealed record TeamDetails(
    TeamRow Team,
    IReadOnlyDictionary<string, string> PointDescriptions,
    int MaxEndorsements,
    TeamJiraRow? Jira,
    int Boards,
    int Members);

/// <summary>A change to <c>teams</c>; a null member is left as it is, an empty <see cref="Description"/> clears it.</summary>
public sealed record TeamPatch(
    string? Name,
    string? Description,
    IReadOnlyDictionary<string, string>? PointDescriptions,
    int? MaxEndorsements);

public sealed record ActionItemRow(ActionItemInfo Item, string? BoardTitle);

/// <param name="Status">Which items: <c>open</c>, <c>done</c> or <c>all</c>.</param>
/// <param name="Assignee">One member's items; ignored when <paramref name="Unassigned"/>.</param>
public sealed record ActionItemQuery(
    string Status,
    PrincipalId? Assignee,
    bool Unassigned,
    bool Newest,
    ListCursor? After,
    int Limit);

/// <summary>Keyset position in a list ordered by creation time, then id: <c>&lt;unix ms&gt;.&lt;uuid&gt;</c>.</summary>
public readonly record struct ListCursor(DateTimeOffset CreatedAt, Guid Id) {
    public override string ToString() =>
        $"{CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}.{Id:D}";

    public static bool TryParse(string? value, out ListCursor cursor) {
        cursor = default;
        var dot = value?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (dot <= 0
            || !long.TryParse(value.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || ms > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            || !Guid.TryParseExact(value.AsSpan(dot + 1), "D", out var id)) {
            return false;
        }
        cursor = new ListCursor(DateTimeOffset.FromUnixTimeMilliseconds(ms), id);
        return true;
    }
}

/// <summary>
/// The <c>teams</c> rows the HTTP routes read and write, and the reads around them. Rows the team actor owns
/// (<c>team_members</c>, R24) are only written here when the team is created: nobody can be writing a team that
/// does not exist yet (create in place, §c.2).
/// </summary>
public sealed class TeamsRepo(NpgsqlDataSource dataSource) {
    public async Task<TeamRow?> FindAsync(TeamId team, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "select name, description, deleted_at is not null from teams where id = $1");
        command.Parameters.Add(new() { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new TeamRow(team, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2));
    }

    public async Task<IReadOnlyList<TeamListRow>> ListAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select t.id, t.name, t.description, m.role,
                   exists (select 1 from team_favorites f where f.user_id = m.user_id and f.team_id = t.id)
            from team_members m join teams t on t.id = m.team_id
            where m.user_id = $1 and t.deleted_at is null
            order by lower(t.name), t.id
            """);
        command.Parameters.Add(new() { Value = user.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var teams = new List<TeamListRow>();
        while (await reader.ReadAsync(cancellationToken)) {
            teams.Add(new TeamListRow(
                new TeamId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                MembershipCache.ParseRole(reader.GetString(3)),
                reader.GetBoolean(4)));
        }
        return teams;
    }

    public async Task<TeamDetails?> DetailsAsync(TeamId team, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select t.name, t.description, t.deleted_at is not null, t.point_descriptions::text, t.max_endorsements_per_board,
                   j.site_host, j.project_key, j.board_id, j.ticket_prefix, j.status, j.members_can_edit,
                   (select count(*) from retro_boards b
                    where b.team_id = t.id and b.deleted_at is null and b.archived_at is null)::int,
                   (select count(*) from team_members m where m.team_id = t.id)::int
            from teams t left join team_jira_config j on j.team_id = t.id
            where t.id = $1
            """);
        command.Parameters.Add(new() { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var row = new TeamRow(team, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2));
        var jira = reader.IsDBNull(5)
            ? null
            : new TeamJiraRow(
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetBoolean(10));
        return new TeamDetails(row, ParsePointDescriptions(reader.GetString(3)), reader.GetInt16(4), jira, reader.GetInt32(11), reader.GetInt32(12));
    }

    /// <summary>
    /// The new team, its owner, and what every team starts with, in one transaction (§d): the default template and
    /// the default endorsement types.
    /// </summary>
    public async Task CreateAsync(
        TeamId team,
        string name,
        string? description,
        PrincipalId owner,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        IEnumerable<NpgsqlBatchCommand> commands = [
            Command(
                """
                insert into teams (id, name, description, created_by, created_at, updated_at)
                values ($1, $2, $3, $4, $5, $5)
                """,
                team.Value,
                name,
                Text(description),
                owner.Value,
                now),
            Command(
                "insert into team_members (team_id, user_id, role, joined_at) values ($1, $2, 'owner', $3)",
                team.Value,
                owner.Value,
                now),
            .. TemplatesRepo.SeedDefault(team, now),
            .. EndorsementsRepo.SeedDefaultTypes(team, now),
            Audit("team.create", owner, team, team.Value.ToString(), new Dictionary<string, string>(), now),
        ];
        await WriteAsync(commands, cancellationToken);
    }

    /// <returns>False when the team is missing or deleted.</returns>
    public Task<bool> UpdateAsync(
        TeamId team,
        TeamPatch patch,
        PrincipalId by,
        DateTimeOffset now,
        CancellationToken cancellationToken) {
        var update = Command(
            """
            update teams set
              name = coalesce($2, name),
              description = case when $3::text is null then description else nullif($3, '') end,
              point_descriptions = coalesce($4, point_descriptions),
              max_endorsements_per_board = coalesce($5, max_endorsements_per_board),
              updated_at = $6
            where id = $1 and deleted_at is null
            """,
            team.Value,
            Text(patch.Name),
            Text(patch.Description),
            new NpgsqlParameter {
                Value = patch.PointDescriptions is { } descriptions ? PointDescriptionsJson(descriptions) : DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            },
            new NpgsqlParameter {
                Value = patch.MaxEndorsements is { } max ? (short)max : DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Smallint,
            },
            now);
        return WriteIfAsync(update, Audit("team.settings", by, team, team.Value.ToString(), new Dictionary<string, string>(), now), cancellationToken);
    }

    /// <summary>Soft delete (§0.1): everything the team owns becomes inaccessible now; a job erases it after 30 days.</summary>
    /// <returns>False when the team was missing or already deleted.</returns>
    public Task<bool> SoftDeleteAsync(TeamId team, PrincipalId by, DateTimeOffset now, CancellationToken cancellationToken) {
        return WriteIfAsync(
            Command("update teams set deleted_at = $2, updated_at = $2 where id = $1 and deleted_at is null", team.Value, now),
            Audit("team.delete", by, team, team.Value.ToString(), new Dictionary<string, string>(), now),
            cancellationToken);
    }

    public async Task SetFavoriteAsync(PrincipalId user, TeamId team, bool favorite, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = favorite
            ? dataSource.CreateCommand("insert into team_favorites (user_id, team_id, created_at) values ($1, $2, $3) on conflict do nothing")
            : dataSource.CreateCommand("delete from team_favorites where user_id = $1 and team_id = $2");
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = team.Value });
        if (favorite) {
            command.Parameters.Add(new() { Value = now });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The team's members with names and avatars; never their email addresses.</summary>
    public async Task<IReadOnlyList<MemberInfo>> MembersAsync(TeamId team, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"""
            select m.user_id, {UsersRepo.DisplayNameSql}, m.role, u.avatar_blob_id
            from team_members m join users u on u.id = m.user_id
            where m.team_id = $1
            """);
        command.Parameters.Add(new() { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<MemberInfo>();
        while (await reader.ReadAsync(cancellationToken)) {
            members.Add(new MemberInfo(
                new PrincipalId(reader.GetGuid(0)),
                reader.GetString(1),
                MembershipCache.ParseRole(reader.GetString(2)),
                reader.IsDBNull(3) ? null : new BlobId(reader.GetGuid(3))));
        }
        return members;
    }

    /// <summary>How <paramref name="user"/> appears in a roster, for the team actor's <c>MemberAdded</c>.</summary>
    public async Task<(string Name, BlobId? Avatar)?> MemberCardAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand($"select {UsersRepo.DisplayNameSql}, u.avatar_blob_id from users u where u.id = $1");
        command.Parameters.Add(new() { Value = user.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return (reader.GetString(0), reader.IsDBNull(1) ? null : new BlobId(reader.GetGuid(1)));
    }

    /// <summary>
    /// One page of the team's action items, newest or oldest first. The room shows only open and recently closed
    /// items; this list is the full history (U30 decision 2).
    /// </summary>
    public async Task<(IReadOnlyList<ActionItemRow> Items, ListCursor? Next)> ActionItemsAsync(
        TeamId team,
        ActionItemQuery query,
        CancellationToken cancellationToken) {
        var after = query.After is null
            ? "true"
            : query.Newest ? "(t.created_at, t.id) < ($5, $6)" : "(t.created_at, t.id) > ($5, $6)";
        var order = query.Newest ? "t.created_at desc, t.id desc" : "t.created_at, t.id";
        await using var command = dataSource.CreateCommand($"""
            select t.id, t.text, t.source_version, t.created_at,
                   (select count(*) from retro_comments c where c.card_id = t.source_card_id)::int,
                   t.source_board_id, t.source_card_id, t.assigned_to, t.done_at, t.done_by, t.created_by, b.title
            from team_action_items t
            left join retro_boards b on b.id = t.source_board_id and b.deleted_at is null
            where t.team_id = $1
              and ($2 = 'all' or ($2 = 'open') = (t.done_at is null))
              and ($3::uuid is null or t.assigned_to = $3)
              and (not $4 or t.assigned_to is null)
              and {after}
            order by {order}
            limit $7
            """);
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = query.Status });
        command.Parameters.Add(new() { Value = (object?)query.Assignee?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new() { Value = query.Unassigned });
        command.Parameters.Add(new() { Value = query.After?.CreatedAt ?? DateTimeOffset.UnixEpoch });
        command.Parameters.Add(new() { Value = query.After?.Id ?? Guid.Empty });
        command.Parameters.Add(new() { Value = query.Limit + 1 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ActionItemRow>();
        var createdAt = new List<DateTimeOffset>();
        while (await reader.ReadAsync(cancellationToken)) {
            var created = reader.GetFieldValue<DateTimeOffset>(3);
            createdAt.Add(created);
            items.Add(new ActionItemRow(
                new ActionItemInfo(
                    new ActionItemId(reader.GetGuid(0)),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    created.ToUnixTimeMilliseconds(),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : new BoardId(reader.GetGuid(5)),
                    reader.IsDBNull(6) ? null : new CardId(reader.GetGuid(6)),
                    reader.IsDBNull(7) ? null : new PrincipalId(reader.GetGuid(7)),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8).ToUnixTimeMilliseconds(),
                    reader.IsDBNull(9) ? null : new PrincipalId(reader.GetGuid(9)),
                    reader.IsDBNull(10) ? null : new PrincipalId(reader.GetGuid(10))),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        if (items.Count <= query.Limit) {
            return (items, null);
        }
        items.RemoveAt(query.Limit);
        var last = items[^1].Item;
        return (items, new ListCursor(createdAt[query.Limit - 1], last.Id.Value));
    }

    /// <summary>An <c>audit_log</c> row about a team: ids and codes only, never content or addresses (§e).</summary>
    public static NpgsqlBatchCommand Audit(
        string action,
        PrincipalId? actor,
        TeamId team,
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
        return new("insert into audit_log (at, actor_id, team_id, action, target, meta) values ($1, $2, $3, $4, $5, $6)") {
            Parameters = {
                new() { Value = now },
                new() { Value = (object?)actor?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new() { Value = team.Value },
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

    /// <summary>Runs <paramref name="change"/> and, only when it touched a row, <paramref name="then"/>, in one transaction.</summary>
    /// <returns>False, with nothing committed, when <paramref name="change"/> touched no row.</returns>
    public Task<bool> WriteIfAsync(NpgsqlBatchCommand change, NpgsqlBatchCommand then, CancellationToken cancellationToken) {
        return WriteIfAsync(dataSource, change, then, cancellationToken);
    }

    public static async Task<bool> WriteIfAsync(
        NpgsqlDataSource dataSource,
        NpgsqlBatchCommand change,
        NpgsqlBatchCommand then,
        CancellationToken cancellationToken) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var first = new NpgsqlBatch(connection, transaction) { BatchCommands = { change } }) {
            if (await first.ExecuteNonQueryAsync(cancellationToken) == 0) {
                return false;
            }
        }
        await using (var second = new NpgsqlBatch(connection, transaction) { BatchCommands = { then } }) {
            await second.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <summary>A batch command whose values are plain parameters; pass an <see cref="NpgsqlParameter"/> to type a null.</summary>
    public static NpgsqlBatchCommand Command(string sql, params object?[] values) {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values) {
            command.Parameters.Add(value as NpgsqlParameter ?? new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        return command;
    }

    /// <summary>A text parameter that may be null.</summary>
    public static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    public static string PointDescriptionsJson(IReadOnlyDictionary<string, string> descriptions) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            foreach (var (key, value) in descriptions) {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Dictionary<string, string> ParsePointDescriptions(string json) {
        using var document = JsonDocument.Parse(json);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) {
            if (property.Value.ValueKind == JsonValueKind.String) {
                descriptions[property.Name] = property.Value.GetString()!;
            }
        }
        return descriptions;
    }
}
