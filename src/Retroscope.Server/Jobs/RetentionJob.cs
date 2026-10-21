using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Jobs;

/// <summary>
/// A periodic cleanup that runs only on the leader (§b retention). The first run waits <see cref="FirstRunDelay"/>
/// so a restart storm does not start with a burst of deletes, and so short-lived hosts (tests, the route inventory)
/// never run it. A failed run is logged and retried at the next interval.
/// </summary>
public abstract partial class ScheduledJob(TimeProvider time, IServiceProvider services, ILogger logger) : BackgroundService {
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    protected TimeProvider Time => time;

    protected IServiceProvider Services => services;

    protected ILogger Logger => logger;

    protected abstract TimeSpan Interval { get; }

    /// <summary>One pass; safe to run at any time and again.</summary>
    public abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(FirstRunDelay, time, stoppingToken);
            while (true) {
                if (services.GetService<ILeaderEpoch>() is null or { IsLeader: true }) {
                    try {
                        await RunOnceAsync(stoppingToken);
                    } catch (Exception e) when (e is not OperationCanceledException) {
                        LogRunFailed(logger, e, GetType().Name);
                    }
                }
                await Task.Delay(Interval, time, stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        }
    }

    protected static NpgsqlParameter Parameter<T>(T value) => new NpgsqlParameter<T> { TypedValue = value };

    [LoggerMessage(1, LogLevel.Error, "{Job} run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string job);
}

public sealed record RetentionReport(
    int QuickSessions,
    int QuickBoards,
    int SkippedLive,
    int Cursors,
    int Blobs,
    int DeadMails,
    int AuditRows);

/// <summary>
/// Hourly retention (§b): idle quick sessions and boards, stale client cursors, unattached board images, dead
/// outbox rows and old audit rows. Rooms are deleted through <see cref="ActorRegistry.TryPurgeAsync"/>, which keeps
/// the room key closed while the rows go, so a racing subscribe or ask waits instead of loading a half-deleted room
/// (R18), and a room someone is in is skipped until the next run.
/// </summary>
public sealed partial class RetentionJob(
    NpgsqlDataSource dataSource,
    ActorRegistry registry,
    BlobsRepo blobs,
    TimeProvider time,
    IServiceProvider services,
    ILogger<RetentionJob> logger) : ScheduledJob(time, services, logger) {
    public static readonly TimeSpan QuickRoomIdle = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeadMailAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan AuditAge = TimeSpan.FromDays(180);
    public const int CursorAgeDays = 14;

    /// <summary>Rooms looked at per kind and run; the next run takes the rest.</summary>
    public const int RoomBatch = 500;

    public const int BlobBatch = 200;

    private const string IdleSessionsSql = """
        select id from poker_sessions
        where team_id is null and last_activity_at < $1
        order by last_activity_at, id
        limit $2
        """;

    private const string IdleBoardsSql = """
        select id from retro_boards
        where team_id is null and last_activity_at < $1
        order by last_activity_at, id
        limit $2
        """;

    // One statement, so the room, its cursors and its recent-activity rows go together. The idle test is repeated:
    // the room may have been used between the listing and the purge.
    private const string PurgeSessionSql = """
        with gone as (
          delete from poker_sessions where id = $1 and team_id is null and last_activity_at < $2 returning id),
        cursors as (
          delete from client_cursors where room_key = $3 and exists (select 1 from gone)),
        activity as (
          delete from recent_activity where entity_kind = 'poker' and entity_id in (select id from gone))
        select count(*)::int from gone
        """;

    private const string PurgeBoardSql = """
        with gone as (
          delete from retro_boards where id = $1 and team_id is null and last_activity_at < $2 returning id),
        cursors as (
          delete from client_cursors where room_key = $3 and exists (select 1 from gone)),
        activity as (
          delete from recent_activity where entity_kind = 'board' and entity_id in (select id from gone))
        select count(*)::int from gone
        """;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    public async Task<RetentionReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();
        var idleBefore = now - QuickRoomIdle;
        var (sessions, skippedSessions) = await PurgeIdleAsync(
            IdleSessionsSql, PurgeSessionSql, id => RoomKey.Poker(new SessionId(id)), idleBefore, cancellationToken);
        var (boards, skippedBoards) = await PurgeIdleAsync(
            IdleBoardsSql, PurgeBoardSql, id => RoomKey.Board(new BoardId(id)), idleBefore, cancellationToken);

        var cursors = await ExecuteAsync(
            "delete from client_cursors where updated_on < $1",
            cancellationToken,
            Parameter(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-CursorAgeDays)));

        var deletedBlobs = 0;
        int batch;
        do {
            batch = await blobs.DeleteUnattachedAsync(now - BlobsRepo.UnattachedTtl, BlobBatch, cancellationToken);
            deletedBlobs += batch;
        } while (batch == BlobBatch);

        var mails = await ExecuteAsync(
            "delete from email_outbox where body_text is null and created_at < $1",
            cancellationToken,
            Parameter(now - DeadMailAge));
        var audit = await ExecuteAsync("delete from audit_log where at < $1", cancellationToken, Parameter(now - AuditAge));

        var report = new RetentionReport(
            sessions, boards, skippedSessions + skippedBoards, cursors, deletedBlobs, mails, audit);
        LogRan(Logger, report);
        return report;
    }

    private async Task<(int Purged, int Skipped)> PurgeIdleAsync(
        string listSql,
        string purgeSql,
        Func<Guid, RoomKey> key,
        DateTimeOffset idleBefore,
        CancellationToken cancellationToken) {
        var ids = new List<Guid>();
        await using (var command = dataSource.CreateCommand(listSql)) {
            command.Parameters.Add(Parameter(idleBefore));
            command.Parameters.Add(Parameter(RoomBatch));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                ids.Add(reader.GetGuid(0));
            }
        }

        var purged = 0;
        var skipped = 0;
        foreach (var id in ids) {
            var room = key(id);
            var deleted = 0;
            var ran = await registry.TryPurgeAsync(
                room,
                async token => {
                    await using var command = dataSource.CreateCommand(purgeSql);
                    command.Parameters.Add(Parameter(id));
                    command.Parameters.Add(Parameter(idleBefore));
                    command.Parameters.Add(Parameter(room.ToString()));
                    deleted = (int)(await command.ExecuteScalarAsync(token))!;
                },
                cancellationToken);
            if (!ran) {
                skipped++;
            }
            purged += deleted;
        }
        return (purged, skipped);
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters) {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(2, LogLevel.Information, "Retention: {Report}")]
    private static partial void LogRan(ILogger logger, RetentionReport report);
}
