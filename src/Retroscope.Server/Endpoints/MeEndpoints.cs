using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

public sealed record MePrincipal(PrincipalId Id, PrincipalKind Kind, string Name, BlobId? Avatar) {
    public static MePrincipal Of(Principal principal) => new(principal.Id, principal.Kind, principal.DisplayName, principal.AvatarBlobId);
}

public sealed record MeUser(
    string Email,
    bool EmailVerified,
    string FullName,
    string? Nickname,
    string Theme,
    bool IsAdmin,
    bool HasPassword);

/// <param name="OpenActions">The team's open action items, for the header's Action items tab.</param>
public sealed record MeTeam(TeamId Id, string Name, TeamRole Role, int OpenActions);

/// <param name="Teams">By name, ignoring case.</param>
/// <param name="LastTeamId">The team visited last among <paramref name="Teams"/>; where a signed-in <c>/</c> leads.</param>
public sealed record MeResponse(
    MePrincipal Principal,
    MeUser User,
    IReadOnlyList<MeTeam> Teams,
    int UnreadMentions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TeamId? LastTeamId);

/// <param name="Nickname">An empty string clears it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileRequest(string? FullName, string? Nickname, string? Theme);

/// <param name="Current">Required when the account already has a password.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PasswordRequest(string? Current, string? New);

public sealed record SessionInfo(
    Guid Id,
    AuthMethod Method,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? UserAgent,
    bool Current);

/// <summary>Builds <c>/api/me</c>, which is also the boot JSON's <c>me</c>.</summary>
public sealed class MeBuilder(UsersRepo users) {
    /// <returns>Null for a caller that is not a signed-in user, or whose account is gone.</returns>
    public async Task<MeResponse?> BuildAsync(Caller caller, CancellationToken cancellationToken) {
        if (!caller.IsUser) {
            return null;
        }
        var id = caller.Principal.Id;
        if (await users.GetProfileAsync(id, cancellationToken) is not { } profile) {
            return null;
        }
        var teams = await users.TeamsAsync(id, cancellationToken);
        var unread = await users.UnreadMentionsAsync(id, cancellationToken);
        return new MeResponse(
            new MePrincipal(id, PrincipalKind.User, profile.DisplayName, profile.AvatarBlobId),
            new MeUser(profile.Email, profile.EmailVerified, profile.FullName, profile.Nickname, profile.Theme, profile.IsAdmin, profile.HasPassword),
            [.. teams.Teams.Select(x => new MeTeam(x.Id, x.Name, x.Role, x.OpenActions))],
            unread,
            teams.LastTeamId);
    }
}

/// <summary>Adds <c>me</c> (null when signed out) and the user's theme to the shell's boot JSON.</summary>
public sealed class MeBootContributor(MeBuilder builder) : IBootContributor {
    public async ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
        var caller = await boot.Http.GetCallerAsync();
        var me = caller is null ? null : await builder.BuildAsync(caller, cancellationToken);
        boot.Set("me", JsonSerializer.SerializeToElement(me, AuthJsonContext.Default.MeResponse));
        if (me is not null) {
            boot.Theme = me.User.Theme;
        }
    }
}

/// <summary>
/// A signed-in <c>/</c> leads to the team the user visited last, else their first team by name, else the
/// create-or-join screen at <c>/teams</c> (UX18 decision 8). It cannot see a pending invite, which waits in the
/// browser: the client lets that win on in-app navigation, and sign-in and the create-or-join screen resume it.
/// </summary>
public sealed class HomeRedirect(UsersRepo users) : IShellRedirect {
    public async ValueTask<string?> RedirectAsync(ShellRoute route, Caller caller, CancellationToken cancellationToken) {
        if (route.Pattern != "/") {
            return null;
        }
        return await users.HomeTeamAsync(caller.Principal.Id, cancellationToken) is { } team ? $"/teams/{team.Value}" : "/teams";
    }
}

public static class MeEndpoints {
    public const int MaxFullNameLength = 100;
    public const int MaxNicknameLength = 40;

    /// <summary>Adding a first password needs a session this young from an OIDC or magic-link sign-in (S17).</summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(10);

    public static readonly RateLimit Read = RateLimit.Define(
        "me.read", RateRule.PerIp(600, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(300, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Write = RateLimit.Define(
        "me.write", RateRule.PerIp(120, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(60, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit PasswordChange = RateLimit.Define(
        "me.password", RateRule.PerIp(30, TimeSpan.FromHours(1)), RateRule.PerPrincipal(10, TimeSpan.FromHours(1)));

    private static readonly string[] Themes = ["light", "dark", "system"];

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<MeBuilder>();
        services.AddSingleton<IBootContributor, MeBootContributor>();
        services.AddSingleton<IShellRedirect, HomeRedirect>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/me", GetAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Read);
        app.MapPatch("/api/me", PatchAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Write);
        app.MapPut("/api/me/password", ChangePasswordAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(PasswordChange);
        app.MapPut("/api/me/avatar", PutAvatarAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Write);
        app.MapDelete("/api/me/avatar", DeleteAvatarAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Write);
        app.MapGet("/api/me/sessions", ListSessionsAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Read);
        app.MapDelete("/api/me/sessions/{id:guid}", DeleteSessionAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Write);
    }

    private static async Task<IResult> GetAsync(HttpContext context, MeBuilder builder) {
        var caller = await context.GetCallerAsync();
        var me = caller is null ? null : await builder.BuildAsync(caller, context.RequestAborted);
        return me is null
            ? Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated")
            : Results.Json(me, AuthJsonContext.Default.MeResponse);
    }

    private static async Task<IResult> PatchAsync(
        ProfileRequest body,
        HttpContext context,
        UsersRepo users,
        Sessions sessions,
        MeBuilder builder,
        TimeProvider time) {
        var fields = new Dictionary<string, string>();
        var fullName = body.FullName?.Trim();
        if (fullName is not null && (fullName.Length is 0 or > MaxFullNameLength || !TextRules.IsPlainText(fullName))) {
            fields["fullName"] = fullName.Length == 0 ? "required" : "invalid";
        }
        var nickname = body.Nickname?.Trim();
        if (nickname is { Length: > 0 } && (nickname.Length > MaxNicknameLength || !TextRules.IsPlainText(nickname))) {
            fields["nickname"] = "invalid";
        }
        if (body.Theme is not null && !Themes.Contains(body.Theme, StringComparer.Ordinal)) {
            fields["theme"] = "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }

        var caller = (await context.GetCallerAsync())!;
        var updated = await users.UpdateProfileAsync(
            caller.Principal.Id, new ProfilePatch(fullName, nickname, body.Theme), time.GetUtcNow(), context.RequestAborted);
        if (updated is null) {
            return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        sessions.EvictUser(caller.Principal.Id);
        var me = await builder.BuildAsync(caller, context.RequestAborted);
        return Results.Json(me, AuthJsonContext.Default.MeResponse);
    }

    // §d: `current` is required when a password exists; a first password needs a fresh OIDC or magic-link session
    // (S17). Every session is revoked and this browser gets a new token.
    private static async Task<IResult> ChangePasswordAsync(
        PasswordRequest body,
        HttpContext context,
        UsersRepo users,
        Sessions sessions,
        HashGate gate,
        TimeProvider time) {
        if (PasswordRules.Check(body.New) is { } problem) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["new"] = problem });
        }
        var cancellationToken = context.RequestAborted;
        var caller = (await context.GetCallerAsync())!;
        var session = await sessions.CurrentAsync(context);
        var credentials = await users.FindCredentialsAsync(caller.Principal.Id, cancellationToken);
        if (session is null || credentials is null) {
            return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
        }

        var now = time.GetUtcNow();
        if (credentials.PasswordHash is null) {
            if (session.Method == AuthMethod.Password || now - session.CreatedAt > ReauthWindow) {
                return Problem.Result(StatusCodes.Status403Forbidden, "reauth_required");
            }
        } else if (string.IsNullOrEmpty(body.Current)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["current"] = "required" });
        }

        var gated = await gate.RunAsync(
            () => credentials.PasswordHash is not { } hash || Bcrypt.Verify(body.Current!, hash) ? Bcrypt.Hash(body.New!) : null,
            cancellationToken);
        if (!gated.Admitted) {
            return HashGate.BusyResult();
        }
        if (gated.Value is not { } newHash) {
            return Problem.Result(StatusCodes.Status403Forbidden, "wrong_password");
        }

        var action = credentials.PasswordHash is null ? "auth.password.set" : "auth.password.change";
        await users.WriteAsync(
            [
                UsersRepo.SetPasswordHash(caller.Principal.Id, newHash, now),
                UsersRepo.Audit(action, caller.Principal.Id, caller.Principal.Id.ToString(), new Dictionary<string, string>(), now),
            ],
            cancellationToken);
        await sessions.RevokeAllAsync(caller.Principal.Id, keep: null, cancellationToken);
        await sessions.SignInAsync(context, caller.Principal, caller.EmailVerified, session.Method, cancellationToken);
        return Results.NoContent();
    }

    // The body is the raw image (the client crops it to 128 px); its type comes from its magic bytes, never the header.
    private static async Task<IResult> PutAvatarAsync(
        HttpContext context,
        UsersRepo users,
        Sessions sessions,
        TimeProvider time) {
        var cancellationToken = context.RequestAborted;
        var body = await ReadBodyAsync(context.Request, BlobsRepo.AvatarMaxBytes, cancellationToken);
        if (body is null) {
            return Problem.Result(StatusCodes.Status413PayloadTooLarge, "too_large");
        }
        if (!SniffedImage.TryCreate(body, out var image)) {
            return Problem.Result(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
        }
        var user = (await context.GetCallerAsync())!.Principal.Id;
        if (!await users.SetAvatarAsync(user, image, onlyIfNone: false, time.GetUtcNow(), cancellationToken)) {
            return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        sessions.EvictUser(user);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAvatarAsync(HttpContext context, UsersRepo users, Sessions sessions, TimeProvider time) {
        var user = (await context.GetCallerAsync())!.Principal.Id;
        if (!await users.ClearAvatarAsync(user, time.GetUtcNow(), context.RequestAborted)) {
            return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        sessions.EvictUser(user);
        return Results.NoContent();
    }

    /// <returns>Null when the body is longer than <paramref name="max"/>.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int max, CancellationToken cancellationToken) {
        if (request.ContentLength > max) {
            return null;
        }
        var buffer = new byte[max + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0) {
            total += read;
        }
        return total > max ? null : buffer[..total];
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext context, SessionsRepo repo, Sessions sessions) {
        var caller = (await context.GetCallerAsync())!;
        var current = caller.AuthSessionId;
        var rows = await repo.ListAsync(caller.Principal.Id, context.RequestAborted);
        IReadOnlyList<SessionInfo> list = [
            .. rows.Select(x => {
                var id = Sessions.PublicId(x.IdHash);
                return new SessionInfo(id, x.Method, x.CreatedAt, x.LastSeenAt, x.UserAgent, id == current);
            }),
        ];
        return Results.Json(list, AuthJsonContext.Default.IReadOnlyListSessionInfo);
    }

    private static async Task<IResult> DeleteSessionAsync(Guid id, HttpContext context, SessionsRepo repo, Sessions sessions) {
        var caller = (await context.GetCallerAsync())!;
        var rows = await repo.ListAsync(caller.Principal.Id, context.RequestAborted);
        var target = rows.FirstOrDefault(x => Sessions.PublicId(x.IdHash) == id);
        if (target is null || await sessions.RevokeAsync(caller.Principal.Id, [target.IdHash], context.RequestAborted) == 0) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (id == caller.AuthSessionId) {
            Sessions.ClearCookie(context.Response);
        }
        return Results.NoContent();
    }
}
