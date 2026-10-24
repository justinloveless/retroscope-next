using System.Text.Json;
using System.Text.RegularExpressions;
using Retroscope.Core.Adf;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// The web editor's copy of the allow-list (<c>web/src/rich/adf-schema.json</c>, UX11 decision 5) checked field for
/// field against <see cref="AdfSchema"/>: nodes, marks, attributes and their rules, content contexts with their
/// counts and tokens, block marks by parent, and the code mark's companions. A change to either side that the other
/// does not follow fails here, naming the field.
/// </summary>
[Trait("Category", "Adf")]
public sealed class AdfSchemaWebMirrorTests {
    private const string WebFile = "web/src/rich/adf-schema.json";

    private static readonly string[] Tokens = [AdfSchema.OpaqueBlock, AdfSchema.OpaqueInline];

    private static readonly JsonElement Web = Load();

    [Fact]
    public void Nodes_are_the_editable_ones_plus_the_tokens() {
        var expected = AdfSchema.Nodes.Values.Where(node => node.Editable).Select(node => node.Type).Concat(Tokens);

        Assert.Equal(Sorted(expected), Sorted(WebNodes().Select(node => node.Name)));
    }

    [Fact]
    public void Marks_are_the_servers_plus_the_mark_token() {
        Assert.Equal(Sorted(AdfSchema.Marks.Keys.Append(AdfSchema.OpaqueMark)), Sorted(Web.GetProperty("marks").EnumerateObject().Select(mark => mark.Name)));
        foreach (var (type, spec) in AdfSchema.Marks) {
            var web = Web.GetProperty("marks").GetProperty(type);
            Assert.True(spec.Block == Flag(web, "block"), $"marks.{type}.block");
        }
    }

    [Fact]
    public void Node_attributes_match_name_for_name_and_rule_for_rule() {
        foreach (var spec in AdfSchema.Nodes.Values.Where(node => node.Editable && node.Type != "doc")) {
            CheckAttrs($"nodes.{spec.Type}", spec.Attrs, Web.GetProperty("nodes").GetProperty(spec.Type).GetProperty("attrs"));
        }
    }

    [Fact]
    public void Mark_attributes_match_name_for_name_and_rule_for_rule() {
        foreach (var spec in AdfSchema.Marks.Values) {
            CheckAttrs($"marks.{spec.Type}", spec.Attrs, Web.GetProperty("marks").GetProperty(spec.Type).GetProperty("attrs"));
        }
    }

    [Fact]
    public void Each_node_holds_its_contexts_types_with_tokens_where_the_context_takes_them() {
        foreach (var spec in AdfSchema.Nodes.Values.Where(node => node.Editable)) {
            var web = Web.GetProperty("nodes").GetProperty(spec.Type);
            var children = Children(web);
            if (spec.Content is null) {
                Assert.True(children.Count == 0, $"nodes.{spec.Type} is a leaf on the server but has children here");
                continue;
            }
            var context = AdfSchema.Contexts[spec.Content];
            var expected = context.Types.Where(type => AdfSchema.Nodes[type].Editable).ToList();
            if (context.OpaqueChildren) {
                expected.Add(context.Inline ? AdfSchema.OpaqueInline : AdfSchema.OpaqueBlock);
            }
            Assert.True(Sorted(expected).SequenceEqual(Sorted(children)), $"nodes.{spec.Type} ({spec.Content}): web {string.Join(',', Sorted(children))}");
            Assert.True(context.Min == Int(web, "min", 0), $"nodes.{spec.Type}.min");
            Assert.True(context.Max == Int(web, "max", int.MaxValue), $"nodes.{spec.Type}.max");
        }
    }

    [Fact]
    public void Leaf_nodes_carry_the_servers_inline_flag_and_marks() {
        foreach (var spec in AdfSchema.Nodes.Values.Where(node => node.Editable && node.Type != "text")) {
            var web = Web.GetProperty("nodes").GetProperty(spec.Type);
            Assert.True(spec.Inline == Flag(web, "inline"), $"nodes.{spec.Type}.inline");
            var marks = web.TryGetProperty("marks", out var list) ? list.EnumerateArray().Select(m => m.GetString()!) : [];
            Assert.True(Sorted(spec.Marks).SequenceEqual(Sorted(marks)), $"nodes.{spec.Type}.marks");
        }
    }

    [Fact]
    public void Block_marks_by_parent_are_the_contexts_paragraph_and_heading_marks() {
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in AdfSchema.Nodes.Values.Where(node => node.Editable && node.Content is not null)) {
            var context = AdfSchema.Contexts[spec.Content!];
            foreach (var (child, marks) in new[] { ("paragraph", context.ParagraphMarks), ("heading", context.HeadingMarks) }) {
                if (marks.Count > 0) {
                    expected[$"{spec.Type}.{child}"] = string.Join(',', Sorted(marks));
                }
            }
        }
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var parent in Web.GetProperty("contextMarks").EnumerateObject()) {
            foreach (var child in parent.Value.EnumerateObject()) {
                actual[$"{parent.Name}.{child.Name}"] = string.Join(',', Sorted(child.Value.EnumerateArray().Select(m => m.GetString()!)));
            }
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Code_combines_with_the_code_text_marks_and_the_mark_token() {
        var web = Web.GetProperty("codeCombinesWith").EnumerateArray().Select(m => m.GetString()!);

        Assert.Equal(Sorted(AdfSchema.CodeTextMarks.Where(m => m != "code").Append(AdfSchema.OpaqueMark)), Sorted(web));
    }

    [Fact]
    public void Text_marks_are_the_plain_marks_less_code() {
        var web = Web.GetProperty("marks").EnumerateObject()
            .Where(m => !Flag(m.Value, "block") && !Flag(m.Value, "token") && m.Name != "code")
            .Select(m => m.Name);

        Assert.Equal(Sorted(AdfSchema.TextMarks), Sorted(web));
    }

    [Fact]
    public void Token_names_and_refs_are_the_servers() {
        Assert.Equal(AdfSchema.Keep, Web.GetProperty("stash").GetString());
        var island = AdfLossiness.IslandId(JsonDocument.Parse("""{"type":"extension","attrs":{"extensionKey":"x"}}""").RootElement);
        foreach (var (owner, name) in new[] { ("nodes", AdfSchema.OpaqueBlock), ("nodes", AdfSchema.OpaqueInline), ("marks", AdfSchema.OpaqueMark) }) {
            var token = Web.GetProperty(owner).GetProperty(name);
            Assert.True(Flag(token, "token"), $"{owner}.{name}.token");
            var attrs = token.GetProperty("attrs").EnumerateObject().ToList();
            Assert.Equal([AdfSchema.Ref], attrs.Select(a => a.Name));
            var pattern = new Regex($"^(?:{attrs[0].Value.GetProperty("pattern").GetString()})\\z", RegexOptions.CultureInvariant);
            Assert.Matches(pattern, island);
        }
    }

    private static void CheckAttrs(string owner, IReadOnlyList<AdfAttrRule> rules, JsonElement web) {
        var kept = rules.Where(rule => rule.Type != AdfAttrType.Refused).ToList();
        Assert.True(
            Sorted(kept.Select(rule => rule.Name)).SequenceEqual(Sorted(web.EnumerateObject().Select(a => a.Name))),
            $"{owner}.attrs: web {string.Join(',', Sorted(web.EnumerateObject().Select(a => a.Name)))}");
        foreach (var rule in kept) {
            CheckRule($"{owner}.{rule.Name}", rule, ResolveRule(web.GetProperty(rule.Name)));
        }
    }

    private static void CheckRule(string at, AdfAttrRule rule, JsonElement web) {
        var type = web.GetProperty("type").GetString();
        Assert.True(rule.Required == Flag(web, "required"), $"{at}.required");
        switch (rule.Type) {
            case AdfAttrType.Boolean:
                Assert.True(type == "bool", $"{at}: bool on the server, {type} here");
                break;
            case AdfAttrType.Integer:
                Assert.True(type == "int", $"{at}: int on the server, {type} here");
                CheckRange(at, rule, web);
                break;
            case AdfAttrType.Number:
                Assert.True(type == "number", $"{at}: number on the server, {type} here");
                CheckRange(at, rule, web);
                break;
            case AdfAttrType.NumberList:
                Assert.True(type == "numberList", $"{at}: numberList on the server, {type} here");
                CheckRange(at, rule, web);
                break;
            case AdfAttrType.Enum:
                Assert.True(type == "enum" && !Flag(web, "orColor"), $"{at}: enum on the server, {type} here");
                Assert.True(rule.Values.SequenceEqual(web.GetProperty("values").EnumerateArray().Select(v => v.GetString()!)), $"{at}.values");
                break;
            case AdfAttrType.String:
                CheckString(at, rule, web, type);
                break;
            default:
                Assert.Fail($"{at}: {rule.Type} has no web form");
                break;
        }
    }

    private static void CheckString(string at, AdfAttrRule rule, JsonElement web, string? type) {
        switch (rule.Check) {
            case AdfCheck.Color:
                Assert.True(type == "color", $"{at}: colour on the server, {type} here");
                return;
            case AdfCheck.StatusColor:
                Assert.True(type == "enum" && Flag(web, "orColor"), $"{at}: status colour on the server, {type} here");
                Assert.True(rule.Values.SequenceEqual(web.GetProperty("values").EnumerateArray().Select(v => v.GetString()!)), $"{at}.values");
                return;
            case AdfCheck.Href:
                Assert.True(type == "href" && !web.TryGetProperty("schemes", out _), $"{at}: link href on the server, {type} here");
                return;
            case AdfCheck.CardUrl:
                Assert.True(type == "href", $"{at}: card url on the server, {type} here");
                Assert.Equal(["http", "https"], web.GetProperty("schemes").EnumerateArray().Select(s => s.GetString()!));
                return;
            case AdfCheck.Timestamp:
                Assert.True(type == "timestamp", $"{at}: timestamp on the server, {type} here");
                return;
        }
        Assert.True(type == "string", $"{at}: string on the server, {type} here");
        // A plain or patterned string: the web rule must accept exactly what the server writes, over a corpus at
        // every length bound and every printable ASCII character.
        var pattern = web.TryGetProperty("pattern", out var p) ? new Regex($"^(?:{p.GetString()})\\z", RegexOptions.CultureInvariant) : null;
        var min = Int(web, "min", 0);
        var max = Int(web, "max", int.MaxValue);
        foreach (var sample in Corpus()) {
            var length = AdfSchema.CodePoints(sample);
            var webAccepts = length >= min && length <= max && !sample.Any(char.IsControl) && (pattern is null || pattern.IsMatch(sample));
            var serverAccepts = AdfSchema.ValidString(rule, sample, write: true);
            Assert.True(webAccepts == serverAccepts, $"{at}: {JsonSerializer.Serialize(sample)} is {(serverAccepts ? "accepted" : "refused")} by the server");
        }
    }

    private static void CheckRange(string at, AdfAttrRule rule, JsonElement web) {
        Assert.True(rule.Min == web.GetProperty("min").GetDouble(), $"{at}.min");
        Assert.True(rule.Max == web.GetProperty("max").GetDouble(), $"{at}.max");
    }

    private static IEnumerable<string> Corpus() {
        int[] bounds = [0, 1, 2, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 66, 67, 99, 100, 101, 127, 128, 129, 254, 255, 256, 511, 512, 513, 1023, 1024, 1025, 2048, 2049, 4095, 4096, 4097];
        foreach (var n in bounds) {
            yield return new string('a', n);
            yield return n >= 2 ? ":" + new string('a', n - 2) + ":" : new string(':', n);
            yield return string.Concat(Enumerable.Repeat("\U0001F600", n));
        }
        for (var c = (char)0; c < 0x80; c++) {
            yield return $"a{c}b";
            yield return $":a{c}b:";
            yield return c.ToString();
        }
        yield return "\u00E9";
        yield return "a\u0085b";
        yield return "a\u200Bb";
    }

    private static List<string> Children(JsonElement node) {
        var list = new List<string>();
        foreach (var member in new[] { "first", "children" }) {
            if (node.TryGetProperty(member, out var items)) {
                list.AddRange(items.EnumerateArray().Select(i => i.GetString()!));
            }
        }
        return Expand(list);
    }

    private static List<string> Expand(IEnumerable<string> list) =>
        [.. list.SelectMany(item => item.StartsWith('@')
            ? Expand(Web.GetProperty("groups").GetProperty(item[1..]).EnumerateArray().Select(i => i.GetString()!))
            : [item]).Distinct()];

    private static JsonElement ResolveRule(JsonElement rule) =>
        rule.ValueKind == JsonValueKind.String ? Web.GetProperty("rules").GetProperty(rule.GetString()!) : rule;

    private static IEnumerable<(string Name, JsonElement Value)> WebNodes() =>
        Web.GetProperty("nodes").EnumerateObject().Select(node => (node.Name, node.Value));

    private static bool Flag(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement owner, string name, int fallback) =>
        owner.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Distinct().Order(StringComparer.Ordinal)];

    private static JsonElement Load() {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot.Path, WebFile)));
        return document.RootElement.Clone();
    }
}
