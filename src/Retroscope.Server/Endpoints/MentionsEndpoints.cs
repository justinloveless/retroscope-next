using System.Text.Json.Serialization;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

/// <param name="Next">The cursor of the next page; written as <c>null</c> on the last one.</param>
public sealed record MentionsPage(IReadOnlyList<MentionItem> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Next);

/// <summary><c>GET /api/me/mentions</c> and <c>POST /api/me/mentions/seen</c> (§d, §0.1): only boards the user can still read.</summary>
public static class MentionsEndpoints {
    public static readonly RateLimit Read = RateLimit.Define(
        "me.mentions", RateRule.PerPrincipal(120, TimeSpan.FromMinutes(1)), RateRule.PerIp(300, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<MentionsRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/me/mentions", ListAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Read);
        app.MapPost("/api/me/mentions/seen", SeenAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Read);
    }

    private static async Task<IResult> ListAsync(HttpContext context, string? cursor, int? limit, MentionsRepo repo) {
        if (!ListCursor.TryParse(cursor, out var after)) {
            return Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["cursor"] = "invalid" });
        }
        var caller = (await context.GetCallerAsync())!;
        var page = await repo.ListAsync(caller.Principal.Id, after, ListCursor.Limit(limit), context.RequestAborted);
        return Results.Json(new MentionsPage(page.Items, page.Next?.ToString()), RoomHttpJsonContext.Default.MentionsPage);
    }

    private static async Task<IResult> SeenAsync(HttpContext context, MentionsRepo repo) {
        var caller = (await context.GetCallerAsync())!;
        await repo.MarkSeenAsync(caller.Principal.Id, context.RequestAborted);
        return Results.NoContent();
    }
}
