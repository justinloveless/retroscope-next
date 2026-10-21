using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace Retroscope.Server.Hosting;

/// <param name="Size">Bytes of the raw file.</param>
/// <param name="ETag">Quoted strong ETag of the raw bytes, from the manifest.</param>
/// <param name="Brotli">Size of the <c>.br</c> sibling, or null when there is none.</param>
/// <param name="Gzip">Size of the <c>.gz</c> sibling, or null when there is none.</param>
public sealed record WebFile(string PublicPath, string PhysicalPath, long Size, string ETag, long? Brotli, long? Gzip);

/// <summary>
/// The output of <c>web/build.mjs</c>: <c>manifest.json</c> and <c>csp.json</c> (shapes in docs/decisions/U10.md).
/// A missing build (tests, a server started without <c>web/dist</c>) loads as empty so the API still serves.
/// </summary>
public sealed class WebBuild {
    public static readonly WebBuild Empty = new(
        "dev",
        [],
        [],
        FrozenDictionary<string, IReadOnlyList<string>>.Empty,
        FrozenDictionary<string, IReadOnlyList<string>>.Empty,
        FrozenDictionary<string, string>.Empty,
        FrozenDictionary<string, WebFile>.Empty,
        [],
        null);

    private WebBuild(
        string buildId,
        IReadOnlyList<string> entry,
        IReadOnlyList<string> css,
        FrozenDictionary<string, IReadOnlyList<string>> routes,
        FrozenDictionary<string, IReadOnlyList<string>> routeCss,
        FrozenDictionary<string, string> assets,
        FrozenDictionary<string, WebFile> files,
        IReadOnlyList<string> scriptHashes,
        string? themeScript) {
        BuildId = buildId;
        Entry = entry;
        Css = css;
        Routes = routes;
        RouteCss = routeCss;
        Assets = assets;
        Files = files;
        ScriptHashes = scriptHashes;
        ThemeScript = themeScript;
    }

    /// <summary>Identifies the deployed front end; the first 12 hex digits of the manifest's sha256.</summary>
    public string BuildId { get; }

    /// <summary>The entry chunk first, then its static imports.</summary>
    public IReadOnlyList<string> Entry { get; }

    public IReadOnlyList<string> Css { get; }

    /// <summary>Route pattern → the route chunk and its static imports.</summary>
    public FrozenDictionary<string, IReadOnlyList<string>> Routes { get; }

    public FrozenDictionary<string, IReadOnlyList<string>> RouteCss { get; }

    /// <summary><c>web/assets</c> logical name → hashed public URL.</summary>
    public FrozenDictionary<string, string> Assets { get; }

    public FrozenDictionary<string, WebFile> Files { get; }

    /// <summary><c>sha256-…</c> sources for <c>script-src</c>.</summary>
    public IReadOnlyList<string> ScriptHashes { get; }

    /// <summary>Inlined byte for byte: the CSP hash covers exactly this text.</summary>
    public string? ThemeScript { get; }

    public static WebBuild Load(string webRoot, ILogger logger) {
        var manifestPath = Path.Combine(webRoot, "manifest.json");
        if (!File.Exists(manifestPath)) {
            HostingLog.NoWebBuild(logger, webRoot);
            return Empty;
        }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        var files = new Dictionary<string, WebFile>(StringComparer.Ordinal);
        foreach (var file in Members(root, "files")) {
            if (!IsAssetPath(file.Name)) {
                throw new InvalidOperationException($"manifest.json lists '{file.Name}' outside /assets/.");
            }
            var physical = Path.Combine(webRoot, file.Name.TrimStart('/'));
            files[file.Name] = new WebFile(
                file.Name,
                physical,
                file.Value.GetProperty("size").GetInt64(),
                file.Value.GetProperty("etag").GetString() ?? throw new InvalidOperationException($"{file.Name} has no etag."),
                OptionalSize(file.Value, "br"),
                OptionalSize(file.Value, "gz"));
        }

        string[] scriptHashes = [];
        string? themeScript = null;
        var cspPath = Path.Combine(webRoot, "csp.json");
        if (File.Exists(cspPath)) {
            using var csp = JsonDocument.Parse(File.ReadAllBytes(cspPath));
            scriptHashes = [.. Strings(csp.RootElement, "scriptHashes")];
            themeScript = csp.RootElement.TryGetProperty("themeScript", out var script) ? script.GetString() : null;
        }

        return new WebBuild(
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes))[..12],
            [.. Strings(root, "entry")],
            [.. Strings(root, "css")],
            ListMap(root, "routes"),
            ListMap(root, "routeCss"),
            Members(root, "assets").ToFrozenDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.Ordinal),
            files.ToFrozenDictionary(StringComparer.Ordinal),
            scriptHashes,
            themeScript);
    }

    private static bool IsAssetPath(string path) =>
        path.StartsWith("/assets/", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal);

    private static long? OptionalSize(JsonElement file, string name) =>
        file.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static JsonProperty[] Members(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? [.. value.EnumerateObject()] : [];

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(x => x.GetString() ?? "")
            : [];

    private static FrozenDictionary<string, IReadOnlyList<string>> ListMap(JsonElement root, string name) =>
        Members(root, name).ToFrozenDictionary(
            x => x.Name,
            x => (IReadOnlyList<string>)[.. x.Value.EnumerateArray().Select(y => y.GetString() ?? "")],
            StringComparer.Ordinal);
}
