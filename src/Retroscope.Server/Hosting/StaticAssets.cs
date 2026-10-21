using System.Collections.Frozen;
using Microsoft.Net.Http.Headers;

namespace Retroscope.Server.Hosting;

/// <summary>
/// Serves <c>/assets/*</c> from the manifest's file list only (§g.7). Names are content-hashed, so responses are
/// <c>immutable</c>; the precompressed sibling is chosen br → gzip → identity, each with its own strong ETag.
/// Bytes stay on disk and go out with <c>SendFileAsync</c>.
/// </summary>
public sealed class StaticAssets(WebBuild build) {
    public const string Prefix = "/assets/";
    public const string CacheControl = "public, max-age=31536000, immutable";

    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string> {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".html"] = "text/html; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".mp3"] = "audio/mpeg",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static void Use(WebApplication app) {
        var assets = app.Services.GetRequiredService<StaticAssets>();
        app.Use((context, next) => context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal)
            ? assets.ServeAsync(context)
            : next(context));
    }

    public Task ServeAsync(HttpContext context) {
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) {
            response.Headers.Allow = "GET, HEAD";
            return Problem.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed");
        }
        if (!build.Files.TryGetValue(request.Path.Value ?? "", out var file)) {
            return Problem.WriteAsync(context, StatusCodes.Status404NotFound, "not_found");
        }

        var (encoding, suffix, length) = Negotiate(request, file);
        var etag = suffix.Length == 0 ? file.ETag : file.ETag.Insert(file.ETag.Length - 1, suffix);
        var headers = response.Headers;
        headers.CacheControl = CacheControl;
        headers.ETag = etag;
        if (file.Brotli is not null || file.Gzip is not null) {
            headers.Vary = HeaderNames.AcceptEncoding;
        }

        if (Matches(request.Headers.IfNoneMatch, etag)) {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file.PublicPath), "application/octet-stream");
        if (encoding is not null) {
            headers.ContentEncoding = encoding;
        }
        response.ContentLength = length;
        return HttpMethods.IsHead(request.Method)
            ? Task.CompletedTask
            : response.SendFileAsync(file.PhysicalPath + suffix, 0, length, context.RequestAborted);
    }

    private static (string? Encoding, string Suffix, long Length) Negotiate(HttpRequest request, WebFile file) {
        if (file.Brotli is null && file.Gzip is null) {
            return (null, "", file.Size);
        }
        var accepted = request.GetTypedHeaders().AcceptEncoding;
        if (file.Brotli is { } br && Accepts(accepted, "br")) {
            return ("br", ".br", br);
        }
        if (file.Gzip is { } gz && Accepts(accepted, "gzip")) {
            return ("gzip", ".gz", gz);
        }
        return (null, "", file.Size);
    }

    private static bool Accepts(IList<StringWithQualityHeaderValue> accepted, string coding) {
        double? exact = null;
        double? wildcard = null;
        foreach (var value in accepted) {
            var quality = value.Quality ?? 1;
            if (string.Equals(value.Value.Value, coding, StringComparison.OrdinalIgnoreCase)) {
                exact = quality;
            } else if (value.Value.Value == "*") {
                wildcard = quality;
            }
        }
        return (exact ?? wildcard ?? 0) > 0;
    }

    // Weak comparison (RFC 9110 §13.1.2): a cached W/ tag still revalidates.
    private static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string etag) {
        foreach (var header in ifNoneMatch) {
            foreach (var candidate in (header ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                if (candidate == "*" || string.Equals(StripWeak(candidate), etag, StringComparison.Ordinal)) {
                    return true;
                }
            }
        }
        return false;
    }

    private static string StripWeak(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
