using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Retroscope.Server.Hosting;

/// <summary>
/// The HTML shell for every front-end route (§d, §g.2): stylesheets, <c>modulepreload</c>s for the route's chunks,
/// the boot JSON in <c>&lt;script type="application/json" id="boot"&gt;</c>, then the hashed theme script, which
/// reads that element before first paint. <c>/api/boot?path=</c> returns the same boot JSON for in-app navigation.
/// </summary>
public sealed class Shell {
    public const string BootPath = "/api/boot";
    private const int MaxBootPathLength = 2048;

    private static readonly JsonWriterOptions BootJson = new() {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly WebBuild _build;
    private readonly IReadOnlyList<IBootContributor> _contributors;
    private readonly FrozenDictionary<string, (byte[] Head, byte[] Tail)> _pages;

    public Shell(WebBuild build, IEnumerable<IBootContributor> contributors) {
        _build = build;
        _contributors = [.. contributors];
        _pages = ShellRoute.All.ToFrozenDictionary(x => x.Pattern, x => Render(build, x), StringComparer.Ordinal);
    }

    public static void Map(IEndpointRouteBuilder app) {
        foreach (var route in ShellRoute.All) {
            app.MapGet(route.Template, (HttpContext context, Shell shell) => shell.PageAsync(context, route))
                .RequireRoutePolicy(RoutePolicy.Public)
                .RequireRateLimit(RateLimits.Shell)
                .ExcludeFromDescription();
        }
        app.MapGet(BootPath, (HttpContext context, Shell shell, string? path) => shell.BootAsync(context, path))
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(RateLimits.Boot);
    }

    public async Task PageAsync(HttpContext context, ShellRoute route) {
        var parameters = context.Request.RouteValues
            .Where(x => x.Key != "rest" && x.Value is string)
            .ToDictionary(x => x.Key, x => (string)x.Value!, StringComparer.Ordinal);
        var boot = await BuildAsync(context, route, parameters);

        var (head, tail) = _pages[route.Pattern];
        var buffer = new ArrayBufferWriter<byte>(head.Length + tail.Length + 1024);
        buffer.Write(head);
        using (var writer = new Utf8JsonWriter(buffer, BootJson)) {
            boot.WriteTo(writer, _build.BuildId);
        }
        buffer.Write(tail);

        var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.ContentLength = buffer.WrittenCount;
        await response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    public async Task BootAsync(HttpContext context, string? path) {
        if (string.IsNullOrEmpty(path) || path.Length > MaxBootPathLength || path[0] != '/') {
            await Problem.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid");
            return;
        }
        var pathOnly = path.Split('?', '#')[0];
        if (ShellRoute.Resolve(pathOnly) is not var (route, parameters)) {
            await Problem.WriteAsync(context, StatusCodes.Status404NotFound, "not_found");
            return;
        }
        var boot = await BuildAsync(context, route, parameters);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, BootJson)) {
            boot.WriteTo(writer, _build.BuildId);
        }
        var response = context.Response;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.ContentLength = buffer.WrittenCount;
        await response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    private async Task<BootContext> BuildAsync(HttpContext context, ShellRoute route, IReadOnlyDictionary<string, string> parameters) {
        var boot = new BootContext(context, route, parameters);
        foreach (var contributor in _contributors) {
            await contributor.ContributeAsync(boot, context.RequestAborted);
        }
        return boot;
    }

    // Everything but the boot JSON is fixed per route, so it is rendered once at startup.
    private static (byte[] Head, byte[] Tail) Render(WebBuild build, ShellRoute route) {
        var html = HtmlEncoder.Default;
        var head = new StringBuilder(2048);
        head.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>Retroscope</title>\n");
        var styles = build.Css.Concat(build.RouteCss.GetValueOrDefault(route.Pattern) ?? []);
        foreach (var href in styles) {
            head.Append("<link rel=\"stylesheet\" href=\"").Append(html.Encode(href)).Append("\">\n");
        }
        if (route.IsPoker && build.Assets.TryGetValue("m6x11plus.woff2", out var font)) {
            head.Append("<link rel=\"preload\" href=\"").Append(html.Encode(font))
                .Append("\" as=\"font\" type=\"font/woff2\" crossorigin>\n");
        }
        var preloads = build.Entry.Skip(1).Concat(build.Routes.GetValueOrDefault(route.Pattern) ?? []).Distinct(StringComparer.Ordinal);
        foreach (var href in preloads) {
            head.Append("<link rel=\"modulepreload\" href=\"").Append(html.Encode(href)).Append("\">\n");
        }
        head.Append("<script type=\"application/json\" id=\"boot\">");

        var tail = new StringBuilder(1024);
        tail.Append("</script>\n");
        if (build.ThemeScript is { } theme) {
            tail.Append("<script>").Append(theme).Append("</script>\n");
        }
        if (build.Entry.Count > 0) {
            tail.Append("<script type=\"module\" src=\"").Append(html.Encode(build.Entry[0])).Append("\"></script>\n");
        }
        tail.Append("</head>\n<body>\n<div id=\"app\"></div>\n</body>\n</html>\n");

        return (Encoding.UTF8.GetBytes(head.ToString()), Encoding.UTF8.GetBytes(tail.ToString()));
    }
}
