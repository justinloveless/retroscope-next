using Npgsql;
using Retroscope.Core.Room;

namespace Retroscope.Server.Hosting;

/// <summary>
/// <c>/healthz</c> answers 200 only while this process holds the leader lease, the database answers and shutdown
/// has not begun (§c.9: there is no standby, so one probe suffices).
/// </summary>
public static class Health {
    public const string Path = "/healthz";

    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(2);

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet(Path, CheckAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimit.Unlimited)
            .ExcludeFromDescription();
    }

    public static async Task CheckAsync(HttpContext context) {
        var services = context.RequestServices;
        var leader = services.GetService<ILeaderEpoch>();
        var healthy = leader is { IsLeader: true }
            && !services.GetRequiredService<ShutdownCoordinator>().IsStopping
            && await DatabaseAnswersAsync(services, context.RequestAborted);
        if (!healthy) {
            await Problem.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable");
            return;
        }
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("ok", context.RequestAborted);
    }

    private static async Task<bool> DatabaseAnswersAsync(IServiceProvider services, CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DatabaseTimeout);
        try {
            var dataSource = services.GetRequiredService<NpgsqlDataSource>();
            await using var command = dataSource.CreateCommand("select 1");
            return await command.ExecuteScalarAsync(timeout.Token) is 1;
        } catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException) {
            return false;
        }
    }

    // `retroscope healthcheck`: the chiseled image has no shell or curl, so the binary probes itself.
    public static async Task<int> ProbeAsync(string[] args) {
        var port = Environment.GetEnvironmentVariable("HEALTHCHECK_PORT") ?? "8080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{Path}"));
            return response.IsSuccessStatusCode ? 0 : 1;
        } catch (HttpRequestException) {
            return 1;
        } catch (TaskCanceledException) {
            return 1;
        }
    }
}
