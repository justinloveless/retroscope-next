using System.Globalization;
using System.Text;
using System.Text.Json;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Adf;

// The allow-list walker behind AdfToDoc (§f). Known nodes map to their Doc kinds, unknown containers are
// flattened to their children and unknown leaves to their text. Every node visited counts against the size cap.
internal sealed class AdfReader(AdfMediaMap media) {
    private enum MarkKind {
        Link,
        Bold,
        Italic,
        Strike,
        Underline,
    }

    private readonly record struct Mark(MarkKind Kind, string Href);

    private readonly record struct Piece(DocInline Leaf, Mark[] Marks);

    private int _nodes;

    // Set when a node was skipped for depth or size; cleared by the caller per block.
    public bool Truncated { get; set; }

    public List<DocBlock> Blocks(JsonElement parent, int depth) {
        var blocks = new List<DocBlock>();
        var inlines = new List<Piece>();
        foreach (var child in AdfJson.Children(parent)) {
            if (IsInline(AdfJson.Type(child))) {
                if (Enter(depth)) {
                    Inline(child, depth, inlines);
                }
                continue;
            }
            AddParagraph(blocks, inlines);
            Block(child, depth, blocks);
        }
        AddParagraph(blocks, inlines);
        return blocks;
    }

    public void Block(JsonElement node, int depth, List<DocBlock> output) {
        if (!Enter(depth)) {
            return;
        }
        var inner = depth + 1;
        switch (AdfJson.Type(node)) {
            case "paragraph":
            case "taskItem":
            case "decisionItem":
                AddParagraph(output, Pieces(node, inner));
                break;
            case "heading":
                var heading = TrimLines(Group(Pieces(node, inner)));
                if (!IsBlank(heading)) {
                    output.Add(new DocHeading(HeadingLevel(node), heading));
                }
                break;
            case "bulletList":
                AddList(output, ListItems(node, inner), items => new DocBulletList(items));
                break;
            case "orderedList":
                AddList(output, ListItems(node, inner), items => new DocOrderedList(OrderedStart(node), items));
                break;
            case "decisionList":
                AddList(output, DecisionItems(node, inner), items => new DocBulletList(items));
                break;
            case "codeBlock":
                output.Add(new DocCodeBlock(CodeLanguage(node), CodeText(node, inner)));
                break;
            case "blockquote":
                output.Add(new DocQuote(Blocks(node, inner)));
                break;
            case "rule":
                output.Add(new DocRule());
                break;
            case "panel":
                output.Add(new DocPanel(PanelKind(AdfJson.Attr(node, "panelType")), Blocks(node, inner)));
                break;
            case "expand":
            case "nestedExpand":
                output.Add(new DocExpand(AdfJson.Attr(node, "title") ?? "", Blocks(node, inner)));
                break;
            case "table":
                output.Add(new DocTable(TableRows(node, inner)));
                break;
            case "taskList":
                var tasks = new List<DocTaskItem>();
                TaskItems(node, inner, tasks);
                if (tasks.Count > 0) {
                    output.Add(new DocTaskList(tasks));
                }
                break;
            case "mediaSingle":
            case "mediaGroup":
                MediaBlock(node, inner, output);
                break;
            case "media":
                AddParagraph(output, [new Piece(Media(node), [])]);
                break;
            case "blockCard":
            case "embedCard":
                AddParagraph(output, [new Piece(Card(node), [])]);
                break;
            case var type when IsInline(type):
                var pieces = new List<Piece>();
                Inline(node, depth, pieces);
                AddParagraph(output, pieces);
                break;
            default:
                Flatten(node, inner, output);
                break;
        }
    }

    // Counts the node; false when it lies beyond the depth or size cap and must be skipped.
    private bool Enter(int depth) {
        if (depth > AdfToDoc.MaxDepth || _nodes >= AdfToDoc.MaxNodes) {
            Truncated = true;
            return false;
        }
        _nodes++;
        return true;
    }

    private void Flatten(JsonElement node, int depth, List<DocBlock> output) {
        if (AdfJson.Children(node).Any()) {
            output.AddRange(Blocks(node, depth));
            return;
        }
        var text = AdfJson.Text(node) ?? AdfJson.Attr(node, "text");
        if (!string.IsNullOrEmpty(text)) {
            AddParagraph(output, [new Piece(new DocText(text), [])]);
        }
    }

    private List<DocListItem> ListItems(JsonElement list, int depth) {
        var items = new List<DocListItem>();
        foreach (var child in AdfJson.Children(list)) {
            if (AdfJson.Type(child) == "listItem") {
                if (Enter(depth)) {
                    items.Add(new DocListItem(Blocks(child, depth + 1)));
                }
            } else {
                var blocks = new List<DocBlock>();
                Block(child, depth, blocks);
                items.Add(new DocListItem(blocks));
            }
        }
        return items;
    }

    private List<DocListItem> DecisionItems(JsonElement list, int depth) {
        var items = new List<DocListItem>();
        foreach (var child in AdfJson.Children(list)) {
            var blocks = new List<DocBlock>();
            Block(child, depth, blocks);
            items.Add(new DocListItem(blocks));
        }
        return items;
    }

    private static void AddList(List<DocBlock> output, List<DocListItem> items, Func<List<DocListItem>, DocBlock> create) {
        if (items.Count > 0) {
            output.Add(create(items));
        }
    }

    private List<DocTableRow> TableRows(JsonElement table, int depth) {
        var rows = new List<DocTableRow>();
        foreach (var row in AdfJson.Children(table)) {
            if (!Enter(depth)) {
                continue;
            }
            var cells = new List<DocTableCell>();
            foreach (var cell in AdfJson.Children(row)) {
                if (Enter(depth + 1)) {
                    cells.Add(new DocTableCell(AdfJson.Type(cell) == "tableHeader", Blocks(cell, depth + 2)));
                }
            }
            rows.Add(new DocTableRow(cells));
        }
        return rows;
    }

    // Nested task lists are flattened into their parent: the Doc has no task indentation.
    private void TaskItems(JsonElement list, int depth, List<DocTaskItem> output) {
        foreach (var child in AdfJson.Children(list)) {
            if (!Enter(depth)) {
                continue;
            }
            var type = AdfJson.Type(child);
            if (type == "taskList") {
                TaskItems(child, depth + 1, output);
                continue;
            }
            var done = AdfJson.Attr(child, "state") == "DONE";
            var pieces = type == "taskItem" ? Pieces(child, depth + 1) : BlockPieces(child, depth + 1);
            output.Add(new DocTaskItem(done, TrimLines(Group(pieces))));
        }
    }

    // A block task item holds paragraphs; they become lines of one task.
    private List<Piece> BlockPieces(JsonElement node, int depth) {
        var pieces = new List<Piece>();
        foreach (var block in Blocks(node, depth)) {
            if (pieces.Count > 0) {
                pieces.Add(new Piece(new DocLineBreak(), []));
            }
            pieces.Add(new Piece(new DocText(PlainText(block)), []));
        }
        return pieces;
    }

    private void MediaBlock(JsonElement node, int depth, List<DocBlock> output) {
        var media = new List<Piece>();
        var caption = new List<Piece>();
        foreach (var child in AdfJson.Children(node)) {
            if (!Enter(depth)) {
                continue;
            }
            if (AdfJson.Type(child) == "caption") {
                Collect(child, depth + 1, caption);
            } else {
                media.Add(new Piece(Media(child), []));
            }
        }
        AddParagraph(output, media);
        AddParagraph(output, caption);
    }

    private List<Piece> Pieces(JsonElement node, int depth) {
        var pieces = new List<Piece>();
        Collect(node, depth, pieces);
        return pieces;
    }

    private void Collect(JsonElement parent, int depth, List<Piece> pieces) {
        foreach (var child in AdfJson.Children(parent)) {
            if (Enter(depth)) {
                Inline(child, depth, pieces);
            }
        }
    }

    private void Inline(JsonElement node, int depth, List<Piece> pieces) {
        switch (AdfJson.Type(node)) {
            case "text":
                AddText(pieces, AdfJson.Text(node) ?? "", node);
                break;
            case "hardBreak":
                pieces.Add(new Piece(new DocLineBreak(), []));
                break;
            case "mention":
                var name = AdfJson.Attr(node, "text") ?? "";
                pieces.Add(new Piece(new DocText(name.StartsWith('@') ? name : "@" + name), []));
                break;
            case "emoji":
                AddPlain(pieces, AdfJson.Attr(node, "text") ?? AdfJson.Attr(node, "shortName"));
                break;
            case "date":
                AddPlain(pieces, FormatDate(AdfJson.Attr(node, "timestamp")));
                break;
            case "status":
                var text = AdfJson.Attr(node, "text");
                if (!string.IsNullOrEmpty(text)) {
                    pieces.Add(new Piece(new DocStatus(text, StatusColor(AdfJson.Attr(node, "color"))), []));
                }
                break;
            case "inlineCard":
                pieces.Add(new Piece(Card(node), []));
                break;
            case "media":
            case "mediaInline":
                pieces.Add(new Piece(Media(node), []));
                break;
            case "placeholder":
                break;
            default:
                if (AdfJson.Children(node).Any()) {
                    Collect(node, depth + 1, pieces);
                } else {
                    AddPlain(pieces, AdfJson.Text(node) ?? AdfJson.Attr(node, "text"));
                }
                break;
        }
    }

    private static void AddPlain(List<Piece> pieces, string? text) {
        if (!string.IsNullOrEmpty(text)) {
            pieces.Add(new Piece(new DocText(text), []));
        }
    }

    private static void AddText(List<Piece> pieces, string text, JsonElement node) {
        if (text.Length > 0) {
            var (marks, code) = Marks(node);
            pieces.Add(new Piece(code ? new DocCode(text) : new DocText(text), marks));
        }
    }

    private static (Mark[] Marks, bool Code) Marks(JsonElement node) {
        var marks = new List<Mark>();
        var code = false;
        foreach (var mark in AdfJson.Children(node, "marks")) {
            Mark? parsed = AdfJson.Type(mark) switch {
                "strong" => new Mark(MarkKind.Bold, ""),
                "em" => new Mark(MarkKind.Italic, ""),
                "strike" => new Mark(MarkKind.Strike, ""),
                "underline" => new Mark(MarkKind.Underline, ""),
                "link" when AdfHref.Allowed(AdfJson.Attr(mark, "href")) is { } href => new Mark(MarkKind.Link, href),
                _ => null,
            };
            code |= AdfJson.Type(mark) == "code";
            if (parsed is { } value && !marks.Any(existing => existing.Kind == value.Kind)) {
                marks.Add(value);
            }
        }
        return (marks.ToArray(), code);
    }

    private DocInline Media(JsonElement node) {
        if (AdfJson.Attr(node, "type") == "external") {
            var alt = AdfJson.Attr(node, "alt");
            var url = AdfHref.Allowed(AdfJson.Attr(node, "url"));
            var label = new DocText(string.IsNullOrEmpty(alt) ? AdfToDoc.MissingImageText : alt);
            return url != null ? new DocLink(url, [label]) : new DocText(AdfToDoc.MissingImageText);
        }
        var attachment = media.Resolve(AdfJson.Attr(node, "id"));
        if (attachment == null) {
            return new DocText(AdfToDoc.MissingImageText);
        }
        return attachment.IsImage
            ? new DocAttachment(attachment.Id, attachment.FileName)
            : new DocText($"{attachment.FileName} (open in Jira)");
    }

    private static DocInline Card(JsonElement node) {
        var url = AdfJson.Attr(node, "url");
        var href = AdfHref.Allowed(url);
        return href != null ? new DocLink(href, [new DocText(url!)]) : new DocText(url ?? "");
    }

    private static void AddParagraph(List<DocBlock> output, List<Piece> pieces) {
        var inlines = TrimLines(Group(pieces));
        pieces.Clear();
        if (!IsBlank(inlines)) {
            output.Add(new DocParagraph(inlines));
        }
    }

    private static bool IsBlank(List<DocInline> inlines) {
        return inlines.All(inline => inline is DocLineBreak || inline is DocText text && MarkupSyntax.IsBlank(text.Value));
    }

    // Whitespace at the edges of a line and trailing line breaks do not show; markup drops them too.
    private static List<DocInline> TrimLines(List<DocInline> inlines) {
        var output = new List<DocInline>(inlines.Count);
        for (var i = 0; i < inlines.Count; i++) {
            var node = inlines[i];
            if (node is DocText text) {
                var value = text.Value.AsSpan();
                if (i == 0 || inlines[i - 1] is DocLineBreak) {
                    value = value.TrimStart(" \t");
                }
                if (i == inlines.Count - 1 || inlines[i + 1] is DocLineBreak) {
                    value = value.TrimEnd(" \t");
                }
                if (value.IsEmpty) {
                    continue;
                }
                node = value.Length == text.Value.Length ? text : new DocText(value.ToString());
            }
            output.Add(node);
        }
        while (output.Count > 0 && output[^1] is DocLineBreak) {
            output.RemoveAt(output.Count - 1);
        }
        return output;
    }

    private static List<DocInline> Group(List<Piece> pieces) => MoveEdgeWhitespace(Group(pieces, 0, pieces.Count, []));

    // Emphasis cannot open or close on whitespace in markup, so whitespace and line breaks at the edges of a bold,
    // italic or strike node move outside it. It looks the same and keeps the paragraph editable.
    private static List<DocInline> MoveEdgeWhitespace(IReadOnlyList<DocInline> nodes) {
        var output = new List<DocInline>();
        foreach (var node in nodes) {
            switch (node) {
                case DocStyled { Style: not DocStyle.Underline } styled:
                    var content = MoveEdgeWhitespace(styled.Content);
                    var leading = Peel(content, fromStart: true);
                    var trailing = Peel(content, fromStart: false);
                    leading.ForEach(inline => Append(output, inline));
                    if (content.Count > 0) {
                        output.Add(new DocStyled(styled.Style, content));
                    }
                    trailing.ForEach(inline => Append(output, inline));
                    break;
                case DocStyled styled:
                    output.Add(new DocStyled(styled.Style, MoveEdgeWhitespace(styled.Content)));
                    break;
                case DocLink link:
                    output.Add(new DocLink(link.Href, MoveEdgeWhitespace(link.Content)));
                    break;
                default:
                    Append(output, node);
                    break;
            }
        }
        return output;
    }

    // Removes the whitespace at one end of content and returns it in document order.
    private static List<DocInline> Peel(List<DocInline> content, bool fromStart) {
        var peeled = new List<DocInline>();
        while (content.Count > 0) {
            var index = fromStart ? 0 : content.Count - 1;
            if (content[index] is DocLineBreak lineBreak) {
                content.RemoveAt(index);
                peeled.Add(lineBreak);
                continue;
            }
            if (content[index] is not DocText text) {
                break;
            }
            var kept = fromStart ? text.Value.AsSpan().TrimStart(" \t").Length : text.Value.AsSpan().TrimEnd(" \t").Length;
            if (kept == text.Value.Length) {
                break;
            }
            var edge = fromStart ? text.Value[..^kept] : text.Value[kept..];
            peeled.Add(new DocText(edge));
            if (kept == 0) {
                content.RemoveAt(index);
                continue;
            }
            content[index] = new DocText(fromStart ? text.Value[^kept..] : text.Value[..kept]);
            break;
        }
        if (!fromStart) {
            peeled.Reverse();
        }
        return peeled;
    }

    // Rebuilds nested marks from ADF's flat mark sets. At each position the mark that covers the longest run of
    // pieces becomes the outer node (ties go to the earlier MarkKind), so "_**a** b_" comes back as italic around
    // bold rather than as two italics. Emphasis never opens on whitespace, which looks the same without it.
    private static List<DocInline> Group(List<Piece> pieces, int from, int to, List<Mark> applied) {
        var output = new List<DocInline>();
        var i = from;
        while (i < to) {
            Mark? best = null;
            var bestEnd = i;
            var neutral = IsNeutral(pieces[i].Leaf);
            foreach (var mark in pieces[i].Marks) {
                if (applied.Contains(mark) || neutral && mark.Kind != MarkKind.Link && mark.Kind != MarkKind.Underline) {
                    continue;
                }
                var end = RunEnd(pieces, i, to, mark);
                if (best == null || end > bestEnd || end == bestEnd && mark.Kind < best.Value.Kind) {
                    (best, bestEnd) = (mark, end);
                }
            }
            if (best is not { } outer) {
                Append(output, pieces[i].Leaf);
                i++;
                continue;
            }
            applied.Add(outer);
            var children = Group(pieces, i, bestEnd, applied);
            applied.RemoveAt(applied.Count - 1);
            output.Add(outer.Kind == MarkKind.Link ? new DocLink(outer.Href, children) : new DocStyled(Style(outer.Kind), children));
            i = bestEnd;
        }
        return output;
    }

    // Where a mark's run ends. A link needs the mark on every piece: a markup link label cannot span lines. For
    // emphasis, line breaks (which carry no marks in ADF) and whitespace (which shows the same either way) are neutral:
    // the run continues over them while the mark resumes after them, but ends on the last piece with visible text.
    private static int RunEnd(List<Piece> pieces, int start, int to, Mark mark) {
        if (mark.Kind == MarkKind.Link) {
            var end = start + 1;
            while (end < to && pieces[end].Marks.Contains(mark)) {
                end++;
            }
            return end;
        }
        var last = start;
        for (var i = start + 1; i < to; i++) {
            var neutral = IsNeutral(pieces[i].Leaf);
            if (pieces[i].Marks.Contains(mark) && !neutral) {
                last = i;
            } else if (!neutral) {
                break;
            }
        }
        return last + 1;
    }

    private static bool IsNeutral(DocInline leaf) => leaf is DocLineBreak || leaf is DocText text && MarkupSyntax.IsBlank(text.Value);

    private static void Append(List<DocInline> output, DocInline leaf) {
        if (leaf is DocText text && output.Count > 0 && output[^1] is DocText previous) {
            output[^1] = new DocText(previous.Value + text.Value);
        } else {
            output.Add(leaf);
        }
    }

    private static DocStyle Style(MarkKind kind) {
        return kind switch {
            MarkKind.Bold => DocStyle.Bold,
            MarkKind.Italic => DocStyle.Italic,
            MarkKind.Strike => DocStyle.Strike,
            MarkKind.Underline => DocStyle.Underline,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private string CodeText(JsonElement node, int depth) {
        var text = new StringBuilder();
        foreach (var child in AdfJson.Children(node)) {
            if (Enter(depth)) {
                text.Append(AdfJson.Text(child));
            }
        }
        return text.ToString();
    }

    private static string? CodeLanguage(JsonElement node) {
        var language = AdfJson.Attr(node, "language");
        return language != null && MarkupSyntax.IsLanguage(language) ? language : null;
    }

    private static int HeadingLevel(JsonElement node) {
        return (int)Math.Clamp(AdfJson.Number(node, "level") ?? 1, 1, 6);
    }

    // Markup list markers hold at most nine digits.
    private static int OrderedStart(JsonElement node) {
        var order = AdfJson.Number(node, "order") ?? 1;
        return order >= 0 && order <= 999_999_999 && order == Math.Floor(order) ? (int)order : 1;
    }

    private static DocPanelKind PanelKind(string? panelType) {
        return panelType switch {
            "note" => DocPanelKind.Note,
            "warning" => DocPanelKind.Warning,
            "success" or "tip" => DocPanelKind.Success,
            "error" => DocPanelKind.Error,
            _ => DocPanelKind.Info,
        };
    }

    private static DocStatusColor StatusColor(string? color) {
        return color switch {
            "purple" => DocStatusColor.Purple,
            "blue" => DocStatusColor.Blue,
            "red" => DocStatusColor.Red,
            "yellow" => DocStatusColor.Yellow,
            "green" => DocStatusColor.Green,
            _ => DocStatusColor.Neutral,
        };
    }

    // ADF dates are UTC midnight as milliseconds since the epoch.
    private static string? FormatDate(string? timestamp) {
        if (!long.TryParse(timestamp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) {
            return timestamp;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string PlainText(DocNode node) {
        return node switch {
            DocText text => text.Value,
            DocCode code => code.Value,
            DocCodeBlock block => block.Text,
            DocLineBreak => " ",
            DocStatus status => status.Text,
            DocAttachment attachment => attachment.Alt,
            _ => string.Concat(node.Children.Select(PlainText)),
        };
    }

    private static bool IsInline(string? type) {
        return type is "text" or "hardBreak" or "mention" or "emoji" or "date" or "status" or "inlineCard"
            or "mediaInline" or "placeholder" or "inlineExtension";
    }
}
