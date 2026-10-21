using System.Text.Json;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Realtime.Stores;

/// <summary>The team's poker sessions and boards, the rooms a roster or settings change fans out to.</summary>
public sealed record TeamRooms(IReadOnlyList<SessionId> Sessions, IReadOnlyList<BoardId> Boards);

/// <summary>
/// Loads a team room and persists what the team actor owns (§c.2). Writes are column-scoped (R24): board actors
/// set <c>source_card_id</c> to null through their deletes, so no statement here writes a column its event does not
/// name, and an action item is never rewritten from memory.
/// </summary>
public sealed class TeamStore : SqlRoomStore<TeamState, TeamEvent> {
    // §c.2: catches up copies a crash left behind between the board actor's commit and the team actor's.
    private const string RepairSql = """
        update team_action_items t set text = c.body, source_version = c.version
        from retro_cards c
        where c.id = t.source_card_id and t.team_id = $1 and c.version > t.source_version
        """;

    private const string TeamSql = """
        select name, point_descriptions::text, max_endorsements_per_board from teams where id = $1 and deleted_at is null
        """;

    private const string MembersSql = """
        select m.user_id, coalesce(nullif(u.nickname, ''), nullif(u.full_name, ''), 'User'), m.role, u.avatar_blob_id
        from team_members m join users u on u.id = m.user_id
        where m.team_id = $1
        """;

    private const string ActionItemsSql = """
        select t.id, t.text, t.source_version, t.created_at,
               (select count(*) from retro_comments c where c.card_id = t.source_card_id)::int,
               t.source_board_id, t.source_card_id, t.assigned_to, t.done_at, t.done_by, t.created_by
        from team_action_items t
        where t.team_id = $1 and (t.done_at is null or t.done_at >= $2)
        """;

    private const string EndorsementTypesSql = """
        select id, name, emoji, archived_at is not null, description
        from endorsement_types where team_id = $1 order by sort_key, id
        """;

    private readonly Leader _leader;
    private readonly MembershipCache _memberships;

    public TeamStore(NpgsqlDataSource dataSource, Leader leader, CursorRepo cursors, MembershipCache memberships)
        : base(dataSource, leader, cursors) {
        _leader = leader;
        _memberships = memberships;
    }

    public override async Task<TeamState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        var team = new TeamId(room.Id);
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await _leader.CommitFencedAsync(connection, [Command(RepairSql, team.Value)], cancellationToken);

        await using var batch = new NpgsqlBatch(connection) {
            BatchCommands = {
                Command(TeamSql, team.Value),
                Command(MembersSql, team.Value),
                Command(ActionItemsSql, team.Value, now - TeamProjection.ClosedItemWindow),
                Command(EndorsementTypesSql, team.Value),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var state = new TeamState(team, reader.GetString(0)) {
            PointDescriptions = ParsePointDescriptions(reader.GetString(1)),
            MaxEndorsements = reader.GetInt16(2),
        };

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var member = new MemberInfo(
                new PrincipalId(reader.GetGuid(0)),
                reader.GetString(1),
                MembershipCache.ParseRole(reader.GetString(2)),
                reader.IsDBNull(3) ? null : new BlobId(reader.GetGuid(3)));
            state.Members[member.Pid] = member;
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var item = new ActionItemInfo(
                new ActionItemId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3).ToUnixTimeMilliseconds(),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : new BoardId(reader.GetGuid(5)),
                reader.IsDBNull(6) ? null : new CardId(reader.GetGuid(6)),
                reader.IsDBNull(7) ? null : new PrincipalId(reader.GetGuid(7)),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8).ToUnixTimeMilliseconds(),
                reader.IsDBNull(9) ? null : new PrincipalId(reader.GetGuid(9)),
                reader.IsDBNull(10) ? null : new PrincipalId(reader.GetGuid(10)));
            state.ActionItems[item.Id] = item;
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.EndorsementTypes.Add(new EndorsementTypeInfo(
                new EndorsementTypeId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        // The rows just read are the truth after a reload (an earlier commit may have had an unknown outcome).
        _memberships.Publish(team, Roles(state.Members.Values));
        return state;
    }

    /// <summary>The team's sessions and live (not deleted) boards.</summary>
    public async Task<TeamRooms> RoomsAsync(TeamId team, CancellationToken cancellationToken) {
        await using var batch = DataSource.CreateBatch();
        batch.BatchCommands.Add(Command("select id from poker_sessions where team_id = $1", team.Value));
        batch.BatchCommands.Add(Command("select id from retro_boards where team_id = $1 and deleted_at is null", team.Value));
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<SessionId>();
        while (await reader.ReadAsync(cancellationToken)) {
            sessions.Add(new SessionId(reader.GetGuid(0)));
        }
        await reader.NextResultAsync(cancellationToken);
        var boards = new List<BoardId>();
        while (await reader.ReadAsync(cancellationToken)) {
            boards.Add(new BoardId(reader.GetGuid(0)));
        }
        return new TeamRooms(sessions, boards);
    }

    public static Dictionary<PrincipalId, TeamRole> Roles(IEnumerable<MemberInfo> members) {
        return members.ToDictionary(x => x.Pid, x => x.Role);
    }

    protected override IEnumerable<NpgsqlBatchCommand> Plan(RoomKey room, TeamState state, IReadOnlyList<TeamEvent> events) {
        var team = room.Id;
        foreach (var evt in events) {
            switch (evt) {
                case TeamEvent.ActionItemAssigned e:
                    yield return Command(
                        "update team_action_items set assigned_to = $2 where id = $1 and team_id = $3",
                        e.Item.Id.Value,
                        e.Item.AssignedTo?.Value,
                        team);
                    break;
                case TeamEvent.ActionItemDoneSet e:
                    yield return Command(
                        "update team_action_items set done_at = $2, done_by = $3 where id = $1 and team_id = $4",
                        e.Item.Id.Value,
                        e.Item.DoneAt is { } doneAt ? DateTimeOffset.FromUnixTimeMilliseconds(doneAt) : null,
                        e.Item.DoneBy?.Value,
                        team);
                    break;
                case TeamEvent.ActionItemTextSynced e:
                    yield return Command(
                        "update team_action_items set text = $2, source_version = $3 where id = $1 and team_id = $4 and source_version < $3",
                        e.ItemId.Value,
                        e.Text,
                        e.SourceVersion,
                        team);
                    break;
                case TeamEvent.ActionItemDeleted e:
                    yield return Command("delete from team_action_items where id = $1 and team_id = $2", e.ItemId.Value, team);
                    break;
                case TeamEvent.MemberAdded e:
                    yield return Command(
                        "insert into team_members (team_id, user_id, role) values ($1, $2, $3)",
                        team,
                        e.Member.Pid.Value,
                        MembershipCache.RoleName(e.Member.Role));
                    break;
                case TeamEvent.MemberRoleSet e:
                    yield return Command(
                        "update team_members set role = $3 where team_id = $1 and user_id = $2",
                        team,
                        e.Member.Value,
                        MembershipCache.RoleName(e.Role));
                    break;
                case TeamEvent.MemberRemoved e:
                    yield return Command("delete from team_members where team_id = $1 and user_id = $2", team, e.Member.Value);
                    break;
            }
        }
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

    private static NpgsqlBatchCommand Command(string sql, params object?[] values) {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values) {
            command.Parameters.Add(new() { Value = value ?? DBNull.Value });
        }
        return command;
    }
}
