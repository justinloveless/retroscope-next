using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// Pins the editor's allow-list (<see cref="AdfSchema"/>) to the vendored @atlaskit/adf-schema full.json: every node,
/// mark, attribute, enum value, range and content expression in the table exists in the schema. When the vendored
/// version is bumped, the set comparisons list what the new schema added, for a decision.
/// </summary>
[Trait("Category", "Adf")]
public sealed class AdfSchemaTests {
    public const string Version = "57.7.2";

    // Schema marks the editor does not edit; text and nodes carry them as rsMark tokens.
    private static readonly string[] PreservedMarks = ["annotation", "border", "breakout", "dataConsumer", "fontSize", "fragment"];

    // Link attributes that only media links carry; a link mark with them is kept whole as an rsMark.
    private static readonly string[] PreservedLinkAttrs = ["collection", "id", "occurrenceKey"];

    private static readonly JsonElement Definitions = LoadDefinitions();

    [Fact]
    public void One_schema_version_is_vendored() {
        var folder = Path.Combine(RepoRoot.Path, "tests", "fixtures", "adf-schema");
        Assert.Equal([Version], Directory.GetDirectories(folder).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(folder, Version, "LICENSE")));
    }

    [Fact]
    public void Every_schema_node_is_in_the_table_and_nothing_else_is() {
        var schemaNodes = Defs().Where(def => def.Name.EndsWith("_node", StringComparison.Ordinal))
            .SelectMany(def => TypesOf(def.Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Sorted(schemaNodes), Sorted(AdfSchema.Nodes.Keys));
    }

    [Fact]
    public void Every_schema_mark_is_edited_or_preserved_and_nothing_else_is() {
        var schemaMarks = Defs().Where(def => def.Name.EndsWith("_mark", StringComparison.Ordinal))
            .SelectMany(def => TypesOf(def.Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Sorted(schemaMarks), Sorted(AdfSchema.Marks.Keys.Concat(PreservedMarks)));
    }

    [Fact]
    public void Node_attributes_are_exactly_the_schemas() {
        foreach (var (type, spec) in AdfSchema.Nodes.Where(node => node.Value.Editable)) {
            var variants = AttrVariants(NodeDefs(type)).ToList();
            var schemaAttrs = variants.SelectMany(variant => variant.Properties.Keys).ToHashSet(StringComparer.Ordinal);

            Assert.True(Sorted(schemaAttrs).SequenceEqual(Sorted(spec.Attrs.Select(attr => attr.Name))), $"{type}: schema {string.Join(',', Sorted(schemaAttrs))}");
            foreach (var attr in spec.Attrs) {
                CheckAttr(type, attr, variants);
            }
            // An attribute every variant requires is required here too.
            var required = variants.Count == 0 ? [] : variants.Select(v => v.Required).Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));
            foreach (var name in required) {
                Assert.True(spec.Attr(name)!.Required, $"{type}.{name} is required by the schema");
            }
        }
    }

    [Fact]
    public void Mark_attributes_are_the_schemas_minus_the_preserved_link_ones() {
        foreach (var (type, spec) in AdfSchema.Marks) {
            var variants = AttrVariants(Defs().Where(def => def.Name == type + "_mark").Select(def => def.Value)).ToList();
            var schemaAttrs = variants.SelectMany(variant => variant.Properties.Keys).ToHashSet(StringComparer.Ordinal);
            var expected = type == "link" ? schemaAttrs.Except(PreservedLinkAttrs) : schemaAttrs;

            Assert.Equal(Sorted(expected), Sorted(spec.Attrs.Select(attr => attr.Name)));
            foreach (var attr in spec.Attrs) {
                CheckAttr(type, attr, variants);
            }
        }
    }

    [Fact]
    public void Each_node_holds_exactly_the_children_the_schema_allows() {
        foreach (var (type, spec) in AdfSchema.Nodes.Where(node => node.Value.Editable)) {
            var defs = NodeDefs(type).ToList();
            var children = defs.SelectMany(ContentItems).SelectMany(TypesOf).ToHashSet(StringComparer.Ordinal);
            if (spec.Content is null) {
                Assert.True(children.Count == 0, $"{type} is a leaf here but the schema gives it content");
                continue;
            }
            var context = AdfSchema.Contexts[spec.Content];
            Assert.True(Sorted(children).SequenceEqual(Sorted(context.Types)), $"{type} ({spec.Content}): schema {string.Join(',', Sorted(children))}");
            if (spec.ContentRequired) {
                Assert.True(defs.Any(def => Required(def).Contains("content")), $"{type}: content is not required by the schema");
            }
        }
    }

    [Fact]
    public void Contexts_mirror_their_schema_groups_counts_and_block_marks() {
        foreach (var context in AdfSchema.Contexts.Values) {
            var def = Definitions.GetProperty(context.Schema);
            var items = ContentItems(def).ToList();
            var refs = items.SelectMany(RefNames).ToHashSet(StringComparer.Ordinal);

            Assert.True(Sorted(items.SelectMany(TypesOf)).SequenceEqual(Sorted(context.Types)), $"{context.Name} types");
            Assert.True(MinItems(def) == context.Min, $"{context.Name} minItems");
            Assert.True((MaxItems(def) ?? int.MaxValue) == context.Max, $"{context.Name} maxItems");
            Assert.Equal(refs.Contains("paragraph_with_alignment_node"), context.ParagraphMarks.Contains("alignment"));
            Assert.Equal(refs.Contains("paragraph_with_indentation_node"), context.ParagraphMarks.Contains("indentation"));
            Assert.Equal(refs.Contains("heading_with_alignment_node"), context.HeadingMarks.Contains("alignment"));
            Assert.Equal(refs.Contains("heading_with_indentation_node"), context.HeadingMarks.Contains("indentation"));
        }
    }

    [Fact]
    public void Text_marks_are_the_schemas_formatted_and_code_text_marks() {
        Assert.Equal(Sorted(MarkTypes("formatted_text_inline_node").Except(PreservedMarks)), Sorted(AdfSchema.TextMarks));
        Assert.Equal(Sorted(MarkTypes("code_inline_node").Except(PreservedMarks)), Sorted(AdfSchema.CodeTextMarks));
    }

    [Fact]
    public void Colours_follow_the_schema_pattern() {
        var pattern = Definitions.GetProperty("textColor_mark").GetProperty("properties").GetProperty("attrs")
            .GetProperty("properties").GetProperty("color").GetProperty("pattern").GetString();

        Assert.Equal("^#[0-9a-fA-F]{6}$", pattern);
        Assert.True(AdfSchema.IsColor("#0aF19c"));
        Assert.False(AdfSchema.IsColor("#fff"));
        Assert.False(AdfSchema.IsColor("#ffffffff"));
        Assert.False(AdfSchema.IsColor("red"));
    }

    [Theory]
    [InlineData("localId", "\"a1b2-c3_d4\"", true, true)]
    [InlineData("localId", "\"has space\"", false, false)]
    [InlineData("localId", "\"\"", false, false)]
    [InlineData("href", "\"https://ok.example/a\"", true, true)]
    [InlineData("href", "\"https://ok.example/a b\"", true, false)]
    [InlineData("href", "\"https://ok.example/\u202Eevil\"", true, false)]
    [InlineData("href", "\"javascript:alert(1)\"", false, false)]
    [InlineData("href", "\"//evil.example\"", false, false)]
    [InlineData("href", "\"mailto:a@b.example\"", true, true)]
    [InlineData("url", "\"mailto:a@b.example\"", false, false)]
    [InlineData("level", "3", true, true)]
    [InlineData("level", "3.0", false, false)]
    [InlineData("level", "-0", false, false)]
    [InlineData("level", "7", false, false)]
    [InlineData("level", "\"3\"", false, false)]
    [InlineData("timestamp", "\"1730505600000\"", true, true)]
    [InlineData("timestamp", "\"253402300800000\"", false, false)]
    [InlineData("timestamp", "\"01\"", false, false)]
    [InlineData("color", "\"#fF0099\"", true, true)]
    [InlineData("color", "\"url(x)\"", false, false)]
    [InlineData("statusColor", "\"#ff0099\"", true, false)]
    [InlineData("statusColor", "\"green\"", true, true)]
    [InlineData("width", "1e309", false, false)]
    [InlineData("width", "50.5", true, true)]
    public void Value_rules_read_leniently_and_write_strictly(string rule, string json, bool read, bool write) {
        var attr = rule switch {
            "localId" => AdfSchema.Node("paragraph")!.Attr("localId")!,
            "href" => AdfSchema.Mark("link")!.Attrs[0],
            "url" => AdfSchema.Node("inlineCard")!.Attr("url")!,
            "level" => AdfSchema.Node("heading")!.Attr("level")!,
            "timestamp" => AdfSchema.Node("date")!.Attr("timestamp")!,
            "color" => AdfSchema.Mark("textColor")!.Attrs[0],
            "statusColor" => AdfSchema.Node("status")!.Attr("color")!,
            _ => AdfSchema.Node("layoutColumn")!.Attr("width")!,
        };
        using var value = JsonDocument.Parse(json);

        Assert.Equal(read, AdfSchema.Valid(attr, value.RootElement, write: false));
        Assert.Equal(write, AdfSchema.Valid(attr, value.RootElement, write: true));
    }

    [Fact]
    public void Text_refuses_controls_and_keeps_line_breaks_only_in_code() {
        Assert.True(AdfSchema.ValidText("tab\there", codeBlock: false));
        Assert.False(AdfSchema.ValidText("line\nbreak", codeBlock: false));
        Assert.True(AdfSchema.ValidText("line\r\nbreak", codeBlock: true));
        Assert.False(AdfSchema.ValidText("nul\u0000", codeBlock: true));
        Assert.False(AdfSchema.ValidText("c1\u0085", codeBlock: false));
        Assert.False(AdfSchema.ValidText("", codeBlock: false));
        using var lone = JsonDocument.Parse("\"\\ud800x\"");
        Assert.False(AdfSchema.TryString(lone.RootElement, out _));
    }

    private static void CheckAttr(string owner, AdfAttrRule attr, List<AttrVariant> variants) {
        var schemas = variants.Where(v => v.Properties.ContainsKey(attr.Name)).Select(v => v.Properties[attr.Name]).ToList();
        Assert.True(schemas.Count > 0, $"{owner}.{attr.Name} is not in the schema");
        // Variants (media's file and external forms, mediaSingle's percent and pixel widths) widen each other.
        // An enum here may narrow a free string in the schema (status style).
        if (attr.Type == AdfAttrType.Enum && schemas.Any(s => s.TryGetProperty("enum", out _))) {
            var allowed = schemas.SelectMany(s => s.TryGetProperty("enum", out var values) ? values.EnumerateArray().Select(v => v.GetString()!) : [])
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(attr.Values.All(allowed.Contains), $"{owner}.{attr.Name}: {string.Join(',', attr.Values)} not within {string.Join(',', allowed)}");
        }
        var minimum = schemas.Select(s => s.TryGetProperty("minimum", out var min) ? min.GetDouble() : double.MinValue).Min();
        var maximum = schemas.Select(s => s.TryGetProperty("maximum", out var max) ? max.GetDouble() : double.MaxValue).Max();
        Assert.True(attr.Min >= minimum && attr.Max <= maximum, $"{owner}.{attr.Name} range {attr.Min}..{attr.Max} outside {minimum}..{maximum}");
        foreach (var schema in schemas) {
            if (attr.Type is AdfAttrType.Integer or AdfAttrType.Number && schema.TryGetProperty("type", out var type)) {
                Assert.Equal("number", type.GetString());
            }
            if (attr.Type == AdfAttrType.Boolean) {
                Assert.Equal("boolean", schema.GetProperty("type").GetString());
            }
            if (attr.Type == AdfAttrType.String && schema.TryGetProperty("type", out var stringType)) {
                Assert.Equal("string", stringType.GetString());
            }
            if (attr.Type == AdfAttrType.String && schema.TryGetProperty("pattern", out var pattern)) {
                Assert.True(attr.Check is AdfCheck.Color or AdfCheck.StatusColor, $"{owner}.{attr.Name} has a schema pattern {pattern}");
            }
        }
    }

    private sealed record AttrVariant(Dictionary<string, JsonElement> Properties, HashSet<string> Required);

    private static IEnumerable<AttrVariant> AttrVariants(IEnumerable<JsonElement> defs) {
        foreach (var def in defs) {
            foreach (var attrs in Members(def, "attrs")) {
                foreach (var variant in Flatten(attrs)) {
                    if (!variant.TryGetProperty("properties", out var properties)) {
                        continue;
                    }
                    yield return new AttrVariant(
                        properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal),
                        Required(variant));
                }
            }
        }
    }

    // The definitions whose `type` is this node type (paragraph has several: with alignment, with no marks…).
    private static IEnumerable<JsonElement> NodeDefs(string type) =>
        Defs().Where(def => def.Name.EndsWith("_node", StringComparison.Ordinal) && OwnType(def.Value) == type).Select(def => def.Value);

    private static string? OwnType(JsonElement def) =>
        def.TryGetProperty("properties", out var properties) && properties.TryGetProperty("type", out var type) && type.TryGetProperty("enum", out var values)
            ? values[0].GetString()
            : def.TryGetProperty("allOf", out var allOf) ? OwnType(Resolve(allOf[0])) : null;

    private static IEnumerable<JsonElement> ContentItems(JsonElement def) {
        if (def.TryGetProperty("anyOf", out _) && !def.TryGetProperty("properties", out _)) {
            return [def];
        }
        if (def.TryGetProperty("type", out var type) && type.GetString() == "array") {
            return Items(def);
        }
        // allOf: the last member that names content wins (mediaSingle_caption narrows mediaSingle's content).
        var content = Members(def, "content").LastOrDefault();
        return content.ValueKind == JsonValueKind.Undefined ? [] : Items(Resolve(content));
    }

    private static IEnumerable<JsonElement> Items(JsonElement array) {
        if (!array.TryGetProperty("items", out var items)) {
            return [];
        }
        return items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToList() : [items];
    }

    private static int MinItems(JsonElement def) {
        var source = def.TryGetProperty("type", out var type) && type.GetString() == "array" ? def : Members(def, "content").Select(Resolve).LastOrDefault();
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty("minItems", out var min) ? min.GetInt32() : 0;
    }

    private static int? MaxItems(JsonElement def) {
        var source = def.TryGetProperty("type", out var type) && type.GetString() == "array" ? def : Members(def, "content").Select(Resolve).LastOrDefault();
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty("maxItems", out var max) ? max.GetInt32() : null;
    }

    // A member of an object definition, looking through allOf.
    private static IEnumerable<JsonElement> Members(JsonElement def, string name) {
        if (def.TryGetProperty("properties", out var properties) && properties.TryGetProperty(name, out var member)) {
            yield return member;
        }
        if (def.TryGetProperty("allOf", out var allOf)) {
            foreach (var part in allOf.EnumerateArray()) {
                foreach (var found in Members(Resolve(part), name)) {
                    yield return found;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement schema) {
        schema = Resolve(schema);
        if (schema.TryGetProperty("anyOf", out var anyOf)) {
            return anyOf.EnumerateArray().SelectMany(Flatten);
        }
        return [schema];
    }

    private static IEnumerable<string> TypesOf(JsonElement schema) {
        schema = Resolve(schema);
        if (schema.TryGetProperty("anyOf", out var anyOf)) {
            return anyOf.EnumerateArray().SelectMany(TypesOf);
        }
        return OwnType(schema) is { } type ? [type] : [];
    }

    private static IEnumerable<string> RefNames(JsonElement schema) {
        if (schema.TryGetProperty("$ref", out var reference)) {
            var name = reference.GetString()!["#/definitions/".Length..];
            var def = Definitions.GetProperty(name);
            return def.TryGetProperty("anyOf", out _) && !def.TryGetProperty("properties", out _) ? RefNames(def) : [name];
        }
        return schema.TryGetProperty("anyOf", out var anyOf) ? anyOf.EnumerateArray().SelectMany(RefNames) : [];
    }

    private static IEnumerable<string> MarkTypes(string defName) =>
        Members(Definitions.GetProperty(defName), "marks").SelectMany(Items).SelectMany(Flatten).Select(OwnType).OfType<string>();

    private static JsonElement Resolve(JsonElement schema) {
        while (schema.TryGetProperty("$ref", out var reference)) {
            schema = Definitions.GetProperty(reference.GetString()!["#/definitions/".Length..]);
        }
        return schema;
    }

    private static HashSet<string> Required(JsonElement def) =>
        def.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal)
            : Members(def, "content").Any() && def.TryGetProperty("allOf", out var allOf)
                ? allOf.EnumerateArray().SelectMany(part => Required(Resolve(part))).ToHashSet(StringComparer.Ordinal)
                : [];

    private static IEnumerable<(string Name, JsonElement Value)> Defs() =>
        Definitions.EnumerateObject().Select(def => (def.Name, def.Value));

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Distinct().Order(StringComparer.Ordinal)];

    private static JsonElement LoadDefinitions() {
        var path = Path.Combine(RepoRoot.Path, "tests", "fixtures", "adf-schema", Version, "full.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("definitions").Clone();
    }
}
