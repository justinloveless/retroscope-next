namespace Retroscope.Server.Hosting;

/// <summary>
/// The §e header set. It is written before the request runs, so an endpoint with stricter needs (blobs use
/// <c>default-src 'none'; sandbox</c>) overwrites it, and every response, errors included, carries it.
/// </summary>
public sealed class SecurityHeaders {
    private readonly string _csp;

    public SecurityHeaders(HostingOptions options, WebBuild build) {
        _csp = Csp.Build(options.PublicUrl, build.ScriptHashes);
    }

    public string ContentSecurityPolicy => _csp;

    public void Apply(HttpResponse response) {
        var headers = response.Headers;
        headers.ContentSecurityPolicy = _csp;
        headers.StrictTransportSecurity = "max-age=63072000; includeSubDomains";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
    }

    public static void Use(WebApplication app) {
        var headers = app.Services.GetRequiredService<SecurityHeaders>();
        app.Use((context, next) => {
            headers.Apply(context.Response);
            return next(context);
        });
    }
}

public static class Csp {
    public static string Build(Uri publicUrl, IEnumerable<string> scriptHashes) {
        var socketScheme = publicUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var scripts = string.Concat(scriptHashes.Select(x => $" '{x}'"));
        return string.Join(
            "; ",
            "default-src 'none'",
            $"script-src 'self'{scripts}",
            "style-src 'self'",
            "img-src 'self' data: blob:",
            "font-src 'self'",
            $"connect-src 'self' {socketScheme}://{publicUrl.Authority}",
            "media-src 'self'",
            "manifest-src 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            "base-uri 'none'",
            "object-src 'none'",
            "require-trusted-types-for 'script'",
            "trusted-types 'none'");
    }
}
