using System.Collections.Concurrent;
using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

public enum ActivityKind : byte {
    Team,
    Board,
    Poker,
}

public sealed record RecentTeam(TeamId Id, string Name, long LastAt);

public sealed record RecentBoard(BoardId Id, string Code, string Title, string? TeamName, long LastAt);

public sealed record RecentSession(SessionId Id, string Code, TeamId? TeamId, string? TeamName, long LastAt);

public sealed record Dashboard(
    IReadOnlyList<RecentTeam> Teams,
    IReadOnlyList<RecentTeam> Favorites,
    IReadOnlyList<RecentBoard> Boards,
    IReadOnlyList<RecentSession> Sessions);

/// <summary>
/// <c>recent_activity</c> (§c.5), written by the server only and debounced in memory to once per 5 minutes per
/// (user, entity), and the dashboard read built on it.
/// </summary>
public sealed class ActivityRepo(NpgsqlDataSource dataSource, TimeProvider time) {
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    private const int MaxRemembered = 65_536;
    private const int DashboardRows = 5;

    private readonly ConcurrentDictionary<(PrincipalId, ActivityKind, Guid), DateTimeOffset> _written = new();

    /// <summary>Records that <paramref name="user"/> opened the entity, at most once per <see cref="Debounce"/>.</summary>
    public async Task TouchAsync(PrincipalId user, ActivityKind kind, Guid entity, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        var key = (user, kind, entity);
        if (_written.TryGetValue(key, out var last) && now - last < Debounce) {
            return;
        }
        if (_written.Count >= MaxRemembered) {
            _written.Clear();
        }
        _written[key] = now;
        await using var command = dataSource.CreateCommand("""
            insert into recent_activity (user_id, entity_kind, entity_id, last_at) values ($1, $2, $3, $4)
            on conflict (user_id, entity_kind, entity_id) do update set last_at = greatest(recent_activity.last_at, excluded.last_at)
            """);
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = KindName(kind) });
        command.Parameters.Add(new() { Value = entity });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// One batched read (§d): the user's teams (most recently used first) and favorites, and the five most recent
    /// boards and poker sessions they visited or created, each re-checked against what the user may still open.
    /// </summary>
    public async Task<Dashboard> DashboardAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(TeamsRepo.Command(
            """
            select t.id, t.name, (extract(epoch from greatest(m.joined_at, a.last_at)) * 1000)::bigint,
                   exists (select 1 from team_favorites f where f.user_id = m.user_id and f.team_id = t.id)
            from team_members m
            join teams t on t.id = m.team_id and t.deleted_at is null
            left join recent_activity a on a.user_id = m.user_id and a.entity_kind = 'team' and a.entity_id = t.id
            where m.user_id = $1
            order by 3 desc, t.id
            """,
            user.Value));
        // Board access mirrors Policy.Can(Subscribe) for a board, as UsersRepo.UnreadMentionsAsync does.
        batch.BatchCommands.Add(TeamsRepo.Command(
            """
            with seen as (
              select entity_id as id, last_at from recent_activity where user_id = $1 and entity_kind = 'board'
              union all
              select id, created_at from retro_boards where created_by = $1
            )
            select b.id, b.code, b.title, t.name, (extract(epoch from max(s.last_at)) * 1000)::bigint
            from seen s
            join retro_boards b on b.id = s.id and b.deleted_at is null and b.archived_at is null
            left join teams t on t.id = b.team_id
            left join team_members tm on tm.team_id = b.team_id and tm.user_id = $1
            where (b.team_id is null or t.deleted_at is null)
              and (tm.user_id is not null
                or (b.visibility = 'link')
                or (b.visibility = 'password'
                    and ((b.team_id is null and b.created_by = $1)
                      or exists (select 1 from board_access_grants g
                                 where g.board_id = b.id and g.principal_id = $1 and g.password_epoch = b.password_set_at))))
            group by b.id, b.code, b.title, t.name
            order by 5 desc, b.id
            limit $2
            """,
            user.Value,
            DashboardRows));
        batch.BatchCommands.Add(TeamsRepo.Command(
            """
            with seen as (
              select entity_id as id, last_at from recent_activity where user_id = $1 and entity_kind = 'poker'
              union all
              select id, created_at from poker_sessions where created_by = $1
            )
            select p.id, p.code, t.id, t.name, (extract(epoch from max(s.last_at)) * 1000)::bigint
            from seen s
            join poker_sessions p on p.id = s.id
            left join teams t on t.id = p.team_id
            left join team_members tm on tm.team_id = p.team_id and tm.user_id = $1
            where p.team_id is null or (t.deleted_at is null and tm.user_id is not null)
            group by p.id, p.code, t.id, t.name
            order by 5 desc, p.id
            limit $2
            """,
            user.Value,
            DashboardRows));
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var teams = new List<RecentTeam>();
        var favorites = new List<RecentTeam>();
        while (await reader.ReadAsync(cancellationToken)) {
            var team = new RecentTeam(new TeamId(reader.GetGuid(0)), reader.GetString(1), reader.GetInt64(2));
            teams.Add(team);
            if (reader.GetBoolean(3)) {
                favorites.Add(team);
            }
        }
        await reader.NextResultAsync(cancellationToken);
        var boards = new List<RecentBoard>();
        while (await reader.ReadAsync(cancellationToken)) {
            boards.Add(new RecentBoard(
                new BoardId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)));
        }
        await reader.NextResultAsync(cancellationToken);
        var sessions = new List<RecentSession>();
        while (await reader.ReadAsync(cancellationToken)) {
            sessions.Add(new RecentSession(
                new SessionId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : new TeamId(reader.GetGuid(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)));
        }
        return new Dashboard(teams, favorites, boards, sessions);
    }

    private static string KindName(ActivityKind kind) => kind switch {
        ActivityKind.Team => "team",
        ActivityKind.Board => "board",
        _ => "poker",
    };
}
