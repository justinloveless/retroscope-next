using System.Text.Json.Serialization;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuestRequest(string? DisplayName);

public sealed record GuestResponse(MePrincipal Principal);

public static class GuestEndpoints {
    /// <summary>Guest creation: 20/h/IP (§e).</summary>
    public static readonly RateLimit Create = RateLimit.Define("guest.create", RateRule.PerIp(20, TimeSpan.FromHours(1)));

    public static readonly RateLimit Rename = RateLimit.Define(
        "guest.rename", RateRule.PerIp(60, TimeSpan.FromHours(1)), RateRule.PerPrincipal(20, TimeSpan.FromHours(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<GuestsRepo>();
        services.AddSingleton<Guests>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapPost("/api/guest", IssueAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Create);
        app.MapPatch("/api/guest", RenameAsync)
            .RequireRoutePolicy(RoutePolicy.Guest)
            .RequireRateLimit(Rename);
    }

    // A browser that already holds a live guest cookie is renamed rather than given a second guest, so its seats keep
    // their identity. A signed-in user does not need a guest.
    private static async Task<IResult> IssueAsync(GuestRequest body, HttpContext context, Guests guests) {
        if (!GuestNames.TryNormalize(body.DisplayName, out var name, out var error)) {
            return Invalid(error);
        }
        var caller = await context.GetCallerAsync();
        if (caller is { IsUser: true }) {
            return Problem.Result(StatusCodes.Status409Conflict, "signed_in");
        }
        if (caller is not null) {
            var renamed = await guests.RenameAsync(caller.Principal.Id, name, context.RequestAborted);
            return Results.Json(new GuestResponse(MePrincipal.Of(renamed)), AuthJsonContext.Default.GuestResponse);
        }
        var guest = await guests.IssueAsync(context, name, context.RequestAborted);
        return Results.Json(
            new GuestResponse(MePrincipal.Of(guest)), AuthJsonContext.Default.GuestResponse, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RenameAsync(GuestRequest body, HttpContext context, Guests guests) {
        if (!GuestNames.TryNormalize(body.DisplayName, out var name, out var error)) {
            return Invalid(error);
        }
        var caller = (await context.GetCallerAsync())!;
        if (caller.IsUser) {
            return Problem.Result(StatusCodes.Status403Forbidden, "not_guest");
        }
        var renamed = await guests.RenameAsync(caller.Principal.Id, name, context.RequestAborted);
        return Results.Json(new GuestResponse(MePrincipal.Of(renamed)), AuthJsonContext.Default.GuestResponse);
    }

    private static IResult Invalid(string error) =>
        Problem.Result(StatusCodes.Status400BadRequest, "invalid", new Dictionary<string, string> { ["displayName"] = error });
}
