using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Server.Data;
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

    protected static NpgsqlBatchCommand Statement(string sql, params NpgsqlParameter[] parameters) {
        var statement = new NpgsqlBatchCommand(sql);
        statement.Parameters.AddRange(parameters);
        return statement;
    }

    /// <summary>
    /// One delete batch as its own transaction behind the leader fence (R1): once another instance holds the epoch,
    /// or this one knows it lost the lease, the batch throws <see cref="FencingLostException"/> and the pass stops.
    /// </summary>
    /// <returns>The rows the first statement affected.</returns>
    protected static async Task<int> FencedAsync(
        NpgsqlDataSource dataSource,
        Leader leader,
        CancellationToken cancellationToken,
        params NpgsqlBatchCommand[] statements) {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await leader.CommitFencedAsync(connection, statements, cancellationToken);
        return statements[0].RecordsAffected;
    }

    /// <summary>
    /// Pages through candidates in key order, so a page whose rooms are all in use never hides the ones after it.
    /// </summary>
    protected static async Task<List<(Guid Id, DateTimeOffset At)>> PageAsync(
        NpgsqlDataSource dataSource,
        string sql,
        DateTimeOffset before,
        (Guid Id, DateTimeOffset At) after,
        int limit,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(Parameter(before));
        command.Parameters.Add(Parameter(after.At));
        command.Parameters.Add(Parameter(after.Id));
        command.Parameters.Add(Parameter(limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var page = new List<(Guid, DateTimeOffset)>();
        while (await reader.ReadAsync(cancellationToken)) {
            page.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }
        return page;
    }

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
/// (R18), and a room someone is in is skipped until the next run. Every delete is fenced: a server that loses
/// leadership mid-pass deletes nothing more.
/// </summary>
public sealed partial class RetentionJob(
    NpgsqlDataSource dataSource,
    Leader leader,
    ActorRegistry registry,
    TimeProvider time,
    IServiceProvider services,
    ILogger<RetentionJob> logger) : ScheduledJob(time, services, logger) {
    public static readonly TimeSpan QuickRoomIdle = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeadMailAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan AuditAge = TimeSpan.FromDays(180);
    public const int CursorAgeDays = 14;

    /// <summary>Rooms purged per kind and run; the next run takes the rest.</summary>
    public const int RoomBatch = 500;

    public const int BlobBatch = 200;

    private const string IdleSessionsSql = """
        select id, last_activity_at from poker_sessions
        where team_id is null and last_activity_at < $1 and (last_activity_at, id) > ($2, $3)
        order by last_activity_at, id
        limit $4
        """;

    private const string IdleBoardsSql = """
        select id, last_activity_at from retro_boards
        where team_id is null and last_activity_at < $1 and (last_activity_at, id) > ($2, $3)
        order by last_activity_at, id
        limit $4
        """;

    // The idle test is repeated: the room may have been used between the listing and the purge. Its cursors and
    // recent-activity rows go in the same transaction, and only once the room is gone.
    private const string PurgeSessionSql = "delete from poker_sessions where id = $1 and team_id is null and last_activity_at < $2";

    private const string PurgeBoardSql = "delete from retro_boards where id = $1 and team_id is null and last_activity_at < $2";

    private const string SessionCursorsSql = """
        delete from client_cursors where room_key = $1 and not exists (select 1 from poker_sessions where id = $2)
        """;

    private const string BoardCursorsSql = """
        delete from client_cursors where room_key = $1 and not exists (select 1 from retro_boards where id = $2)
        """;

    private const string SessionActivitySql = """
        delete from recent_activity where entity_kind = 'poker' and entity_id = $1
          and not exists (select 1 from poker_sessions where id = $1)
        """;

    private const string BoardActivitySql = """
        delete from recent_activity where entity_kind = 'board' and entity_id = $1
          and not exists (select 1 from retro_boards where id = $1)
        """;

    /// <summary>Candidates listed per query; tests lower it to page without hundreds of rooms.</summary>
    internal int PageSize { get; init; } = RoomBatch;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <exception cref="FencingLostException">Leadership was lost; the pass stopped at that batch.</exception>
    public async Task<RetentionReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();
        var idleBefore = now - QuickRoomIdle;
        var (sessions, skippedSessions) = await PurgeIdleAsync(
            IdleSessionsSql,
            id => RoomKey.Poker(new SessionId(id)),
            (id, room) => [
                Statement(PurgeSessionSql, Parameter(id), Parameter(idleBefore)),
                Statement(SessionCursorsSql, Parameter(room), Parameter(id)),
                Statement(SessionActivitySql, Parameter(id)),
            ],
            idleBefore,
            cancellationToken);
        var (boards, skippedBoards) = await PurgeIdleAsync(
            IdleBoardsSql,
            id => RoomKey.Board(new BoardId(id)),
            (id, room) => [
                Statement(PurgeBoardSql, Parameter(id), Parameter(idleBefore)),
                Statement(BoardCursorsSql, Parameter(room), Parameter(id)),
                Statement(BoardActivitySql, Parameter(id)),
            ],
            idleBefore,
            cancellationToken);

        var cursors = await FencedAsync(
            dataSource,
            leader,
            cancellationToken,
            Statement(
                "delete from client_cursors where updated_on < $1",
                Parameter(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-CursorAgeDays))));

        var deletedBlobs = 0;
        int batch;
        do {
            batch = await FencedAsync(
                dataSource,
                leader,
                cancellationToken,
                Statement(BlobsRepo.DeleteUnattachedSql, Parameter(now - BlobsRepo.UnattachedTtl), Parameter(BlobBatch)));
            deletedBlobs += batch;
        } while (batch == BlobBatch);

        var mails = await FencedAsync(
            dataSource,
            leader,
            cancellationToken,
            Statement("delete from email_outbox where body_text is null and created_at < $1", Parameter(now - DeadMailAge)));
        var audit = await FencedAsync(
            dataSource, leader, cancellationToken, Statement("delete from audit_log where at < $1", Parameter(now - AuditAge)));

        var report = new RetentionReport(
            sessions, boards, skippedSessions + skippedBoards, cursors, deletedBlobs, mails, audit);
        LogRan(Logger, report);
        return report;
    }

    /// <param name="purge">The room's statements; the first deletes the room itself and counts it.</param>
    private async Task<(int Purged, int Skipped)> PurgeIdleAsync(
        string listSql,
        Func<Guid, RoomKey> key,
        Func<Guid, string, NpgsqlBatchCommand[]> purge,
        DateTimeOffset idleBefore,
        CancellationToken cancellationToken) {
        var purged = 0;
        var skipped = 0;
        var after = (Id: Guid.Empty, At: DateTimeOffset.MinValue);
        while (purged < RoomBatch) {
            var page = await PageAsync(dataSource, listSql, idleBefore, after, PageSize, cancellationToken);
            foreach (var (id, _) in page) {
                if (purged == RoomBatch) {
                    break;
                }
                var room = key(id);
                var ran = await registry.TryPurgeAsync(
                    room,
                    async token => purged += await FencedAsync(dataSource, leader, token, purge(id, room.ToString())),
                    cancellationToken);
                if (!ran) {
                    skipped++;
                }
            }
            if (page.Count < PageSize) {
                break;
            }
            after = page[^1];
        }
        return (purged, skipped);
    }

    [LoggerMessage(2, LogLevel.Information, "Retention: {Report}")]
    private static partial void LogRan(ILogger logger, RetentionReport report);
}
