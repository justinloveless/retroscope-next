using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignupRequest(string? Email, string? Password, string? FullName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(string? Email, string? Password);

/// <summary>Audit values derived from secrets the log must not hold (S22).</summary>
public sealed class AuthAudit(Keyring keyring) {
    public string EmailHmac(string email) =>
        Convert.ToHexStringLower(keyring.Mac(KeyPurpose.AuditEmail, Encoding.UTF8.GetBytes(email)));
}

public static class AuthEndpoints {
    public const int MaxEmailLength = 254;

    public static readonly RateLimit Signup = RateLimit.Define("auth.signup", RateRule.PerIp(5, TimeSpan.FromHours(1)));

    public static readonly RateLimit Login = RateLimit.Define("auth.login", RateRule.PerIp(10, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Logout = RateLimit.Define(
        "auth.logout", RateRule.PerIp(60, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)));

    // One statement for both outcomes: it creates the principal and the user when the address is free and returns the
    // owner's id when it is taken. Both outcomes write an audit row, so both commit a write and wait for the same WAL
    // flush: a read-only taken path answered measurably faster (no account oracle).
    private const string SignupSql = """
        with taken as (
          select id from users where email = $2
        ), principal as (
          insert into principals (id, kind, created_at)
          select $1, 'user', $5 where not exists (select 1 from taken)
          returning id
        ), created as (
          insert into users (id, email, email_verified_at, password_hash, full_name, mentions_seen_at, created_at, updated_at)
          select id, $2, null, $3, $4, $5, $5, $5 from principal
          returning id
        ), audited as (
          insert into audit_log (at, actor_id, action, target, meta)
          select $5, (select id from created),
                 case when exists (select 1 from created) then 'auth.signup' else 'auth.signup.taken' end,
                 coalesce((select id from created), (select id from taken))::text,
                 jsonb_build_object('email_hmac', $6::text, 'ip', $7::text)
        )
        select (select id from created), (select id from taken)
        """;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(_ => Keyring.Parse(
            configuration["APP_KEYS"] ?? throw new InvalidOperationException("APP_KEYS is not set.")));
        services.AddSingleton<AuthAudit>();
        services.AddSingleton<UsersRepo>();
        services.AddSingleton<SessionsRepo>();
        services.AddSingleton<Sessions>();
        services.AddSingleton<AccountClaims>();
        services.AddSingleton(_ => new HashGate());
        services.AddSingleton<LoginThrottle>();
        services.TryAddSingleton<IAccountMail, NoAccountMail>();
        services.AddSingleton<ICallerResolver, AuthCallerResolver>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/api/auth/signup", SignupAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Signup);
        app.MapPost("/api/auth/login", LoginAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Login);
        app.MapPost("/api/auth/logout", LogoutAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Logout);
        app.MapPost("/api/auth/logout-all", LogoutAllAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Logout);
    }

    /// <summary>Lower-cased and trimmed, or null when it cannot be an address.</summary>
    public static string? NormalizeEmail(string? email) {
        if (email is null) {
            return null;
        }
        var normalized = UsersRepo.NormalizeEmail(email);
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        var valid = normalized.Length is >= 3 and <= MaxEmailLength
            && at > 0
            && at == normalized.LastIndexOf('@')
            && at < normalized.Length - 1
            && !normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        return valid ? normalized : null;
    }

    // Always 202 (no account oracle): an existing address gets a "you already have an account" mail instead. The
    // password is hashed and the signup statement runs either way, so both paths cost the same.
    private static async Task<IResult> SignupAsync(
        SignupRequest body,
        HttpContext context,
        NpgsqlDataSource dataSource,
        UsersRepo users,
        HashGate gate,
        IAccountMail mail,
        AuthAudit audit,
        TimeProvider time) {
        var fields = new Dictionary<string, string>();
        var email = NormalizeEmail(body.Email);
        if (email is null) {
            fields["email"] = "invalid";
        }
        if (PasswordRules.Check(body.Password) is { } passwordProblem) {
            fields["password"] = passwordProblem;
        }
        var fullName = body.FullName?.Trim() ?? "";
        if (fullName.Length is 0 or > MeEndpoints.MaxFullNameLength || !GuestNames.IsPlainText(fullName)) {
            fields["fullName"] = fullName.Length == 0 ? "required" : "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }

        var cancellationToken = context.RequestAborted;
        var hashed = await gate.RunAsync(() => Bcrypt.Hash(body.Password!), cancellationToken);
        if (!hashed.Admitted) {
            return HashGate.BusyResult();
        }
        var ip = ClientIp.Of(context);
        var id = new PrincipalId(Guid.CreateVersion7());
        var signup = new SignupRow(
            id,
            email!,
            hashed.Value,
            fullName,
            time.GetUtcNow(),
            audit.EmailHmac(email!),
            ClientIp.AuditPrefix(ip));
        var (created, existing) = await CreateOrFindAsync(dataSource, users, signup, cancellationToken);
        if (created) {
            await mail.SignedUpAsync(id, email!, ip, cancellationToken);
        } else if (existing is { } owner) {
            await mail.AlreadyRegisteredAsync(owner, email!, ip, cancellationToken);
        }
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    /// <returns>Whether the user was created, else the id of the account that holds the address.</returns>
    private static async Task<(bool Created, PrincipalId? Existing)> CreateOrFindAsync(
        NpgsqlDataSource dataSource,
        UsersRepo users,
        SignupRow signup,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(SignupSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = signup.Id.Value });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = signup.Email });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = signup.PasswordHash });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = signup.FullName });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = signup.Now });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = signup.EmailHmac });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = signup.IpPrefix });
        try {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.IsDBNull(0)) {
                return (true, null);
            }
            return (false, reader.IsDBNull(1) ? null : new PrincipalId(reader.GetGuid(1)));
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "users_email_uq") {
            // A concurrent signup took the address between the lookup and the insert; only that race pays the lookup.
            return (false, await users.FindIdAsync(signup.Email, cancellationToken));
        }
    }

    // §e: the password is compared as raw UTF-8 with no policy (imported 6-character passwords work) and is never
    // rehashed. Unknown, disabled and password-less accounts pay for a dummy verify so timing reveals nothing.
    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        HttpContext context,
        UsersRepo users,
        Sessions sessions,
        HashGate gate,
        LoginThrottle throttle,
        IAccountMail mail,
        AuthAudit audit,
        MeBuilder me,
        TimeProvider time) {
        var account = NormalizeEmail(body.Email);
        if (account is null || string.IsNullOrEmpty(body.Password)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid");
        }
        var cancellationToken = context.RequestAborted;
        var ip = ClientIp.Of(context);
        var ipPrefix = ClientIp.AuditPrefix(ip);
        if (throttle.RetryAfter(account, ipPrefix) is { } wait) {
            return RateLimits.LimitedResult(wait);
        }

        var credentials = await users.FindCredentialsAsync(account, cancellationToken);
        var password = body.Password;
        var verified = await gate.RunAsync(
            () => credentials is { Disabled: false, PasswordHash: { } hash } ? Bcrypt.Verify(password, hash) : Bcrypt.DummyVerify(password),
            cancellationToken);
        if (!verified.Admitted) {
            return HashGate.BusyResult();
        }

        var now = time.GetUtcNow();
        if (!verified.Value) {
            var capped = throttle.RecordFailure(account, ipPrefix);
            await users.WriteAsync(
                [UsersRepo.Audit("auth.login.fail", null, null, new Dictionary<string, string> {
                    ["email_hmac"] = audit.EmailHmac(account),
                    ["ip"] = ipPrefix,
                }, now)],
                cancellationToken);
            if (capped && credentials is not null) {
                await mail.LoginCappedAsync(credentials.Id, account, ip, cancellationToken);
            }
            return Problem.Result(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        throttle.RecordSuccess(account, ipPrefix);
        var profile = await users.GetProfileAsync(credentials!.Id, cancellationToken);
        var principal = new Principal(profile!.Id, PrincipalKind.User, profile.DisplayName, profile.AvatarBlobId, profile.IsAdmin);
        var session = await sessions.SignInAsync(context, principal, profile.EmailVerified, AuthMethod.Password, cancellationToken);
        await users.WriteAsync(
            [UsersRepo.Audit("auth.login", principal.Id, principal.Id.ToString(), new Dictionary<string, string> {
                ["method"] = "password",
                ["ip"] = ipPrefix,
            }, now)],
            cancellationToken);
        var response = await me.BuildAsync(session.ToCaller(), cancellationToken);
        return Results.Json(response, AuthJsonContext.Default.MeResponse);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, Sessions sessions) {
        if (await sessions.CurrentAsync(context) is { } session) {
            await sessions.RevokeAsync(session.User.Id, [session.IdHash], context.RequestAborted);
        }
        Sessions.ClearCookie(context.Response);
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAllAsync(HttpContext context, Sessions sessions) {
        var caller = (await context.GetCallerAsync())!;
        await sessions.RevokeAllAsync(caller.Principal.Id, keep: null, context.RequestAborted);
        Sessions.ClearCookie(context.Response);
        return Results.NoContent();
    }

    private sealed record SignupRow(
        PrincipalId Id,
        string Email,
        string PasswordHash,
        string FullName,
        DateTimeOffset Now,
        string EmailHmac,
        string IpPrefix);
}
