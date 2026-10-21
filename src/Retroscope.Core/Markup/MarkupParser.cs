using static Retroscope.Core.Markup.MarkupSyntax;

namespace Retroscope.Core.Markup;

// Total over every string: malformed syntax is literal text, never an error. The grammar is specified in
// tests/fixtures/markup/README.md and mirrored by web/src/markup/parse.js.
public static class MarkupParser {
    public static Doc Parse(string markup, MarkupDialect dialect) {
        var lines = markup.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return new Doc(new BlockParser(dialect).Parse(lines, 0, topLevel: true));
    }

    private sealed class BlockParser(MarkupDialect dialect) {
        private bool Jira => dialect != MarkupDialect.Retro;

        public List<DocBlock> Parse(IReadOnlyList<string> lines, int depth, bool topLevel) {
            var blocks = new List<DocBlock>();
            var i = 0;
            while (i < lines.Count) {
                var line = lines[i];
                if (IsBlank(line)) {
                    i++;
                } else if (TryFence(line, out var fenceLength, out var language)) {
                    blocks.Add(ParseFence(lines, ref i, fenceLength, language));
                } else if (depth < MaxBlockDepth && line[0] == '>') {
                    blocks.Add(ParseQuote(lines, ref i, depth));
                } else if (depth < MaxBlockDepth && TryListMarker(line, out var marker)) {
                    blocks.Add(ParseList(lines, ref i, marker, depth));
                } else if (Jira && TryHeading(line, out var level, out var content)) {
                    blocks.Add(new DocHeading(level, ParseInlines(content)));
                    i++;
                } else if (Jira && IsRule(line)) {
                    blocks.Add(new DocRule());
                    i++;
                } else if (topLevel && TryIsland(line, out var islandId)) {
                    blocks.Add(new DocIsland(islandId, []));
                    i++;
                } else {
                    blocks.Add(ParseParagraph(lines, ref i, depth, topLevel));
                }
            }
            return blocks;
        }

        private bool StartsBlock(string line, int depth, bool topLevel) {
            return TryFence(line, out _, out _)
                || depth < MaxBlockDepth && (line[0] == '>' || TryListMarker(line, out _))
                || Jira && (TryHeading(line, out _, out _) || IsRule(line))
                || topLevel && TryIsland(line, out _);
        }

        private DocParagraph ParseParagraph(IReadOnlyList<string> lines, ref int i, int depth, bool topLevel) {
            var parts = new List<string> { StripLine(lines[i]) };
            i++;
            while (i < lines.Count && !IsBlank(lines[i]) && !StartsBlock(lines[i], depth, topLevel)) {
                parts.Add(StripLine(lines[i]));
                i++;
            }
            return new DocParagraph(ParseInlines(string.Join('\n', parts)));
        }

        private static DocCodeBlock ParseFence(IReadOnlyList<string> lines, ref int i, int fenceLength, string? language) {
            var content = new List<string>();
            i++;
            while (i < lines.Count) {
                var line = lines[i];
                i++;
                if (IsClosingFence(line, fenceLength)) {
                    break;
                }
                content.Add(line);
            }
            return new DocCodeBlock(language, string.Join('\n', content));
        }

        private DocQuote ParseQuote(IReadOnlyList<string> lines, ref int i, int depth) {
            var inner = new List<string>();
            while (i < lines.Count && lines[i].Length > 0 && lines[i][0] == '>') {
                var line = lines[i];
                inner.Add(line.Length > 1 && line[1] == ' ' ? line[2..] : line[1..]);
                i++;
            }
            return new DocQuote(Parse(inner, depth + 1, topLevel: false));
        }

        private DocBlock ParseList(IReadOnlyList<string> lines, ref int i, ListMarker first, int depth) {
            var items = new List<DocListItem>();
            while (i < lines.Count && TryListMarker(lines[i], out var marker) && marker.Ordered == first.Ordered) {
                var itemLines = new List<string> { marker.Content };
                i++;
                while (i < lines.Count) {
                    var line = lines[i];
                    if (IsBlank(line)) {
                        var next = i;
                        while (next < lines.Count && IsBlank(lines[next])) {
                            next++;
                        }
                        if (next == lines.Count || Indent(lines[next]) < marker.Width) {
                            break;
                        }
                        for (; i < next; i++) {
                            itemLines.Add(lines[i][Math.Min(Indent(lines[i]), marker.Width)..]);
                        }
                    } else if (Indent(line) >= marker.Width) {
                        itemLines.Add(line[marker.Width..]);
                        i++;
                    } else {
                        break;
                    }
                }
                items.Add(new DocListItem(Parse(itemLines, depth + 1, topLevel: false)));
            }
            return first.Ordered ? new DocOrderedList(first.Start, items) : new DocBulletList(items);
        }

        private bool TryIsland(string line, out string id) {
            id = "";
            if (dialect != MarkupDialect.JiraDescription) {
                return false;
            }
            var trimmed = line.AsSpan().TrimEnd(" \t");
            if (!trimmed.StartsWith(IslandPrefix, StringComparison.Ordinal) || !trimmed.EndsWith("]]", StringComparison.Ordinal)) {
                return false;
            }
            var candidate = trimmed[IslandPrefix.Length..^2];
            if (!IsIslandId(candidate)) {
                return false;
            }
            id = candidate.ToString();
            return true;
        }

        private List<DocInline> ParseInlines(string text) {
            return new InlineParser(text, dialect == MarkupDialect.Retro).Parse();
        }
    }

    private static bool TryFence(string line, out int fenceLength, out string? language) {
        fenceLength = CountLeading(line, '`');
        language = null;
        if (fenceLength < 3) {
            return false;
        }
        var info = line.AsSpan(fenceLength).Trim(" \t");
        if (info.IsEmpty) {
            return true;
        }
        if (!IsLanguage(info)) {
            return false;
        }
        language = info.ToString();
        return true;
    }

    private static bool IsClosingFence(string line, int fenceLength) {
        var run = CountLeading(line, '`');
        return run >= fenceLength && IsBlank(line[run..]);
    }

    private static int Indent(string line) => CountLeading(line, ' ');

    private static string StripLine(string line) => line.Trim(' ', '\t');
}
