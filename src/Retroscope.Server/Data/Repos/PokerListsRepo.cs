using System.Text.Json.Serialization;
using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

/// <param name="Key">Null for rounds with a ticket but no parent ("various topics"); written as <c>null</c>.</param>
public sealed record ParentKeyCount([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Key, int Count);

/// <param name="Pointed">Revealed rounds that are not pending previews.</param>
/// <param name="Total">Rounds that are not pending previews, the same count the room posts to the team (U21).</param>
/// <param name="ParentKeys">Per parent key over rounds with a real ticket, in order of first appearance in the strip.</param>
public sealed record PokerSessionListItem(
    SessionId Id,
    string Code,
    long CreatedAt,
    int Pointed,
    int Total,
    IReadOnlyList<ParentKeyCount> ParentKeys);

public sealed record SessionOwner(TeamId? Team);

/// <summary>Poker sessions created over HTTP (create in place, §c.2) and the team's session list with its stats.</summary>
public sealed class PokerListsRepo(NpgsqlDataSource dataSource) {
    /// <summary>
    /// A new session with its first, placeholder round (the original created round 1 on first open), as one
    /// statement. A team session copies presence and spotlight-follow from the team's latest session. False, writing
    /// nothing, when the code is already taken in any letter case.
    /// </summary>
    public async Task<bool> CreateSessionAsync(
        SessionId session,
        RoundId firstRound,
        RoomCode code,
        TeamId? team,
        PrincipalId createdBy,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            with latest as (
              select presence_enabled, follow_spotlight from poker_sessions
              where team_id = $3 order by created_at desc, id desc limit 1
            ), created as (
              insert into poker_sessions (id, code, team_id, created_by, presence_enabled, follow_spotlight,
                                          current_round_id, next_round_number)
              select $1, $2, $3, $4, coalesce((select presence_enabled from latest), true),
                     coalesce((select follow_spotlight from latest), true), $5, 2
              where not exists (select 1 from poker_sessions where upper(code) = upper($2))
              returning id
            )
            insert into poker_rounds (id, session_id, number, sort_key) select $5, id, 1, 0 from created
            """);
        command.Parameters.Add(new() { Value = session.Value });
        command.Parameters.Add(new() { Value = code.Value });
        command.Parameters.Add(RetroListsRepo.Uuid(team?.Value));
        command.Parameters.Add(new() { Value = createdBy.Value });
        command.Parameters.Add(new() { Value = firstRound.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>The team of a session that exists; null when there is no such session.</summary>
    public async Task<SessionOwner?> OwnerAsync(SessionId session, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("select team_id from poker_sessions where id = $1");
        command.Parameters.Add(new() { Value = session.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new SessionOwner(reader.IsDBNull(0) ? null : new TeamId(reader.GetGuid(0)));
    }

    public async Task<Page<PokerSessionListItem>> ListTeamSessionsAsync(
        TeamId team,
        ListCursor? cursor,
        int limit,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select s.id, s.code, s.created_at, coalesce(r.pointed, 0), coalesce(r.total, 0), k.keys, k.counts
            from poker_sessions s
            left join lateral (
              select count(*) filter (where state = 'revealed')::int as pointed, count(*)::int as total
              from poker_rounds where session_id = s.id and not is_pending
            ) r on true
            left join lateral (
              select array_agg(parent_key order by first, parent_key) as keys, array_agg(n order by first, parent_key) as counts
              from (
                select parent_key, count(*)::int as n, min(sort_key) as first
                from poker_rounds
                where session_id = s.id and not is_pending and ticket_key is not null
                group by parent_key
              ) g
            ) k on true
            where s.team_id = $1 and ($2::timestamptz is null or (s.created_at, s.id) < ($2, $3))
            order by s.created_at desc, s.id desc
            limit $4
            """);
        command.Parameters.Add(new() { Value = team.Value });
        ListCursor.AddParameters(command, cursor);
        command.Parameters.Add(new() { Value = limit + 1 });
        var rows = new List<(PokerSessionListItem, ListCursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var id = reader.GetGuid(0);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(2);
            var keys = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string?[]>(5);
            var counts = reader.IsDBNull(6) ? [] : reader.GetFieldValue<int[]>(6);
            rows.Add((
                new PokerSessionListItem(
                    new SessionId(id),
                    reader.GetString(1),
                    createdAt.ToUnixTimeMilliseconds(),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    [.. keys.Zip(counts, (key, count) => new ParentKeyCount(key, count))]),
                new ListCursor(createdAt, id)));
        }
        return Page.Of(rows, limit);
    }
}
