namespace Retroscope.Server.Hosting;

/// <summary>
/// §d CSRF rule for every unsafe method: <c>X-RS: 1</c> and <c>Origin == PUBLIC_ORIGIN</c>, or no Origin with
/// <c>Sec-Fetch-Site: same-origin</c>. There is no CORS, so a cross-site page cannot send the custom header
/// without a preflight that is never answered. WebSocket upgrades are GETs; the transport checks their Origin.
/// </summary>
public static class CsrfFilter {
    public const string HeaderName = "X-RS";

    public static bool IsAllowed(HttpRequest request, string publicOrigin) {
        var method = request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) {
            return true;
        }
        var headers = request.Headers;
        if (headers[HeaderName] is not [ "1" ]) {
            return false;
        }
        return headers.Origin.Count switch {
            0 => headers["Sec-Fetch-Site"] is [ "same-origin" ],
            1 => string.Equals(headers.Origin[0], publicOrigin, StringComparison.Ordinal),
            _ => false,
        };
    }

    public static void Use(WebApplication app) {
        var origin = app.Services.GetRequiredService<HostingOptions>().PublicOrigin;
        app.Use((context, next) => IsAllowed(context.Request, origin)
            ? next(context)
            : Problem.WriteAsync(context, StatusCodes.Status403Forbidden, "csrf"));
    }
}
