using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Jobs;

public sealed record TeamPurgeReport(int Teams, int Skipped);

/// <summary>
/// Hard-deletes teams 30 days after their soft delete (§0.1, §b). Everything the team owns goes with it by
/// cascade (sessions, boards and their images, members, invites, Jira settings); the client cursors and
/// recent-activity rows of its rooms, which have no foreign key, are deleted in the same fenced statement, so a server
/// that loses leadership mid-pass deletes nothing more. The team's room key is held closed for the delete. Its poker
/// and board rooms need no purge of their own: a room of a soft-deleted team loads as missing, so a racing load can
/// neither see nor write anything.
/// </summary>
public sealed partial class TeamPurgeJob(
    NpgsqlDataSource dataSource,
    Leader leader,
    ActorRegistry registry,
    TimeProvider time,
    IServiceProvider services,
    ILogger<TeamPurgeJob> logger) : ScheduledJob(time, services, logger) {
    public static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    /// <summary>Teams purged per run; the next run takes the rest.</summary>
    public const int Batch = 100;

    private const string DueSql = """
        select id, deleted_at from teams
        where deleted_at < $1 and (deleted_at, id) > ($2, $3)
        order by deleted_at, id
        limit $4
        """;

    // The deleted_at test is repeated: an undelete would have to clear it, and nothing may purge a live team. The
    // audit row is the statement's own result, one per team purged. Both
    // lookups by room are index lookups: client_cursors by its key's leading column, recent_activity by
    // recent_activity_entity_ix (0002).
    internal const string PurgeSql = """
        with doomed as (
          select id from teams where id = $1 and deleted_at < $2),
        rooms as (
          select 'team' as kind, d.id from doomed d
          union all
          select 'poker', s.id from poker_sessions s join doomed d on d.id = s.team_id
          union all
          select 'board', b.id from retro_boards b join doomed d on d.id = b.team_id),
        cursors as (
          delete from client_cursors where room_key in (select kind || ':' || id::text from rooms)),
        activity as (
          delete from recent_activity where (entity_kind, entity_id) in (select kind, id from rooms)),
        gone as (
          delete from teams where id in (select id from doomed) returning id)
        insert into audit_log (at, team_id, action, target) select $3, id, 'team.purge', id::text from gone
        """;

    /// <summary>Candidates listed per query; tests lower it to page without hundreds of teams.</summary>
    internal int PageSize { get; init; } = Batch;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <exception cref="FencingLostException">Leadership was lost; the pass stopped at that team.</exception>
    public async Task<TeamPurgeReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();
        var due = now - Grace;
        var purged = 0;
        var skipped = 0;
        var after = (Id: Guid.Empty, At: DateTimeOffset.MinValue);
        while (purged < Batch) {
            var page = await PageAsync(dataSource, DueSql, due, after, PageSize, cancellationToken);
            foreach (var (id, _) in page) {
                if (purged == Batch) {
                    break;
                }
                var ran = await registry.TryPurgeAsync(
                    RoomKey.Team(new TeamId(id)),
                    async token => purged += await FencedAsync(
                        dataSource, leader, token, Statement(PurgeSql, Parameter(id), Parameter(due), Parameter(now))),
                    cancellationToken);
                if (ran) {
                    Services.GetService<MembershipCache>()?.Forget(new TeamId(id));
                } else {
                    skipped++;
                }
            }
            if (page.Count < PageSize) {
                break;
            }
            after = page[^1];
        }
        var report = new TeamPurgeReport(purged, skipped);
        LogRan(Logger, report);
        return report;
    }

    [LoggerMessage(1, LogLevel.Information, "Team purge: {Report}")]
    private static partial void LogRan(ILogger logger, TeamPurgeReport report);
}
