namespace Retroscope.Server.Hosting;

/// <summary>
/// Old invite links (<c>/invite/:token</c>) keep working after cutover (§h.6): they redirect to <c>/invite#t=:token</c>,
/// where the token lives in the fragment and never reaches a server again. The redirect is not cacheable.
/// </summary>
public static class LegacyInvite {
    public const string Prefix = "/invite";

    public static void Map(IEndpointRouteBuilder app) {
        // Response.Redirect rather than Results.Redirect: the result type logs its Location, which holds the token.
        app.MapGet(Prefix + "/{token}", (HttpContext context, string token) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Redirect($"{Prefix}#t={Uri.EscapeDataString(token)}");
        })
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimits.Shell);
    }
}
