using System.Text.Json.Serialization;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

public sealed record EndorsementTypesPage(
    IReadOnlyList<EndorsementTypeInfo> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

/// <param name="Description">An empty string clears it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EndorsementTypeRequest(string? Name = null, string? Emoji = null, string? Description = null);

public sealed record Leaderboard(IReadOnlyList<LeaderboardEntry> Entries);

public static class EndorsementsEndpoints {
    public const int MaxTypeNameLength = 60;
    public const int MaxTypeDescriptionLength = 200;
    public const int MaxEmojiLength = 8;
    public const int MaxReceived = 100;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<EndorsementsRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/teams/{id:guid}/endorsement-types", TypesAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(TeamsEndpoints.Read);
        app.MapPost("/api/teams/{id:guid}/endorsement-types", CreateTypeAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
        app.MapPatch("/api/teams/{id:guid}/endorsement-types/{typeId:guid}", UpdateTypeAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
        app.MapDelete("/api/teams/{id:guid}/endorsement-types/{typeId:guid}", ArchiveTypeAsync)
            .RequireRoutePolicy(RoutePolicy.TeamAdmin)
            .RequireRateLimit(TeamsEndpoints.Write);
        app.MapGet("/api/teams/{id:guid}/endorsements/leaderboard", LeaderboardAsync)
            .RequireRoutePolicy(RoutePolicy.TeamMember)
            .RequireRateLimit(TeamsEndpoints.Read);
        app.MapGet("/api/me/endorsements", ReceivedAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(TeamsEndpoints.Read);
    }

    private static async Task<IResult> TypesAsync(Guid id, HttpContext context, TeamAccess access, EndorsementsRepo endorsements) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var types = await endorsements.TypesAsync(check.TeamId, context.RequestAborted);
        return Results.Json(new EndorsementTypesPage(types, null), TeamHttpJsonContext.Default.EndorsementTypesPage);
    }

    private static async Task<IResult> CreateTypeAsync(
        Guid id,
        EndorsementTypeRequest body,
        HttpContext context,
        TeamAccess access,
        EndorsementsRepo endorsements,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var fields = new Dictionary<string, string>();
        var name = TeamText.Line(body.Name, MaxTypeNameLength, fields, "name", required: true);
        var emoji = TeamText.Line(body.Emoji, MaxEmojiLength, fields, "emoji", required: true);
        var description = TeamText.Line(body.Description, MaxTypeDescriptionLength, fields, "description", required: false);
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        var type = await endorsements.CreateTypeAsync(
            check.TeamId, name!, emoji!, string.IsNullOrEmpty(description) ? null : description, time.GetUtcNow(), context.RequestAborted);
        return Results.Json(type, TeamHttpJsonContext.Default.EndorsementTypeInfo, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateTypeAsync(
        Guid id,
        Guid typeId,
        EndorsementTypeRequest body,
        HttpContext context,
        TeamAccess access,
        EndorsementsRepo endorsements) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        var fields = new Dictionary<string, string>();
        var name = TeamText.Line(body.Name, MaxTypeNameLength, fields, "name", required: body.Name is not null);
        var emoji = TeamText.Line(body.Emoji, MaxEmojiLength, fields, "emoji", required: body.Emoji is not null);
        var description = TeamText.Line(body.Description, MaxTypeDescriptionLength, fields, "description", required: false);
        if (fields.Count > 0) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", fields);
        }
        var patch = new EndorsementTypePatch(name, emoji, description);
        return await endorsements.UpdateTypeAsync(check.TeamId, new EndorsementTypeId(typeId), patch, context.RequestAborted) is { } type
            ? Results.Json(type, TeamHttpJsonContext.Default.EndorsementTypeInfo)
            : TeamAccess.NotFound();
    }

    private static async Task<IResult> ArchiveTypeAsync(
        Guid id,
        Guid typeId,
        HttpContext context,
        TeamAccess access,
        EndorsementsRepo endorsements,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.TeamManageSettings);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        return await endorsements.ArchiveTypeAsync(check.TeamId, new EndorsementTypeId(typeId), time.GetUtcNow(), context.RequestAborted)
            ? Results.NoContent()
            : TeamAccess.NotFound();
    }

    private static async Task<IResult> LeaderboardAsync(
        Guid id,
        string? period,
        HttpContext context,
        TeamAccess access,
        EndorsementsRepo endorsements,
        TimeProvider time) {
        var check = await access.CheckAsync(context, id, Capability.Subscribe);
        if (check.Refusal is { } refusal) {
            return refusal;
        }
        TimeSpan? window = period switch {
            null or "30d" => TimeSpan.FromDays(30),
            "90d" => TimeSpan.FromDays(90),
            "all" => null,
            _ => TimeSpan.Zero,
        };
        if (window == TimeSpan.Zero) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["period"] = "invalid" });
        }
        var since = window is { } span ? time.GetUtcNow() - span : (DateTimeOffset?)null;
        var entries = await endorsements.LeaderboardAsync(check.TeamId, since, context.RequestAborted);
        return Results.Json(new Leaderboard(entries), TeamHttpJsonContext.Default.Leaderboard);
    }

    private static async Task<IResult> ReceivedAsync(HttpContext context, EndorsementsRepo endorsements) {
        var caller = (await context.GetCallerAsync())!;
        var received = await endorsements.ReceivedAsync(caller.Principal.Id, MaxReceived, context.RequestAborted);
        return Results.Json(received, TeamHttpJsonContext.Default.IReadOnlyListReceivedEndorsement);
    }
}
