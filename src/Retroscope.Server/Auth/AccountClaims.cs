using System.Net;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Auth;

/// <summary>
/// Mail the auth core asks for. The email unit (U17) implements it with tokens, the outbox and <c>EmailBudget</c>;
/// until it is registered the mail is not sent. Callers answer the same way whether or not a mail goes out.
/// </summary>
public interface IAccountMail {
    /// <summary>A new account signed up: send the verification link.</summary>
    Task SignedUpAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken);

    /// <summary>Someone signed up with an address that already has an account: tell its owner.</summary>
    Task AlreadyRegisteredAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken);

    /// <summary>The account's hourly failed-login cap was reached: send a one-time sign-in link instead of locking it.</summary>
    Task LoginCappedAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken);
}

internal sealed class NoAccountMail : IAccountMail {
    public Task SignedUpAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AlreadyRegisteredAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LoginCappedAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>How someone proved they own an account's mailbox.</summary>
public enum OwnershipProof {
    Google,
    MagicLink,
}

/// <summary>
/// The takeover rule, in one place (§e; S1, S4). An account whose email is unverified is unclaimed. The first proof
/// of mailbox ownership (a trusted OIDC email or a consumed magic link) claims it: the email becomes verified, the
/// password is cleared, every session is revoked and every external identity is unlinked, so whoever registered the
/// address first (a squatter, or an imported unverified account) loses access. A verified account is never claimed.
/// </summary>
public sealed class AccountClaims(NpgsqlDataSource dataSource, Sessions sessions, TimeProvider time) {
    /// <returns>True when the account was unverified and is now claimed; false when it was already verified (or is gone).</returns>
    public async Task<bool> ClaimAsync(PrincipalId user, OwnershipProof proof, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        var revoked = new List<byte[]>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken)) {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var claim = new NpgsqlBatch(connection, transaction) { BatchCommands = { UsersRepo.Claim(user, now) } };
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0) {
                return false;
            }
            // Identities linked while the account was unverified belong to whoever squatted it (an untrusted email at
            // sign-in, or the squatter's own explicit link); left in place they would sign in to the claimed account.
            var unlinked = new List<string>();
            await using (var revoke = new NpgsqlBatch(connection, transaction) {
                BatchCommands = {
                    SessionsRepo.DeleteAllOf(user),
                    UsersRepo.UnlinkIdentities(user),
                    UsersRepo.Audit("auth.claim", user, user.ToString(), new Dictionary<string, string> { ["proof"] = Name(proof) }, now),
                },
            }) {
                await using var reader = await revoke.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) {
                    revoked.Add(reader.GetFieldValue<byte[]>(0));
                }
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) {
                    unlinked.Add(reader.GetString(0));
                }
            }
            if (unlinked.Count > 0) {
                await using var log = new NpgsqlBatch(connection, transaction);
                foreach (var provider in unlinked) {
                    log.BatchCommands.Add(UsersRepo.Audit("auth.unlink", null, user.ToString(), new Dictionary<string, string> {
                        ["provider"] = provider,
                        ["reason"] = "claim",
                    }, now));
                }
                await log.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        sessions.Revoked(revoked);
        return true;
    }

    private static string Name(OwnershipProof proof) => proof switch {
        OwnershipProof.Google => "google",
        OwnershipProof.MagicLink => "magic_link",
        _ => throw new ArgumentOutOfRangeException(nameof(proof)),
    };
}
