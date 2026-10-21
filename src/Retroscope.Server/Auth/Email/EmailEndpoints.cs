using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Auth.Email;

/// <summary>
/// Magic link, password reset and email verification (§d, §e). Requests always answer 202, whether or not the
/// address has an account or the mail fits the budget. Tokens are consumed only by these POSTs, never by a GET, so
/// a mail scanner that prefetches the link changes nothing.
/// </summary>
public static class EmailEndpoints {
    public static readonly RateLimit MagicRequest = RateLimit.Define("auth.magic.request", RateRule.PerIp(20, TimeSpan.FromHours(1)));

    public static readonly RateLimit ResetRequest = RateLimit.Define("auth.reset.request", RateRule.PerIp(20, TimeSpan.FromHours(1)));

    /// <summary>Per address, checked in the handler; over it the request still answers 202 and nothing is sent.</summary>
    public static readonly RateLimit MagicPerEmail = RateLimit.Define("auth.magic.email", RateRule.PerSubject(3, TimeSpan.FromHours(1)));

    /// <inheritdoc cref="MagicPerEmail"/>
    public static readonly RateLimit ResetPerEmail = RateLimit.Define("auth.reset.email", RateRule.PerSubject(3, TimeSpan.FromHours(1)));

    public static readonly RateLimit Consume = RateLimit.Define("auth.token.consume", RateRule.PerIp(30, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Verify = RateLimit.Define(
        "auth.verify.consume", RateRule.PerIp(30, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)));

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/api/auth/magic/request", MagicRequestAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(MagicRequest);
        app.MapPost("/api/auth/magic/preview", MagicPreviewAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Consume);
        app.MapPost("/api/auth/magic/consume", MagicConsumeAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Consume);
        app.MapPost("/api/auth/reset/request", ResetRequestAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(ResetRequest);
        app.MapPost("/api/auth/reset/consume", ResetConsumeAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Consume);
        app.MapPost("/api/auth/verify/consume", VerifyConsumeAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Verify);
    }

    /// <summary><c>ada@example.com</c> → <c>a•••@example.com</c>.</summary>
    public static string MaskEmail(string email) {
        var at = email.LastIndexOf('@');
        return at <= 0 ? "•••" : $"{char.ConvertFromUtf32(char.ConvertToUtf32(email, 0))}•••{email[at..]}";
    }

    // An unknown address gets a link too (consuming it creates the account), so known and unknown addresses do the
    // same work. Only a disabled account gets nothing.
    private static async Task<IResult> MagicRequestAsync(
        EmailRequest body,
        HttpContext context,
        UsersRepo users,
        RateLimiter limiter,
        AccountMail mail) {
        if (AuthEndpoints.NormalizeEmail(body.Email) is not { } email) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["email"] = "invalid" });
        }
        var cancellationToken = context.RequestAborted;
        if (limiter.TryAcquire(MagicPerEmail, new RateKeys(null, null, email), out _)) {
            var account = await users.FindCredentialsAsync(email, cancellationToken);
            if (account is not { Disabled: true }) {
                await mail.MagicLinkAsync(account?.Id, email, ClientIp.Of(context), cancellationToken);
            }
        }
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> MagicPreviewAsync(TokenRequest body, HttpContext context, Tokens tokens) {
        if (await tokens.PeekAsync(body.Token, TokenPurpose.MagicLink, context.RequestAborted) is not { } use) {
            return InvalidToken();
        }
        return Results.Json(new MagicPreviewResponse(MaskEmail(use.Email)), AuthJsonContext.Default.MagicPreviewResponse);
    }

    // The click on "Sign in as j•••@x.com?" (S18). A link for an address without an account creates a verified one; a
    // link to an unverified account claims it (§e takeover rule) before signing in. The rule is for proof "by someone
    // other than the current session holder": a browser already signed in to that account only verifies it, so an
    // honest user keeps their password and other sessions.
    private static async Task<IResult> MagicConsumeAsync(
        TokenRequest body,
        HttpContext context,
        Tokens tokens,
        UsersRepo users,
        AccountClaims claims,
        Sessions sessions,
        MeBuilder me,
        TimeProvider time) {
        var cancellationToken = context.RequestAborted;
        if (await tokens.ConsumeAsync(body.Token, TokenPurpose.MagicLink, null, null, cancellationToken) is not { } use) {
            return InvalidToken();
        }
        var now = time.GetUtcNow();
        var user = use.User ?? await users.FindIdAsync(use.Email, cancellationToken);
        if (user is null) {
            var created = new PrincipalId(Guid.CreateVersion7());
            user = await users.CreateAsync(new NewUser(created, use.Email, PasswordHash: null, FullName: "", EmailVerifiedAt: now, now), cancellationToken)
                ? created
                : await users.FindIdAsync(use.Email, cancellationToken);
        }
        if (user is not { } id || await users.FindCredentialsAsync(id, cancellationToken) is not { Disabled: false }) {
            return InvalidToken();
        }

        if (await context.GetCallerAsync() is { IsUser: true } caller && caller.Principal.Id == id) {
            if (!caller.EmailVerified) {
                await users.WriteAsync(
                    [
                        UsersRepo.MarkVerified(id, now),
                        UsersRepo.Audit("auth.verify", id, id.ToString(), new Dictionary<string, string> { ["proof"] = "magic_link" }, now),
                    ],
                    cancellationToken);
                sessions.EvictUser(id);
            }
        } else {
            await claims.ClaimAsync(id, OwnershipProof.MagicLink, cancellationToken);
        }
        var profile = (await users.GetProfileAsync(id, cancellationToken))!;
        var principal = new Principal(id, PrincipalKind.User, profile.DisplayName, profile.AvatarBlobId, profile.IsAdmin);
        var session = await sessions.SignInAsync(context, principal, profile.EmailVerified, AuthMethod.MagicLink, cancellationToken);
        await users.WriteAsync(
            [UsersRepo.Audit("auth.login", id, id.ToString(), new Dictionary<string, string> {
                ["method"] = "magic_link",
                ["ip"] = ClientIp.AuditPrefix(ClientIp.Of(context)),
            }, now)],
            cancellationToken);
        var response = await me.BuildAsync(session.ToCaller(), cancellationToken);
        return Results.Json(response, AuthJsonContext.Default.MeResponse);
    }

    private static async Task<IResult> ResetRequestAsync(
        EmailRequest body,
        HttpContext context,
        UsersRepo users,
        RateLimiter limiter,
        AccountMail mail) {
        if (AuthEndpoints.NormalizeEmail(body.Email) is not { } email) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["email"] = "invalid" });
        }
        var cancellationToken = context.RequestAborted;
        if (limiter.TryAcquire(ResetPerEmail, new RateKeys(null, null, email), out _)
            && await users.FindCredentialsAsync(email, cancellationToken) is { Disabled: false } account) {
            await mail.PasswordResetAsync(account.Id, email, ClientIp.Of(context), cancellationToken);
        }
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    // Proves mailbox control, so it also verifies the email. Every session is revoked: whoever knew the old password
    // is out. The client signs in with the new password next. A disabled account is refused like a dead link and its
    // token stays unconsumed; the check is inside the consuming transaction, so a disable cannot slip in between.
    private static async Task<IResult> ResetConsumeAsync(
        ResetConsumeRequest body,
        HttpContext context,
        Tokens tokens,
        Sessions sessions,
        HashGate gate,
        TimeProvider time) {
        if (PasswordRules.Check(body.Password) is { } problem) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["password"] = problem });
        }
        var cancellationToken = context.RequestAborted;
        if (await tokens.PeekAsync(body.Token, TokenPurpose.PasswordReset, cancellationToken) is null) {
            return InvalidToken();
        }
        var password = body.Password!;
        var hashed = await gate.RunAsync(() => Bcrypt.Hash(password), cancellationToken);
        if (!hashed.Admitted) {
            return HashGate.BusyResult();
        }

        var now = time.GetUtcNow();
        var revoked = new List<byte[]>();
        var use = await tokens.ConsumeAsync(body.Token, TokenPurpose.PasswordReset, null, async (use, connection, transaction) => {
            var user = use.User!.Value;
            await using var batch = new NpgsqlBatch(connection, transaction) {
                BatchCommands = {
                    new("""
                        update users set password_hash = $2, email_verified_at = coalesce(email_verified_at, $3), updated_at = $3
                        where id = $1 and disabled_at is null
                        """) {
                        Parameters = { new() { Value = user.Value }, new() { Value = hashed.Value }, new() { Value = now } },
                    },
                    UsersRepo.Audit("auth.reset", user, user.ToString(), new Dictionary<string, string> {
                        ["ip"] = ClientIp.AuditPrefix(ClientIp.Of(context)),
                    }, now),
                    SessionsRepo.DeleteAllOf(user),
                },
            };
            await using (var reader = await batch.ExecuteReaderAsync(cancellationToken)) {
                while (await reader.ReadAsync(cancellationToken)) {
                    revoked.Add(reader.GetFieldValue<byte[]>(0));
                }
            }
            return batch.BatchCommands[0].RecordsAffected == 1;
        }, cancellationToken);
        if (use is null) {
            return InvalidToken();
        }
        sessions.Revoked(revoked);
        sessions.EvictUser(use.User!.Value);
        Sessions.ClearCookie(context.Response);
        return Results.NoContent();
    }

    // Verifies only for a session of the token's own user (S4): a link opened in someone else's signed-in browser
    // must not verify that person's account, and is left unconsumed.
    private static async Task<IResult> VerifyConsumeAsync(
        TokenRequest body,
        HttpContext context,
        Tokens tokens,
        Sessions sessions,
        TimeProvider time) {
        var caller = (await context.GetCallerAsync())!;
        var cancellationToken = context.RequestAborted;
        var now = time.GetUtcNow();
        var user = caller.Principal.Id;
        var use = await tokens.ConsumeAsync(body.Token, TokenPurpose.VerifyEmail, user, async (_, connection, transaction) => {
            await using var batch = new NpgsqlBatch(connection, transaction) {
                BatchCommands = {
                    UsersRepo.MarkVerified(user, now),
                    UsersRepo.Audit("auth.verify", user, user.ToString(), new Dictionary<string, string>(), now),
                },
            };
            await batch.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
        if (use is null) {
            return await tokens.PeekAsync(body.Token, TokenPurpose.VerifyEmail, cancellationToken) is null
                ? InvalidToken()
                : Problem.Result(StatusCodes.Status403Forbidden, "other_account");
        }
        sessions.EvictUser(user);
        return Results.NoContent();
    }

    private static IResult InvalidToken() => Problem.Result(StatusCodes.Status400BadRequest, "invalid_token");
}
