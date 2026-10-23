using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Retroscope.Core.Adf;

/// <summary>An image uploaded in this editing session (phase 11 fills the ledger): what a new media node may name.</summary>
public sealed record AdfUpload(string Name, double? Width, double? Height);

/// <summary>A refused write: a code from <see cref="AdfWriteCodes"/> and the dotted index path of the offending member
/// ("content.3.content.0.attrs.href"). It never carries content.</summary>
public readonly record struct AdfWriteError(string Code, string Path);

/// <param name="Base">The description as Jira holds it now, projected; tokens resolve against it.</param>
public sealed record AdfWriteContext(AdfEditView Base) {
    /// <summary>Uploads of this session by media id (phase 11); none until then.</summary>
    public Func<string, AdfUpload?> Upload { get; init; } = _ => null;

    /// <summary>The display name of an account mention search returned for this team (phase 9); null when unknown.</summary>
    public Func<string, string?> KnownAccount { get; init; } = _ => null;
}

public static class AdfWriteCodes {
    public const string InvalidJson = "invalid_json";
    public const string NotDoc = "not_doc";
    public const string TooLarge = "too_large";
    public const string TooDeep = "too_deep";
    public const string TooManyNodes = "too_many_nodes";
    public const string TooLong = "too_long";
    public const string UnknownNode = "unknown_node";
    public const string InvalidNesting = "invalid_nesting";
    public const string InvalidAttr = "invalid_attr";
    public const string InvalidMark = "invalid_mark";
    public const string InvalidText = "invalid_text";
    public const string InvalidHref = "invalid_href";
    public const string InvalidColor = "invalid_color";
    public const string TableShape = "table_shape";
    public const string DuplicateLocalId = "duplicate_local_id";
    public const string UnknownMedia = "unknown_media";
    public const string InvalidMedia = "invalid_media";
    public const string UnknownMention = "unknown_mention";
    public const string OpaqueDuplicated = "opaque_duplicated";
    public const string OpaqueMisplaced = "opaque_misplaced";
    public const string ReservedName = "reserved_name";
}

/// <summary>
/// The write validator for rich description saves (UX10, server-path §2). It rebuilds the document from the
/// allow-list, never echoing the client: only members <see cref="AdfSchema"/> names are written, every string is
/// re-encoded, and each opaque token, mark token and attribute stash is replaced by the original bytes from the base
/// (the issue as Jira holds it at save time). Pure; never throws on input.
/// </summary>
public static class AdfWrite {
    public const int MaxErrors = 20;

    private static readonly HashSet<string> MediaAttrNames = new(StringComparer.Ordinal) {
        "type", "id", "collection", "alt", "occurrenceKey", "url", "width", "height", "localId", AdfSchema.Keep,
    };

    // Copied from the base, never from the client: the client may only move and resize an image.
    private static readonly string[] MediaIdentity = ["type", "id", "collection", "alt", "occurrenceKey", "url"];

    /// <param name="missingRefs">Tokens the base does not hold: the save started from another version (stale).</param>
    public static bool TryWrite(
        ReadOnlySpan<byte> utf8,
        AdfWriteContext context,
        out byte[]? adf,
        out IReadOnlyList<AdfWriteError> errors,
        out IReadOnlyList<string> missingRefs) {
        ArgumentNullException.ThrowIfNull(context);
        using var writer = new Writer(context);
        adf = writer.Write(utf8);
        errors = writer.Errors;
        missingRefs = writer.Missing;
        if (writer.Errors.Count > 0 || writer.Missing.Count > 0) {
            adf = null;
            return false;
        }
        return true;
    }

    private sealed class Writer(AdfWriteContext context) : IDisposable {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly AdfBase _base = context.Base.Base;
        private readonly AdfEditLimits _limits = context.Base.Limits;
        private readonly List<string> _path = [];
        private readonly Dictionary<string, int> _opaqueUsed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _markUsed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _itemIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
        private Utf8JsonWriter? _json;
        private int _nodes;
        private int _text;
        private bool _tooDeep;

        public List<AdfWriteError> Errors { get; } = [];

        private Utf8JsonWriter Json => _json!;

        public List<string> Missing { get; } = [];

        public byte[]? Write(ReadOnlySpan<byte> utf8) {
            if (!context.Base.Editable) {
                Error(AdfWriteCodes.NotDoc);
                return null;
            }
            if (utf8.Length > _limits.Bytes) {
                Error(AdfWriteCodes.TooLarge);
                return null;
            }
            JsonDocument document;
            try {
                document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions {
                    // Each ADF level is two JSON levels (node, content array); the margin covers attrs and marks.
                    MaxDepth = 2 * (_limits.Depth + 2) + 8,
                    AllowDuplicateProperties = false,
                });
            } catch (JsonException) {
                Error(AdfWriteCodes.InvalidJson);
                return null;
            }
            using (document) {
                // Only what JSON itself needs is escaped, so text costs Jira's size cap what it costs in the base.
                _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Root(document.RootElement);
                Json.Flush();
                if (_nodes > _limits.Nodes) {
                    Error(AdfWriteCodes.TooManyNodes);
                }
                if (_text > _limits.Text) {
                    Error(AdfWriteCodes.TooLong);
                }
                if (_buffer.WrittenCount > _limits.Bytes) {
                    Error(AdfWriteCodes.TooLarge);
                }
                return _buffer.WrittenSpan.ToArray();
            }
        }

        private void Root(JsonElement root) {
            var ok = root.ValueKind == JsonValueKind.Object
                && AdfJson.Type(root) == "doc"
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetRawText() == "1"
                && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                && root.EnumerateObject().All(member => member.Name is "type" or "version" or "content");
            if (!ok) {
                Error(AdfWriteCodes.NotDoc);
                return;
            }
            Json.WriteStartObject();
            Json.WriteString("type", "doc");
            Json.WriteNumber("version", 1);
            Json.WritePropertyName("content");
            _path.Add("content");
            Content(root.GetProperty("content"), AdfSchema.Contexts["doc"], 1);
            _path.RemoveAt(_path.Count - 1);
            Json.WriteEndObject();
        }

        private void Content(JsonElement content, AdfContext context, int depth) {
            Json.WriteStartArray();
            var index = 0;
            foreach (var child in content.EnumerateArray()) {
                _path.Add(index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Node(child, context, depth);
                _path.RemoveAt(_path.Count - 1);
            }
            Json.WriteEndArray();
            var count = content.GetArrayLength();
            if (count < context.Min || count > context.Max) {
                Error(AdfWriteCodes.InvalidNesting);
            }
        }

        private void Node(JsonElement node, AdfContext context, int depth) {
            _nodes++;
            if (depth > _limits.Depth) {
                if (!_tooDeep) {
                    _tooDeep = true;
                    Error(AdfWriteCodes.TooDeep);
                }
                Json.WriteNullValue();
                return;
            }
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var typeValue) || !AdfSchema.TryString(typeValue, out var type)) {
                Refuse(AdfWriteCodes.UnknownNode);
                return;
            }
            if (type is AdfSchema.OpaqueBlock or AdfSchema.OpaqueInline) {
                Token(node, type, context);
                return;
            }
            var spec = AdfSchema.Node(type);
            if (AdfSchema.IsReserved(type) || spec is not { Editable: true }) {
                Refuse(AdfSchema.IsReserved(type) ? AdfWriteCodes.ReservedName : AdfWriteCodes.UnknownNode);
                return;
            }
            if (!context.Types.Contains(type)) {
                Refuse(AdfWriteCodes.InvalidNesting);
                return;
            }
            foreach (var member in node.EnumerateObject()) {
                var known = member.Name switch {
                    "type" or "attrs" or "marks" => true,
                    "content" => spec.Content is not null,
                    "text" => type == "text",
                    _ => false,
                };
                if (!known) {
                    At(member.Name, AdfSchema.IsReserved(member.Name) ? AdfWriteCodes.ReservedName
                        : member.Name == "content" ? AdfWriteCodes.InvalidNesting : AdfWriteCodes.InvalidAttr);
                }
            }

            Json.WriteStartObject();
            Json.WriteString("type", type);
            var attrs = Attrs(node, spec);
            if (spec.Content is { } contentContext) {
                if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) {
                    _path.Add("content");
                    Json.WritePropertyName("content");
                    Content(content, AdfSchema.Contexts[contentContext], depth + 1);
                    _path.RemoveAt(_path.Count - 1);
                    Structure(type, content, attrs);
                } else if (node.TryGetProperty("content", out _) || spec.ContentRequired) {
                    At("content", AdfWriteCodes.InvalidNesting);
                }
            }
            if (node.TryGetProperty("marks", out var marks)) {
                Marks(marks, type, context, node);
            }
            if (type == "text") {
                Text(node, context);
            }
            Json.WriteEndObject();
        }

        private void Text(JsonElement node, AdfContext context) {
            if (!node.TryGetProperty("text", out var value) || !AdfSchema.TryString(value, out var text)
                || !AdfSchema.ValidText(text, context.Name == "codeBlock")) {
                At("text", AdfWriteCodes.InvalidText);
                return;
            }
            _text += AdfSchema.CodePoints(text);
            Json.WriteString("text", text);
        }

        // Opaque tokens: the original bytes, at most as often as the base holds them, where the original may stand.
        private void Token(JsonElement node, string type, AdfContext context) {
            if (!TokenRef(node, out var id)) {
                Refuse(AdfWriteCodes.ReservedName);
                return;
            }
            if (!_base.Opaque.TryGetValue(id, out var entry)) {
                Miss(id);
                Json.WriteNullValue();
                return;
            }
            var used = _opaqueUsed.GetValueOrDefault(id);
            _opaqueUsed[id] = used + 1;
            if (used >= entry.Raws.Count) {
                Error(AdfWriteCodes.OpaqueDuplicated);
            } else if (!context.OpaqueChildren || (type == AdfSchema.OpaqueInline) != context.Inline
                || !(context.Types.Contains(entry.Kind) || entry.Contexts.Contains(context.Name))) {
                Error(AdfWriteCodes.OpaqueMisplaced);
            }
            Json.WriteRawValue(entry.Raws[Math.Min(used, entry.Raws.Count - 1)], skipInputValidation: true);
        }

        private Dictionary<string, JsonElement> Attrs(JsonElement node, AdfNodeSpec spec) {
            var written = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (!node.TryGetProperty("attrs", out var attrs)) {
                if (spec.AttrsRequired) {
                    At("attrs", AdfWriteCodes.InvalidAttr);
                }
                return written;
            }
            _path.Add("attrs");
            if (attrs.ValueKind != JsonValueKind.Object) {
                Error(AdfWriteCodes.InvalidAttr);
                _path.RemoveAt(_path.Count - 1);
                return written;
            }
            Json.WriteStartObject("attrs");
            switch (spec.Type) {
                case "media":
                    MediaAttrs(attrs, written);
                    break;
                case "mention":
                    MentionAttrs(attrs, spec, written);
                    break;
                default:
                    GenericAttrs(attrs, spec, written);
                    break;
            }
            Keep(attrs, spec.Type, written);
            Json.WriteEndObject();
            foreach (var rule in spec.Attrs) {
                if (rule.Required && !written.ContainsKey(rule.Name)) {
                    At(rule.Name, AdfWriteCodes.InvalidAttr);
                }
            }
            CrossRules(spec.Type, written);
            _path.RemoveAt(_path.Count - 1);
            return written;
        }

        private void GenericAttrs(JsonElement attrs, AdfNodeSpec spec, Dictionary<string, JsonElement> written) {
            foreach (var attr in attrs.EnumerateObject()) {
                if (attr.Name == AdfSchema.Keep) {
                    continue;
                }
                var rule = spec.Attr(attr.Name);
                if (AdfSchema.IsReserved(attr.Name) || rule is null || rule.Type == AdfAttrType.Refused) {
                    At(attr.Name, AdfSchema.IsReserved(attr.Name) ? AdfWriteCodes.ReservedName : AdfWriteCodes.InvalidAttr);
                    continue;
                }
                if (ValueError(rule, attr.Value) is { } code) {
                    At(attr.Name, code);
                    continue;
                }
                WriteAttr(attr.Name, rule, attr.Value);
                written[attr.Name] = attr.Value;
            }
        }

        // A media node names a base image (its identity copied from the base) or an upload of this session.
        private void MediaAttrs(JsonElement attrs, Dictionary<string, JsonElement> written) {
            var spec = AdfSchema.Node("media")!;
            foreach (var attr in attrs.EnumerateObject()) {
                if (!MediaAttrNames.Contains(attr.Name)) {
                    At(attr.Name, AdfSchema.IsReserved(attr.Name) ? AdfWriteCodes.ReservedName : AdfWriteCodes.InvalidAttr);
                }
            }
            var type = attrs.TryGetProperty("type", out var typeValue) && AdfSchema.TryString(typeValue, out var t) ? t : null;
            JsonElement? original = null;
            AdfUpload? upload = null;
            string? id = null;
            if (type is "file" or "link") {
                id = attrs.TryGetProperty("id", out var idValue) && AdfSchema.TryString(idValue, out var i) ? i : null;
                if (id is not null && _base.Media.TryGetValue(id, out var media)) {
                    original = media;
                } else if (id is not null && type == "file" && context.Upload(id) is { } uploaded) {
                    upload = uploaded;
                } else {
                    At("id", AdfWriteCodes.UnknownMedia);
                }
            } else if (type == "external") {
                // An external image is invisible here (CSP) and a tracking pixel for every Jira viewer: base only.
                var url = attrs.TryGetProperty("url", out var urlValue) && AdfSchema.TryString(urlValue, out var u) ? u : null;
                if (url is not null && _base.ExternalMedia.TryGetValue(url, out var media)) {
                    original = media;
                } else {
                    At("url", AdfWriteCodes.InvalidMedia);
                }
            } else {
                At("type", AdfWriteCodes.InvalidMedia);
            }
            if (original is { } source && source.TryGetProperty("attrs", out var baseAttrs)) {
                foreach (var name in MediaIdentity) {
                    if (baseAttrs.TryGetProperty(name, out var value)) {
                        Json.WritePropertyName(name);
                        value.WriteTo(Json);
                        written[name] = value;
                    }
                }
            } else if (upload is not null) {
                written["type"] = typeValue;
                Json.WriteString("type", "file");
                Json.WriteString("id", id);
                Json.WriteString("collection", "");
                Json.WriteString("alt", upload.Name);
            }
            foreach (var name in (ReadOnlySpan<string>)["width", "height", "localId"]) {
                if (!attrs.TryGetProperty(name, out var value)) {
                    continue;
                }
                var rule = spec.Attr(name)!;
                if (ValueError(rule, value) is { } code) {
                    At(name, code);
                    continue;
                }
                WriteAttr(name, rule, value);
                written[name] = value;
            }
            if (upload is not null) {
                foreach (var (name, size) in (ReadOnlySpan<(string, double?)>)[("width", upload.Width), ("height", upload.Height)]) {
                    if (!written.ContainsKey(name) && size is { } pixels) {
                        Json.WriteNumber(name, pixels);
                    }
                }
            }
        }

        // A mention names an account the description already mentions or one mention search returned: every new
        // mention makes Jira notify that account.
        private void MentionAttrs(JsonElement attrs, AdfNodeSpec spec, Dictionary<string, JsonElement> written) {
            var id = attrs.TryGetProperty("id", out var idValue) && AdfSchema.TryString(idValue, out var i) ? i : null;
            var display = id is null ? null : context.KnownAccount(id);
            if (id is not null && AdfSchema.Valid(spec.Attr("id")!, idValue, write: true) && !_base.Mentions.Contains(id) && display is null) {
                At("id", AdfWriteCodes.UnknownMention);
            }
            foreach (var attr in attrs.EnumerateObject()) {
                if (attr.Name == AdfSchema.Keep || (attr.Name == "text" && display is not null)) {
                    continue;
                }
                var rule = spec.Attr(attr.Name);
                if (AdfSchema.IsReserved(attr.Name) || rule is null) {
                    At(attr.Name, AdfSchema.IsReserved(attr.Name) ? AdfWriteCodes.ReservedName : AdfWriteCodes.InvalidAttr);
                    continue;
                }
                if (ValueError(rule, attr.Value) is { } code) {
                    At(attr.Name, code);
                    continue;
                }
                WriteAttr(attr.Name, rule, attr.Value);
                written[attr.Name] = attr.Value;
            }
            if (display is not null) {
                Json.WriteString("text", "@" + display);
            }
        }

        // The stash comes back from the base, for the attributes the client did not set.
        private void Keep(JsonElement attrs, string type, Dictionary<string, JsonElement> written) {
            if (!attrs.TryGetProperty(AdfSchema.Keep, out var keepValue)) {
                return;
            }
            if (!AdfSchema.TryString(keepValue, out var id)) {
                At(AdfSchema.Keep, AdfWriteCodes.ReservedName);
                return;
            }
            if (!_base.Keeps.TryGetValue(id, out var entry)) {
                Miss(id);
                return;
            }
            if (!entry.NodeTypes.Contains(type)) {
                At(AdfSchema.Keep, AdfWriteCodes.OpaqueMisplaced);
                return;
            }
            foreach (var (name, raw) in entry.Members) {
                if (!written.ContainsKey(name)) {
                    Json.WritePropertyName(name);
                    Json.WriteRawValue(raw, skipInputValidation: true);
                }
            }
        }

        private void CrossRules(string type, Dictionary<string, JsonElement> written) {
            switch (type) {
                case "panel" when written.ContainsKey("panelColor") && written.TryGetValue("panelType", out var panelType) && panelType.GetString() != "custom":
                    At("panelColor", AdfWriteCodes.InvalidAttr);
                    break;
                case "mediaSingle":
                    var pixel = written.TryGetValue("widthType", out var widthType) && widthType.GetString() == "pixel";
                    var width = written.TryGetValue("width", out var w) ? w.GetDouble() : (double?)null;
                    if (pixel ? width is null : width > 100) {
                        At("width", AdfWriteCodes.InvalidAttr);
                    }
                    break;
                case "tableCell" or "tableHeader" when written.TryGetValue("colwidth", out var colwidth):
                    var colspan = written.TryGetValue("colspan", out var span) ? span.GetInt64() : 1;
                    if (colwidth.GetArrayLength() != colspan) {
                        At("colwidth", AdfWriteCodes.InvalidAttr);
                    }
                    break;
                case "taskItem" or "decisionItem" when written.TryGetValue("localId", out var localId) && !_itemIds.Add(localId.GetString()!):
                    // Jira keys task tracking on localId.
                    At("localId", AdfWriteCodes.DuplicateLocalId);
                    break;
            }
        }

        // Rules over a node's children once they are written.
        private void Structure(string type, JsonElement content, Dictionary<string, JsonElement> attrs) {
            switch (type) {
                case "mediaSingle":
                    var children = content.EnumerateArray().Select(AdfJson.Type).ToList();
                    if (children.Count == 0 || children[0] != "media" || (children.Count == 2 && children[1] != "caption")) {
                        At("content", AdfWriteCodes.InvalidNesting);
                    }
                    break;
                case "table":
                    var grid = AdfTableGrid.Check(content.EnumerateArray().Select(row => AdfJson.Children(row).Select(Spans)));
                    var grandfathered = attrs.TryGetValue("localId", out var localId) && _base.IrregularTables.Contains(localId.GetString()!);
                    if (grid != true && !grandfathered) {
                        At("content", AdfWriteCodes.TableShape);
                    }
                    break;
            }
        }

        private static (int Colspan, int Rowspan, int Widths) Spans(JsonElement cell) {
            var colspan = AdfJson.TryAttr(cell, "colspan", out var c) && AdfSchema.IsInteger(c, out var cs) && cs is >= 1 and <= AdfSchema.MaxSpan ? (int)cs : 1;
            var rowspan = AdfJson.TryAttr(cell, "rowspan", out var r) && AdfSchema.IsInteger(r, out var rs) && rs is >= 1 and <= AdfSchema.MaxSpan ? (int)rs : 1;
            return (colspan, rowspan, colspan);
        }

        private void Marks(JsonElement marks, string type, AdfContext context, JsonElement node) {
            _path.Add("marks");
            if (marks.ValueKind != JsonValueKind.Array) {
                Error(AdfWriteCodes.InvalidMark);
                _path.RemoveAt(_path.Count - 1);
                return;
            }
            var codeText = type == "text" && marks.EnumerateArray().Any(mark => AdfJson.Type(mark) == "code");
            IReadOnlySet<string> allowed = type switch {
                "text" when context.Name == "codeBlock" => new HashSet<string>(),
                "text" => codeText ? AdfSchema.CodeTextMarks : AdfSchema.TextMarks,
                "paragraph" => context.ParagraphMarks,
                "heading" => context.HeadingMarks,
                _ => AdfSchema.Node(type)!.Marks.ToHashSet(StringComparer.Ordinal),
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var blockMarks = 0;
            Json.WriteStartArray("marks");
            var index = 0;
            foreach (var mark in marks.EnumerateArray()) {
                _path.Add(index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var markType = AdfJson.Type(mark);
                if (markType == AdfSchema.OpaqueMark) {
                    MarkToken(mark, type, context);
                } else if (markType is not null && AdfSchema.IsReserved(markType)) {
                    Error(AdfWriteCodes.ReservedName);
                } else if (AdfSchema.Mark(markType) is not { } spec || !allowed.Contains(markType!) || !seen.Add(markType!)) {
                    Error(AdfWriteCodes.InvalidMark);
                } else {
                    blockMarks += spec.Block ? 1 : 0;
                    Mark(mark, spec);
                }
                _path.RemoveAt(_path.Count - 1);
            }
            Json.WriteEndArray();
            if (blockMarks > 1) {
                Error(AdfWriteCodes.InvalidMark);
            }
            _path.RemoveAt(_path.Count - 1);
        }

        private void Mark(JsonElement mark, AdfMarkSpec spec) {
            Json.WriteStartObject();
            Json.WriteString("type", spec.Type);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in mark.EnumerateObject()) {
                if (member.Name is not ("type" or "attrs")) {
                    At(member.Name, AdfSchema.IsReserved(member.Name) ? AdfWriteCodes.ReservedName : AdfWriteCodes.InvalidMark);
                }
            }
            if (mark.TryGetProperty("attrs", out var attrs)) {
                _path.Add("attrs");
                if (attrs.ValueKind != JsonValueKind.Object) {
                    Error(AdfWriteCodes.InvalidMark);
                } else {
                    Json.WriteStartObject("attrs");
                    foreach (var attr in attrs.EnumerateObject()) {
                        var rule = spec.Attrs.FirstOrDefault(r => r.Name == attr.Name);
                        if (rule is null) {
                            At(attr.Name, AdfSchema.IsReserved(attr.Name) ? AdfWriteCodes.ReservedName : AdfWriteCodes.InvalidMark);
                        } else if (ValueError(rule, attr.Value) is { } code) {
                            At(attr.Name, code);
                        } else {
                            WriteAttr(attr.Name, rule, attr.Value);
                            names.Add(attr.Name);
                        }
                    }
                    Json.WriteEndObject();
                }
                _path.RemoveAt(_path.Count - 1);
            }
            if (spec.Attrs.Any(rule => rule.Required && !names.Contains(rule.Name))) {
                Error(AdfWriteCodes.InvalidMark);
            }
            Json.WriteEndObject();
        }

        private void MarkToken(JsonElement mark, string type, AdfContext context) {
            if (!TokenRef(mark, out var id)) {
                Error(AdfWriteCodes.ReservedName);
                return;
            }
            if (!_base.Marks.TryGetValue(id, out var entry)) {
                Miss(id);
                return;
            }
            var placed = type == "text" ? entry.On.Contains("text") && context.Name != "codeBlock" : entry.On.Contains(type);
            if (!placed) {
                Error(AdfWriteCodes.OpaqueMisplaced);
            }
            var used = _markUsed.GetValueOrDefault(id);
            _markUsed[id] = used + 1;
            Json.WriteRawValue(entry.Raws[Math.Min(used, entry.Raws.Count - 1)], skipInputValidation: true);
        }

        // {"type": rs…, "attrs": {"ref": "…"}} and nothing else.
        private static bool TokenRef(JsonElement token, out string id) {
            id = "";
            if (token.EnumerateObject().Any(member => member.Name is not ("type" or "attrs"))
                || !token.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object
                || attrs.EnumerateObject().Any(member => member.Name != AdfSchema.Ref)
                || !attrs.TryGetProperty(AdfSchema.Ref, out var value) || !AdfSchema.TryString(value, out id)) {
                return false;
            }
            return id.Length is > 0 and <= 64;
        }

        private string? ValueError(AdfAttrRule rule, JsonElement value) {
            if (AdfSchema.Valid(rule, value, write: true)) {
                return null;
            }
            // A value Jira itself wrote into this description may stay as it is.
            var grandfathered = AdfSchema.TryString(value, out var text) && AdfSchema.Valid(rule, value, write: false) && rule.Check switch {
                AdfCheck.Href or AdfCheck.CardUrl => _base.Hrefs.Contains(text),
                AdfCheck.StatusColor => _base.StatusColors.Contains(text),
                _ => false,
            };
            if (grandfathered) {
                return null;
            }
            return rule.Check switch {
                AdfCheck.Href or AdfCheck.CardUrl => AdfWriteCodes.InvalidHref,
                AdfCheck.Color or AdfCheck.StatusColor => AdfWriteCodes.InvalidColor,
                _ => AdfWriteCodes.InvalidAttr,
            };
        }

        private void WriteAttr(string name, AdfAttrRule rule, JsonElement value) {
            Json.WritePropertyName(name);
            if (rule.Type is AdfAttrType.String or AdfAttrType.Enum) {
                AdfSchema.TryString(value, out var text);
                Json.WriteStringValue(text);
            } else {
                // Numbers keep the digits the client sent (already range-checked); booleans and lists are rebuilt the same way.
                value.WriteTo(Json);
            }
        }

        public void Dispose() => _json?.Dispose();

        private void Miss(string id) {
            if (_missing.Add(id)) {
                Missing.Add(id);
            }
        }

        private void Refuse(string code) {
            Error(code);
            Json.WriteNullValue();
        }

        private void At(string member, string code) {
            _path.Add(member);
            Error(code);
            _path.RemoveAt(_path.Count - 1);
        }

        private void Error(string code) {
            if (Errors.Count < MaxErrors) {
                Errors.Add(new AdfWriteError(code, string.Join('.', _path)));
            }
        }
    }
}
