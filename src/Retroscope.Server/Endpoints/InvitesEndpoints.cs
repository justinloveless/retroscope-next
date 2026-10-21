using System.Text;
using System.Text.Json.Serialization;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Core.Team;
using Retroscope.Server.Auth.Email;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Endpoints;

/// <param name="Email">Email invites, masked.</param>
/// <param name="Url">Link invites: the link to share, opened from the sealed token.</param>
public sealed record InviteInfo(Guid Id, InviteKind Kind, TeamRole Role, long ExpiresAt, long CreatedAt, string? Email = null, string? Url = null);

public sealed record InvitesPage(
    IReadOnlyList<InviteInfo> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

/// <param name="Role">Email invites only; link invites always make members (S19).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InviteRequest(InviteKind? Kind, string? Email = null, TeamRole? Role = null, int? ExpiresInDays = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InviteTokenRequest(string? Token);

public sealed record InvitePreview(string TeamName, InviteKind Kind, bool Expired);

public sealed record InviteAccepted(TeamId TeamId);

public static class InvitesEndpoints {
    public static readonly TimeSpan EmailInviteLifetime = TimeSpan.FromDays(7);
    public const int MaxLinkDays = 30;
    public const int DefaultLinkDays = 7;

    // Imported invite tokens are the original's strings, not 43-character base64url ones; this only bounds the input.
    private const int MaxTokenLength = 200;

    public static readonly RateLimit Read = TeamsEndpoints.Read;

    public static readonly RateLimit Write = TeamsEndpoints.Write;

    public static readonly RateLimit Create = RateLimit.Define(
        "invites.create", RateRule.PerIp(60, TimeSpan.FromHours(1)), RateRule.PerPrincipal(30, TimeSpan.FromHours(1)));

    public static readonly RateLimit Preview = RateLimit.Define("invites.preview", RateRule.PerIp(30, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Accept = RateLimit.Define(
        "invites.accept", RateRule.PerIp(30, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(10, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<InvitesRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/teams/{id:guid}/invites", ListAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(Read);
        app.MapPost("/api/teams/{id:guid}/invites", CreateAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(Create);
        app.MapDelete("/api/teams/{id:guid}/invites/{inviteId:guid}", RevokeAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(Write);
        app.MapPost("/api/invites/preview", PreviewAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Preview);
        app.MapPost("/api/invites/accept", AcceptAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Accept);
    }

    /// <summary>Where an invite token leads: <c>PUBLIC_URL/invite#t=…</c>, so the token never reaches a server log.</summary>
    public static string Link(HostingOptions hosting, string token) => $"{hosting.PublicOrigin}/invite#t={token}";

    private static async Task<IResult> ListAsync(
        Guid id,
        HttpContext context,
        TeamAccess access,
        InvitesRepo invites,
        Keyring keyring,
        HostingOptions hosting,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageMembers);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var rows = await invites.LiveAsync(check.TeamId, time.GetUtcNow(), context.RequestAborted);
        var page = new InvitesPage([.. rows.Select(x => Info(x, LinkOf(x, keyring, hosting)))], null);
        return Results.Json(page, TeamHttpJsonContext.Default.InvitesPage);
    }

    /// <summary>
    /// An email invite needs a verified sender (S8) and, to grant admin, the owner (role changes are owner-only). The
    /// invite row and its mail commit together; over the email budget the invite is still created and the mail is
    /// dropped, as every mail-sending route does.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid id,
        InviteRequest body,
        HttpContext context,
        TeamAccess access,
        TeamsRepo teams,
        Keyring keyring,
        HostingOptions hosting,
        EmailBudget budget,
        Outbox outbox,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageMembers);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var now = time.GetUtcNow();
        var inviteId = Guid.CreateVersion7(now);
        var token = OpaqueToken.New();
        InviteRow invite;
        OutboxMail? mail = null;
        switch (body.Kind) {
            case InviteKind.Email: {
                if (body.ExpiresInDays is not null) {
                    return Invalid("expiresInDays");
                }
                if (AuthEndpoints.NormalizeEmail(body.Email) is not { } email) {
                    return Invalid("email");
                }
                var role = body.Role ?? TeamRole.Member;
                if (role == TeamRole.Owner) {
                    return Invalid("role");
                }
                if (!check.Caller!.EmailVerified) {
                    return Problem.Result(StatusCodes.Status403Forbidden, "email_unverified");
                }
                if (role == TeamRole.Admin && !access.Can(check.Caller, check.TeamId, Capability.TeamManageAdmins)) {
                    return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
                }
                invite = new InviteRow(inviteId, check.TeamId, InviteKind.Email, email, role, null, now, now + EmailInviteLifetime);
                if (budget.TryReserve(email, ClientIp.Of(context), check.Who)) {
                    mail = Templates.TeamInvite(check.Team!.Name, Link(hosting, token)).To(email);
                }
                break;
            }
            case InviteKind.Link: {
                if (body.Email is not null) {
                    return Invalid("email");
                }
                if (body.Role is not (null or TeamRole.Member)) {
                    return Invalid("role");
                }
                var days = body.ExpiresInDays ?? DefaultLinkDays;
                if (days is < 1 or > MaxLinkDays) {
                    return Invalid("expiresInDays");
                }
                var box = keyring.Seal(KeyPurpose.InviteBox, Encoding.UTF8.GetBytes(token), InvitesRepo.AssociatedData(inviteId));
                invite = new InviteRow(inviteId, check.TeamId, InviteKind.Link, null, TeamRole.Member, box, now, now.AddDays(days));
                break;
            }
            default:
                return Invalid("kind");
        }

        var meta = new Dictionary<string, string> {
            ["kind"] = InvitesRepo.KindName(invite.Kind),
            ["role"] = MembershipCache.RoleName(invite.Role),
        };
        await teams.WriteAsync(
            [
                InvitesRepo.Insert(invite, TokenHash.Of(token), check.Who),
                TeamsRepo.Audit("team.invite.create", check.Who, check.TeamId, inviteId.ToString(), meta, now),
                .. mail is null ? [] : new[] { Outbox.Insert(mail, now) },
            ],
            context.RequestAborted);
        if (mail is not null) {
            outbox.Kick();
        }
        var url = invite.Kind == InviteKind.Link ? Link(hosting, token) : null;
        return Results.Json(Info(invite, url), TeamHttpJsonContext.Default.InviteInfo, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        Guid inviteId,
        HttpContext context,
        TeamAccess access,
        InvitesRepo invites,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageMembers);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        return await invites.RevokeAsync(check.TeamId, inviteId, check.Who, time.GetUtcNow(), context.RequestAborted)
            ? Results.NoContent()
            : TeamAccess.NotFound();
    }

    /// <summary>What the invite page shows before anyone signs in: the team's name and whether the invite expired.</summary>
    private static async Task<IResult> PreviewAsync(InviteTokenRequest body, HttpContext context, InvitesRepo invites, TimeProvider time) {
        if (await FindAsync(body.Token, invites, context.RequestAborted) is not { } found) {
            return TeamAccess.NotFound();
        }
        if (Dead(found) is { } dead) {
            return dead;
        }
        var preview = new InvitePreview(found.TeamName, found.Invite.Kind, found.Invite.ExpiresAt <= time.GetUtcNow());
        return Results.Json(preview, TeamHttpJsonContext.Default.InvitePreview);
    }

    /// <summary>
    /// Joins the team through its actor (R24). An email invite binds to its address: the caller's email must be that
    /// address and verified, and the invite is used once. A link invite is reusable until it expires or is revoked.
    /// Accepting while already a member changes nothing, so a member link never demotes an admin. Replaying a used email
    /// invite only answers its team to a caller who is still a member: it never re-admits someone since removed.
    /// </summary>
    private static async Task<IResult> AcceptAsync(
        InviteTokenRequest body,
        HttpContext context,
        InvitesRepo invites,
        UsersRepo users,
        TeamsRepo teams,
        TeamActors actors,
        MembershipCache memberships,
        TimeProvider time) {
        var cancellationToken = context.RequestAborted;
        if (await FindAsync(body.Token, invites, cancellationToken) is not { } found) {
            return TeamAccess.NotFound();
        }
        var caller = (await context.GetCallerAsync())!;
        var user = caller.Principal.Id;
        var invite = found.Invite;
        var now = time.GetUtcNow();
        if (invite.Kind == InviteKind.Email && found.AcceptedBy == user && !found.TeamDeleted) {
            await memberships.LoadAsync(invite.Team, cancellationToken);
            return memberships.RoleIn(invite.Team, user) is null
                ? TeamAccess.NotFound()
                : Results.Json(new InviteAccepted(invite.Team), TeamHttpJsonContext.Default.InviteAccepted);
        }
        if (Dead(found) is { } dead) {
            return dead;
        }
        if (invite.ExpiresAt <= now) {
            return Problem.Result(StatusCodes.Status410Gone, "expired");
        }

        var claimed = false;
        if (invite.Kind == InviteKind.Email) {
            if (await users.GetProfileAsync(user, cancellationToken) is not { } profile) {
                return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
            }
            if (!profile.EmailVerified) {
                return Problem.Result(StatusCodes.Status403Forbidden, "email_unverified");
            }
            if (!string.Equals(profile.Email, invite.Email, StringComparison.Ordinal)) {
                return Problem.Result(StatusCodes.Status403Forbidden, "email_mismatch");
            }
            if (!await invites.ClaimEmailInviteAsync(invite.Id, user, now, cancellationToken)) {
                return TeamAccess.NotFound();
            }
            claimed = true;
        }

        if (await teams.MemberCardAsync(user, cancellationToken) is not { } card) {
            return Problem.Result(StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        var member = new MemberInfo(user, card.Name, invite.Role, card.Avatar);
        var result = await actors.AskAsync(invite.Team, new TeamInput.MemberAdded(member), cancellationToken);
        if (TeamActors.Failure(context, result) is { } failure) {
            if (claimed) {
                await invites.ReleaseEmailInviteAsync(invite.Id, user, CancellationToken.None);
            }
            return failure;
        }
        if (result.Status == AskStatus.Accepted) {
            var meta = new Dictionary<string, string> {
                ["invite"] = invite.Id.ToString(),
                ["kind"] = InvitesRepo.KindName(invite.Kind),
                ["role"] = MembershipCache.RoleName(invite.Role),
            };
            await teams.WriteAsync(
                [
                    .. invite.Kind == InviteKind.Link ? new[] { InvitesRepo.CountUse(invite.Id) } : [],
                    TeamsRepo.Audit("team.member.add", user, invite.Team, user.ToString(), meta, now),
                ],
                cancellationToken);
        }
        return Results.Json(new InviteAccepted(invite.Team), TeamHttpJsonContext.Default.InviteAccepted);
    }

    private static async Task<InviteLookup?> FindAsync(string? token, InvitesRepo invites, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) {
            return null;
        }
        return await invites.FindByTokenAsync(TokenHash.Of(token), cancellationToken);
    }

    /// <summary>Why an invite can no longer be used by anyone; null while it can (expiry aside).</summary>
    private static IResult? Dead(InviteLookup found) {
        if (found.TeamDeleted || (found.Invite.Kind == InviteKind.Email && found.AcceptedBy is not null)) {
            return TeamAccess.NotFound();
        }
        return found.Revoked ? Problem.Result(StatusCodes.Status410Gone, "revoked") : null;
    }

    private static string? LinkOf(InviteRow invite, Keyring keyring, HostingOptions hosting) {
        if (invite.TokenBox is not { } box
            || !keyring.TryOpen(KeyPurpose.InviteBox, box, InvitesRepo.AssociatedData(invite.Id), out var token)) {
            return null;
        }
        return Link(hosting, Encoding.UTF8.GetString(token));
    }

    private static InviteInfo Info(InviteRow invite, string? url) => new(
        invite.Id,
        invite.Kind,
        invite.Role,
        invite.ExpiresAt.ToUnixTimeMilliseconds(),
        invite.CreatedAt.ToUnixTimeMilliseconds(),
        invite.Email is { } email ? EmailEndpoints.MaskEmail(email) : null,
        url);

    private static IResult Invalid(string field) =>
        Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { [field] = "invalid" });
}
