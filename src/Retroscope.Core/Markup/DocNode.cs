using System.Text.Json.Serialization;

namespace Retroscope.Core.Markup;

// The one rendered-document tree (DESIGN §f, §g.6): Retro Markup and Jira ADF both become a Doc, and the
// client builds DOM from it with createElement/textContent only. The node set is closed: every kind is
// allow-listed by construction. Panel, table, expand, task, underline, status and attachment come only from
// ADF (U6); the markup grammar never produces them and MarkupWriter refuses them.
[JsonConverter(typeof(DocJsonConverter))]
public sealed class Doc(IReadOnlyList<DocBlock> blocks) {
    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public IEnumerable<DocNode> Descendants() {
        var stack = new Stack<DocNode>();
        for (var i = Blocks.Count - 1; i >= 0; i--) {
            stack.Push(Blocks[i]);
        }
        while (stack.Count > 0) {
            var node = stack.Pop();
            yield return node;
            var children = node.Children;
            for (var i = children.Count - 1; i >= 0; i--) {
                stack.Push(children[i]);
            }
        }
    }
}

public abstract class DocNode {
    private protected DocNode() { }

    public virtual IReadOnlyList<DocNode> Children => [];
}

public abstract class DocBlock : DocNode {
    private protected DocBlock() { }
}

public abstract class DocInline : DocNode {
    private protected DocInline() { }
}

public sealed class DocParagraph(IReadOnlyList<DocInline> content) : DocBlock {
    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocHeading(int level, IReadOnlyList<DocInline> content) : DocBlock {
    public int Level { get; } = level is >= 1 and <= 6 ? level : throw new ArgumentOutOfRangeException(nameof(level));

    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocBulletList(IReadOnlyList<DocListItem> items) : DocBlock {
    public IReadOnlyList<DocListItem> Items { get; } = items;

    public override IReadOnlyList<DocNode> Children => Items;
}

public sealed class DocOrderedList(int start, IReadOnlyList<DocListItem> items) : DocBlock {
    public int Start { get; } = start >= 0 ? start : throw new ArgumentOutOfRangeException(nameof(start));

    public IReadOnlyList<DocListItem> Items { get; } = items;

    public override IReadOnlyList<DocNode> Children => Items;
}

public sealed class DocListItem(IReadOnlyList<DocBlock> blocks) : DocNode {
    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public sealed class DocQuote(IReadOnlyList<DocBlock> blocks) : DocBlock {
    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public sealed class DocCodeBlock(string? language, string text) : DocBlock {
    public string? Language { get; } = language;

    public string Text { get; } = text;
}

public sealed class DocRule : DocBlock {
}

// A Jira block kept verbatim on save (§f). Blocks is the read-only rendering; the markup token carries only the id.
public sealed class DocIsland(string id, IReadOnlyList<DocBlock> blocks) : DocBlock {
    public string Id { get; } = id;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public enum DocPanelKind {
    Info,
    Note,
    Warning,
    Success,
    Error,
}

public sealed class DocPanel(DocPanelKind kind, IReadOnlyList<DocBlock> blocks) : DocBlock {
    public DocPanelKind Kind { get; } = kind;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public sealed class DocTable(IReadOnlyList<DocTableRow> rows) : DocBlock {
    public IReadOnlyList<DocTableRow> Rows { get; } = rows;

    public override IReadOnlyList<DocNode> Children => Rows;
}

public sealed class DocTableRow(IReadOnlyList<DocTableCell> cells) : DocNode {
    public IReadOnlyList<DocTableCell> Cells { get; } = cells;

    public override IReadOnlyList<DocNode> Children => Cells;
}

public sealed class DocTableCell(bool header, IReadOnlyList<DocBlock> blocks) : DocNode {
    public bool Header { get; } = header;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public sealed class DocExpand(string title, IReadOnlyList<DocBlock> blocks) : DocBlock {
    public string Title { get; } = title;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public sealed class DocTaskList(IReadOnlyList<DocTaskItem> items) : DocBlock {
    public IReadOnlyList<DocTaskItem> Items { get; } = items;

    public override IReadOnlyList<DocNode> Children => Items;
}

public sealed class DocTaskItem(bool done, IReadOnlyList<DocInline> content) : DocNode {
    public bool Done { get; } = done;

    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocText(string value) : DocInline {
    public string Value { get; } = value;
}

public enum DocStyle {
    Bold,
    Italic,
    Strike,
    Underline,
}

public sealed class DocStyled(DocStyle style, IReadOnlyList<DocInline> content) : DocInline {
    public DocStyle Style { get; } = style;

    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocCode(string value) : DocInline {
    public string Value { get; } = value;
}

public sealed class DocLink(string href, IReadOnlyList<DocInline> content) : DocInline {
    public string Href { get; } = href;

    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

// Name is the token's text only; renderers re-resolve it from the current principal name (§g.6).
public sealed class DocMention(Guid userId, string name) : DocInline {
    public Guid UserId { get; } = userId;

    public string Name { get; } = name;
}

public sealed class DocImage(Guid blobId, string alt) : DocInline {
    public Guid BlobId { get; } = blobId;

    public string Alt { get; } = alt;
}

public sealed class DocAttachment(string attachmentId, string alt) : DocInline {
    public string AttachmentId { get; } = attachmentId;

    public string Alt { get; } = alt;
}

public enum DocStatusColor {
    Neutral,
    Purple,
    Blue,
    Red,
    Yellow,
    Green,
}

public sealed class DocStatus(string text, DocStatusColor color) : DocInline {
    public string Text { get; } = text;

    public DocStatusColor Color { get; } = color;
}

public sealed class DocLineBreak : DocInline {
}
