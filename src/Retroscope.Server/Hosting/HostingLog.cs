namespace Retroscope.Server.Hosting;

internal static partial class HostingLog {
    [LoggerMessage(1, LogLevel.Warning, "No web build at {WebRoot}; the shell serves no scripts")]
    public static partial void NoWebBuild(ILogger logger, string webRoot);

    [LoggerMessage(2, LogLevel.Error, "Unhandled exception on {Method} {Endpoint}")]
    public static partial void Unhandled(ILogger logger, Exception exception, string method, string? endpoint);

    [LoggerMessage(3, LogLevel.Error, "Shutdown step in phase {Phase} failed")]
    public static partial void ShutdownStepFailed(ILogger logger, Exception exception, ShutdownPhase phase);
}
