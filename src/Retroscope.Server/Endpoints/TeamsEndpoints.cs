using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Stores;

namespace Retroscope.Server.Endpoints;

public sealed record TeamCreated(TeamId Id);

public sealed record TeamJiraSummary(
    bool Configured,
    string Status,
    bool MembersCanEdit,
    string? SiteHost = null,
    string? ProjectKey = null,
    long? BoardId = null,
    string? TicketPrefix = null);

public sealed record TeamCounts(int Boards, int Members);

public sealed record TeamResponse(
    TeamId Id,
    string Name,
    string? Description,
    TeamRole Role,
    IReadOnlyDictionary<string, string> PointDescriptions,
    int MaxEndorsements,
    TeamJiraSummary Jira,
    TeamCounts Counts);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTeamRequest(string? Name, string? Description);

/// <param name="Description">An empty string clears it.</param>
/// <param name="PointDescriptions">The whole map, deck values to text; empty texts are dropped.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeamPatchRequest(
    string? Name,
    string? Description,
    Dictionary<string, string>? PointDescriptions,
    int? MaxEndorsementsPerBoard);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteTeamRequest(string? ConfirmName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemberRoleRequest(TeamRole? Role);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferOwnershipRequest(PrincipalId? UserId);

/// <summary>An <see cref="ActionItemInfo"/> plus the title of the board it came from, while that board exists.</summary>
public sealed record ActionItemListItem(
    ActionItemId Id,
    string Text,
    int SourceVersion,
    long CreatedAt,
    int CommentCount,
    BoardId? SourceBoardId,
    CardId? SourceCardId,
    PrincipalId? AssignedTo,
    long? DoneAt,
    PrincipalId? DoneBy,
    PrincipalId? CreatedBy,
    string? BoardTitle);

public sealed record ActionItemsPage(
    IReadOnlyList<ActionItemListItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

/// <summary>The caller and the team a team route acts on, once <see cref="TeamAccess.CheckAsync"/> let them through.</summary>
public sealed record TeamCheck(Caller? Caller, TeamRow? Team, IResult? Refusal) {
    public PrincipalId Who => Caller!.Principal.Id;

    public TeamId TeamId => Team!.Id;
}

/// <summary>
/// Finishes a team route's authorization with <see cref="Policy.Can(Principal?, Capability, ResourceFacts, IAccessView)"/>
/// (the route policy only required a user). A missing or deleted team and a team the caller is not in answer the same
/// 404, so non-members learn nothing; a member without the capability gets 403.
/// </summary>
public sealed class TeamAccess(TeamsRepo teams, MembershipCache memberships) {
    public async Task<TeamCheck> CheckAsync(HttpContext context, Guid id, Capability capability) {
        var caller = await context.GetCallerAsync();
        var team = new TeamId(id);
        var row = await teams.FindAsync(team, context.RequestAborted);
        if (caller is null || row is null || row.Deleted) {
            return new TeamCheck(null, null, NotFound());
        }
        await memberships.LoadAsync(team, context.RequestAborted);
        if (!Can(caller, team, Capability.Subscribe)) {
            return new TeamCheck(null, null, NotFound());
        }
        if (!Can(caller, team, capability)) {
            return new TeamCheck(null, null, Problem.Result(StatusCodes.Status403Forbidden, "forbidden"));
        }
        return new TeamCheck(caller, row, null);
    }

    public bool Can(Caller caller, TeamId team, Capability capability) {
        return Policy.Can(caller.Principal, capability, Facts(team), memberships);
    }

    public TeamRole? RoleOf(TeamId team, PrincipalId principal) => memberships.RoleIn(team, principal);

    public static ResourceFacts Facts(TeamId team) => new(RoomKey.Team(team), Team: team);

    public static IResult NotFound() => Problem.Result(StatusCodes.Status404NotFound, "not_found");
}

/// <summary>
/// HTTP-originated changes to rows the team actor owns (§c.2, R24): membership goes through the actor, which writes
/// <c>team_members</c>, publishes the roster to the membership cache and fans the change out to the team's rooms.
/// </summary>
public sealed class TeamActors(ActorRegistry registry) {
    public Task<AskResult> AskAsync(TeamId team, TeamInput input, CancellationToken cancellationToken) {
        return registry.AskAsync<TeamState, TeamInput>(RoomKey.Team(team), input, null, cancellationToken);
    }

    /// <summary>
    /// Loads the team room before an HTTP write to <c>teams</c>, so the <c>SettingsChanged</c> asked afterwards finds the
    /// old values and fans the new ones out; a room loaded after the write would see nothing to change.
    /// </summary>
    public async Task WarmAsync(TeamId team, CancellationToken cancellationToken) {
        try {
            await registry.QueryAsync<TeamState, TeamInput, bool>(RoomKey.Team(team), view => ValueTask.FromResult(view.Found), cancellationToken);
        } catch (RoomUnavailableException) {
            // The write still lands; rooms that miss the fan-out read the new values when they next load.
        }
    }

    /// <summary>Kicks everyone from the team's rooms after a soft delete.</summary>
    public void TeamGone(TeamRooms rooms) {
        foreach (var session in rooms.Sessions) {
            registry.AccessChanged(RoomKey.Poker(session));
        }
        foreach (var board in rooms.Boards) {
            registry.AccessChanged(RoomKey.Board(board));
        }
    }

    /// <summary>The response for an ask that did not go through; null when it was accepted or changed nothing.</summary>
    public static IResult? Failure(HttpContext context, AskResult result) {
        switch (result.Status) {
            case AskStatus.Accepted or AskStatus.NoOp:
                return null;
            case AskStatus.NotFound:
                return TeamAccess.NotFound();
            case AskStatus.Unavailable:
                context.Response.Headers.RetryAfter = "1";
                return Problem.Result(StatusCodes.Status503ServiceUnavailable, "unavailable");
            default:
                return Problem.Result(StatusCodes.Status409Conflict, "conflict");
        }
    }
}

public static class TeamsEndpoints {
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;
    public const int MaxPointDescriptionLength = 100;
    public const int MaxEndorsementsPerBoard = 50;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;

    public static readonly RateLimit Read = RateLimit.Define(
        "teams.read", RateRule.PerIp(600, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(300, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Write = RateLimit.Define(
        "teams.write", RateRule.PerIp(120, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(60, TimeSpan.FromMinutes(1)));

    /// <summary>§d: 10 teams a day per user.</summary>
    public static readonly RateLimit Create = RateLimit.Define(
        "teams.create", RateRule.PerIp(30, TimeSpan.FromDays(1)), RateRule.PerPrincipal(10, TimeSpan.FromDays(1)));

    private static readonly string[] ItemStatuses = ["open", "done", "all"];

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<TeamsRepo>();
        services.AddSingleton<ActivityRepo>();
        services.AddSingleton<IRoomVisits>(sp => sp.GetRequiredService<ActivityRepo>());
        services.AddSingleton<TeamAccess>();
        services.AddSingleton<TeamActors>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/api/teams", CreateAsync)
            .RequireRoutePolicy(RoutePolicy.VerifiedUser)
            .RequireRateLimit(Create);
        app.MapGet("/api/teams/{id:guid}", GetAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Read);
        app.MapPatch("/api/teams/{id:guid}", PatchAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(Write);
        app.MapDelete("/api/teams/{id:guid}", DeleteAsync)
            .RequireRoutePolicy(RoutePolicy.TeamOwner)
            .RequireRateLimit(Write);
        app.MapGet("/api/teams/{id:guid}/members", MembersAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Read);
        app.MapPatch("/api/teams/{id:guid}/members/{userId:guid}", SetRoleAsync)
            .RequireRoutePolicy(RoutePolicy.TeamOwner)
            .RequireRateLimit(Write);
        // Admins remove members, the owner removes admins, and anyone but the owner may leave (§d, S19).
        app.MapDelete("/api/teams/{id:guid}/members/{userId:guid}", RemoveMemberAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Write);
        app.MapPost("/api/teams/{id:guid}/transfer-ownership", TransferAsync)
            .RequireRoutePolicy(RoutePolicy.TeamOwner)
            .RequireRateLimit(Write);
        app.MapGet("/api/teams/{id:guid}/action-items", ActionItemsAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(Read);
    }

    private static async Task<IResult> CreateAsync(
        CreateTeamRequest body,
        HttpContext context,
        TeamsRepo teams,
        MembershipCache memberships,
        TimeProvider time) {
        var fields = new Dictionary<string, string>();
        var name = TeamText.Line(body.Name, MaxNameLength, fields, "name", required: true);
        var description = TeamText.Paragraph(body.Description, MaxDescriptionLength, fields, "description");
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        var caller = (await context.GetCallerAsync())!;
        var now = time.GetUtcNow();
        var team = new TeamId(Guid.CreateVersion7(now));
        await teams.CreateAsync(team, name!, string.IsNullOrEmpty(description) ? null : description, caller.Principal.Id, now, context.RequestAborted);
        memberships.Forget(team);
        return Results.Json(new TeamCreated(team), TeamHttpJsonContext.Default.TeamCreated, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext context, TeamAccess access, TeamsRepo teams, ActivityRepo activity) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        await activity.TouchAsync(check.Who, check.TeamId, context.RequestAborted);
        return await DetailsResultAsync(check, access, teams, context.RequestAborted);
    }

    private static async Task<IResult> PatchAsync(
        Guid id,
        TeamPatchRequest body,
        HttpContext context,
        TeamAccess access,
        TeamsRepo teams,
        TeamActors actors,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var fields = new Dictionary<string, string>();
        var name = TeamText.Line(body.Name, MaxNameLength, fields, "name", required: body.Name is not null);
        var description = TeamText.Paragraph(body.Description, MaxDescriptionLength, fields, "description");
        var descriptions = PointDescriptions(body.PointDescriptions, fields);
        if (body.MaxEndorsementsPerBoard is < 0 or > MaxEndorsementsPerBoard) {
            fields["maxEndorsementsPerBoard"] = "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }

        var cancellationToken = context.RequestAborted;
        var settings = descriptions is not null || body.MaxEndorsementsPerBoard is not null;
        if (settings) {
            await actors.WarmAsync(check.TeamId, cancellationToken);
        }
        var patch = new TeamPatch(name, description, descriptions, body.MaxEndorsementsPerBoard);
        if (!await teams.UpdateAsync(check.TeamId, patch, check.Who, time.GetUtcNow(), cancellationToken)) {
            return TeamAccess.NotFound();
        }
        if (settings) {
            // The rows are written; a failed fan-out only means live rooms pick the values up when they next load.
            await actors.AskAsync(check.TeamId, new TeamInput.SettingsChanged(descriptions, body.MaxEndorsementsPerBoard), cancellationToken);
        }
        return await DetailsResultAsync(check, access, teams, cancellationToken);
    }

    /// <summary>
    /// Soft delete (§d): the owner confirms with the team's name. The team room, then every live session and board of the
    /// team, re-check their subscribers; nobody is a member of a deleted team, so they are kicked.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        [FromBody] DeleteTeamRequest body,
        HttpContext context,
        TeamAccess access,
        TeamsRepo teams,
        TeamActors actors,
        TeamStore store,
        MembershipCache memberships,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamDelete);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        if (!string.Equals(body.ConfirmName, check.Team!.Name, StringComparison.Ordinal)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["confirmName"] = "mismatch" });
        }
        var cancellationToken = context.RequestAborted;
        if (!await teams.SoftDeleteAsync(check.TeamId, check.Who, time.GetUtcNow(), cancellationToken)) {
            return TeamAccess.NotFound();
        }
        await actors.AskAsync(check.TeamId, new TeamInput.TeamDeleted(), cancellationToken);
        // Poker and board rooms do not track their team's deletion; an empty roster makes their access re-check
        // refuse every former member, and their next load sees the deleted team.
        memberships.Publish(check.TeamId, new Dictionary<PrincipalId, TeamRole>());
        actors.TeamGone(await store.RoomsAsync(check.TeamId, cancellationToken));
        return Results.NoContent();
    }

    private static async Task<IResult> MembersAsync(Guid id, HttpContext context, TeamAccess access, TeamsRepo teams) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var members = TeamProjection.Roster(await teams.MembersAsync(check.TeamId, context.RequestAborted));
        return Results.Json(members, TeamHttpJsonContext.Default.IReadOnlyListMemberInfo);
    }

    private static async Task<IResult> SetRoleAsync(
        Guid id,
        Guid userId,
        MemberRoleRequest body,
        HttpContext context,
        TeamAccess access,
        TeamActors actors,
        TeamsRepo teams,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageAdmins);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        if (body.Role is not (TeamRole.Admin or TeamRole.Member)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["role"] = "invalid" });
        }
        var target = new PrincipalId(userId);
        switch (access.RoleOf(check.TeamId, target)) {
            case null:
                return TeamAccess.NotFound();
            case TeamRole.Owner:
                return Problem.Result(StatusCodes.Status409Conflict, "owner");
        }
        var cancellationToken = context.RequestAborted;
        var result = await actors.AskAsync(check.TeamId, new TeamInput.MemberRoleSet(target, body.Role!.Value), cancellationToken);
        if (TeamActors.Failure(context, result) is { } failure) {
            return failure;
        }
        if (result.Status == AskStatus.Accepted) {
            await teams.WriteAsync(
                [
                    TeamsRepo.Audit(
                        "team.role.set",
                        check.Who,
                        check.TeamId,
                        userId.ToString(),
                        new Dictionary<string, string> { ["role"] = MembershipCache.RoleName(body.Role.Value) },
                        time.GetUtcNow()),
                ],
                cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id,
        Guid userId,
        HttpContext context,
        TeamAccess access,
        TeamActors actors,
        TeamsRepo teams,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var target = new PrincipalId(userId);
        var self = target == check.Who;
        var role = access.RoleOf(check.TeamId, target);
        if (role is null) {
            return TeamAccess.NotFound();
        }
        if (role == TeamRole.Owner) {
            return Problem.Result(StatusCodes.Status409Conflict, "owner");
        }
        var needed = role == TeamRole.Admin ? Capability.TeamManageAdmins : Capability.TeamManageMembers;
        if (!self && !access.Can(check.Caller!, check.TeamId, needed)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
        }
        var cancellationToken = context.RequestAborted;
        var result = await actors.AskAsync(check.TeamId, new TeamInput.MemberRemoved(target), cancellationToken);
        if (TeamActors.Failure(context, result) is { } failure) {
            return failure;
        }
        if (result.Status == AskStatus.Accepted) {
            await teams.WriteAsync(
                [
                    TeamsRepo.Audit(
                        self ? "team.member.leave" : "team.member.remove",
                        check.Who,
                        check.TeamId,
                        userId.ToString(),
                        new Dictionary<string, string> { ["role"] = MembershipCache.RoleName(role.Value) },
                        time.GetUtcNow()),
                ],
                cancellationToken);
        }
        return Results.NoContent();
    }

    /// <summary>
    /// The owner hands the team to another member and becomes an admin, in one team actor transaction, so no failure
    /// can leave the team without an owner.
    /// </summary>
    private static async Task<IResult> TransferAsync(
        Guid id,
        TransferOwnershipRequest body,
        HttpContext context,
        TeamAccess access,
        TeamActors actors,
        TeamsRepo teams,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageAdmins);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        if (body.UserId is not { } target || target == check.Who) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["userId"] = "invalid" });
        }
        if (access.RoleOf(check.TeamId, target) is null) {
            return TeamAccess.NotFound();
        }
        var cancellationToken = context.RequestAborted;
        var result = await actors.AskAsync(check.TeamId, new TeamInput.TransferOwnership(check.Who, target), cancellationToken);
        if (result is { Status: AskStatus.Rejected, Code: NackCode.NotFound }) {
            return TeamAccess.NotFound();
        }
        if (TeamActors.Failure(context, result) is { } failure) {
            return failure;
        }
        if (result.Status == AskStatus.Accepted) {
            await teams.WriteAsync(
                [TeamsRepo.Audit("team.owner.transfer", check.Who, check.TeamId, target.ToString(), new Dictionary<string, string>(), time.GetUtcNow())],
                cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ActionItemsAsync(
        Guid id,
        [FromQuery] string? status,
        [FromQuery] string? assignee,
        [FromQuery] string? sort,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        HttpContext context,
        TeamAccess access,
        TeamsRepo teams) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var fields = new Dictionary<string, string>();
        status ??= "open";
        if (!ItemStatuses.Contains(status, StringComparer.Ordinal)) {
            fields["status"] = "invalid";
        }
        PrincipalId? assignedTo = null;
        var unassigned = assignee == "unassigned";
        if (assignee is not null && !unassigned) {
            if (Guid.TryParse(assignee, out var person)) {
                assignedTo = new PrincipalId(person);
            } else {
                fields["assignee"] = "invalid";
            }
        }
        if (sort is not (null or "newest" or "oldest")) {
            fields["sort"] = "invalid";
        }
        if (!ListCursor.TryParse(cursor, out var after)) {
            fields["cursor"] = "invalid";
        }
        if (limit is < 1 or > MaxPageSize) {
            fields["limit"] = "invalid";
        }
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        var query = new ActionItemQuery(status, assignedTo, unassigned, sort != "oldest", after, limit ?? DefaultPageSize);
        var (items, next) = await teams.ActionItemsAsync(check.TeamId, query, context.RequestAborted);
        var page = new ActionItemsPage([.. items.Select(ToListItem)], next?.ToString());
        return Results.Json(page, TeamHttpJsonContext.Default.ActionItemsPage);
    }

    private static async Task<IResult> DetailsResultAsync(TeamCheck check, TeamAccess access, TeamsRepo teams, CancellationToken cancellationToken) {
        if (await teams.DetailsAsync(check.TeamId, cancellationToken) is not { Team.Deleted: false } details) {
            return TeamAccess.NotFound();
        }
        var role = access.RoleOf(check.TeamId, check.Who) ?? TeamRole.Member;
        var jira = details.Jira is { } config
            ? new TeamJiraSummary(true, config.Status, config.MembersCanEdit, config.SiteHost, config.ProjectKey, config.BoardId, config.TicketPrefix)
            : new TeamJiraSummary(false, "ok", true);
        var response = new TeamResponse(
            details.Team.Id,
            details.Team.Name,
            details.Team.Description,
            role,
            details.PointDescriptions,
            details.MaxEndorsements,
            jira,
            new TeamCounts(details.Boards, details.Members));
        return Results.Json(response, TeamHttpJsonContext.Default.TeamResponse);
    }

    /// <summary>Deck values only, texts trimmed and at most 100 characters; null when the map is absent.</summary>
    private static Dictionary<string, string>? PointDescriptions(Dictionary<string, string>? raw, Dictionary<string, string> fields) {
        if (raw is null) {
            return null;
        }
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in raw) {
            if (!Deck.Values.Any(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture) == key)) {
                fields["pointDescriptions"] = "invalid";
                return null;
            }
            var text = TeamText.Line(value, MaxPointDescriptionLength, fields, "pointDescriptions", required: false);
            if (!string.IsNullOrEmpty(text)) {
                descriptions[key] = text;
            }
        }
        return descriptions;
    }

    private static ActionItemListItem ToListItem(ActionItemRow row) {
        var x = row.Item;
        return new ActionItemListItem(
            x.Id, x.Text, x.SourceVersion, x.CreatedAt, x.CommentCount, x.SourceBoardId, x.SourceCardId, x.AssignedTo, x.DoneAt, x.DoneBy,
            x.CreatedBy, row.BoardTitle);
    }
}

/// <summary>Plain-text rules for names and descriptions; lengths count code points, as <c>char_length</c> does.</summary>
public static class TeamText {
    /// <summary>One trimmed line; records a field error and returns null when it breaks the rules.</summary>
    public static string? Line(string? raw, int max, Dictionary<string, string> fields, string field, bool required) {
        var value = raw?.Trim();
        if (value is null) {
            if (required) {
                fields[field] = "required";
            }
            return null;
        }
        if (value.Length == 0 && required) {
            fields[field] = "required";
            return null;
        }
        if (!TextRules.IsPlainText(value) || Length(value) > max) {
            fields[field] = "invalid";
            return null;
        }
        return value;
    }

    /// <summary>Like <see cref="Line"/>, but line breaks are allowed; an empty string stays empty (it clears the value).</summary>
    public static string? Paragraph(string? raw, int max, Dictionary<string, string> fields, string field) {
        var value = raw?.Trim().ReplaceLineEndings("\n");
        if (value is null) {
            return null;
        }
        if (!TextRules.IsPlainText(value.Replace('\n', ' ')) || Length(value) > max) {
            fields[field] = "invalid";
            return null;
        }
        return value;
    }

    public static int Length(string value) => value.EnumerateRunes().Count();
}
