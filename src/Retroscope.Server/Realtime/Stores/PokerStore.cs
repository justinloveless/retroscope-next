using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Realtime.Stores;

/// <summary>
/// Loads a poker session in one batch and plans the SQL of its committed events (§c.8). A round's
/// <c>sort_key</c> is its index in <see cref="PokerState.Order"/>, so every plan keeps the keys dense; the unique
/// constraint on them is deferred, which lets a statement shift keys through a transient duplicate.
/// </summary>
public sealed class PokerStore(NpgsqlDataSource dataSource, Leader leader, CursorRepo cursors)
    : SqlRoomStore<PokerState, PokerEvent>(dataSource, leader, cursors) {
    // Every session belongs to a team (UX18, migration 0003); a session of a deleted team loads as missing.
    private const string SessionSql = """
        select s.code, s.team_id, s.presence_enabled, s.follow_spotlight,
               s.spotlight_holder_id, s.spotlight_client_id, s.spotlight_round_id, s.current_round_id,
               s.next_round_number, t.deleted_at is not null, t.point_descriptions::text,
               j.team_id is not null, j.ticket_prefix, coalesce(j.members_can_edit, true)
        from poker_sessions s
        join teams t on t.id = s.team_id
        left join team_jira_config j on j.team_id = s.team_id
        where s.id = $1
        """;

    private const string RoundsSql = """
        select id, number, ticket_key, parent_key, state, is_active, is_pending, pending_owner_id, version,
               auto_reveal, winner_points, revealed_at, revealed_by, jira_points, jira_points_at, jira_points_by
        from poker_rounds where session_id = $1 order by sort_key
        """;

    private const string VotesSql = """
        select v.round_id, v.principal_id, v.points, v.high_points, v.locked, v.abstained
        from poker_votes v join poker_rounds r on r.id = v.round_id
        where r.session_id = $1
        """;

    // A session seats the team's members (R27).
    private const string RosterSql = $"""
        select p.id, p.kind,
               case when u.id is null then coalesce(p.display_name, 'Player') else {UsersRepo.DisplayNameSql} end,
               u.avatar_blob_id
        from principals p left join users u on u.id = p.id
        where p.id in (select m.user_id from team_members m join poker_sessions s on s.team_id = m.team_id where s.id = $1)
        """;

    public override async Task<PokerState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection) {
            BatchCommands = {
                Command(SessionSql, room.Id),
                Command("select principal_id from poker_observers where session_id = $1", room.Id),
                Command(RoundsSql, room.Id),
                Command(VotesSql, room.Id),
                Command(RosterSql, room.Id),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetBoolean(9)) {
            return null;
        }
        var state = ReadSession(reader, room, now);
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.Observers.Add(new PrincipalId(reader.GetGuid(0)));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var round = ReadRound(reader);
            state.Rounds.Add(round.Id, round);
            state.Order.Add(round.Id);
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var vote = new Vote(
                OrNull<short>(reader, 2),
                OrNull<short>(reader, 3),
                reader.GetBoolean(4),
                reader.GetBoolean(5));
            state.Rounds[new RoundId(reader.GetGuid(0))].Votes.Add(new PrincipalId(reader.GetGuid(1)), vote);
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var pid = new PrincipalId(reader.GetGuid(0));
            var avatar = reader.IsDBNull(3) ? (BlobId?)null : new BlobId(reader.GetGuid(3));
            state.Roster[pid] = new PrincipalInfo(pid, ParseKind(reader.GetString(1)), reader.GetString(2), avatar);
        }
        return state;
    }

    protected override IEnumerable<NpgsqlBatchCommand> Plan(RoomKey room, PokerState state, IReadOnlyList<PokerEvent> events) {
        var session = room.Id;
        foreach (var evt in events) {
            foreach (var command in PlanEvent(session, evt)) {
                yield return command;
            }
        }
        // Coalesced in SQL: the row is only rewritten once the last bump is a minute old (§c.8).
        yield return Command(
            "update poker_sessions set last_activity_at = now() where id = $1 and last_activity_at < now() - interval '1 minute'",
            session);
    }

    private static IEnumerable<NpgsqlBatchCommand> PlanEvent(Guid session, PokerEvent evt) {
        switch (evt) {
            case VoteChanged { Vote: { } vote } e:
                yield return Command("""
                    insert into poker_votes (round_id, principal_id, points, high_points, locked, abstained)
                    values ($1, $2, $3, $4, $5, $6)
                    on conflict (round_id, principal_id) do update
                      set points = excluded.points, high_points = excluded.high_points, locked = excluded.locked,
                          abstained = excluded.abstained, updated_at = now()
                    """,
                    e.RoundId.Value, e.Principal.Value, Small(vote.Points), Small(vote.High), vote.Locked, vote.Abstained);
                break;
            case VoteChanged e:
                yield return Command(
                    "delete from poker_votes where round_id = $1 and principal_id = $2", e.RoundId.Value, e.Principal.Value);
                break;
            case RoundRevealed e:
                yield return Command("""
                    update poker_votes set abstained = true, locked = true, points = null, high_points = null, updated_at = now()
                    where round_id = $1 and not locked
                    """,
                    e.RoundId.Value);
                yield return Command(
                    "update poker_rounds set state = 'revealed', winner_points = $2, revealed_at = $3, revealed_by = $4 where id = $1",
                    e.RoundId.Value, e.Winner, e.At, Id(e.By));
                break;
            case RoundReplayed e:
                yield return UpdateRound(e.Round);
                yield return Command("delete from poker_votes where round_id = $1", e.Round.Id.Value);
                yield return InsertVotes(e.Round.Id, e.Votes);
                break;
            case RoundCreated e:
                yield return Command(
                    "update poker_rounds set sort_key = sort_key + 1 where session_id = $1 and sort_key >= $2", session, e.SortKey);
                yield return InsertRound(session, e.Round, e.SortKey);
                yield return Command(
                    "update poker_sessions set next_round_number = greatest(next_round_number, $2) where id = $1",
                    session, e.Round.Number + 1);
                break;
            case RoundUpdated e:
                yield return UpdateRound(e.Round);
                break;
            case JiraPointsRecorded e:
                yield return UpdateRound(e.Round);
                break;
            case RoundDeleted e:
                yield return Command("""
                    update poker_rounds set sort_key = sort_key - 1
                    where session_id = $1 and sort_key > (select sort_key from poker_rounds where id = $2)
                    """,
                    session, e.RoundId.Value);
                yield return Command("delete from poker_rounds where id = $1", e.RoundId.Value);
                break;
            case RoundsReordered e:
                yield return Command("""
                    update poker_rounds r set sort_key = k.ord - 1
                    from unnest($2::uuid[]) with ordinality k(id, ord)
                    where r.id = k.id and r.session_id = $1
                    """,
                    session, e.Order.Select(x => x.Value).ToArray());
                break;
            case RoundsReset e:
                yield return Command("delete from poker_rounds where session_id = $1", session);
                yield return InsertRound(session, e.Round, 0);
                yield return Command(
                    "update poker_sessions set next_round_number = $2 where id = $1", session, e.Round.Number + 1);
                break;
            case PointerMoved e:
                yield return Command("update poker_sessions set current_round_id = $2 where id = $1", session, e.RoundId.Value);
                break;
            case SpotlightChanged e:
                yield return Command("""
                    update poker_sessions set spotlight_holder_id = $2, spotlight_client_id = $3, spotlight_round_id = $4
                    where id = $1
                    """,
                    session, Id(e.Holder), Id(e.HolderClient), Id(e.RoundId));
                break;
            case SpotlightSettled e:
                yield return Command(
                    "update poker_sessions set spotlight_round_id = $2, spotlight_client_id = $3 where id = $1",
                    session, e.RoundId.Value, Id(e.HolderClient));
                break;
            case SettingsChanged e:
                yield return Command(
                    "update poker_sessions set presence_enabled = $2, follow_spotlight = $3 where id = $1",
                    session, e.PresenceEnabled, e.FollowSpotlight);
                break;
            case ObserverSet { Observer: true } e:
                yield return Command(
                    "insert into poker_observers (session_id, principal_id) values ($1, $2) on conflict do nothing",
                    session, e.Principal.Value);
                break;
            case ObserverSet e:
                yield return Command(
                    "delete from poker_observers where session_id = $1 and principal_id = $2", session, e.Principal.Value);
                break;
            case SessionDeleted:
                yield return Command("delete from client_cursors where room_key = $1", RoomKey.Poker(new SessionId(session)).ToString());
                yield return Command("delete from poker_sessions where id = $1", session);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "The event has no SQL; is it committed?");
        }
    }

    private static NpgsqlBatchCommand InsertRound(Guid session, RoundInfo round, int sortKey) {
        return Command("""
            insert into poker_rounds (id, session_id, number, sort_key, ticket_key, parent_key, state, is_active, is_pending,
                                      pending_owner_id, version, auto_reveal, winner_points, revealed_at, revealed_by,
                                      jira_points, jira_points_at, jira_points_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)
            """,
            round.Id.Value,
            session,
            round.Number,
            sortKey,
            Text(round.TicketKey),
            Text(round.ParentKey),
            StateText(round.State),
            round.Active,
            round.Pending,
            Id(round.PendingOwner),
            round.Version,
            round.AutoReveal,
            Small(round.Winner),
            Timestamp(round.RevealedAt),
            Id(round.RevealedBy),
            JiraPoints(round.Jira),
            Timestamp(round.Jira?.At),
            Id(round.Jira?.By));
    }

    private static NpgsqlBatchCommand UpdateRound(RoundInfo round) {
        return Command("""
            update poker_rounds set ticket_key = $2, parent_key = $3, state = $4, is_active = $5, is_pending = $6,
                   pending_owner_id = $7, version = $8, auto_reveal = $9, winner_points = $10, revealed_at = $11,
                   revealed_by = $12, jira_points = $13, jira_points_at = $14, jira_points_by = $15
            where id = $1
            """,
            round.Id.Value,
            Text(round.TicketKey),
            Text(round.ParentKey),
            StateText(round.State),
            round.Active,
            round.Pending,
            Id(round.PendingOwner),
            round.Version,
            round.AutoReveal,
            Small(round.Winner),
            Timestamp(round.RevealedAt),
            Id(round.RevealedBy),
            JiraPoints(round.Jira),
            Timestamp(round.Jira?.At),
            Id(round.Jira?.By));
    }

    private static NpgsqlBatchCommand InsertVotes(RoundId round, IReadOnlyList<VoteEntry> votes) {
        return Command("""
            insert into poker_votes (round_id, principal_id, points, high_points, locked, abstained)
            select $1, v.principal_id, v.points, v.high_points, v.locked, v.abstained
            from unnest($2::uuid[], $3::smallint[], $4::smallint[], $5::boolean[], $6::boolean[])
                 as v(principal_id, points, high_points, locked, abstained)
            """,
            round.Value,
            votes.Select(x => x.Principal.Value).ToArray(),
            votes.Select(x => x.Vote.Points).ToArray(),
            votes.Select(x => x.Vote.High).ToArray(),
            votes.Select(x => x.Vote.Locked).ToArray(),
            votes.Select(x => x.Vote.Abstained).ToArray());
    }

    private static PokerState ReadSession(NpgsqlDataReader reader, RoomKey room, DateTimeOffset now) {
        var holder = NullableGuid(reader, 4);
        var spotlightRound = NullableGuid(reader, 6);
        return new PokerState {
            SessionId = new SessionId(room.Id),
            Code = reader.GetString(0),
            TeamId = new TeamId(reader.GetGuid(1)),
            PresenceEnabled = reader.GetBoolean(2),
            FollowSpotlight = reader.GetBoolean(3),
            Spotlight = holder is { } h && spotlightRound is { } r
                ? new Spotlight {
                    Holder = new PrincipalId(h),
                    HolderClient = NullableGuid(reader, 5) is { } client ? new ClientId(client) : null,
                    RoundId = new RoundId(r),
                }
                : null,
            CurrentRoundId = NullableGuid(reader, 7) is { } current ? new RoundId(current) : null,
            NextRoundNumber = reader.GetInt32(8),
            PointDescriptions = reader.IsDBNull(10) ? new Dictionary<string, string>() : ParseDescriptions(reader.GetString(10)),
            Jira = reader.GetBoolean(11)
                ? new TeamJiraSettings(true, reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetBoolean(13))
                : TeamJiraSettings.None,
            WarmUntil = now + PokerTiming.WarmUp,
        };
    }

    private static Round ReadRound(NpgsqlDataReader reader) {
        var jiraPoints = reader.IsDBNull(13) ? (decimal?)null : reader.GetDecimal(13);
        return new Round {
            Id = new RoundId(reader.GetGuid(0)),
            Number = reader.GetInt32(1),
            TicketKey = reader.IsDBNull(2) ? null : reader.GetString(2),
            ParentKey = reader.IsDBNull(3) ? null : reader.GetString(3),
            State = reader.GetString(4) == "revealed" ? RoundState.Revealed : RoundState.Voting,
            Active = reader.GetBoolean(5),
            Pending = reader.GetBoolean(6),
            PendingOwner = NullableGuid(reader, 7) is { } owner ? new PrincipalId(owner) : null,
            Version = reader.GetInt32(8),
            AutoReveal = reader.GetBoolean(9),
            Winner = OrNull<short>(reader, 10),
            RevealedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            RevealedBy = NullableGuid(reader, 12) is { } by ? new PrincipalId(by) : null,
            Jira = jiraPoints is { } points
                ? new JiraPoints(
                    points,
                    reader.GetFieldValue<DateTimeOffset>(14),
                    NullableGuid(reader, 15) is { } jiraBy ? new PrincipalId(jiraBy) : default)
                : null,
        };
    }

    // Only string values are descriptions; anything else in the column is ignored rather than failing the load.
    private static Dictionary<string, string> ParseDescriptions(string json) {
        using var document = JsonDocument.Parse(json);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) {
            if (property.Value.ValueKind == JsonValueKind.String) {
                descriptions[property.Name] = property.Value.GetString()!;
            }
        }
        return descriptions;
    }

    private static PrincipalKind ParseKind(string kind) => kind switch {
        "user" => PrincipalKind.User,
        "guest" => PrincipalKind.Guest,
        _ => PrincipalKind.External,
    };

    private static string StateText(RoundState state) => state == RoundState.Revealed ? "revealed" : "voting";

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static T? OrNull<T>(NpgsqlDataReader reader, int ordinal) where T : struct {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static NpgsqlParameter Id<T>(T? id) where T : struct, IGuidId<T> {
        return new() { Value = id is { } value ? value.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Small(short? value) {
        return new() { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Smallint };
    }

    private static NpgsqlParameter Timestamp(long? unixMs) {
        return new() {
            Value = unixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
        };
    }

    private static NpgsqlParameter JiraPoints(JiraPointsInfo? jira) {
        return new() { Value = jira is { } j ? j.Points : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] parameters) {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var parameter in parameters) {
            command.Parameters.Add(parameter as NpgsqlParameter ?? new NpgsqlParameter { Value = parameter });
        }
        return command;
    }
}
