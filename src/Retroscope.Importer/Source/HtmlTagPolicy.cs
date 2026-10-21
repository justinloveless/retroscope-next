using System.Collections.Frozen;
using System.Text.Json;

namespace Retroscope.Importer.Source;

/// <summary>
/// What the HTML → markup converter (DESIGN §h.4) does with each tag. The histogram fails on anything else, so a tag
/// the converter has never seen cannot lose content silently. U41's converter uses the same sets.
/// </summary>
public static class HtmlTagPolicy {
    public const string TextOnlyResourceName = "Retroscope.Importer.Source.html-textonly.json";

    /// <summary>Tags with a markup equivalent.</summary>
    public static FrozenSet<string> Converted { get; } = FrozenSet.Create(StringComparer.Ordinal,
        "strong", "b", "em", "i", "s", "strike", "code", "pre", "p", "div", "br", "ul", "ol", "li", "blockquote", "a", "img");

    /// <summary>Tags that contribute nothing, not even their text.</summary>
    public static FrozenSet<string> Dropped { get; } = FrozenSet.Create(StringComparer.Ordinal, "script", "style", "iframe");

    private static readonly Lock _gate = new();
    private static FrozenSet<string> _textOnly = LoadEmbeddedTextOnly();

    /// <summary>
    /// Tags that contribute only their text: <c>Source/html-textonly.json</c>, plus any tags the operator adds with
    /// <c>--html-textonly &lt;path&gt;</c> (<see cref="AddTextOnly"/>).
    /// </summary>
    public static FrozenSet<string> TextOnly => Volatile.Read(ref _textOnly);

    public static bool IsKnown(string tag) => Converted.Contains(tag) || Dropped.Contains(tag) || TextOnly.Contains(tag);

    /// <summary>
    /// Adds the tags of an operator's file (same shape as <c>html-textonly.json</c>) to <see cref="TextOnly"/> for the
    /// rest of the process, so a tag found by <c>plan</c> can be accepted without a rebuild. The built-in tags stay.
    /// </summary>
    /// <exception cref="SettingsException">The file is unreadable, malformed, or names a converted or dropped tag.</exception>
    public static void AddTextOnly(string path) {
        var tags = ReadTextOnlyFile(path);
        lock (_gate) {
            Volatile.Write(ref _textOnly, TextOnly.Union(tags).ToFrozenSet(StringComparer.Ordinal));
        }
    }

    internal static IReadOnlyList<string> ReadTextOnlyFile(string path) {
        IReadOnlyList<string> tags;
        try {
            using var stream = File.OpenRead(path);
            tags = ParseTags(stream);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException) {
            throw new SettingsException($"--html-textonly {path}: {exception.Message}");
        }

        foreach (var tag in tags) {
            if (!IsTagName(tag)) {
                throw new SettingsException($"--html-textonly {path}: \"{tag}\" is not a lower-case tag name.");
            }
            if (Converted.Contains(tag) || Dropped.Contains(tag)) {
                throw new SettingsException($"--html-textonly {path}: <{tag}> is already converted or dropped and cannot be text-only.");
            }
        }
        return tags;
    }

    private static bool IsTagName(string tag) =>
        tag.Length > 0 && char.IsAsciiLetterLower(tag[0]) && tag.All(x => char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x == '-');

    private static FrozenSet<string> LoadEmbeddedTextOnly() {
        using var stream = typeof(HtmlTagPolicy).Assembly.GetManifestResourceStream(TextOnlyResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {TextOnlyResourceName}.");
        return ParseTags(stream).ToFrozenSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ParseTags(Stream stream) {
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString() ?? "")];
    }
}
