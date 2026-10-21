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

    /// <summary>Tags that contribute only their text (<c>Source/html-textonly.json</c>, extended by the operator).</summary>
    public static FrozenSet<string> TextOnly { get; } = LoadTextOnly();

    public static bool IsKnown(string tag) => Converted.Contains(tag) || Dropped.Contains(tag) || TextOnly.Contains(tag);

    private static FrozenSet<string> LoadTextOnly() {
        using var stream = typeof(HtmlTagPolicy).Assembly.GetManifestResourceStream(TextOnlyResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {TextOnlyResourceName}.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToFrozenSet(StringComparer.Ordinal);
    }
}
