using System.Net.WebSockets;
using Retroscope.Core.Protocol;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Realtime.Transport;

/// <summary>
/// <c>GET /ws</c> (§c.3): one same-origin socket per tab, authenticated by the session cookie on upgrade. The route
/// policy answers an upgrade without a signed-in user with 401 before it is accepted; a verified email is not needed.
/// Refusals that the client must be able to tell apart (bad Origin, too many sockets from one IP) are close codes on an
/// accepted socket, because a browser cannot read the HTTP status of a failed upgrade.
/// </summary>
public static class WsEndpoint {
    public const string Path = "/ws";

    /// <summary>Upgrade attempts. After a restart every tab reconnects once within <c>bye.retryMs</c>.</summary>
    public static readonly RateLimit Upgrades = RateLimit.Define(
        "ws",
        RateRule.PerIp(120, TimeSpan.FromMinutes(1)),
        RateRule.PerPrincipal(30, TimeSpan.FromMinutes(1)));

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet(Path, (RequestDelegate)HandleAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Upgrades);
    }

    private static async Task HandleAsync(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            await Problem.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid");
            return;
        }
        var services = context.RequestServices;
        if (services.GetRequiredService<ShutdownCoordinator>().IsStopping) {
            await Problem.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable");
            return;
        }
        var caller = await context.GetCallerAsync() ?? throw new InvalidOperationException("The route policy admits callers only.");
        var options = services.GetRequiredService<TransportOptions>();
        var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = options.KeepAliveInterval,
            KeepAliveTimeout = options.KeepAliveTimeout,
            DangerousEnableCompression = false,
        });
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WsEndpoint).FullName!);

        var origin = services.GetRequiredService<HostingOptions>().PublicOrigin;
        if (context.Request.Headers.Origin is not [{ } sent] || !string.Equals(sent, origin, StringComparison.Ordinal)) {
            await RefuseAsync(socket, CloseCodes.BadOrigin, "bad origin", options, logger);
            return;
        }

        var registry = services.GetRequiredService<ConnectionRegistry>();
        var connection = new Connection(
            socket,
            caller,
            ClientIp.LimitKey(context),
            options,
            registry,
            services.GetRequiredService<IRoomGateway>(),
            services.GetRequiredService<TimeProvider>(),
            logger);
        switch (registry.TryAdd(connection)) {
            case Admission.TooManyFromIp:
                await RefuseAsync(socket, CloseCodes.RateLimited, "too many sockets", options, logger);
                return;
            case Admission.Stopping:
                await RefuseAsync(socket, CloseCodes.Restart, "restart", options, logger);
                return;
        }

        var gateway = services.GetRequiredService<IRoomGateway>();
        try {
            await connection.RunAsync(context.RequestAborted);
        } finally {
            registry.Remove(connection);
            if (connection.Client is not null) {
                gateway.Disconnected(connection, connection.Rooms());
            }
        }
    }

    private static async Task RefuseAsync(WebSocket socket, int code, string reason, TransportOptions options, ILogger logger) {
        TransportLog.Refused(logger, code, reason);
        using var timeout = new CancellationTokenSource(options.CloseTimeout);
        try {
            await socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token);
        } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) {
            socket.Abort();
        } finally {
            socket.Dispose();
        }
    }
}
