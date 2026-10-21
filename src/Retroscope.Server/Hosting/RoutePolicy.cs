namespace Retroscope.Server.Hosting;

/// <summary>
/// Who may call a route (§d). Every endpoint declares one with <see cref="RoutePolicyExtensions.RequireRoutePolicy"/>;
/// the route-inventory test fails the build otherwise.
/// </summary>
public enum RoutePolicy {
    Public,
    Guest,
    User,
    VerifiedUser,
    TeamMember,
    TeamAdmin,
    TeamOwner,
    AppAdmin,
    BoardAccess,
    BoardManage,
    SessionManage,
}

public sealed record RoutePolicyMetadata(RoutePolicy Policy);

public static class RoutePolicyExtensions {
    public static TBuilder RequireRoutePolicy<TBuilder>(this TBuilder builder, RoutePolicy policy)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RoutePolicyMetadata(policy));
}

/// <summary>
/// Enforces the caller-level half of each policy before the handler runs. Team, board and session policies need the
/// resource: the middleware only requires the right kind of caller, and the handler finishes with
/// <c>Policy.Can</c>. An endpoint without a policy is refused, so a forgotten declaration fails closed.
/// </summary>
public static class RoutePolicyFilter {
    public static void Use(WebApplication app) {
        app.Use(async (context, next) => {
            // Only routes declare policies; the framework's own 405 rejection endpoint is not a RouteEndpoint.
            if (context.GetEndpoint() is not RouteEndpoint endpoint) {
                await next(context);
                return;
            }
            var policy = endpoint.Metadata.GetMetadata<RoutePolicyMetadata>()?.Policy;
            if (policy is null) {
                await Problem.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden");
                return;
            }
            var refusal = await CheckAsync(context, policy.Value);
            if (refusal is { } problem) {
                await Problem.WriteAsync(context, problem.Status, problem.Code);
                return;
            }
            await next(context);
        });
    }

    public static async ValueTask<(int Status, string Code)?> CheckAsync(HttpContext context, RoutePolicy policy) {
        if (policy == RoutePolicy.Public) {
            return null;
        }
        var caller = await context.GetCallerAsync();
        if (caller is null) {
            return (StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        return policy switch {
            RoutePolicy.Guest or RoutePolicy.BoardAccess or RoutePolicy.BoardManage or RoutePolicy.SessionManage => null,
            RoutePolicy.User or RoutePolicy.TeamMember or RoutePolicy.TeamAdmin or RoutePolicy.TeamOwner =>
                caller.IsUser ? null : (StatusCodes.Status401Unauthorized, "unauthenticated"),
            RoutePolicy.VerifiedUser when !caller.IsUser => (StatusCodes.Status401Unauthorized, "unauthenticated"),
            RoutePolicy.VerifiedUser => caller.EmailVerified ? null : (StatusCodes.Status403Forbidden, "email_unverified"),
            RoutePolicy.AppAdmin when !caller.IsUser => (StatusCodes.Status401Unauthorized, "unauthenticated"),
            RoutePolicy.AppAdmin => caller.Principal.IsAppAdmin ? null : (StatusCodes.Status403Forbidden, "forbidden"),
            _ => (StatusCodes.Status403Forbidden, "forbidden"),
        };
    }
}
