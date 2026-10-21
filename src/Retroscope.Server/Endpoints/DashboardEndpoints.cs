using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

public static class DashboardEndpoints {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<ActivityRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/api/dashboard", GetAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(TeamsEndpoints.Read);
    }

    private static async Task<IResult> GetAsync(HttpContext context, ActivityRepo activity) {
        var caller = (await context.GetCallerAsync())!;
        var dashboard = await activity.DashboardAsync(caller.Principal.Id, context.RequestAborted);
        return Results.Json(dashboard, TeamHttpJsonContext.Default.Dashboard);
    }
}
