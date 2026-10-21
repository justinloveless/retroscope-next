namespace Retroscope.Server.Hosting;

/// <summary>
/// The §e header set. It is written before the request runs, so an endpoint with stricter needs (blobs use
/// <c>default-src 'none'; sandbox</c>) overwrites it, and every response, errors included, carries it.
/// <c>Strict-Transport-Security</c> is sent only when <c>PUBLIC_URL</c> is https: over plain http browsers ignore it,
/// and a local http deployment must not tell a browser to insist on https for its host.
/// </summary>
public sealed class SecurityHeaders {
    private const string StrictTransportSecurity = "max-age=63072000; includeSubDomains";

    private readonly string _csp;
    private readonly bool _https;

    public SecurityHeaders(HostingOptions options, WebBuild build) {
        _csp = Csp.Build(options.PublicUrl, build.ScriptHashes);
        _https = options.PublicUrl.Scheme == Uri.UriSchemeHttps;
    }

    public string ContentSecurityPolicy => _csp;

    public void Apply(HttpResponse response) {
        var headers = response.Headers;
        headers.ContentSecurityPolicy = _csp;
        if (_https) {
            headers.StrictTransportSecurity = StrictTransportSecurity;
        }
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
