using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Retroscope.Core.Adf;

internal enum AdfAttrType {
    String,
    Enum,
    Integer,
    Number,
    Boolean,
    NumberList,

    /// <summary>The editor never edits a node that carries it: its presence makes the node opaque.</summary>
    Refused,
}

/// <summary>Extra value rules on top of an attribute's JSON type.</summary>
internal enum AdfCheck {
    None,
    LocalId,
    Language,
    Color,
    StatusColor,
    Href,
    CardUrl,
    Timestamp,
    EmojiShortName,
    EmojiId,
    AccountId,
}

/// <summary>
/// One attribute the editor understands. A value that fails its rule makes the node opaque when the attribute is
/// <see cref="Required"/> or <see cref="Structural"/>; otherwise the projection moves it into the node's rsKeep stash.
/// </summary>
internal sealed record AdfAttrRule(string Name, AdfAttrType Type) {
    public bool Required { get; init; }

    public bool Structural { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    public double Min { get; init; } = double.MinValue;

    public double Max { get; init; } = double.MaxValue;

    /// <summary>In Unicode code points.</summary>
    public int MinLength { get; init; }

    /// <summary>In Unicode code points.</summary>
    public int MaxLength { get; init; } = 2048;

    public AdfCheck Check { get; init; }
}

/// <summary>A content expression: the node types a parent may hold, how many, and which block marks they may carry.</summary>
/// <param name="Schema">The full.json definition or group this context mirrors.</param>
/// <param name="OpaqueChildren">Whether a child may become an opaque token on its own; otherwise the parent does.</param>
internal sealed record AdfContext(
    string Name,
    string Schema,
    FrozenSet<string> Types,
    int Min,
    int Max,
    bool Inline,
    bool OpaqueChildren,
    FrozenSet<string> ParagraphMarks,
    FrozenSet<string> HeadingMarks);

/// <param name="Editable">False for nodes the editor only preserves (extensions, synced blocks…): always opaque.</param>
/// <param name="Content">The context of the node's children; null for a leaf.</param>
/// <param name="Marks">Marks the node itself may carry, besides the block marks its context allows.</param>
internal sealed record AdfNodeSpec(
    string Type,
    bool Editable,
    bool Inline,
    string? Content,
    IReadOnlyList<AdfAttrRule> Attrs,
    IReadOnlyList<string> Marks,
    bool ContentRequired,
    bool AttrsRequired) {
    public AdfAttrRule? Attr(string name) {
        foreach (var attr in Attrs) {
            if (attr.Name == name) {
                return attr;
            }
        }
        return null;
    }
}

/// <param name="Block">A block mark (on paragraph or heading) rather than a text mark.</param>
internal sealed record AdfMarkSpec(string Type, IReadOnlyList<AdfAttrRule> Attrs, bool Block);

/// <summary>
/// The one ADF allow-list (UX10): the nodes, marks, attributes and nesting the rich editor may write, read by the edit
/// projection and by the write validator. It mirrors @atlaskit/adf-schema full.json (the vendored copy under
/// tests/fixtures/adf-schema is pinned by AdfSchemaTests) and is narrower where the editor needs a stricter value.
/// </summary>
internal static partial class AdfSchema {
    public const string OpaqueBlock = "rsOpaque";
    public const string OpaqueInline = "rsOpaqueInline";
    public const string OpaqueMark = "rsMark";
    public const string Keep = "rsKeep";
    public const string Ref = "ref";

    /// <summary>
    /// The deepest ADF level the editor edits; deeper nodes become tokens. A PATCH body holds the description two JSON
    /// levels down, each ADF level adds two (node, content array) and a text node's link mark four more (marks, mark,
    /// attrs); ASP.NET binds at most 64, so even a node one past the cap must stay within that.
    /// </summary>
    public const int MaxDepth = 28;

    /// <summary>
    /// The JSON nesting a written description may reach, its doc object counting 1: Jira's issue JSON holds it two
    /// levels down and the server reads issues at the default 64, so a save past this could never be read back.
    /// </summary>
    public const int MaxJsonDepth = 62;
    public const int MaxNodes = AdfToDoc.MaxNodes;
    public const int MaxText = Markup.MarkupLimits.JiraTextLength;
    public const int MaxBytes = 1024 * 1024;
    public const int MaxSpan = 64;
    public const int MaxColumns = 64;
    public const double MaxPixels = 10_000;
    public const long MaxTimestamp = 253_402_300_799_999;

    private static readonly string[] MediaLayouts = ["wide", "full-width", "center", "wrap-right", "wrap-left", "align-end", "align-start"];
    private static readonly string[] Valign = ["top", "middle", "bottom"];

    public static FrozenDictionary<string, AdfContext> Contexts { get; } = BuildContexts();

    public static FrozenDictionary<string, AdfNodeSpec> Nodes { get; } = BuildNodes();

    public static FrozenDictionary<string, AdfMarkSpec> Marks { get; } = BuildMarks();

    /// <summary>Every member name the allow-list knows: what an error path may name.</summary>
    public static FrozenSet<string> MemberNames { get; } = Nodes.Values.SelectMany(node => node.Attrs.Select(attr => attr.Name))
        .Concat(Marks.Values.SelectMany(mark => mark.Attrs.Select(attr => attr.Name)))
        .Concat(["type", "version", "attrs", "content", "marks", "text", Keep, Ref])
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Marks a formatted text node may carry.</summary>
    public static FrozenSet<string> TextMarks { get; } =
        FrozenSet.Create(StringComparer.Ordinal, "link", "em", "strong", "strike", "subsup", "underline", "textColor", "backgroundColor");

    /// <summary>Marks a text node carrying <c>code</c> may carry (the schema's code_inline_node).</summary>
    public static FrozenSet<string> CodeTextMarks { get; } = FrozenSet.Create(StringComparer.Ordinal, "code", "link");

    public static AdfNodeSpec? Node(string? type) => type is not null && Nodes.TryGetValue(type, out var spec) ? spec : null;

    public static AdfMarkSpec? Mark(string? type) => type is not null && Marks.TryGetValue(type, out var spec) ? spec : null;

    /// <summary>Names the server owns: rs followed by a capital letter.</summary>
    public static bool IsReserved(string name) => name.Length > 2 && name[0] == 'r' && name[1] == 's' && char.IsAsciiLetterUpper(name[2]);

    /// <summary>
    /// Whether <paramref name="value"/> passes <paramref name="rule"/>. The projection reads leniently
    /// (<paramref name="write"/> false); a write holds new values to the strict form, so the server never rewrites a link.
    /// </summary>
    public static bool Valid(AdfAttrRule rule, JsonElement value, bool write) {
        switch (rule.Type) {
            case AdfAttrType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case AdfAttrType.Integer:
                return IsInteger(value, out var integer) && integer >= rule.Min && integer <= rule.Max;
            case AdfAttrType.Number:
                return IsNumber(value, out var number) && number >= rule.Min && number <= rule.Max;
            case AdfAttrType.NumberList:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxSpan) {
                    return false;
                }
                foreach (var item in value.EnumerateArray()) {
                    if (!IsNumber(item, out var width) || width < rule.Min || width > rule.Max) {
                        return false;
                    }
                }
                return true;
            case AdfAttrType.Enum:
                return TryString(value, out var choice) && rule.Values.Contains(choice);
            case AdfAttrType.String:
                return TryString(value, out var text) && ValidString(rule, text, write);
            default:
                return false;
        }
    }

    public static bool ValidString(AdfAttrRule rule, string text, bool write) {
        var length = CodePoints(text);
        if (length < rule.MinLength || length > rule.MaxLength || HasControl(text)) {
            return false;
        }
        return rule.Check switch {
            AdfCheck.LocalId => LocalId().IsMatch(text),
            AdfCheck.Language => Language().IsMatch(text),
            AdfCheck.Color => IsColor(text),
            AdfCheck.StatusColor => rule.Values.Contains(text) || (!write && IsColor(text)),
            AdfCheck.Href => IsHref(text, write, mailto: true),
            AdfCheck.CardUrl => IsHref(text, write, mailto: false),
            AdfCheck.Timestamp => Timestamp().IsMatch(text) && long.Parse(text, CultureInfo.InvariantCulture) <= MaxTimestamp,
            AdfCheck.EmojiShortName => EmojiShortName().IsMatch(text),
            AdfCheck.EmojiId => EmojiId().IsMatch(text),
            AdfCheck.AccountId => AccountId().IsMatch(text),
            _ => true,
        };
    }

    /// <summary>
    /// Text of a text node: non-empty, no unpaired surrogate, no control character except tab, and a line break only
    /// inside a code block (where the schema keeps line breaks in the text).
    /// </summary>
    public static bool ValidText(string text, bool codeBlock) {
        if (text.Length == 0) {
            return false;
        }
        foreach (var c in text) {
            var allowed = c == '\t' || (codeBlock && c is '\n' or '\r');
            if ((c < ' ' && !allowed) || c is >= '\u007f' and <= '\u009f') {
                return false;
            }
        }
        return true;
    }

    public static bool IsColor(string text) {
        if (text.Length != 7 || text[0] != '#') {
            return false;
        }
        for (var i = 1; i < 7; i++) {
            if (!char.IsAsciiHexDigit(text[i])) {
                return false;
            }
        }
        return true;
    }

    /// <summary>A string member, refusing an escaped unpaired surrogate (which <see cref="AdfJson.String"/> would replace).</summary>
    public static bool TryString(JsonElement value, out string text) {
        text = "";
        if (value.ValueKind != JsonValueKind.String) {
            return false;
        }
        try {
            text = value.GetString()!;
        } catch (InvalidOperationException) {
            return false;
        }
        for (var i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
            } else if (char.IsSurrogate(text[i])) {
                return false;
            }
        }
        return true;
    }

    /// <summary>An integer written as a plain decimal literal (no exponent, fraction or "-0").</summary>
    public static bool IsInteger(JsonElement value, out long integer) {
        integer = 0;
        return value.ValueKind == JsonValueKind.Number
            && IntegerLiteral().IsMatch(value.GetRawText())
            && value.TryGetInt64(out integer);
    }

    public static bool IsNumber(JsonElement value, out double number) {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    public static int CodePoints(string text) {
        var count = 0;
        for (var i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
            }
            count++;
        }
        return count;
    }

    private static bool HasControl(string text) {
        foreach (var c in text) {
            if (char.IsControl(c)) {
                return true;
            }
        }
        return false;
    }

    // Read: anything AdfHref would show as a link. Write: only a value already in its normalised form, with no
    // invisible formatting characters (bidi overrides, zero-width), so a link never reads as something else.
    private static bool IsHref(string text, bool write, bool mailto) {
        if (!mailto && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        var allowed = AdfHref.Allowed(text);
        if (!write) {
            return allowed is not null;
        }
        if (allowed != text) {
            return false;
        }
        foreach (var c in text) {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse) {
                return false;
            }
        }
        return true;
    }

    private static FrozenDictionary<string, AdfContext> BuildContexts() {
        string[] align = ["alignment", "indentation"];
        string[] none = [];
        var contexts = new[] {
            Context("doc", "doc_node", 0, align, align,
                "blockCard", "codeBlock", "mediaSingle", "paragraph", "taskList", "orderedList", "bulletList", "blockquote",
                "decisionList", "embedCard", "extension", "heading", "mediaGroup", "rule", "panel", "table", "bodiedExtension",
                "expand", "layoutSection", "syncBlock", "bodiedSyncBlock"),
            Context("block", "layoutColumn_node", 1, align, align,
                "blockCard", "paragraph", "mediaSingle", "codeBlock", "taskList", "bulletList", "orderedList", "heading",
                "mediaGroup", "decisionList", "rule", "panel", "blockquote", "extension", "embedCard", "table", "expand",
                "bodiedExtension"),
            Context("nonNestable", "bodiedExtension_node", 1, none, none,
                "paragraph", "panel", "blockquote", "orderedList", "bulletList", "rule", "heading", "codeBlock", "mediaGroup",
                "mediaSingle", "decisionList", "taskList", "table", "blockCard", "embedCard", "extension"),
            Context("expand", "expand_node", 1, none, none,
                "paragraph", "panel", "blockquote", "orderedList", "bulletList", "rule", "heading", "codeBlock", "mediaGroup",
                "mediaSingle", "decisionList", "taskList", "table", "blockCard", "embedCard", "extension", "nestedExpand"),
            Context("cell", "table_cell_content", 1, ["alignment"], align,
                "paragraph", "panel", "blockquote", "orderedList", "bulletList", "rule", "heading", "codeBlock", "mediaSingle",
                "mediaGroup", "decisionList", "taskList", "blockCard", "embedCard", "extension", "nestedExpand"),
            Context("nestedExpand", "nestedExpand_content", 1, none, none,
                "paragraph", "heading", "mediaSingle", "mediaGroup", "codeBlock", "bulletList", "orderedList", "taskList",
                "decisionList", "rule", "panel", "blockquote", "extension"),
            Context("panel", "panel_node", 1, none, none,
                "paragraph", "heading", "bulletList", "orderedList", "blockCard", "mediaGroup", "mediaSingle", "codeBlock",
                "taskList", "rule", "decisionList", "extension"),
            Context("blockquote", "blockquote_node", 1, none, none,
                "paragraph", "orderedList", "bulletList", "codeBlock", "mediaSingle", "mediaGroup", "extension"),
            Context("listItem", "listItem_node", 1, none, none,
                "paragraph", "bulletList", "orderedList", "taskList", "mediaSingle", "codeBlock", "extension"),
            Context("list", "bulletList_node", 1, none, none, "listItem") with { OpaqueChildren = false },
            Context("taskList", "taskList_node", 1, none, none, "taskItem", "taskList", "blockTaskItem"),
            Context("decisionList", "decisionList_node", 1, none, none, "decisionItem") with { OpaqueChildren = false },
            Context("inline", "inline_node", 0, none, none,
                "text", "date", "emoji", "hardBreak", "inlineCard", "mention", "placeholder", "status", "inlineExtension",
                "mediaInline") with { Inline = true },
            Context("caption", "caption_node", 0, none, none,
                "hardBreak", "mention", "emoji", "date", "placeholder", "inlineCard", "status", "text") with { Inline = true },
            Context("codeBlock", "codeBlock_node", 0, none, none, "text") with { Inline = true, OpaqueChildren = false },
            Context("table", "table_node", 1, none, none, "tableRow") with { OpaqueChildren = false },
            Context("tableRow", "table_row_node", 0, none, none, "tableCell", "tableHeader") with { OpaqueChildren = false },
            Context("layoutSection", "layoutSection_full_node", 2, none, none, "layoutColumn") with { Max = 3, OpaqueChildren = false },
            Context("mediaSingle", "mediaSingle_caption_node", 1, none, none, "media", "caption") with { Max = 2, OpaqueChildren = false },
            Context("mediaGroup", "mediaGroup_node", 1, none, none, "media") with { OpaqueChildren = false },
        };
        return contexts.ToFrozenDictionary(context => context.Name, StringComparer.Ordinal);
    }

    private static AdfContext Context(string name, string schema, int min, string[] paragraphMarks, string[] headingMarks, params string[] types) =>
        new(
            name,
            schema,
            types.ToFrozenSet(StringComparer.Ordinal),
            min,
            int.MaxValue,
            Inline: false,
            OpaqueChildren: true,
            paragraphMarks.ToFrozenSet(StringComparer.Ordinal),
            headingMarks.ToFrozenSet(StringComparer.Ordinal));

    private static FrozenDictionary<string, AdfNodeSpec> BuildNodes() {
        var localId = LocalIdRule(required: false);
        var nodes = new[] {
            Block("doc", "doc", required: true),
            Block("paragraph", "inline", required: false, localId),
            Block("heading", "inline", required: false, Int("level", 1, 6) with { Required = true }, localId) with { AttrsRequired = true },
            Leaf("text", inline: true),
            Leaf("hardBreak", inline: true, Enum("text", "\n"), localId),
            Block("bulletList", "list", required: true, localId),
            Block("orderedList", "list", required: true, Int("order", 0, int.MaxValue), localId),
            Block("listItem", "listItem", required: true, localId),
            Block("blockquote", "blockquote", required: true, localId),
            Leaf("rule", inline: false, localId),
            Block("codeBlock", "codeBlock", required: false,
                Str("language", 32, AdfCheck.Language), Str("uniqueId", 64), localId, Bool("wrap"), Bool("hideLineNumbers")),
            Block("panel", "panel", required: true,
                Enum("panelType", "info", "note", "tip", "warning", "error", "success", "custom") with { Required = true },
                Str("panelColor", 7, AdfCheck.Color), Str("panelIcon", 64), Str("panelIconId", 64), Str("panelIconText", 16), localId)
                with { AttrsRequired = true },
            Block("expand", "expand", required: true, Str("title", 255), localId),
            Block("nestedExpand", "nestedExpand", required: true, Str("title", 255), localId) with { AttrsRequired = true },
            Block("table", "table", required: true,
                Enum("layout", "wide", "full-width", "center", "align-end", "align-start", "default"),
                Num("width", 0, MaxPixels), Bool("isNumberColumnEnabled"), Enum("displayMode", "default", "fixed"), localId),
            Block("tableRow", "tableRow", required: true, localId),
            Cell("tableCell", localId),
            Cell("tableHeader", localId),
            Block("layoutSection", "layoutSection", required: true, localId),
            Block("layoutColumn", "block", required: true,
                Num("width", 0, 100) with { Required = true }, Enum("valign", Valign), localId) with { AttrsRequired = true },
            Block("taskList", "taskList", required: true, LocalIdRule(required: true)) with { AttrsRequired = true },
            Block("taskItem", "inline", required: false,
                LocalIdRule(required: true), Enum("state", "TODO", "DONE") with { Required = true }) with { AttrsRequired = true },
            Block("decisionList", "decisionList", required: true, LocalIdRule(required: true)) with { AttrsRequired = true },
            Block("decisionItem", "inline", required: false,
                LocalIdRule(required: true), Enum("state", "DECIDED", "UNDECIDED") with { Required = true }) with { AttrsRequired = true },
            Block("mediaSingle", "mediaSingle", required: true,
                Enum("layout", MediaLayouts) with { Required = true }, Num("width", 0, MaxPixels),
                Enum("widthType", "percentage", "pixel"), localId) with { Marks = ["link"] },
            Block("mediaGroup", "mediaGroup", required: true),
            Leaf("media", inline: false,
                Enum("type", "file", "link", "external") with { Required = true }, Str("id", 255) with { MinLength = 1 }, Str("collection", 255),
                Str("alt", 1024), Num("width", 0, MaxPixels), Num("height", 0, MaxPixels), Str("occurrenceKey", 255) with { MinLength = 1 },
                Str("url", 4096), localId) with { AttrsRequired = true, Marks = ["link"] },
            Block("caption", "caption", required: false, localId),
            Leaf("mention", inline: true,
                Str("id", 128, AdfCheck.AccountId) with { Required = true }, Str("text", 100), Str("accessLevel", 32),
                Enum("userType", "DEFAULT", "SPECIAL", "APP"), localId) with { AttrsRequired = true },
            Leaf("emoji", inline: true,
                Str("shortName", 66, AdfCheck.EmojiShortName) with { Required = true }, Str("id", 64, AdfCheck.EmojiId),
                Str("text", 16), localId) with { AttrsRequired = true },
            Leaf("date", inline: true, Str("timestamp", 15, AdfCheck.Timestamp) with { Required = true }, localId) with { AttrsRequired = true },
            Leaf("status", inline: true,
                Str("text", 64) with { Required = true, MinLength = 1 },
                Str("color", 7, AdfCheck.StatusColor) with { Required = true, Values = ["neutral", "purple", "blue", "red", "yellow", "green"] },
                Enum("style", "bold", "subtle"), localId) with { AttrsRequired = true },
            Leaf("inlineCard", inline: true, Str("url", 4096, AdfCheck.CardUrl) with { Required = true }, Refused("data"), localId)
                with { AttrsRequired = true },
            Leaf("blockCard", inline: false,
                Str("url", 4096, AdfCheck.CardUrl) with { Required = true }, Refused("data"), Refused("datasource"), Refused("width"),
                Refused("layout"), localId) with { AttrsRequired = true },
            Leaf("embedCard", inline: false,
                Str("url", 4096, AdfCheck.CardUrl) with { Required = true }, Enum("layout", MediaLayouts) with { Required = true },
                Num("width", 0, 100), Num("originalWidth", 0, 100_000), Num("originalHeight", 0, 100_000), localId)
                with { AttrsRequired = true },
            Preserve("extension", inline: false),
            Preserve("bodiedExtension", inline: false),
            Preserve("inlineExtension", inline: true),
            Preserve("syncBlock", inline: false),
            Preserve("bodiedSyncBlock", inline: false),
            Preserve("placeholder", inline: true),
            Preserve("mediaInline", inline: true),
            Preserve("blockTaskItem", inline: false),
        };
        return nodes.ToFrozenDictionary(node => node.Type, StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, AdfMarkSpec> BuildMarks() {
        var marks = new[] {
            new AdfMarkSpec("strong", [], Block: false),
            new AdfMarkSpec("em", [], Block: false),
            new AdfMarkSpec("strike", [], Block: false),
            new AdfMarkSpec("underline", [], Block: false),
            new AdfMarkSpec("code", [], Block: false),
            new AdfMarkSpec("link", [Str("href", 4096, AdfCheck.Href) with { Required = true }, Str("title", 512)], Block: false),
            new AdfMarkSpec("textColor", [Str("color", 7, AdfCheck.Color) with { Required = true }], Block: false),
            new AdfMarkSpec("backgroundColor", [Str("color", 7, AdfCheck.Color) with { Required = true }], Block: false),
            new AdfMarkSpec("subsup", [Enum("type", "sub", "sup") with { Required = true }], Block: false),
            new AdfMarkSpec("alignment", [Enum("align", "center", "end") with { Required = true }], Block: true),
            new AdfMarkSpec("indentation", [Int("level", 1, 6) with { Required = true }], Block: true),
        };
        return marks.ToFrozenDictionary(mark => mark.Type, StringComparer.Ordinal);
    }

    private static AdfNodeSpec Block(string type, string context, bool required, params AdfAttrRule[] attrs) =>
        new(type, Editable: true, Inline: false, context, attrs, [], ContentRequired: required, AttrsRequired: false);

    private static AdfNodeSpec Leaf(string type, bool inline, params AdfAttrRule[] attrs) =>
        new(type, Editable: true, inline, Content: null, attrs, [], ContentRequired: false, AttrsRequired: false);

    private static AdfNodeSpec Cell(string type, AdfAttrRule localId) =>
        Block(type, "cell", required: true,
            Int("colspan", 1, MaxSpan) with { Structural = true },
            Int("rowspan", 1, MaxSpan) with { Structural = true },
            new AdfAttrRule("colwidth", AdfAttrType.NumberList) { Min = 0, Max = MaxPixels, Structural = true },
            Str("background", 7, AdfCheck.Color),
            Enum("valign", Valign),
            localId);

    private static AdfNodeSpec Preserve(string type, bool inline) =>
        new(type, Editable: false, inline, Content: null, [], [], ContentRequired: false, AttrsRequired: false);

    private static AdfAttrRule LocalIdRule(bool required) => Str("localId", 64, AdfCheck.LocalId) with { Required = required };

    private static AdfAttrRule Str(string name, int maxLength, AdfCheck check = AdfCheck.None) =>
        new(name, AdfAttrType.String) { MaxLength = maxLength, Check = check };

    private static AdfAttrRule Enum(string name, params string[] values) => new(name, AdfAttrType.Enum) { Values = values };

    private static AdfAttrRule Int(string name, double min, double max) => new(name, AdfAttrType.Integer) { Min = min, Max = max };

    private static AdfAttrRule Num(string name, double min, double max) => new(name, AdfAttrType.Number) { Min = min, Max = max };

    private static AdfAttrRule Bool(string name) => new(name, AdfAttrType.Boolean);

    private static AdfAttrRule Refused(string name) => new(name, AdfAttrType.Refused);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalId();

    [GeneratedRegex(@"^[A-Za-z0-9+#._-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Language();

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,14})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^:[A-Za-z0-9_+\-]{1,64}:\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmojiShortName();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmojiId();

    [GeneratedRegex(@"^[A-Za-z0-9:_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountId();

    [GeneratedRegex(@"^(0|-?[1-9][0-9]{0,17})\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerLiteral();
}
