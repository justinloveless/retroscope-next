using Microsoft.Extensions.DependencyInjection.Extensions;
using Retroscope.Core.Room;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Realtime.Transport;

public static class TransportModule {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(new TransportOptions());
        services.TryAddSingleton<IRoomGateway, UnavailableRoomGateway>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<IConnectionRevoker>(sp => sp.GetRequiredService<ConnectionRegistry>());
    }

    public static void Map(IEndpointRouteBuilder app) {
        var registry = app.ServiceProvider.GetRequiredService<ConnectionRegistry>();
        var shutdown = app.ServiceProvider.GetRequiredService<ShutdownCoordinator>();
        shutdown.Register(ShutdownPhase.StopAccepting, _ => {
            registry.StopAccepting();
            return Task.CompletedTask;
        });
        shutdown.Register(ShutdownPhase.CloseSockets, registry.CloseAllAsync);
        WsEndpoint.Map(app);
    }
}
