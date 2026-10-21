using Npgsql;
using Retroscope.Core.Room;
using Retroscope.Server.Auth.Email;

namespace Retroscope.Server.Jobs;

/// <summary>
/// Sends <c>email_outbox</c> (§e). A due row is leased by pushing its next attempt out, then sent; success deletes it,
/// failure backs off. After <see cref="MaxAttempts"/> failures the body is nulled (S23), so a live link does not sit in
/// the database; the row stays as a record of the failure until retention removes it. Only the leader sends.
/// </summary>
public sealed partial class EmailWorker(
    NpgsqlDataSource dataSource,
    Outbox outbox,
    SmtpSender sender,
    SmtpOptions options,
    TimeProvider time,
    IServiceProvider services,
    ILogger<EmailWorker> logger) : BackgroundService {
    public const int MaxAttempts = 6;
    public const int BatchSize = 20;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a row being sent is hidden from other runs; a crash mid-send retries it after this.</summary>
    public static readonly TimeSpan SendLease = TimeSpan.FromMinutes(10);

    /// <summary>The wait after the 1st … 5th failure; the 6th is final.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    ];

    private const int MaxErrorLength = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.IsConfigured) {
            LogNotConfigured(logger);
            return;
        }
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (services.GetService<ILeaderEpoch>() is null or { IsLeader: true }) {
                    while (await RunOnceAsync(stoppingToken) == BatchSize) {
                    }
                }
            } catch (Exception e) when (e is not OperationCanceledException) {
                LogRunFailed(logger, e);
            }
            try {
                await outbox.WaitAsync(PollInterval, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    /// <summary>Sends up to <see cref="BatchSize"/> due mails, one at a time.</summary>
    /// <returns>How many were attempted.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken) {
        var due = await LeaseDueAsync(cancellationToken);
        foreach (var row in due) {
            try {
                await sender.SendAsync(new OutboxMail(row.To, row.Subject, row.Body), cancellationToken);
                await ExecuteAsync("delete from email_outbox where id = $1", cancellationToken, row.Id);
            } catch (Exception e) when (e is SmtpFailureException or ArgumentException) {
                await RecordFailureAsync(row, e.Message, cancellationToken);
            }
        }
        return due.Count;
    }

    private async Task<IReadOnlyList<DueMail>> LeaseDueAsync(CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        await using var command = dataSource.CreateCommand("""
            update email_outbox set next_attempt_at = $2
            where id in (
              select id from email_outbox
              where next_attempt_at <= $1 and body_text is not null
              order by next_attempt_at, id
              limit $3
              for update skip locked)
            returning id, to_address, subject, body_text, attempts
            """);
        command.Parameters.Add(new() { Value = now });
        command.Parameters.Add(new() { Value = now + SendLease });
        command.Parameters.Add(new() { Value = BatchSize });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var due = new List<DueMail>();
        while (await reader.ReadAsync(cancellationToken)) {
            due.Add(new DueMail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return [.. due.OrderBy(x => x.Id)];
    }

    private async Task RecordFailureAsync(DueMail row, string error, CancellationToken cancellationToken) {
        var attempts = row.Attempts + 1;
        var final = attempts >= MaxAttempts;
        var now = time.GetUtcNow();
        var lastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        await ExecuteAsync(
            """
            update email_outbox set
              attempts = $2,
              last_error = $3,
              body_text = case when $4 then null else body_text end,
              next_attempt_at = case when $4 then next_attempt_at else $5 end
            where id = $1
            """,
            cancellationToken,
            row.Id,
            attempts,
            lastError,
            final,
            now + Backoff[Math.Min(attempts, Backoff.Count) - 1]);
        if (final) {
            LogGaveUp(logger, row.Id, attempts, lastError);
        } else {
            LogRetrying(logger, row.Id, attempts, lastError);
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params object[] parameters) {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(1, LogLevel.Warning, "SMTP_HOST is not set; outgoing mail stays queued")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "Email worker run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "Mail {Id} failed (attempt {Attempts}): {Error}")]
    private static partial void LogRetrying(ILogger logger, long id, int attempts, string error);

    [LoggerMessage(4, LogLevel.Error, "Mail {Id} failed {Attempts} times and was dropped: {Error}")]
    private static partial void LogGaveUp(ILogger logger, long id, int attempts, string error);

    private sealed record DueMail(long Id, string To, string Subject, string Body, int Attempts);
}
