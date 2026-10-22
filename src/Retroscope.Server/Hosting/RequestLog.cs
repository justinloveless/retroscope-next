using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Console;

namespace Retroscope.Server.Hosting;

/// <summary>
/// One Information line per request (§i.3): method, path, status and duration; never the query, headers or body.
/// The legacy invite link carries its token in the path, so any path under <c>/invite</c> is logged as
/// <c>/invite/[redacted]</c> (S23).
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

    private const int MaxDecodedLayers = 4;

    public static void AddLogging(IServiceCollection services, IConfiguration configuration) {
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
                        Redact(context.Request.Path, context.Features.Get<IHttpRequestFeature>()?.RawTarget),
                        context.Response.StatusCode,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
        });
    }

    /// <summary>
    /// Any path that starts with <c>/invite</c> and has anything after it is redacted, judged on the raw request target
    /// and on <see cref="HttpRequest.Path"/>, each also percent-decoded and with dot segments and empty segments removed:
    /// Kestrel leaves <c>%2F</c> encoded in the path, and a hand-made <c>/invite%2F…</c> or <c>/x/../INVITE/…</c> must not
    /// slip a token into the log.
    /// </summary>
    public static string Redact(PathString path, string? rawTarget = null) =>
        StartsWithInvite(path.Value) || StartsWithInvite(rawTarget) ? RedactedInvitePath : path.Value ?? "/";

    private static bool StartsWithInvite(string? target) {
        if (string.IsNullOrEmpty(target)) {
            return false;
        }
        var path = PathOf(target);
        for (var layer = 0; layer <= MaxDecodedLayers; layer++) {
            if (IsInvite(path) || IsInvite(RemoveDotSegments(path))) {
                return true;
            }
            var decoded = Uri.UnescapeDataString(path);
            if (decoded == path) {
                return false;
            }
            path = decoded;
        }
        return false;
    }

    private static bool IsInvite(string path) =>
        path.Length > LegacyInvite.Prefix.Length && path.StartsWith(LegacyInvite.Prefix, StringComparison.OrdinalIgnoreCase);

    // The raw target may be in absolute form (http://host/path) and carries the query.
    private static string PathOf(string target) {
        var query = target.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? target : target[..query];
        if (path.StartsWith('/')) {
            return path;
        }
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        var slash = scheme < 0 ? -1 : path.IndexOf('/', scheme + 3);
        return slash < 0 ? "/" : path[slash..];
    }

    private static string RemoveDotSegments(string path) {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', '\\')) {
            if (segment == "..") {
                if (segments.Count > 0) {
                    segments.RemoveAt(segments.Count - 1);
                }
            } else if (segment is not ("" or ".")) {
                segments.Add(segment);
            }
        }
        return "/" + string.Join('/', segments);
    }

    [LoggerMessage(1, LogLevel.Information, "{Method} {Path} {Status} in {ElapsedMs:0.0} ms")]
    private static partial void Completed(ILogger logger, string method, string path, int status, double elapsedMs);
}
