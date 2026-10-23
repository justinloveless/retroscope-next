using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Retroscope.Core.Adf;

/// <summary>An embedded media node of the description and the attachment the proxy serves for it (null: unresolved).</summary>
public sealed record AdfEditMedia(string Id, string? AttachmentId, string? Name, bool Image, double? Width, double? Height);

/// <summary>One distinct opaque node: its token ref, original type and original ADF, as Jira holds it.</summary>
public sealed record AdfOpaquePart(string Ref, string Kind, bool Inline, string Raw);

/// <summary>One distinct opaque mark (an rsMark token).</summary>
public sealed record AdfOpaqueMark(string Ref, string Kind);

/// <summary>The caps a save of this description is held to: the defaults, or the description's own size if larger.</summary>
public sealed record AdfEditLimits(int Nodes, int Depth, int Text, int Bytes);

/// <summary>
/// A description as the rich editor receives it (UX10): the ADF with everything the editor does not understand replaced
/// by tokens, plus what a save needs to restore those tokens byte for byte. Nothing here is stored; it is computed from
/// the issue as Jira holds it, and recomputed from Jira's current copy when a save arrives.
/// </summary>
public sealed class AdfEditView {
    internal AdfEditView(bool editable, string adf, IReadOnlyList<AdfEditMedia> media, IReadOnlyList<AdfOpaquePart> opaque,
        IReadOnlyList<AdfOpaqueMark> marks, AdfEditLimits limits, AdfBase @base) {
        Editable = editable;
        Adf = adf;
        Media = media;
        Opaque = opaque;
        Marks = marks;
        Limits = limits;
        Base = @base;
    }

    /// <summary>False when Jira's description is not an ADF document the editor can write back.</summary>
    public bool Editable { get; }

    /// <summary>The projected document (empty when not editable).</summary>
    public string Adf { get; }

    public IReadOnlyList<AdfEditMedia> Media { get; }

    public IReadOnlyList<AdfOpaquePart> Opaque { get; }

    public IReadOnlyList<AdfOpaqueMark> Marks { get; }

    public AdfEditLimits Limits { get; }

    internal AdfBase Base { get; }
}

/// <summary>What the write validator needs from the description a save started from.</summary>
internal sealed class AdfBase {
    public Dictionary<string, OpaqueEntry> Opaque { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MarkEntry> Marks { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, KeepEntry> Keeps { get; } = new(StringComparer.Ordinal);

    /// <summary>Editable file and link media by id: the node as Jira wrote it.</summary>
    public Dictionary<string, JsonElement> Media { get; } = new(StringComparer.Ordinal);

    /// <summary>Editable external media by url.</summary>
    public Dictionary<string, JsonElement> ExternalMedia { get; } = new(StringComparer.Ordinal);

    /// <summary>Account ids the editable mentions name.</summary>
    public HashSet<string> Mentions { get; } = new(StringComparer.Ordinal);

    /// <summary>Every href and card url in the description, which a save may keep as Jira wrote it.</summary>
    public HashSet<string> Hrefs { get; } = new(StringComparer.Ordinal);

    public HashSet<string> StatusColors { get; } = new(StringComparer.Ordinal);

    /// <summary>localIds of tables whose grid is not rectangular, which a save may keep that way.</summary>
    public HashSet<string> IrregularTables { get; } = new(StringComparer.Ordinal);

    public sealed class OpaqueEntry(string kind, bool inline) {
        public string Kind { get; } = kind;

        public bool Inline { get; } = inline;

        /// <summary>The original of each occurrence, in document order.</summary>
        public List<string> Raws { get; } = [];

        /// <summary>The contexts the occurrences sat in.</summary>
        public HashSet<string> Contexts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class MarkEntry(string kind) {
        public string Kind { get; } = kind;

        public List<string> Raws { get; } = [];

        /// <summary>"text", or the type of the node the mark sat on.</summary>
        public HashSet<string> On { get; } = new(StringComparer.Ordinal);

        /// <summary>Jira's copy had it beside another mark of its kind, or another block mark, which a save may keep.</summary>
        public bool Shared { get; set; }
    }

    public sealed class KeepEntry(IReadOnlyList<KeyValuePair<string, string>> members) {
        /// <summary>The stashed attributes, values as Jira wrote them.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Members { get; } = members;

        public HashSet<string> NodeTypes { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Projects a Jira description into the editable form (UX10, server-path §1.3). A node the allow-list knows, with valid
/// attributes, passes through; any other node becomes an <c>rsOpaque</c>/<c>rsOpaqueInline</c> token whose ref is
/// the node's <see cref="AdfLossiness.IslandId"/>; an unknown or invalid mark becomes an <c>rsMark</c> token; unknown
/// or invalid optional attributes move into one <c>rsKeep</c> stash. Pure: no IO, clock or reflection.
/// </summary>
public static class AdfEditProjection {
    private const string EmptyDoc = """{"type":"doc","version":1,"content":[]}""";

    public static AdfEditView Project(JsonElement? description, IReadOnlyList<AdfAttachment> attachments) {
        if (description is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } doc) {
            using var empty = JsonDocument.Parse(EmptyDoc);
            return Project(empty.RootElement, attachments);
        }
        if (!IsWritableDoc(doc)) {
            return new AdfEditView(false, "", [], [], [], Limits(new Measure(), doc), new AdfBase());
        }
        var projector = new Projector(doc);
        var root = projector.Root(doc);
        var serializer = new Serializer();
        var adf = serializer.Write(root);
        CollectHrefs(doc, serializer.Base.Hrefs);
        var media = MediaTable(doc, attachments, serializer.Base);
        var opaque = serializer.Base.Opaque
            .Select(entry => new AdfOpaquePart(entry.Key, entry.Value.Kind, entry.Value.Inline, entry.Value.Raws[0]))
            .ToList();
        var marks = serializer.Base.Marks.Select(entry => new AdfOpaqueMark(entry.Key, entry.Value.Kind)).ToList();
        return new AdfEditView(true, adf, media, opaque, marks, Limits(serializer.Measure, doc), serializer.Base);
    }

    // Root: {"type":"doc","version":1,"content":[...]} and nothing else, or the save could not be written back whole.
    private static bool IsWritableDoc(JsonElement doc) {
        if (doc.ValueKind != JsonValueKind.Object || AdfJson.Type(doc) != "doc") {
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in doc.EnumerateObject()) {
            var ok = names.Add(member.Name) && member.Name switch {
                "type" => true,
                "version" => member.Value.ValueKind == JsonValueKind.Number && member.Value.GetRawText() == "1",
                "content" => member.Value.ValueKind == JsonValueKind.Array,
                _ => false,
            };
            if (!ok) {
                return false;
            }
        }
        return names.Contains("content") && names.Contains("version");
    }

    private static AdfEditLimits Limits(Measure measure, JsonElement doc) => new(
        Math.Max(AdfSchema.MaxNodes, measure.Nodes),
        Math.Max(AdfSchema.MaxDepth, measure.Depth),
        Math.Max(AdfSchema.MaxText, measure.Text),
        Math.Max(AdfSchema.MaxBytes, Encoding.UTF8.GetByteCount(doc.GetRawText())));

    private static List<AdfEditMedia> MediaTable(JsonElement doc, IReadOnlyList<AdfAttachment> attachments, AdfBase @base) {
        var map = AdfMediaMap.Build(doc, attachments);
        return @base.Media.Select(entry => {
            var attachment = map.Resolve(entry.Key);
            return new AdfEditMedia(
                entry.Key,
                attachment?.Id,
                attachment?.FileName,
                attachment?.IsImage ?? false,
                AdfJson.Number(entry.Value, "width"),
                AdfJson.Number(entry.Value, "height"));
        }).ToList();
    }

    private static void CollectHrefs(JsonElement node, HashSet<string> hrefs) {
        switch (node.ValueKind) {
            case JsonValueKind.Object:
                foreach (var member in node.EnumerateObject()) {
                    if (member.Name == "attrs" && member.Value.ValueKind == JsonValueKind.Object) {
                        foreach (var name in (ReadOnlySpan<string>)["href", "url"]) {
                            if (member.Value.TryGetProperty(name, out var value) && AdfSchema.TryString(value, out var href)) {
                                hrefs.Add(href);
                            }
                        }
                    }
                    CollectHrefs(member.Value, hrefs);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) {
                    CollectHrefs(item, hrefs);
                }
                break;
        }
    }

    internal sealed class Measure {
        public int Nodes { get; set; }

        public int Depth { get; set; }

        public int Text { get; set; }
    }

    // The projected tree, built first so that a structural parent can still turn opaque after its children were seen.

    private abstract class Item(JsonElement source) {
        public JsonElement Source { get; } = source;
    }

    private sealed class Token(JsonElement source, bool inline) : Item(source) {
        public bool Inline { get; } = inline;
    }

    private sealed class Node(JsonElement source, string type) : Item(source) {
        public string Type { get; } = type;

        public List<KeyValuePair<string, JsonElement>>? Attrs { get; set; }

        public List<KeyValuePair<string, JsonElement>> Stash { get; } = [];

        public List<Item>? Content { get; set; }

        public string? ContentContext { get; set; }

        public List<MarkItem>? Marks { get; set; }

        public string? Text { get; set; }

        public bool Irregular { get; set; }
    }

    private sealed record MarkItem(JsonElement Source, bool Opaque);

    private sealed class Projector {
        private readonly Dictionary<string, int> _itemIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _tableIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _claimedItemIds = new(StringComparer.Ordinal);
        private int _visited;

        public Projector(JsonElement doc) {
            CountIds(doc);
        }

        public Node Root(JsonElement doc) {
            var root = new Node(doc, "doc") { ContentContext = "doc", Content = [] };
            var context = AdfSchema.Contexts["doc"];
            foreach (var child in doc.GetProperty("content").EnumerateArray()) {
                root.Content.Add(Project(child, context, 1));
            }
            return root;
        }

        private Item Project(JsonElement node, AdfContext context, int depth) {
            _visited++;
            var type = AdfJson.Type(node);
            var spec = AdfSchema.Node(type);
            if (spec is not { Editable: true } || !context.Types.Contains(type!)
                || depth > AdfSchema.MaxDepth || _visited > AdfSchema.MaxNodes) {
                return Opaque(node, context);
            }
            var projected = new Node(node, type!);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in node.EnumerateObject()) {
                if (!names.Add(member.Name) || !Member(projected, spec, member, context, depth)) {
                    return Opaque(node, context);
                }
            }
            if (spec.AttrsRequired && projected.Attrs is null
                || spec.ContentRequired && projected.Content is null
                || type == "text" && projected.Text is null
                || !RequiredAttrs(projected, spec)
                || !NodeRules(projected)) {
                return Opaque(node, context);
            }
            return projected;
        }

        private bool Member(Node projected, AdfNodeSpec spec, JsonProperty member, AdfContext context, int depth) {
            switch (member.Name) {
                case "type":
                    return true;
                case "attrs":
                    return Attrs(projected, spec, member.Value);
                case "content":
                    return spec.Content is not null && Content(projected, AdfSchema.Contexts[spec.Content], member.Value, depth);
                case "marks":
                    return member.Value.ValueKind == JsonValueKind.Array && Marks(projected, context, member.Value);
                case "text":
                    if (projected.Type != "text" || !AdfSchema.TryString(member.Value, out var text) || !AdfSchema.ValidText(text, context.Name == "codeBlock")) {
                        return false;
                    }
                    projected.Text = text;
                    return true;
                default:
                    return false;
            }
        }

        private static bool Attrs(Node projected, AdfNodeSpec spec, JsonElement attrs) {
            if (attrs.ValueKind != JsonValueKind.Object) {
                return false;
            }
            projected.Attrs = [];
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attr in attrs.EnumerateObject()) {
                if (!names.Add(attr.Name)) {
                    return false;
                }
                var rule = spec.Attr(attr.Name);
                if (rule is null) {
                    projected.Stash.Add(new(attr.Name, attr.Value));
                } else if (rule.Type == AdfAttrType.Refused) {
                    return false;
                } else if (AdfSchema.Valid(rule, attr.Value, write: false)) {
                    projected.Attrs.Add(new(attr.Name, attr.Value));
                } else if (rule.Required || rule.Structural) {
                    return false;
                } else {
                    projected.Stash.Add(new(attr.Name, attr.Value));
                }
            }
            return true;
        }

        private bool Content(Node projected, AdfContext context, JsonElement content, int depth) {
            if (content.ValueKind != JsonValueKind.Array) {
                return false;
            }
            var count = content.GetArrayLength();
            if (count < context.Min || count > context.Max) {
                return false;
            }
            projected.ContentContext = context.Name;
            projected.Content = [];
            foreach (var child in content.EnumerateArray()) {
                var item = Project(child, context, depth + 1);
                if (item is Token && !context.OpaqueChildren) {
                    return false;
                }
                projected.Content.Add(item);
            }
            return true;
        }

        private static bool Marks(Node projected, AdfContext context, JsonElement marks) {
            if (projected.Type == "text" && context.Name == "codeBlock" && marks.GetArrayLength() > 0) {
                return false;
            }
            projected.Marks = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var allowed = AllowedMarks(projected, context, marks);
            foreach (var mark in marks.EnumerateArray()) {
                var type = AdfJson.Type(mark);
                var keep = type is not null && allowed.Contains(type) && seen.Add(type) && ValidMark(mark, AdfSchema.Mark(type)!);
                projected.Marks.Add(new MarkItem(mark, Opaque: !keep));
            }
            // alignment and indentation exclude each other: the second becomes opaque.
            var block = projected.Marks.Where(m => !m.Opaque && AdfSchema.Mark(AdfJson.Type(m.Source))!.Block).Skip(1).ToList();
            foreach (var extra in block) {
                projected.Marks[projected.Marks.IndexOf(extra)] = extra with { Opaque = true };
            }
            return true;
        }

        private static IReadOnlySet<string> AllowedMarks(Node projected, AdfContext context, JsonElement marks) {
            switch (projected.Type) {
                case "text":
                    var code = marks.EnumerateArray().Any(mark => AdfJson.Type(mark) == "code");
                    return code ? AdfSchema.CodeTextMarks : AdfSchema.TextMarks;
                case "paragraph":
                    return context.ParagraphMarks;
                case "heading":
                    return context.HeadingMarks;
                default:
                    return AdfSchema.Node(projected.Type)!.Marks.ToHashSet(StringComparer.Ordinal);
            }
        }

        private static bool ValidMark(JsonElement mark, AdfMarkSpec spec) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in mark.EnumerateObject()) {
                if (!names.Add(member.Name) || member.Name is not ("type" or "attrs")) {
                    return false;
                }
            }
            if (!mark.TryGetProperty("attrs", out var attrs)) {
                return spec.Attrs.All(rule => !rule.Required);
            }
            if (attrs.ValueKind != JsonValueKind.Object) {
                return false;
            }
            var attrNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attr in attrs.EnumerateObject()) {
                var rule = spec.Attrs.FirstOrDefault(r => r.Name == attr.Name);
                if (!attrNames.Add(attr.Name) || rule is null || !AdfSchema.Valid(rule, attr.Value, write: false)) {
                    return false;
                }
            }
            return spec.Attrs.All(rule => !rule.Required || attrNames.Contains(rule.Name));
        }

        private static bool RequiredAttrs(Node projected, AdfNodeSpec spec) {
            foreach (var rule in spec.Attrs) {
                if (rule.Required && projected.Attrs?.Any(attr => attr.Key == rule.Name) != true) {
                    return false;
                }
            }
            return true;
        }

        // Rules that span attributes or children. False makes the node opaque.
        private bool NodeRules(Node projected) {
            switch (projected.Type) {
                case "panel":
                    // A colour only means something on a custom panel; any other keeps it as Jira wrote it.
                    if (Attr(projected, "panelType") is { } panelType && panelType.GetString() != "custom") {
                        MoveToStash(projected, "panelColor");
                    }
                    return true;
                case "mediaSingle":
                    var widthType = Attr(projected, "widthType")?.GetString();
                    var width = Attr(projected, "width") is { } w ? w.GetDouble() : (double?)null;
                    if (widthType == "pixel" ? width is null : width > 100) {
                        MoveToStash(projected, "width");
                        MoveToStash(projected, "widthType");
                    }
                    return projected.Content![0] is Node { Type: "media" }
                        && (projected.Content.Count == 1 || projected.Content[1] is Node { Type: "caption" });
                case "taskItem":
                case "decisionItem":
                    // Jira keys task tracking on localId, so only the first of a repeated id stays editable.
                    var id = Attr(projected, "localId")!.Value.GetString()!;
                    return _itemIds[id] == 1 || _claimedItemIds.Add(id);
                case "table":
                    return Table(projected);
                default:
                    return true;
            }
        }

        private bool Table(Node table) {
            var grid = AdfTableGrid.Check(table.Content!.Select(row => ((Node)row).Content!.Select(cell => Spans((Node)cell))));
            if (grid is null) {
                return false;
            }
            if (grid == true) {
                return true;
            }
            // A ragged table Jira wrote stays editable only when its localId lets a save recognise it.
            table.Irregular = true;
            return Attr(table, "localId") is { } localId && _tableIds[localId.GetString()!] == 1;
        }

        private static (int Colspan, int Rowspan, int Widths) Spans(Node cell) {
            var colspan = Attr(cell, "colspan") is { } c ? (int)c.GetInt64() : 1;
            var rowspan = Attr(cell, "rowspan") is { } r ? (int)r.GetInt64() : 1;
            var widths = Attr(cell, "colwidth") is { } list ? list.GetArrayLength() : colspan;
            return (colspan, rowspan, widths);
        }

        private static Token Opaque(JsonElement node, AdfContext context) => new(node, context.Inline);

        private static JsonElement? Attr(Node node, string name) {
            foreach (var attr in node.Attrs ?? []) {
                if (attr.Key == name) {
                    return attr.Value;
                }
            }
            return null;
        }

        private static void MoveToStash(Node node, string name) {
            var index = node.Attrs?.FindIndex(attr => attr.Key == name) ?? -1;
            if (index >= 0) {
                node.Stash.Add(node.Attrs![index]);
                node.Attrs.RemoveAt(index);
            }
        }

        private void CountIds(JsonElement node) {
            if (node.ValueKind == JsonValueKind.Array) {
                foreach (var item in node.EnumerateArray()) {
                    CountIds(item);
                }
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) {
                return;
            }
            var type = AdfJson.Type(node);
            var counts = type is "taskItem" or "decisionItem" ? _itemIds : type == "table" ? _tableIds : null;
            if (counts is not null && AdfJson.TryAttr(node, "localId", out var id) && AdfSchema.TryString(id, out var localId)) {
                counts[localId] = counts.GetValueOrDefault(localId) + 1;
            }
            foreach (var member in node.EnumerateObject()) {
                if (member.Name is "content") {
                    CountIds(member.Value);
                }
            }
        }
    }

    private sealed class Serializer {
        public AdfBase Base { get; } = new();

        public Measure Measure { get; } = new();

        public string Write(Node root) {
            var buffer = new ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(buffer)) {
                json.WriteStartObject();
                json.WriteString("type", "doc");
                json.WriteNumber("version", 1);
                json.WritePropertyName("content");
                Content(json, root.Content!, AdfSchema.Contexts["doc"], 1);
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private void Item(Utf8JsonWriter json, Item item, AdfContext context, int depth) {
            Measure.Nodes++;
            Measure.Depth = Math.Max(Measure.Depth, depth);
            if (item is Token token) {
                var id = AdfLossiness.IslandId(token.Source);
                if (!Base.Opaque.TryGetValue(id, out var entry)) {
                    entry = new AdfBase.OpaqueEntry(AdfJson.Type(token.Source) ?? "unknown", token.Inline);
                    Base.Opaque[id] = entry;
                }
                entry.Raws.Add(token.Source.GetRawText());
                entry.Contexts.Add(context.Name);
                WriteToken(json, token.Inline ? AdfSchema.OpaqueInline : AdfSchema.OpaqueBlock, id);
                return;
            }
            var node = (Node)item;
            Remember(node);
            json.WriteStartObject();
            json.WriteString("type", node.Type);
            foreach (var member in node.Source.EnumerateObject()) {
                switch (member.Name) {
                    case "attrs":
                        Attrs(json, node);
                        break;
                    case "content":
                        json.WritePropertyName("content");
                        Content(json, node.Content!, AdfSchema.Contexts[node.ContentContext!], depth + 1);
                        break;
                    case "marks":
                        Marks(json, node);
                        break;
                    case "text":
                        json.WriteString("text", node.Text);
                        Measure.Text += AdfSchema.CodePoints(node.Text!);
                        break;
                }
            }
            json.WriteEndObject();
        }

        private void Content(Utf8JsonWriter json, List<Item> content, AdfContext context, int depth) {
            json.WriteStartArray();
            foreach (var child in content) {
                Item(json, child, context, depth);
            }
            json.WriteEndArray();
        }

        private void Attrs(Utf8JsonWriter json, Node node) {
            json.WriteStartObject("attrs");
            foreach (var attr in node.Attrs!) {
                json.WritePropertyName(attr.Key);
                attr.Value.WriteTo(json);
            }
            if (node.Stash.Count > 0) {
                var members = node.Stash.Select(attr => new KeyValuePair<string, string>(attr.Key, attr.Value.GetRawText())).ToList();
                var id = KeepRef(members);
                if (!Base.Keeps.TryGetValue(id, out var keep)) {
                    keep = new AdfBase.KeepEntry(members);
                    Base.Keeps[id] = keep;
                }
                keep.NodeTypes.Add(node.Type);
                json.WriteString(AdfSchema.Keep, id);
            }
            json.WriteEndObject();
        }

        private void Marks(Utf8JsonWriter json, Node node) {
            json.WriteStartArray("marks");
            var kinds = node.Marks!.Select(mark => AdfJson.Type(mark.Source)).ToList();
            var blocks = kinds.Count(kind => AdfSchema.Mark(kind) is { Block: true });
            foreach (var mark in node.Marks!) {
                if (!mark.Opaque) {
                    mark.Source.WriteTo(json);
                    continue;
                }
                var id = AdfLossiness.IslandId(mark.Source);
                if (!Base.Marks.TryGetValue(id, out var entry)) {
                    entry = new AdfBase.MarkEntry(AdfJson.Type(mark.Source) ?? "unknown");
                    Base.Marks[id] = entry;
                }
                entry.Raws.Add(mark.Source.GetRawText());
                entry.On.Add(node.Type == "text" ? "text" : node.Type);
                var kind = AdfJson.Type(mark.Source);
                entry.Shared |= kinds.Count(other => other == kind) > 1 || (blocks > 1 && AdfSchema.Mark(kind) is { Block: true });
                WriteToken(json, AdfSchema.OpaqueMark, id);
            }
            json.WriteEndArray();
        }

        private void Remember(Node node) {
            switch (node.Type) {
                case "media" when AdfJson.Attr(node.Source, "type") is "file" or "link" && AdfJson.Attr(node.Source, "id") is { } id:
                    Base.Media.TryAdd(id, node.Source.Clone());
                    break;
                case "media" when AdfJson.Attr(node.Source, "url") is { } url:
                    Base.ExternalMedia.TryAdd(url, node.Source.Clone());
                    break;
                case "mention" when AdfJson.Attr(node.Source, "id") is { } account:
                    Base.Mentions.Add(account);
                    break;
                case "status" when AdfJson.Attr(node.Source, "color") is { } color && AdfSchema.IsColor(color):
                    Base.StatusColors.Add(color);
                    break;
                case "table" when node.Irregular:
                    Base.IrregularTables.Add(AdfJson.Attr(node.Source, "localId")!);
                    break;
            }
        }

        private static string KeepRef(List<KeyValuePair<string, string>> members) {
            var buffer = new ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(buffer)) {
                json.WriteStartObject();
                foreach (var member in members) {
                    json.WritePropertyName(member.Key);
                    json.WriteRawValue(member.Value, skipInputValidation: true);
                }
                json.WriteEndObject();
            }
            using var stash = JsonDocument.Parse(buffer.WrittenMemory);
            return AdfLossiness.IslandId(stash.RootElement);
        }

        internal static void WriteToken(Utf8JsonWriter json, string type, string id) {
            json.WriteStartObject();
            json.WriteString("type", type);
            json.WriteStartObject("attrs");
            json.WriteString(AdfSchema.Ref, id);
            json.WriteEndObject();
            json.WriteEndObject();
        }
    }
}
