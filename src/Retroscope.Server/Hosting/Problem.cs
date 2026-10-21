using System.Text.Json;
using Retroscope.Server.Json;

namespace Retroscope.Server.Hosting;

/// <summary>An <c>application/problem+json</c> body (RFC 9457) with the stable <c>code</c> clients switch on.</summary>
/// <param name="Fields">Validation errors: field name → error code.</param>
public sealed record ProblemBody(
    string Type,
    string Title,
    int Status,
    string Code,
    IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>
/// Every error the server sends is problem+json (§d). Handlers return <see cref="Result"/>; the middleware turns
/// unhandled exceptions and empty error responses (routing 404/405, binding 400, body-size 413) into the same shape.
/// </summary>
public static class Problem {
    public const string ContentType = "application/problem+json";

    public static IResult Result(
        int status,
        string code,
        IReadOnlyDictionary<string, string>? fields = null) =>
        Results.Json(Body(status, code, fields), HostingJsonContext.Default.ProblemBody, ContentType, status);

    public static Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        IReadOnlyDictionary<string, string>? fields = null) {
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            Body(status, code, fields),
            HostingJsonContext.Default.ProblemBody,
            context.RequestAborted);
    }

    public static ProblemBody Body(int status, string code, IReadOnlyDictionary<string, string>? fields = null) =>
        new("about:blank", Title(status), status, code, fields);

    /// <summary>The code for an error the framework produced with no body of its own.</summary>
    public static string DefaultCode(int status) => status switch {
        StatusCodes.Status400BadRequest => "invalid",
        StatusCodes.Status401Unauthorized => "unauthenticated",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status408RequestTimeout => "timeout",
        StatusCodes.Status413PayloadTooLarge => "too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        _ when status >= 500 => "internal",
        _ => "error",
    };

    private static string Title(int status) => status switch {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status405MethodNotAllowed => "Method Not Allowed",
        StatusCodes.Status408RequestTimeout => "Request Timeout",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "Content Too Large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Content",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        StatusCodes.Status507InsufficientStorage => "Insufficient Storage",
        _ when status >= 500 => "Internal Server Error",
        _ => "Error",
    };

    public static void Use(WebApplication app) {
        var headers = app.Services.GetRequiredService<SecurityHeaders>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Problem));
        app.Use(async (context, next) => {
            try {
                await next(context);
            } catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested) {
                var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
                if (status >= 500) {
                    HostingLog.Unhandled(logger, exception, context.Request.Method, context.GetEndpoint()?.DisplayName);
                }
                context.Response.Clear();
                headers.Apply(context.Response);
                await WriteAsync(context, status, DefaultCode(status));
                return;
            }

            var response = context.Response;
            if (response.StatusCode >= 400
                && !response.HasStarted
                && response.ContentType is null
                && response.ContentLength is null or 0
                && !HttpMethods.IsHead(context.Request.Method)) {
                await WriteAsync(context, response.StatusCode, DefaultCode(response.StatusCode));
            }
        });
    }
}
