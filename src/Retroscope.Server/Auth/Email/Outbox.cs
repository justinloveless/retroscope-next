using Npgsql;

namespace Retroscope.Server.Auth.Email;

/// <summary>A plain-text mail to one recipient. Construction refuses anything that could inject a header.</summary>
public sealed record OutboxMail {
    public OutboxMail(string to, string subject, string body) {
        if (!MailHeaders.IsAddress(to)) {
            throw new ArgumentException("The recipient must be one bare address.", nameof(to));
        }
        if (!MailHeaders.IsSafe(subject)) {
            throw new ArgumentException("The subject may not contain line breaks or control characters.", nameof(subject));
        }
        To = to;
        Subject = subject;
        Body = body;
    }

    public string To { get; }

    public string Subject { get; }

    public string Body { get; }
}

public static class MailHeaders {
    public const int MaxAddressLength = 254;

    /// <summary>No CR, LF or other control character, so the value cannot end its header line or start another.</summary>
    public static bool IsSafe(string? value) => value is not null && !value.Any(char.IsControl);

    /// <summary>One bare address (<c>local@domain</c>) with nothing an SMTP command or a header could be split on.</summary>
    public static bool IsAddress(string? value) {
        if (!IsSafe(value) || value!.Length is < 3 or > MaxAddressLength) {
            return false;
        }
        var at = value.LastIndexOf('@');
        return at > 0
            && at < value.Length - 1
            && !value.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';' or '"' or '(' or ')' or '\\');
    }
}

/// <summary>
/// The durable mail queue (§e). A mail is a row in <c>email_outbox</c>, written in the caller's transaction next to
/// whatever it announces (a token, an invite), so neither exists without the other. <see cref="Jobs.EmailWorker"/>
/// sends it; <see cref="Kick"/> wakes the worker after a commit so mail does not wait for the next poll.
/// </summary>
public sealed class Outbox(NpgsqlDataSource dataSource, TimeProvider time) : IDisposable {
    private readonly SemaphoreSlim _pending = new(0, 1);

    public static NpgsqlBatchCommand Insert(OutboxMail mail, DateTimeOffset now) =>
        new("""
            insert into email_outbox (to_address, subject, body_text, next_attempt_at, created_at)
            values ($1, $2, $3, $4, $4)
            """) {
            Parameters = {
                new() { Value = mail.To },
                new() { Value = mail.Subject },
                new() { Value = mail.Body },
                new() { Value = now },
            },
        };

    /// <summary>Writes the mail together with <paramref name="with"/> in one transaction, then wakes the worker.</summary>
    public async Task EnqueueAsync(OutboxMail mail, IEnumerable<NpgsqlBatchCommand> with, CancellationToken cancellationToken) {
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken)) {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var command in with) {
                batch.BatchCommands.Add(command);
            }
            batch.BatchCommands.Add(Insert(mail, time.GetUtcNow()));
            await batch.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }
        Kick();
    }

    /// <summary>Wakes the worker; call it after committing a transaction that inserted with <see cref="Insert"/>.</summary>
    public void Kick() {
        try {
            _pending.Release();
        } catch (SemaphoreFullException) {
            // Already signalled; one wake-up sends everything that is due.
        }
    }

    internal Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _pending.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _pending.Dispose();
}
