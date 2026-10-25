using Npgsql;
using Retroscope.Core.Room;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;

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

public sealed record RetentionReport(int Cursors, int Blobs, int DeadMails, int AuditRows);

/// <summary>
/// Hourly retention (§b): stale client cursors, unattached board images, dead outbox rows and old audit rows. Every
/// room belongs to a team (UX18), so no room ages out here; a team's rooms go with the team (<see cref="TeamPurgeJob"/>).
/// Every delete is fenced: a server that loses leadership mid-pass deletes nothing more.
/// </summary>
public sealed partial class RetentionJob(
    NpgsqlDataSource dataSource,
    Leader leader,
    TimeProvider time,
    IServiceProvider services,
    ILogger<RetentionJob> logger) : ScheduledJob(time, services, logger) {
    public static readonly TimeSpan DeadMailAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan AuditAge = TimeSpan.FromDays(180);
    public const int CursorAgeDays = 14;

    public const int BlobBatch = 200;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <exception cref="FencingLostException">Leadership was lost; the pass stopped at that batch.</exception>
    public async Task<RetentionReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();
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

        var report = new RetentionReport(cursors, deletedBlobs, mails, audit);
        LogRan(Logger, report);
        return report;
    }

    [LoggerMessage(2, LogLevel.Information, "Retention: {Report}")]
    private static partial void LogRan(ILogger logger, RetentionReport report);
}
