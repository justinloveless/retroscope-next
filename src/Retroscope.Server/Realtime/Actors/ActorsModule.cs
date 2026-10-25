using Microsoft.Extensions.DependencyInjection.Extensions;
using Retroscope.Core.Room;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Server.Realtime.Actors;

public static class ActorsModule {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ActorOptions.Default);
        services.AddSingleton(sp => new ActorRuntime(
            sp.GetRequiredService<ActorOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<IRoomVisits>()));
        services.AddSingleton(sp => new ActorRegistry(sp.GetRequiredService<ActorRuntime>(), sp.GetServices<RoomType>()));
        services.AddSingleton<RoomGateway>();
        services.AddSingleton<IRoomGateway>(sp => sp.GetRequiredService<RoomGateway>());
        services.AddHostedService<ActorsLifetime>();
    }

    public static void Map(IEndpointRouteBuilder app) {
    }

    /// <summary>
    /// Registers a room kind: its binding and store come from the container, and so does the
    /// <see cref="IAccessView"/> (the membership cache) every decision resolves roles through.
    /// </summary>
    public static IServiceCollection AddRoom<TState, TInput, TEvent>(
        this IServiceCollection services,
        Func<IServiceProvider, IRoomBinding<TState, TInput, TEvent>> binding,
        Func<IServiceProvider, IRoomStore<TState, TEvent>> store)
        where TState : class
        where TInput : class {
        return services.AddSingleton(sp => RoomType.Of(binding(sp), store(sp), sp.GetRequiredService<IAccessView>()));
    }

    /// <summary>Registers the <c>DrainActors</c> step; a stop that skips the coordinator still drains here.</summary>
    private sealed class ActorsLifetime(ActorRegistry registry, ShutdownCoordinator coordinator) : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) {
            coordinator.Register(ShutdownPhase.DrainActors, registry.ShutdownAsync);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => registry.ShutdownAsync(cancellationToken);
    }
}
