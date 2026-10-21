using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Retroscope.Integration.Tests.Hosting;

// A web/dist lookalike in the shapes of docs/decisions/U10.md. Each variant has distinct bytes, so a test can tell
// which one the server sent.
internal sealed class FakeWebBuild : IDisposable {
    public const string Entry = "/assets/entry-AAAA.js";
    public const string Shared = "/assets/chunks/shared-BBBB.js";
    public const string PokerChunk = "/assets/chunks/poker-CCCC.js";
    public const string LandingChunk = "/assets/chunks/landing-DDDD.js";
    public const string Kit = "/assets/kit-EEEE.css";
    public const string Neotro = "/assets/neotro-FFFF.css";
    public const string Font = "/assets/static/m6x11plus-GGGG.woff2";
    public const string OnDiskOnly = "/assets/secret-HHHH.js";
    public const string ThemeScript = "(function(){document.documentElement.dataset.theme='light'})();";

    public FakeWebBuild() {
        Root = Directory.CreateTempSubdirectory("rs-web-").FullName;
        var files = new JsonObject();
        AddFile(files, Entry, "console.log('entry');", brotli: "BR:entry", gzip: "GZ:entry");
        AddFile(files, Shared, "export const shared = 1;", brotli: null, gzip: "GZ:shared");
        AddFile(files, PokerChunk, "export function mount() {}", brotli: "BR:poker", gzip: "GZ:poker");
        AddFile(files, LandingChunk, "export function mount() {}", brotli: null, gzip: null);
        AddFile(files, Kit, "body{margin:0}", brotli: "BR:kit", gzip: "GZ:kit");
        AddFile(files, Neotro, ".card{}", brotli: null, gzip: null);
        AddFile(files, Font, "wOF2-font-bytes", brotli: null, gzip: null);
        Write(OnDiskOnly, "not in the manifest");

        var routes = new JsonObject();
        var routeCss = new JsonObject();
        foreach (var route in Retroscope.Server.Hosting.ShellRoute.All) {
            var chunk = route.IsPoker ? PokerChunk : LandingChunk;
            routes[route.Pattern] = new JsonArray(chunk, Shared);
            routeCss[route.Pattern] = route.IsPoker ? new JsonArray(Neotro) : new JsonArray();
        }
        var manifest = new JsonObject {
            ["entry"] = new JsonArray(Entry, Shared),
            ["css"] = new JsonArray(Kit),
            ["routes"] = routes,
            ["routeCss"] = routeCss,
            ["styles"] = new JsonObject { ["kit.css"] = Kit },
            ["assets"] = new JsonObject { ["m6x11plus.woff2"] = Font },
            ["files"] = files,
        };
        File.WriteAllText(Path.Combine(Root, "manifest.json"), manifest.ToJsonString());

        ThemeHash = $"sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ThemeScript)))}";
        var csp = new JsonObject { ["scriptHashes"] = new JsonArray(ThemeHash), ["themeScript"] = ThemeScript };
        File.WriteAllText(Path.Combine(Root, "csp.json"), csp.ToJsonString());
    }

    public string Root { get; }

    public string ThemeHash { get; }

    public static string ETagOf(string publicPath) => $"\"etag-{Path.GetFileNameWithoutExtension(publicPath)}\"";

    public Dictionary<string, string?> Settings() => new() { ["WEB_ROOT"] = Root };

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private void AddFile(JsonObject files, string publicPath, string raw, string? brotli, string? gzip) {
        Write(publicPath, raw);
        if (brotli is not null) {
            Write(publicPath + ".br", brotli);
        }
        if (gzip is not null) {
            Write(publicPath + ".gz", gzip);
        }
        files[publicPath] = new JsonObject {
            ["size"] = Encoding.UTF8.GetByteCount(raw),
            ["etag"] = ETagOf(publicPath),
            ["br"] = brotli is null ? null : Encoding.UTF8.GetByteCount(brotli),
            ["gz"] = gzip is null ? null : Encoding.UTF8.GetByteCount(gzip),
        };
    }

    private void Write(string publicPath, string content) {
        var path = Path.Combine(Root, publicPath.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
