using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Adf;

// Decides which top-level ADF blocks the markup editor may own (§f, F5). A block is editable only when it uses the
// round-trippable subset and its rendering, written as markup, parsed again and converted back to ADF, carries the
// same content. Everything else becomes an island and is written back verbatim, so no save is ever lossy.
internal static class AdfLossiness {
    private const int MaxEditableDepth = 32;

    public static bool IsEditable(JsonElement block, IReadOnlyList<DocBlock> rendered) {
        if (!IsRoundTrippableShape(block, "doc", 0)) {
            return false;
        }
        var markup = MarkupWriter.Write(new Doc(rendered));
        var adf = MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.JiraDescription));
        using var written = JsonDocument.Parse(adf);
        return Project([block]) == Project(AdfJson.Children(written.RootElement));
    }

    // sha256 of the canonical subtree, first 12 bytes, base64url: 16 characters, a valid markup island id.
    public static string IslandId(JsonElement block) {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            WriteCanonical(json, block);
        }
        var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return Base64Url.EncodeToString(hash.AsSpan(0, 12));
    }

    // Canonical form: object members sorted by ordinal name, no whitespace, strings re-encoded, numbers as written.
    private static void WriteCanonical(Utf8JsonWriter json, JsonElement element) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var member in element.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal)) {
                    json.WritePropertyName(member.Name);
                    WriteCanonical(json, member.Value);
                }
                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray()) {
                    WriteCanonical(json, item);
                }
                json.WriteEndArray();
                break;
            case JsonValueKind.String:
                json.WriteStringValue(AdfJson.String(element));
                break;
            default:
                json.WriteRawValue(element.GetRawText());
                break;
        }
    }

    // The editable subset (§f): paragraph, heading, lists, codeBlock, blockquote and rule, with text, hardBreak and
    // the strong/em/strike/code/link marks, nested the way the schema and the markup grammar both allow. Any
    // other member, attribute or mark (alignment, colour, link titles, code block options) makes the block an island.
    private static bool IsRoundTrippableShape(JsonElement node, string parent, int depth) {
        if (depth > MaxEditableDepth || node.ValueKind != JsonValueKind.Object) {
            return false;
        }
        var type = AdfJson.Type(node);
        if (type == null || !Allowed(parent).Contains(type)) {
            return false;
        }
        foreach (var member in node.EnumerateObject()) {
            var ok = member.Name switch {
                "type" => true,
                "attrs" => AttrsAllowed(type, member.Value),
                "content" => type != "text" && type != "rule" && type != "hardBreak" && member.Value.ValueKind == JsonValueKind.Array
                    && member.Value.EnumerateArray().All(child => IsRoundTrippableShape(child, type, depth + 1)),
                "marks" => member.Value.ValueKind == JsonValueKind.Array
                    && (type == "text" && parent != "codeBlock"
                        ? member.Value.EnumerateArray().All(MarkAllowed)
                        : member.Value.GetArrayLength() == 0),
                "text" => type == "text" && TextAllowed(AdfJson.String(member.Value), parent),
                _ => false,
            };
            if (!ok) {
                return false;
            }
        }
        return type != "text" || AdfJson.Text(node) != null;
    }

    private static readonly string[] TopLevel = ["paragraph", "heading", "bulletList", "orderedList", "codeBlock", "blockquote", "rule"];
    private static readonly string[] Nestable = ["paragraph", "bulletList", "orderedList", "codeBlock"];
    private static readonly string[] Inline = ["text", "hardBreak"];
    private static readonly string[] CodeText = ["text"];
    private static readonly string[] ListItems = ["listItem"];

    private static string[] Allowed(string parent) {
        return parent switch {
            "doc" => TopLevel,
            "listItem" or "blockquote" => Nestable,
            "paragraph" or "heading" => Inline,
            "codeBlock" => CodeText,
            "bulletList" or "orderedList" => ListItems,
            _ => [],
        };
    }

    private static bool AttrsAllowed(string type, JsonElement attrs) {
        if (attrs.ValueKind != JsonValueKind.Object) {
            return false;
        }
        foreach (var attr in attrs.EnumerateObject()) {
            var ok = (type, attr.Name) switch {
                (_, "localId") => true,
                ("heading", "level") => attr.Value.ValueKind == JsonValueKind.Number,
                ("orderedList", "order") => attr.Value.ValueKind == JsonValueKind.Number,
                ("codeBlock", "language") => attr.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                ("hardBreak", "text") => true,
                _ => false,
            };
            if (!ok) {
                return false;
            }
        }
        return true;
    }

    private static bool MarkAllowed(JsonElement mark) {
        if (mark.ValueKind != JsonValueKind.Object) {
            return false;
        }
        var type = AdfJson.Type(mark);
        foreach (var member in mark.EnumerateObject()) {
            var ok = member.Name switch {
                "type" => type is "strong" or "em" or "strike" or "code" or "link",
                "attrs" => type == "link"
                    ? member.Value.ValueKind == JsonValueKind.Object && member.Value.EnumerateObject().All(attr => attr.Name == "href")
                    : member.Value.ValueKind == JsonValueKind.Object && !member.Value.EnumerateObject().Any(),
                _ => false,
            };
            if (!ok) {
                return false;
            }
        }
        return type != "link" || AdfJson.Attr(mark, "href") != null;
    }

    // Stored markup refuses C0 controls (except tab) and lone surrogates; code keeps its line breaks.
    private static bool TextAllowed(string? text, string parent) {
        if (string.IsNullOrEmpty(text)) {
            return false;
        }
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
            } else if (char.IsSurrogate(c) || c == '\uFFFD' || c < ' ' && c != '\t' && !(c == '\n' && parent == "codeBlock")) {
                return false;
            }
        }
        return true;
    }

    // What a reader sees, independent of how ADF happens to encode it: node kinds, levels, list starts, code
    // language and text, and for each character its mark set. Differences that do not show are folded away: local
    // ids, empty paragraphs, whitespace at line edges (markup trims it), bold/italic/strike on whitespace, trailing
    // hard breaks, and characters in an href that markup percent-encodes.
    internal static string Project(IEnumerable<JsonElement> blocks) {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartArray();
            foreach (var block in blocks) {
                ProjectBlock(json, block);
            }
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void ProjectBlock(Utf8JsonWriter json, JsonElement node) {
        var type = AdfJson.Type(node);
        switch (type) {
            case "paragraph":
            case "heading":
                var lines = Lines(node);
                if (lines.Count == 0) {
                    return;
                }
                json.WriteStartArray();
                json.WriteStringValue(type);
                if (type == "heading") {
                    json.WriteNumberValue(AdfJson.Number(node, "level") ?? 0);
                }
                foreach (var line in lines) {
                    json.WriteStartArray();
                    foreach (var (text, marks) in line) {
                        json.WriteStringValue(text);
                        json.WriteStringValue(marks);
                    }
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                return;
            case "codeBlock":
                json.WriteStartArray();
                json.WriteStringValue(type);
                json.WriteStringValue(AdfJson.Attr(node, "language") ?? "");
                json.WriteStringValue(string.Concat(AdfJson.Children(node).Select(AdfJson.Text)));
                json.WriteEndArray();
                return;
            default:
                json.WriteStartArray();
                json.WriteStringValue(type);
                if (type == "orderedList") {
                    json.WriteNumberValue(AdfJson.Number(node, "order") ?? 1);
                }
                foreach (var child in AdfJson.Children(node)) {
                    ProjectBlock(json, child);
                }
                json.WriteEndArray();
                return;
        }
    }

    // Lines of (text, mark key) runs; a paragraph with no visible text has no lines.
    private static List<List<(string Text, string Marks)>> Lines(JsonElement node) {
        var lines = new List<List<(char Char, string Marks)>> { new() };
        foreach (var child in AdfJson.Children(node)) {
            if (AdfJson.Type(child) == "hardBreak") {
                lines.Add([]);
                continue;
            }
            var text = AdfJson.Text(child) ?? "";
            var marks = MarkKey(child, whitespace: false);
            var whitespaceMarks = MarkKey(child, whitespace: true);
            foreach (var c in text) {
                lines[^1].Add((c, c is ' ' or '\t' ? whitespaceMarks : marks));
            }
        }
        var trimmed = lines.Select(TrimEdges).ToList();
        while (trimmed.Count > 0 && trimmed[^1].Count == 0) {
            trimmed.RemoveAt(trimmed.Count - 1);
        }
        return trimmed.Select(Runs).ToList();
    }

    private static List<(char Char, string Marks)> TrimEdges(List<(char Char, string Marks)> line) {
        var start = 0;
        var end = line.Count;
        while (start < end && line[start].Char is ' ' or '\t') {
            start++;
        }
        while (end > start && line[end - 1].Char is ' ' or '\t') {
            end--;
        }
        return line.GetRange(start, end - start);
    }

    private static List<(string Text, string Marks)> Runs(List<(char Char, string Marks)> line) {
        var runs = new List<(string Text, string Marks)>();
        var text = new StringBuilder();
        for (var i = 0; i < line.Count; i++) {
            text.Append(line[i].Char);
            if (i + 1 == line.Count || line[i + 1].Marks != line[i].Marks) {
                runs.Add((text.ToString(), line[i].Marks));
                text.Clear();
            }
        }
        return runs;
    }

    private static string MarkKey(JsonElement text, bool whitespace) {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var mark in AdfJson.Children(text, "marks")) {
            var type = AdfJson.Type(mark) ?? "?";
            if (whitespace && type is "strong" or "em" or "strike") {
                continue;
            }
            keys.Add(type == "link" ? "link:" + AdfHref.Normalize(AdfJson.Attr(mark, "href") ?? "") : type);
        }
        return string.Join('\n', keys);
    }
}
