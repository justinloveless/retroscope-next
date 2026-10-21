using System.Text;
using static Retroscope.Core.Markup.MarkupSyntax;

namespace Retroscope.Core.Markup;

// Doc → canonical markup. For any Doc the parser produced, Parse(Write(doc)) yields the same Doc.
// ADF-only nodes (panel, table, expand, task list, underline, status, attachment) have no markup form;
// they reach the editor as islands, so writing one is a caller bug.
public static class MarkupWriter {
    public static string Write(Doc doc) => WriteBlocks(doc.Blocks);

    // Blocks are separated by a blank line, except that a list follows a paragraph directly: list markers interrupt
    // a paragraph, so "- a\n  - b" keeps the shape people type.
    private static string WriteBlocks(IReadOnlyList<DocBlock> blocks) {
        var output = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++) {
            if (i > 0) {
                var tight = blocks[i - 1] is DocParagraph && blocks[i] is DocBulletList or DocOrderedList;
                output.Append(tight ? "\n" : "\n\n");
            }
            output.Append(WriteBlock(blocks[i]));
        }
        return output.ToString();
    }

    private static string WriteBlock(DocBlock block) {
        return block switch {
            DocParagraph paragraph => EscapeLineStarts(InlineWriter.Write(paragraph.Content)),
            DocHeading heading => $"{new string('#', heading.Level)} {InlineWriter.Write(heading.Content).Replace('\n', ' ')}",
            DocBulletList list => string.Join('\n', list.Items.Select(item => WriteItem("-", item))),
            DocOrderedList list => string.Join('\n', list.Items.Select((item, index) => WriteItem($"{ItemNumber(list.Start, index)}.", item))),
            DocQuote quote => PrefixLines(WriteBlocks(quote.Blocks)),
            DocCodeBlock code => WriteCodeBlock(code),
            DocRule => "---",
            DocIsland island => $"{IslandPrefix}{island.Id}]]",
            _ => throw Unrepresentable(block),
        };
    }

    // Item numbers after the first are not read back, but must stay a valid marker (at most nine digits).
    private static int ItemNumber(int start, int index) {
        return (long)start + index <= 999_999_999 ? start + index : start;
    }

    private static string WriteItem(string marker, DocListItem item) {
        var body = WriteBlocks(item.Blocks);
        if (body.Length == 0) {
            return marker;
        }
        var indent = new string(' ', marker.Length + 1);
        var lines = body.Split('\n');
        var output = new StringBuilder().Append(marker).Append(' ').Append(lines[0]);
        foreach (var line in lines.Skip(1)) {
            output.Append('\n');
            if (line.Length > 0) {
                output.Append(indent).Append(line);
            }
        }
        return output.ToString();
    }

    private static string PrefixLines(string body) {
        return string.Join('\n', body.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));
    }

    private static string WriteCodeBlock(DocCodeBlock code) {
        var longestRun = code.Text.Split('\n').Max(line => CountLeading(line, '`'));
        var fence = new string('`', Math.Max(3, longestRun + 1));
        var language = code.Language != null && IsLanguage(code.Language) ? code.Language : "";
        return code.Text.Length == 0 ? $"{fence}{language}\n{fence}" : $"{fence}{language}\n{code.Text}\n{fence}";
    }

    // A rendered line can only begin with a block marker character if that character came from text.
    private static string EscapeLineStarts(string paragraph) {
        var lines = paragraph.Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i];
            if (line.Length == 0) {
                continue;
            }
            if (TryListMarker(line, out var marker) && marker.Ordered) {
                var dot = line.IndexOf('.', StringComparison.Ordinal);
                lines[i] = line[..dot] + "\\" + line[dot..];
            } else if (line[0] == '>' || TryListMarker(line, out _) || TryHeading(line, out _, out _) || IsRule(line)) {
                lines[i] = "\\" + line;
            }
        }
        return string.Join('\n', lines);
    }

    internal static ArgumentException Unrepresentable(DocNode node) {
        return new ArgumentException($"{node.GetType().Name} has no markup form; ADF-only content must be an island.", nameof(node));
    }
}
