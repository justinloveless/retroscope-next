using System.Text.Json;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Adf;

// A Jira description ready for the editor (§f "Editing with opaque islands"). Islands maps each island id to the
// original top-level ADF block, verbatim, for MarkupToAdf to splice back. Editable is false only when the
// description is not an ADF doc at all, so nothing could be written back without loss.
public sealed class AdfDescription(Doc doc, IReadOnlyDictionary<string, string> islands, bool editable) {
    public Doc Doc { get; } = doc;

    public IReadOnlyDictionary<string, string> Islands { get; } = islands;

    public bool Editable { get; } = editable;
}

public static class AdfToDoc {
    public const int MaxDepth = 32;
    public const int MaxNodes = 5000;
    public const string TruncatedText = "content truncated, open in Jira";
    public const string MissingImageText = "image (open in Jira)";

    // Read-only rendering, for comments.
    public static Doc Render(JsonElement adf, IReadOnlyList<AdfAttachment> attachments) {
        var reader = new AdfReader(AdfMediaMap.Build(adf, attachments));
        var blocks = new List<DocBlock>();
        if (IsDoc(adf)) {
            blocks.AddRange(reader.Blocks(adf, 1));
        } else if (adf.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) {
            reader.Block(adf, 0, blocks);
        }
        if (reader.Truncated) {
            blocks.Add(TruncatedNotice());
        }
        return new Doc(blocks);
    }

    // Every top-level block that survives markup → ADF unchanged stays editable; every other one becomes an island.
    public static AdfDescription Description(JsonElement adf, IReadOnlyList<AdfAttachment> attachments) {
        if (adf.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) {
            return new AdfDescription(new Doc([]), new Dictionary<string, string>(), editable: true);
        }
        if (!IsDoc(adf)) {
            return new AdfDescription(Render(adf, attachments), new Dictionary<string, string>(), editable: false);
        }
        var reader = new AdfReader(AdfMediaMap.Build(adf, attachments));
        var blocks = new List<DocBlock>();
        var islands = new Dictionary<string, string>(StringComparer.Ordinal);
        var previousTruncated = false;
        foreach (var block in AdfJson.Children(adf)) {
            reader.Truncated = false;
            var rendered = new List<DocBlock>();
            reader.Block(block, 1, rendered);
            var truncated = reader.Truncated;
            if (!truncated && AdfLossiness.IsEditable(block, rendered)) {
                blocks.AddRange(rendered);
                previousTruncated = false;
                continue;
            }
            // Consecutive blocks past the size cap share one notice.
            if (truncated && !previousTruncated) {
                rendered.Add(TruncatedNotice());
            }
            previousTruncated = truncated;
            var id = AdfLossiness.IslandId(block);
            islands.TryAdd(id, block.GetRawText());
            blocks.Add(new DocIsland(id, rendered));
        }
        return new AdfDescription(new Doc(blocks), islands, editable: true);
    }

    private static bool IsDoc(JsonElement adf) => AdfJson.Type(adf) == "doc";

    private static DocParagraph TruncatedNotice() => new([new DocStyled(DocStyle.Italic, [new DocText(TruncatedText)])]);
}
