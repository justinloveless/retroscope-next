using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Core.Markup;

// Wire shape shared with web/src/markup and tests/fixtures/markup (see tests/fixtures/markup/README.md):
// every node is {"t": kind, ...}; container children are "c", scalar content is "v".
public static class DocJson {
    public static string Serialize(Doc doc) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            Write(writer, doc);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void Write(Utf8JsonWriter writer, Doc doc) {
        writer.WriteStartObject();
        writer.WriteString("t", "doc");
        WriteChildren(writer, doc.Blocks);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, DocNode node) {
        writer.WriteStartObject();
        switch (node) {
            case DocParagraph:
                writer.WriteString("t", "p");
                break;
            case DocHeading heading:
                writer.WriteString("t", "h");
                writer.WriteNumber("level", heading.Level);
                break;
            case DocBulletList:
                writer.WriteString("t", "ul");
                break;
            case DocOrderedList ordered:
                writer.WriteString("t", "ol");
                writer.WriteNumber("start", ordered.Start);
                break;
            case DocListItem:
                writer.WriteString("t", "li");
                break;
            case DocQuote:
                writer.WriteString("t", "quote");
                break;
            case DocCodeBlock code:
                writer.WriteString("t", "pre");
                if (code.Language != null) {
                    writer.WriteString("lang", code.Language);
                }
                writer.WriteString("v", code.Text);
                break;
            case DocRule:
                writer.WriteString("t", "hr");
                break;
            case DocIsland island:
                writer.WriteString("t", "island");
                writer.WriteString("id", island.Id);
                break;
            case DocPanel panel:
                writer.WriteString("t", "panel");
                writer.WriteString("kind", Lower(panel.Kind));
                break;
            case DocTable:
                writer.WriteString("t", "table");
                break;
            case DocTableRow:
                writer.WriteString("t", "tr");
                break;
            case DocTableCell cell:
                writer.WriteString("t", cell.Header ? "th" : "td");
                break;
            case DocExpand expand:
                writer.WriteString("t", "expand");
                writer.WriteString("title", expand.Title);
                break;
            case DocTaskList:
                writer.WriteString("t", "tasks");
                break;
            case DocTaskItem task:
                writer.WriteString("t", "task");
                writer.WriteBoolean("done", task.Done);
                break;
            case DocText text:
                writer.WriteString("t", "text");
                writer.WriteString("v", text.Value);
                break;
            case DocStyled styled:
                writer.WriteString("t", StyleTag(styled.Style));
                break;
            case DocCode code:
                writer.WriteString("t", "code");
                writer.WriteString("v", code.Value);
                break;
            case DocLink link:
                writer.WriteString("t", "a");
                writer.WriteString("href", link.Href);
                break;
            case DocMention mention:
                writer.WriteString("t", "mention");
                writer.WriteString("id", mention.UserId.ToString("D"));
                writer.WriteString("name", mention.Name);
                break;
            case DocImage image:
                writer.WriteString("t", "img");
                writer.WriteString("blob", image.BlobId.ToString("D"));
                writer.WriteString("alt", image.Alt);
                break;
            case DocAttachment attachment:
                writer.WriteString("t", "attachment");
                writer.WriteString("id", attachment.AttachmentId);
                writer.WriteString("alt", attachment.Alt);
                break;
            case DocStatus status:
                writer.WriteString("t", "status");
                writer.WriteString("v", status.Text);
                writer.WriteString("color", Lower(status.Color));
                break;
            case DocLineBreak:
                writer.WriteString("t", "br");
                break;
            default:
                throw new ArgumentException($"Unknown node {node.GetType().Name}", nameof(node));
        }
        if (HasChildList(node)) {
            WriteChildren(writer, node.Children);
        }
        writer.WriteEndObject();
    }

    private static bool HasChildList(DocNode node) {
        return node is not (DocCodeBlock or DocRule or DocText or DocCode or DocMention or DocImage
            or DocAttachment or DocStatus or DocLineBreak);
    }

    private static void WriteChildren(Utf8JsonWriter writer, IEnumerable<DocNode> children) {
        writer.WriteStartArray("c");
        foreach (var child in children) {
            WriteNode(writer, child);
        }
        writer.WriteEndArray();
    }

    private static string StyleTag(DocStyle style) {
        return style switch {
            DocStyle.Bold => "b",
            DocStyle.Italic => "i",
            DocStyle.Strike => "s",
            DocStyle.Underline => "u",
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum {
        return value.ToString().ToLowerInvariant();
    }
}

public sealed class DocJsonConverter : JsonConverter<Doc> {
    public override Doc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        throw new NotSupportedException("Docs are produced by the server and never read back; send markup instead.");
    }

    public override void Write(Utf8JsonWriter writer, Doc value, JsonSerializerOptions options) {
        DocJson.Write(writer, value);
    }
}
