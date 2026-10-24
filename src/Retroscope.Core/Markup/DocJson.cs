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
            case DocParagraph paragraph:
                writer.WriteString("t", "p");
                WriteFormat(writer, paragraph.Format);
                break;
            case DocHeading heading:
                writer.WriteString("t", "h");
                writer.WriteNumber("level", heading.Level);
                WriteFormat(writer, heading.Format);
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
                WriteOptional(writer, "color", panel.Color);
                WriteOptional(writer, "icon", panel.Icon);
                break;
            case DocTable table:
                writer.WriteString("t", "table");
                WriteLayout(writer, table.Layout);
                if (table.Numbered) {
                    writer.WriteBoolean("numbered", true);
                }
                if (table.Width is { } tableWidth) {
                    writer.WriteNumber("width", tableWidth);
                }
                break;
            case DocTableRow:
                writer.WriteString("t", "tr");
                break;
            case DocTableCell cell:
                writer.WriteString("t", cell.Header ? "th" : "td");
                WriteCellFormat(writer, cell.Format);
                break;
            case DocLayoutSection:
                writer.WriteString("t", "layout");
                break;
            case DocLayoutColumn column:
                writer.WriteString("t", "col");
                writer.WriteNumber("width", column.Width);
                break;
            case DocMedia media:
                writer.WriteString("t", "media");
                WriteLayout(writer, media.Layout);
                if (media.Width is { } mediaWidth) {
                    writer.WriteNumber("width", mediaWidth);
                    writer.WriteString("unit", media.Unit == DocWidthUnit.Pixel ? "px" : "%");
                }
                if (media.Caption.Count > 0) {
                    WriteChildren(writer, media.Caption, "caption");
                }
                WriteChildren(writer, media.Items);
                writer.WriteEndObject();
                return;
            case DocMediaGroup:
                writer.WriteString("t", "files");
                break;
            case DocDecisionList:
                writer.WriteString("t", "decisions");
                break;
            case DocDecisionItem decision:
                writer.WriteString("t", "decision");
                writer.WriteBoolean("done", decision.Decided);
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
                WriteOptional(writer, "title", link.Title);
                break;
            case DocMarked marked:
                writer.WriteString("t", MarkTag(marked.Kind));
                WriteOptional(writer, "color", marked.Color);
                break;
            case DocUser user:
                writer.WriteString("t", "user");
                writer.WriteString("v", user.Name);
                break;
            case DocDate date:
                writer.WriteString("t", "date");
                writer.WriteString("v", date.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case DocCard card:
                writer.WriteString("t", "card");
                writer.WriteString("href", card.Href);
                if (card.Block) {
                    writer.WriteBoolean("block", true);
                }
                break;
            case DocFile file:
                writer.WriteString("t", "file");
                writer.WriteString("id", file.AttachmentId);
                writer.WriteString("name", file.Name);
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
                if (attachment.Width is { } imageWidth && attachment.Height is { } imageHeight) {
                    writer.WriteNumber("w", imageWidth);
                    writer.WriteNumber("h", imageHeight);
                }
                break;
            case DocStatus status:
                writer.WriteString("t", "status");
                writer.WriteString("v", status.Text);
                writer.WriteString("color", Lower(status.Color));
                if (status.Bold) {
                    writer.WriteBoolean("bold", true);
                }
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
            or DocAttachment or DocStatus or DocLineBreak or DocUser or DocDate or DocCard or DocFile);
    }

    private static void WriteChildren(Utf8JsonWriter writer, IEnumerable<DocNode> children, string name = "c") {
        writer.WriteStartArray(name);
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

    private static string MarkTag(DocMarkKind kind) {
        return kind switch {
            DocMarkKind.Sub => "sub",
            DocMarkKind.Sup => "sup",
            DocMarkKind.TextColor => "fg",
            DocMarkKind.Background => "bg",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value) {
        if (value != null) {
            writer.WriteString(name, value);
        }
    }

    private static void WriteFormat(Utf8JsonWriter writer, DocBlockFormat format) {
        if (format.Align != DocAlign.Start) {
            writer.WriteString("align", Lower(format.Align));
        }
        if (format.Indent > 0) {
            writer.WriteNumber("indent", format.Indent);
        }
    }

    private static void WriteCellFormat(Utf8JsonWriter writer, DocCellFormat format) {
        if (format.ColSpan > 1) {
            writer.WriteNumber("colspan", format.ColSpan);
        }
        if (format.RowSpan > 1) {
            writer.WriteNumber("rowspan", format.RowSpan);
        }
        if (format.ColumnWidths is { Count: > 0 } widths) {
            writer.WriteStartArray("colwidth");
            foreach (var width in widths) {
                writer.WriteNumberValue(width);
            }
            writer.WriteEndArray();
        }
        WriteOptional(writer, "bg", format.Background);
    }

    private static void WriteLayout(Utf8JsonWriter writer, DocLayout layout) {
        if (layout != DocLayout.Default) {
            writer.WriteString("layout", LayoutName(layout));
        }
    }

    private static string LayoutName(DocLayout layout) {
        return layout switch {
            DocLayout.Center => "center",
            DocLayout.Wide => "wide",
            DocLayout.FullWidth => "full-width",
            DocLayout.AlignStart => "align-start",
            DocLayout.AlignEnd => "align-end",
            DocLayout.WrapLeft => "wrap-left",
            DocLayout.WrapRight => "wrap-right",
            _ => "default",
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
