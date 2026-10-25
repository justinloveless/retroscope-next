using System.Text.Json;

namespace Retroscope.Server.Hosting;

/// <summary>
/// Adds members to the boot JSON of the shell and of <c>/api/boot</c>: auth adds <c>me</c> and the user's
/// <c>theme</c>, the room endpoints add the viewer's snapshot. Contributors run in registration order.
/// </summary>
public interface IBootContributor {
    ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a signed-in caller of a shell page elsewhere before any boot contributor runs, e.g. <c>/</c> to the user's
/// team (UX18 decision 8). The first redirect that answers wins.
/// </summary>
public interface IShellRedirect {
    /// <returns>The same-origin path to redirect to, or null to serve the page.</returns>
    ValueTask<string?> RedirectAsync(ShellRoute route, Caller caller, CancellationToken cancellationToken);
}

/// <summary>One boot document under construction.</summary>
public sealed class BootContext {
    private static readonly string[] Themes = ["light", "dark", "system"];
    private readonly List<KeyValuePair<string, JsonElement>> _members = [];
    private string _theme = "system";

    public BootContext(HttpContext http, ShellRoute route, IReadOnlyDictionary<string, string> parameters) {
        Http = http;
        Route = route;
        Parameters = parameters;
    }

    public HttpContext Http { get; }

    public ShellRoute Route { get; }

    /// <summary>The decoded path parameters, e.g. <c>code</c> for <c>/poker/:code</c>.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The user's preference; <c>theme-boot.js</c> reads it before <c>localStorage</c>.</summary>
    public string Theme {
        get => _theme;
        set => _theme = Themes.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ArgumentException($"Theme '{value}' is not light, dark or system.", nameof(value));
    }

    public IReadOnlyList<KeyValuePair<string, JsonElement>> Members => _members;

    /// <summary>Sets a top-level member; serialize the value with the contributor's own source-generated context.</summary>
    public void Set(string name, JsonElement value) {
        if (name is "build" or "theme") {
            throw new ArgumentException($"'{name}' is written by the shell.", nameof(name));
        }
        _members.RemoveAll(x => x.Key == name);
        _members.Add(new(name, value));
    }

    /// <summary>
    /// Writes <c>{build, theme, …members}</c>. The encoder escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>, so the
    /// document can sit inside a <c>&lt;script&gt;</c> element and never close it.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer, string buildId) {
        writer.WriteStartObject();
        writer.WriteString("build", buildId);
        writer.WriteString("theme", _theme);
        foreach (var (name, value) in _members) {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
