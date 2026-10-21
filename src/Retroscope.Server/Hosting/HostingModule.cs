using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Retroscope.Server.Hosting;

public static class HostingModule {
    public const long MaxRequestBodySize = 2 * 1024 * 1024;

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton(_ => HostingOptions.FromConfiguration(configuration));
        services.AddSingleton(sp => WebBuild.Load(
            sp.GetRequiredService<HostingOptions>().WebRoot,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WebBuild>()));
        services.AddSingleton<SecurityHeaders>();
        services.AddSingleton<StaticAssets>();
        services.AddSingleton<Shell>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RateLimiter(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<EmailBudget>();
        services.TryAddSingleton<IRoomAccess, PolicyRoomAccess>();
        services.AddSingleton<RoomResolver>();
        services.AddSingleton<ShutdownCoordinator>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ShutdownCoordinator>());
        services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownCoordinator.GracePeriod);
        services.Configure<KestrelServerOptions>(options => {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 2000;
            options.Limits.MaxRequestBodySize = MaxRequestBodySize;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });
        Forwarded.AddServices(services);
        RequestLog.AddServices(services, configuration);
    }

    // Order (§a): request log → forwarded headers → security headers → static files → CSRF → rate limits → WebSockets. Errors are
    // caught just inside the security headers so problem responses carry them; routing runs after static files so
    // CSRF, rate limits and route policies can read the endpoint's metadata.
    public static void UsePipeline(WebApplication app) {
        RequestLog.Use(app);
        app.UseForwardedHeaders();
        SecurityHeaders.Use(app);
        Problem.Use(app);
        StaticAssets.Use(app);
        app.UseRouting();
        CsrfFilter.Use(app);
        RateLimits.Use(app);
        RoutePolicyFilter.Use(app);
        app.UseWebSockets(new WebSocketOptions {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });
    }

    public static void Map(IEndpointRouteBuilder app) {
        Health.Map(app);
        Shell.Map(app);
        LegacyInvite.Map(app);
    }
}
