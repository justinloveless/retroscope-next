using System.Net;
using Retroscope.Core.Ids;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Auth.Email;

/// <summary>
/// Every account mail: one <see cref="EmailBudget"/> reservation, then the token row and the outbox row in one
/// transaction. Over budget the mail and its token are silently dropped; callers answer the same either way (S8).
/// </summary>
public sealed class AccountMail(Outbox outbox, EmailBudget budget, HostingOptions hosting, TimeProvider time) : IAccountMail {
    public Task SignedUpAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        SendWithTokenAsync(TokenPurpose.VerifyEmail, user, email, ip, Templates.VerifyEmail, cancellationToken);

    public async Task AlreadyRegisteredAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) {
        if (budget.TryReserve(email, ip, sender: null)) {
            await outbox.EnqueueAsync(Templates.AlreadyRegistered(hosting.PublicOrigin).To(email), [], cancellationToken);
        }
    }

    // There is no "unlock" token purpose: signing in is the unlock, so the capped owner gets a magic link (U16 decision 4).
    public Task LoginCappedAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        SendWithTokenAsync(TokenPurpose.MagicLink, user, email, ip, Templates.LoginCapped, cancellationToken);

    /// <param name="user">Null for an address with no account yet; consuming the link creates it.</param>
    public Task MagicLinkAsync(PrincipalId? user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        SendWithTokenAsync(TokenPurpose.MagicLink, user, email, ip, Templates.MagicLink, cancellationToken);

    public Task PasswordResetAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        SendWithTokenAsync(TokenPurpose.PasswordReset, user, email, ip, Templates.PasswordReset, cancellationToken);

    /// <summary>The page that consumes a token, with the token in the fragment: <c>PUBLIC_URL/auth/magic#t=…</c>.</summary>
    public string Link(TokenPurpose purpose, string token) => purpose switch {
        TokenPurpose.MagicLink => $"{hosting.PublicOrigin}/auth/magic#t={token}",
        TokenPurpose.PasswordReset => $"{hosting.PublicOrigin}/auth/reset#t={token}",
        TokenPurpose.VerifyEmail => $"{hosting.PublicOrigin}/auth/verify#t={token}",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private async Task SendWithTokenAsync(
        TokenPurpose purpose,
        PrincipalId? user,
        string email,
        IPAddress? ip,
        Func<string, MailContent> template,
        CancellationToken cancellationToken) {
        if (!budget.TryReserve(email, ip, sender: null)) {
            return;
        }
        var issued = Tokens.Issue(purpose, user, email, time.GetUtcNow());
        await outbox.EnqueueAsync(template(Link(purpose, issued.Token)).To(email), [issued.Insert], cancellationToken);
    }
}
