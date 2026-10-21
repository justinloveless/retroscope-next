using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Jobs;

public sealed record TeamPurgeReport(int Teams, int Skipped);

/// <summary>
/// Hard-deletes teams 30 days after their soft delete (§0.1, §b). Everything the team owns goes with it by
/// cascade (sessions, boards and their images, members, invites, Jira settings); the client cursors and
/// recent-activity rows of its rooms, which have no foreign key, are deleted in the same statement. The team's room
/// key is held closed for the delete. Its poker and board rooms need no purge of their own: a room of a soft-deleted
/// team loads as missing, so a racing load can neither see nor write anything.
/// </summary>
public sealed partial class TeamPurgeJob(
    NpgsqlDataSource dataSource,
    ActorRegistry registry,
    TimeProvider time,
    IServiceProvider services,
    ILogger<TeamPurgeJob> logger) : ScheduledJob(time, services, logger) {
    public static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    public const int Batch = 100;

    private const string DueSql = """
        select id from teams where deleted_at < $1 order by deleted_at, id limit $2
        """;

    // The deleted_at test is repeated: an undelete would have to clear it, and nothing may purge a live team.
    private const string PurgeSql = """
        with doomed as (
          select id from teams where id = $1 and deleted_at < $2),
        rooms as (
          select 'team:' || d.id::text as room_key from doomed d
          union all
          select 'poker:' || s.id::text from poker_sessions s join doomed d on d.id = s.team_id
          union all
          select 'board:' || b.id::text from retro_boards b join doomed d on d.id = b.team_id),
        cursors as (
          delete from client_cursors where room_key in (select room_key from rooms)),
        activity as (
          delete from recent_activity where entity_kind || ':' || entity_id::text in (select room_key from rooms)),
        gone as (
          delete from teams where id in (select id from doomed) returning id),
        audit as (
          insert into audit_log (at, team_id, action, target) select $3, id, 'team.purge', id::text from gone)
        select count(*)::int from gone
        """;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    public async Task<TeamPurgeReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();
        var due = now - Grace;
        var ids = new List<Guid>();
        await using (var command = dataSource.CreateCommand(DueSql)) {
            command.Parameters.Add(Parameter(due));
            command.Parameters.Add(Parameter(Batch));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                ids.Add(reader.GetGuid(0));
            }
        }

        var purged = 0;
        var skipped = 0;
        foreach (var id in ids) {
            var ran = await registry.TryPurgeAsync(
                RoomKey.Team(new TeamId(id)),
                async token => {
                    await using var command = dataSource.CreateCommand(PurgeSql);
                    command.Parameters.Add(Parameter(id));
                    command.Parameters.Add(Parameter(due));
                    command.Parameters.Add(Parameter(now));
                    purged += (int)(await command.ExecuteScalarAsync(token))!;
                },
                cancellationToken);
            if (ran) {
                Services.GetService<MembershipCache>()?.Forget(new TeamId(id));
            } else {
                skipped++;
            }
        }
        var report = new TeamPurgeReport(purged, skipped);
        LogRan(Logger, report);
        return report;
    }

    [LoggerMessage(1, LogLevel.Information, "Team purge: {Report}")]
    private static partial void LogRan(ILogger logger, TeamPurgeReport report);
}
