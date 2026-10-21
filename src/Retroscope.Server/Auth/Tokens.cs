using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;

namespace Retroscope.Server.Auth;

public enum TokenPurpose {
    MagicLink,
    PasswordReset,
    VerifyEmail,
}

/// <summary>An unconsumed, unexpired token: whom it was issued for. <see cref="User"/> is null for a magic link to a new email.</summary>
public sealed record TokenUse(PrincipalId? User, string Email);

/// <summary>A token to put in a link, and the row that makes it valid; insert the row in the same transaction as its mail.</summary>
public sealed record IssuedToken(string Token, NpgsqlBatchCommand Insert);

/// <summary>
/// Magic-link, password-reset and email-verification tokens (§e): 32 CSPRNG bytes, stored only as SHA-256, single use.
/// Consuming one consumes every other unconsumed token of the same purpose for the same account, so an older link in
/// the mailbox stops working too.
/// </summary>
public sealed class Tokens(NpgsqlDataSource dataSource, TimeProvider time) {
    public static TimeSpan Lifetime(TokenPurpose purpose) => purpose switch {
        TokenPurpose.MagicLink => TimeSpan.FromMinutes(15),
        TokenPurpose.PasswordReset => TimeSpan.FromMinutes(30),
        TokenPurpose.VerifyEmail => TimeSpan.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static string ToDb(TokenPurpose purpose) => purpose switch {
        TokenPurpose.MagicLink => "magic_link",
        TokenPurpose.PasswordReset => "password_reset",
        TokenPurpose.VerifyEmail => "verify_email",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static IssuedToken Issue(TokenPurpose purpose, PrincipalId? user, string email, DateTimeOffset now) {
        var token = OpaqueToken.New();
        var insert = new NpgsqlBatchCommand("""
            insert into auth_tokens (token_hash, purpose, user_id, email, expires_at, created_at)
            values ($1, $2, $3, $4, $5, $6)
            """) {
            Parameters = {
                new() { Value = TokenHash.Of(token) },
                new() { Value = ToDb(purpose) },
                new() { Value = (object?)user?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new() { Value = email },
                new() { Value = now + Lifetime(purpose) },
                new() { Value = now },
            },
        };
        return new IssuedToken(token, insert);
    }

    /// <summary>Reads a live token without consuming it (the magic-link page's "Sign in as …?" prompt).</summary>
    public async Task<TokenUse?> PeekAsync(string? token, TokenPurpose purpose, CancellationToken cancellationToken) {
        if (!OpaqueToken.IsWellFormed(token)) {
            return null;
        }
        await using var command = dataSource.CreateCommand("""
            select user_id, email from auth_tokens
            where token_hash = $1 and purpose = $2 and consumed_at is null and expires_at > $3
            """);
        command.Parameters.Add(new() { Value = TokenHash.Of(token!) });
        command.Parameters.Add(new() { Value = ToDb(purpose) });
        command.Parameters.Add(new() { Value = time.GetUtcNow() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>
    /// Consumes a live token and, in the same transaction, runs <paramref name="then"/>; nothing is consumed when the
    /// token is unknown, expired, used, (with <paramref name="requiredUser"/>) issued to someone else, or when
    /// <paramref name="then"/> returns false.
    /// </summary>
    /// <returns>Whom the token was issued for; null when nothing was consumed.</returns>
    public async Task<TokenUse?> ConsumeAsync(
        string? token,
        TokenPurpose purpose,
        PrincipalId? requiredUser,
        Func<TokenUse, NpgsqlConnection, NpgsqlTransaction, Task<bool>>? then,
        CancellationToken cancellationToken) {
        if (!OpaqueToken.IsWellFormed(token)) {
            return null;
        }
        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        TokenUse? use;
        await using (var consume = new NpgsqlCommand("""
            update auth_tokens set consumed_at = $3
            where token_hash = $1 and purpose = $2 and consumed_at is null and expires_at > $3
              and ($4::uuid is null or user_id = $4)
            returning user_id, email
            """, connection, transaction)) {
            consume.Parameters.Add(new() { Value = TokenHash.Of(token!) });
            consume.Parameters.Add(new() { Value = ToDb(purpose) });
            consume.Parameters.Add(new() { Value = now });
            consume.Parameters.Add(new() { Value = (object?)requiredUser?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            await using var reader = await consume.ExecuteReaderAsync(cancellationToken);
            use = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        if (use is null) {
            return null;
        }
        await using (var siblings = new NpgsqlCommand("""
            update auth_tokens set consumed_at = $2
            where purpose = $1 and consumed_at is null and (user_id = $3 or email = $4)
            """, connection, transaction)) {
            siblings.Parameters.Add(new() { Value = ToDb(purpose) });
            siblings.Parameters.Add(new() { Value = now });
            siblings.Parameters.Add(new() { Value = (object?)use.User?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            siblings.Parameters.Add(new() { Value = use.Email });
            await siblings.ExecuteNonQueryAsync(cancellationToken);
        }
        if (then is not null && !await then(use, connection, transaction)) {
            return null;
        }
        await transaction.CommitAsync(CancellationToken.None);
        return use;
    }

    private static TokenUse Read(NpgsqlDataReader reader) =>
        new(reader.IsDBNull(0) ? null : new PrincipalId(reader.GetGuid(0)), reader.GetString(1));
}
