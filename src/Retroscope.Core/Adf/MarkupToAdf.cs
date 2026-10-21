using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Adf;

// Doc (parsed markup) → ADF for Jira comments, new issues and edited descriptions (§f). Island refs are replaced by
// the original ADF text, byte for byte. Output follows the ADF schema's nesting rules, so a few markup shapes are
// lowered: list items and quotes cannot hold headings, rules or quotes, and code cannot carry other marks.
public static class MarkupToAdf {
    public static string Write(Doc doc) {
        return TryWrite(doc, new Dictionary<string, string>(), out var adf, out var missing)
            ? adf
            : throw new ArgumentException($"Island {missing} needs its original ADF; use TryWrite.", nameof(doc));
    }

    // False when the Doc refers to an island the caller does not hold (the write must be refused as stale).
    public static bool TryWrite(
        Doc doc,
        IReadOnlyDictionary<string, string> islands,
        [NotNullWhen(true)] out string? adf,
        [NotNullWhen(false)] out string? missingIsland) {
        missingIsland = doc.Blocks.OfType<DocIsland>().Select(island => island.Id).FirstOrDefault(id => !islands.ContainsKey(id));
        if (missingIsland != null) {
            adf = null;
            return false;
        }
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("type", "doc");
            json.WriteNumber("version", 1);
            json.WriteStartArray("content");
            foreach (var block in doc.Blocks) {
                if (block is DocIsland island) {
                    json.WriteRawValue(islands[island.Id]);
                } else {
                    Block(json, block);
                }
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        adf = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return true;
    }

    private static void Block(Utf8JsonWriter json, DocBlock block) {
        json.WriteStartObject();
        switch (block) {
            case DocParagraph paragraph:
                json.WriteString("type", "paragraph");
                Inlines(json, paragraph.Content);
                break;
            case DocHeading heading:
                json.WriteString("type", "heading");
                json.WriteStartObject("attrs");
                json.WriteNumber("level", heading.Level);
                json.WriteEndObject();
                Inlines(json, heading.Content);
                break;
            case DocBulletList list:
                json.WriteString("type", "bulletList");
                ListItems(json, list.Items);
                break;
            case DocOrderedList list:
                json.WriteString("type", "orderedList");
                json.WriteStartObject("attrs");
                json.WriteNumber("order", list.Start);
                json.WriteEndObject();
                ListItems(json, list.Items);
                break;
            case DocQuote quote:
                json.WriteString("type", "blockquote");
                NestedBlocks(json, quote.Blocks);
                break;
            case DocCodeBlock code:
                json.WriteString("type", "codeBlock");
                if (code.Language != null) {
                    json.WriteStartObject("attrs");
                    json.WriteString("language", code.Language);
                    json.WriteEndObject();
                }
                json.WriteStartArray("content");
                if (code.Text.Length > 0) {
                    json.WriteStartObject();
                    json.WriteString("type", "text");
                    json.WriteString("text", code.Text);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                break;
            case DocRule:
                json.WriteString("type", "rule");
                break;
            default:
                throw Unrepresentable(block);
        }
        json.WriteEndObject();
    }

    private static void ListItems(Utf8JsonWriter json, IReadOnlyList<DocListItem> items) {
        json.WriteStartArray("content");
        foreach (var item in items) {
            json.WriteStartObject();
            json.WriteString("type", "listItem");
            NestedBlocks(json, item.Blocks);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // List items and quotes hold paragraphs, lists and code blocks only, and at least one of them. A list item
    // also starts with a paragraph or code block, so an item that opens with a nested list gets an empty paragraph.
    private static void NestedBlocks(Utf8JsonWriter json, IReadOnlyList<DocBlock> blocks) {
        var lowered = Lower(blocks).ToList();
        json.WriteStartArray("content");
        if (lowered.Count == 0 || lowered[0] is DocBulletList or DocOrderedList) {
            Block(json, new DocParagraph([]));
        }
        foreach (var block in lowered) {
            Block(json, block);
        }
        json.WriteEndArray();
    }

    private static IEnumerable<DocBlock> Lower(IEnumerable<DocBlock> blocks) {
        foreach (var block in blocks) {
            switch (block) {
                case DocHeading heading:
                    yield return new DocParagraph(heading.Content);
                    break;
                case DocQuote quote:
                    foreach (var inner in Lower(quote.Blocks)) {
                        yield return inner;
                    }
                    break;
                case DocRule:
                    break;
                default:
                    yield return block;
                    break;
            }
        }
    }

    private readonly record struct Run(string? Text, bool Code, string[] Marks);

    private static void Inlines(Utf8JsonWriter json, IReadOnlyList<DocInline> inlines) {
        var runs = new List<Run>();
        Flatten(inlines, [], runs);
        json.WriteStartArray("content");
        foreach (var run in runs) {
            json.WriteStartObject();
            if (run.Text == null) {
                json.WriteString("type", "hardBreak");
                json.WriteEndObject();
                continue;
            }
            json.WriteString("type", "text");
            json.WriteString("text", run.Text);
            if (run.Marks.Length > 0 || run.Code) {
                json.WriteStartArray("marks");
                foreach (var mark in run.Marks) {
                    Mark(json, mark);
                }
                if (run.Code) {
                    Mark(json, "code");
                }
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // Marks are written outer to inner; a link mark is "link:" + href. Adjacent text with the same marks merges.
    private static void Flatten(IReadOnlyList<DocInline> inlines, List<string> marks, List<Run> runs) {
        foreach (var inline in inlines) {
            switch (inline) {
                case DocText text:
                    AddText(runs, text.Value, code: false, marks);
                    break;
                case DocLineBreak:
                    runs.Add(new Run(null, false, []));
                    break;
                case DocCode code:
                    AddText(runs, code.Value, code: true, marks.Where(mark => mark.StartsWith("link:", StringComparison.Ordinal)).ToList());
                    break;
                case DocStyled styled:
                    Nested(styled.Content, MarkName(styled.Style), marks, runs);
                    break;
                case DocLink link:
                    Nested(link.Content.Count > 0 ? link.Content : [new DocText(link.Href)], "link:" + link.Href, marks, runs);
                    break;
                case DocMention mention:
                    AddText(runs, "@" + mention.Name, code: false, marks);
                    break;
                case DocImage image:
                    AddText(runs, image.Alt.Length > 0 ? image.Alt : "image", code: false, marks);
                    break;
                default:
                    throw Unrepresentable(inline);
            }
        }
    }

    // A mark already open (a link inside a link) is not repeated; the outer one wins.
    private static void Nested(IReadOnlyList<DocInline> content, string mark, List<string> marks, List<Run> runs) {
        var kind = mark.Split(':', 2)[0];
        if (marks.Any(open => open.Split(':', 2)[0] == kind)) {
            Flatten(content, marks, runs);
            return;
        }
        marks.Add(mark);
        Flatten(content, marks, runs);
        marks.RemoveAt(marks.Count - 1);
    }

    private static void AddText(List<Run> runs, string text, bool code, List<string> marks) {
        if (text.Length == 0) {
            return;
        }
        if (runs.Count > 0 && runs[^1] is { Text: { } previous } last && last.Code == code && last.Marks.SequenceEqual(marks)) {
            runs[^1] = last with { Text = previous + text };
            return;
        }
        runs.Add(new Run(text, code, marks.ToArray()));
    }

    private static void Mark(Utf8JsonWriter json, string mark) {
        json.WriteStartObject();
        if (mark.StartsWith("link:", StringComparison.Ordinal)) {
            json.WriteString("type", "link");
            json.WriteStartObject("attrs");
            json.WriteString("href", mark["link:".Length..]);
            json.WriteEndObject();
        } else {
            json.WriteString("type", mark);
        }
        json.WriteEndObject();
    }

    private static string MarkName(DocStyle style) {
        return style switch {
            DocStyle.Bold => "strong",
            DocStyle.Italic => "em",
            DocStyle.Strike => "strike",
            DocStyle.Underline => "underline",
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    private static ArgumentException Unrepresentable(DocNode node) {
        return new ArgumentException($"{node.GetType().Name} has no markup form and cannot be written as ADF.", nameof(node));
    }
}
