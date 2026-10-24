using System.Text.Json.Serialization;

namespace Retroscope.Core.Markup;

// The one rendered-document tree (DESIGN §f, §g.6): Retro Markup and Jira ADF both become a Doc, and the
// client builds DOM from it with createElement/textContent only. The node set is closed: every kind is
// allow-listed by construction. Panel, table, expand, task, underline, status and attachment come only from
// ADF (U6); the markup grammar never produces them and MarkupWriter refuses them. So do the display-only kinds
// and attributes of UX8 (colour, alignment, layouts, media layout, cards, dates…): they appear only in islands,
// comments and non-editable descriptions, because AdfLossiness islands every block that carries them.
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

public enum DocAlign {
    Start,
    Center,
    End,
}

// Alignment and indentation are ADF block marks (display only, UX8); indent is 0 to 6 steps.
public readonly record struct DocBlockFormat(DocAlign Align, int Indent) {
    public static readonly DocBlockFormat None = new(DocAlign.Start, 0);
}

public sealed class DocParagraph(IReadOnlyList<DocInline> content, DocBlockFormat format = default) : DocBlock {
    public IReadOnlyList<DocInline> Content { get; } = content;

    public DocBlockFormat Format { get; } = format;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocHeading(int level, IReadOnlyList<DocInline> content, DocBlockFormat format = default) : DocBlock {
    public int Level { get; } = level is >= 1 and <= 6 ? level : throw new ArgumentOutOfRangeException(nameof(level));

    public IReadOnlyList<DocInline> Content { get; } = content;

    public DocBlockFormat Format { get; } = format;

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
    Tip,
    Custom,
}

// Color is a DocColor value (custom panels only); Icon is the emoji text a custom panel shows.
public sealed class DocPanel(DocPanelKind kind, IReadOnlyList<DocBlock> blocks, string? color = null, string? icon = null) : DocBlock {
    public DocPanelKind Kind { get; } = kind;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public string? Color { get; } = color;

    public string? Icon { get; } = icon;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public enum DocLayout {
    Default,
    Center,
    Wide,
    FullWidth,
    AlignStart,
    AlignEnd,
    WrapLeft,
    WrapRight,
}

// Width is the table's pixel width when Jira set one; Numbered adds the row-number column.
public sealed class DocTable(IReadOnlyList<DocTableRow> rows, DocLayout layout = DocLayout.Default, bool numbered = false, int? width = null) : DocBlock {
    public IReadOnlyList<DocTableRow> Rows { get; } = rows;

    public DocLayout Layout { get; } = layout;

    public bool Numbered { get; } = numbered;

    public int? Width { get; } = width;

    public override IReadOnlyList<DocNode> Children => Rows;
}

public sealed class DocTableRow(IReadOnlyList<DocTableCell> cells) : DocNode {
    public IReadOnlyList<DocTableCell> Cells { get; } = cells;

    public override IReadOnlyList<DocNode> Children => Cells;
}

public sealed class DocTableCell(bool header, IReadOnlyList<DocBlock> blocks, DocCellFormat format = default) : DocNode {
    public bool Header { get; } = header;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public DocCellFormat Format { get; } = format;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

// A span below 2 is no span; ColumnWidths are pixels, one per spanned column; Background is a DocColor value.
public readonly record struct DocCellFormat(int ColSpan, int RowSpan, IReadOnlyList<int>? ColumnWidths, string? Background);

public sealed class DocLayoutSection(IReadOnlyList<DocLayoutColumn> columns) : DocBlock {
    public IReadOnlyList<DocLayoutColumn> Columns { get; } = columns;

    public override IReadOnlyList<DocNode> Children => Columns;
}

// Width is a percentage of the section.
public sealed class DocLayoutColumn(double width, IReadOnlyList<DocBlock> blocks) : DocNode {
    public double Width { get; } = width;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

public enum DocWidthUnit {
    Percent,
    Pixel,
}

// mediaSingle: one media item (an attachment, a file chip or a link) with its layout, width and caption.
public sealed class DocMedia(DocLayout layout, double? width, DocWidthUnit unit, IReadOnlyList<DocInline> items, IReadOnlyList<DocInline> caption) : DocBlock {
    public DocLayout Layout { get; } = layout;

    public double? Width { get; } = width;

    public DocWidthUnit Unit { get; } = unit;

    public IReadOnlyList<DocInline> Items { get; } = items;

    public IReadOnlyList<DocInline> Caption { get; } = caption;

    public override IReadOnlyList<DocNode> Children => [.. Items, .. Caption];
}

// mediaGroup: a row of attachments and file chips.
public sealed class DocMediaGroup(IReadOnlyList<DocInline> items) : DocBlock {
    public IReadOnlyList<DocInline> Items { get; } = items;

    public override IReadOnlyList<DocNode> Children => Items;
}

public sealed class DocDecisionList(IReadOnlyList<DocDecisionItem> items) : DocBlock {
    public IReadOnlyList<DocDecisionItem> Items { get; } = items;

    public override IReadOnlyList<DocNode> Children => Items;
}

public sealed class DocDecisionItem(bool decided, IReadOnlyList<DocInline> content) : DocNode {
    public bool Decided { get; } = decided;

    public IReadOnlyList<DocInline> Content { get; } = content;

    public override IReadOnlyList<DocNode> Children => Content;
}

public sealed class DocExpand(string title, IReadOnlyList<DocBlock> blocks) : DocBlock {
    public string Title { get; } = title;

    public IReadOnlyList<DocBlock> Blocks { get; } = blocks;

    public override IReadOnlyList<DocNode> Children => Blocks;
}

// Items are DocTaskItems and nested DocTaskLists.
public sealed class DocTaskList(IReadOnlyList<DocNode> items) : DocBlock {
    public IReadOnlyList<DocNode> Items { get; } = items;

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

public sealed class DocLink(string href, IReadOnlyList<DocInline> content, string? title = null) : DocInline {
    public string Href { get; } = href;

    public string? Title { get; } = title;

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

// Width and Height are the image's own pixel size when ADF gave one, so the page keeps its aspect ratio while it loads.
public sealed class DocAttachment(string attachmentId, string alt, int? width = null, int? height = null) : DocInline {
    public string AttachmentId { get; } = attachmentId;

    public string Alt { get; } = alt;

    public int? Width { get; } = width;

    public int? Height { get; } = height;
}

// An attachment the proxy does not serve inline: a chip with its name that opens the issue in Jira.
public sealed class DocFile(string attachmentId, string name) : DocInline {
    public string AttachmentId { get; } = attachmentId;

    public string Name { get; } = name;
}

public enum DocMarkKind {
    Sub,
    Sup,
    TextColor,
    Background,
}

// Display-only text marks. Color is a DocColor value for TextColor and Background, null otherwise.
public sealed class DocMarked(DocMarkKind kind, IReadOnlyList<DocInline> content, string? color = null) : DocInline {
    public DocMarkKind Kind { get; } = kind;

    public IReadOnlyList<DocInline> Content { get; } = content;

    public string? Color { get; } = color;

    public override IReadOnlyList<DocNode> Children => Content;
}

// A Jira user mention: the display text only. The account id never leaves the server.
public sealed class DocUser(string name) : DocInline {
    public string Name { get; } = name;
}

// An ADF date: a UTC calendar day.
public sealed class DocDate(DateOnly day) : DocInline {
    public DateOnly Day { get; } = day;
}

// inlineCard, blockCard and embedCard: a link shown as a card. Jira's smart-link previews need its servers.
public sealed class DocCard(string href, bool block) : DocInline {
    public string Href { get; } = href;

    public bool Block { get; } = block;
}

public enum DocStatusColor {
    Neutral,
    Purple,
    Blue,
    Red,
    Yellow,
    Green,
}

public sealed class DocStatus(string text, DocStatusColor color, bool bold = false) : DocInline {
    public string Text { get; } = text;

    public DocStatusColor Color { get; } = color;

    public bool Bold { get; } = bold;
}

public sealed class DocLineBreak : DocInline {
}
