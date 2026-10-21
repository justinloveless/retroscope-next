using System.Diagnostics;
using Microsoft.Extensions.Logging.Console;

namespace Retroscope.Server.Hosting;

/// <summary>
/// One Information line per request (§i.3): method, path, status and duration; never the query, headers or body.
/// The legacy invite link carries its token in the path, so that path is logged as <c>/invite/[redacted]</c> (S23).
/// ASP.NET Core's own per-request lines (hosting, routing, results) print raw URLs and redirect targets, so the whole
/// <c>Microsoft.AspNetCore</c> category is held at Warning; this line replaces them.
/// Outside Development the console writes JSON with UTC ISO timestamps unless
/// <c>Logging__Console__FormatterName</c> names another formatter. The slim builder registers the simple formatter
/// in code, after configuration is bound, so the default has to be applied as a post-configuration.
/// </summary>
public static partial class RequestLog {
    public const string Category = "Retroscope.Requests";
    public const string FrameworkCategory = "Microsoft.AspNetCore";
    public const string RedactedInvitePath = "/invite/[redacted]";

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddLogging(logging => logging.AddFilter(FrameworkCategory, LogLevel.Warning));
        var formatter = configuration["Logging:Console:FormatterName"];
        services.AddOptions<ConsoleLoggerOptions>().PostConfigure<IHostEnvironment>((options, environment) =>
            options.FormatterName = formatter ?? (environment.IsDevelopment() ? ConsoleFormatterNames.Simple : ConsoleFormatterNames.Json));
        services.AddOptions<JsonConsoleFormatterOptions>().PostConfigure(options => {
            options.TimestampFormat ??= "yyyy-MM-ddTHH:mm:ss.fffZ";
            options.UseUtcTimestamp = true;
        });
    }

    public static void Use(WebApplication app) {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Category);
        app.Use(async (context, next) => {
            var started = Stopwatch.GetTimestamp();
            try {
                await next(context);
            } finally {
                if (context.Request.Path != Health.Path) {
                    Completed(
                        logger,
                        context.Request.Method,
                        Redact(context.Request.Path),
                        context.Response.StatusCode,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
        });
    }

    public static string Redact(PathString path) =>
        path.StartsWithSegments(LegacyInvite.Prefix, out var rest) && rest.HasValue && rest != "/"
            ? RedactedInvitePath
            : path.Value ?? "/";

    [LoggerMessage(1, LogLevel.Information, "{Method} {Path} {Status} in {ElapsedMs:0.0} ms")]
    private static partial void Completed(ILogger logger, string method, string path, int status, double elapsedMs);
}
