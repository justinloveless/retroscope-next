using System.Text;

namespace Retroscope.Server.Hosting;

/// <summary>
/// One front-end route. The list mirrors the frozen <c>web/src/routes.js</c> (a test keeps them equal), and
/// <see cref="Match"/> is a port of <c>core/router.js</c> <c>match()</c>, so the server and the client agree on
/// which screen a path is and what its parameters are.
/// </summary>
public sealed class ShellRoute {
    public static readonly IReadOnlyList<ShellRoute> All = [
        new("/"),
        new("/login"),
        new("/auth/*"),
        new("/account"),
        new("/mentions"),
        new("/teams"),
        new("/teams/:id"),
        new("/teams/:id/settings"),
        new("/teams/:tid/poker/:code"),
        new("/poker/:code"),
        new("/retro/:code"),
        new("/invite"),
        new("/admin/*"),
    ];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly string[] _segments;

    private ShellRoute(string pattern) {
        Pattern = pattern;
        _segments = pattern.Split('/');
        Template = string.Join('/', _segments.Select(x => x switch {
            "*" => "{**rest}",
            _ when x.StartsWith(':') => $"{{{x[1..]}}}",
            _ => x,
        }));
    }

    /// <summary>The client pattern, e.g. <c>/teams/:tid/poker/:code</c>; also the key in <c>manifest.json</c> <c>routes</c>.</summary>
    public string Pattern { get; }

    /// <summary>The ASP.NET Core route template for the same pattern.</summary>
    public string Template { get; }

    public bool IsPoker => Pattern.Contains("/poker/", StringComparison.Ordinal);

    /// <summary>
    /// The pages a signed-out visitor may open: the welcome page, sign-in and the invite (UX18 decision 1). Every other
    /// page sends them to sign in first.
    /// </summary>
    public bool IsOpenToSignedOut => Pattern is "/" or "/login" or "/auth/*" or "/invite";

    public static (ShellRoute Route, IReadOnlyDictionary<string, string> Parameters)? Resolve(string path) {
        foreach (var route in All) {
            if (route.Match(path) is { } parameters) {
                return (route, parameters);
            }
        }
        return null;
    }

    public IReadOnlyDictionary<string, string>? Match(string path) {
        var have = path.Split('/');
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _segments.Length; i++) {
            var want = _segments[i];
            if (want == "*") {
                return parameters;
            }
            if (i >= have.Length) {
                return null;
            }
            if (want.StartsWith(':')) {
                if (have[i].Length == 0 || !TryDecode(have[i], out var value)) {
                    return null;
                }
                parameters[want[1..]] = value;
            } else if (want != have[i]) {
                return null;
            }
        }
        return _segments.Length == have.Length ? parameters : null;
    }

    // decodeURIComponent: a malformed escape makes the route unmatched (U10 decision 10).
    private static bool TryDecode(string segment, out string value) {
        value = "";
        try {
            var bytes = new List<byte>(segment.Length);
            var builder = new StringBuilder(segment.Length);
            for (var i = 0; i < segment.Length; i++) {
                if (segment[i] != '%') {
                    Flush(bytes, builder);
                    builder.Append(segment[i]);
                    continue;
                }
                if (i + 2 >= segment.Length || !char.IsAsciiHexDigit(segment[i + 1]) || !char.IsAsciiHexDigit(segment[i + 2])) {
                    return false;
                }
                bytes.Add(Convert.ToByte(segment.Substring(i + 1, 2), 16));
                i += 2;
            }
            Flush(bytes, builder);
            value = builder.ToString();
            return true;
        } catch (DecoderFallbackException) {
            return false;
        }
    }

    private static void Flush(List<byte> bytes, StringBuilder builder) {
        if (bytes.Count > 0) {
            builder.Append(StrictUtf8.GetString([.. bytes]));
            bytes.Clear();
        }
    }
}
