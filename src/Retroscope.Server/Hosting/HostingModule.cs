namespace Retroscope.Server.Hosting;

public static class HostingModule {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
    }

    // Order (owned by U11): forwarded headers → security headers → static files → CSRF → rate limits → WebSockets.
    public static void UsePipeline(WebApplication app) {
    }

    public static void Map(IEndpointRouteBuilder app) {
        Health.Map(app);
    }
}
