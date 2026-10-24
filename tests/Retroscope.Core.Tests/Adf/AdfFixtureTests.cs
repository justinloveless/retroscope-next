using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Markup;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed class AdfFixtureTests {
    private static readonly string[] NodeTypes = [
        "doc", "paragraph", "heading", "bulletList", "orderedList", "listItem", "codeBlock", "blockquote", "rule",
        "panel", "table", "tableRow", "tableHeader", "tableCell", "expand", "nestedExpand", "taskList", "taskItem",
        "blockTaskItem", "decisionList", "decisionItem", "mediaSingle", "mediaGroup", "media", "mediaInline", "caption",
        "blockCard", "embedCard", "layoutSection", "layoutColumn", "extension", "bodiedExtension", "inlineExtension",
        "text", "hardBreak", "mention", "emoji", "date", "status", "inlineCard", "placeholder",
    ];

    private static readonly string[] MarkTypes = [
        "strong", "em", "strike", "underline", "code", "link", "textColor", "backgroundColor", "subsup", "alignment", "indentation",
    ];

    public static TheoryData<string, string> Cases() {
        var data = new TheoryData<string, string>();
        foreach (var (file, testCase) in AdfFixtures.All()) {
            data.Add(file, AdfFixtures.Name(testCase));
        }
        return data;
    }

    public static TheoryData<string, string> DescriptionCases() {
        var data = new TheoryData<string, string>();
        foreach (var (file, testCase) in AdfFixtures.All().Where(entry => AdfFixtures.IsDescription(entry.Case))) {
            data.Add(file, AdfFixtures.Name(testCase));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Converts_to_the_expected_doc(string file, string name) {
        var testCase = AdfFixtures.Find(file, name);
        var adf = testCase.GetProperty("adf");
        var attachments = AdfFixtures.Attachments(testCase);

        if (!AdfFixtures.IsDescription(testCase)) {
            AdfFixtures.AssertDoc(testCase.GetProperty("doc"), AdfToDoc.Render(adf, attachments));
            return;
        }
        var description = AdfToDoc.Description(adf, attachments);

        AdfFixtures.AssertDoc(testCase.GetProperty("doc"), description.Doc);
        var expectedIslands = testCase.TryGetProperty("islands", out var islands)
            ? islands.EnumerateArray().Select(id => id.GetString()!).ToList()
            : [];
        Assert.Equal(expectedIslands, description.Islands.Keys.ToList());
        Assert.Equal(!testCase.TryGetProperty("editable", out var editable) || editable.GetBoolean(), description.Editable);
        if (testCase.TryGetProperty("markup", out var markup)) {
            Assert.Equal(markup.GetString(), MarkupWriter.Write(description.Doc));
        }
    }

    // The editor's save path: Doc → markup → Doc → ADF. Islands come back byte for byte and every other block
    // carries the same content as the original.
    [Theory]
    [MemberData(nameof(DescriptionCases))]
    public void Description_writes_back_without_loss(string file, string name) {
        var original = AdfFixtures.Find(file, name).GetProperty("adf");
        var description = AdfToDoc.Description(original, AdfFixtures.Attachments(AdfFixtures.Find(file, name)));
        if (!description.Editable) {
            return;
        }
        var markup = MarkupWriter.Write(description.Doc);
        var edited = MarkupParser.Parse(markup, MarkupDialect.JiraDescription);
        Assert.Equal(markup, MarkupWriter.Write(edited));

        Assert.True(MarkupToAdf.TryWrite(edited, description.Islands, out var adf, out var missing), missing);

        using var written = JsonDocument.Parse(adf);
        Assert.Equal(Content(original, description), Content(written.RootElement, description));
    }

    // UX8: the display-only kinds and attributes reach the client only inside islands, so the editor and
    // MarkupWriter never see them.
    [Theory]
    [MemberData(nameof(DescriptionCases))]
    public void Blocks_outside_islands_hold_only_markup_kinds(string file, string name) {
        var testCase = AdfFixtures.Find(file, name);
        var description = AdfToDoc.Description(testCase.GetProperty("adf"), AdfFixtures.Attachments(testCase));
        if (!description.Editable) {
            return;
        }
        var editable = new Doc(description.Doc.Blocks.Where(block => block is not DocIsland).ToList());

        Assert.Null(DocKinds.DisallowedIn(editable, MarkupDialect.JiraDescription));
    }

    [Fact]
    public void Fixtures_cover_every_node_type_and_mark() {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, testCase) in AdfFixtures.All()) {
            Collect(testCase.GetProperty("adf"), nodes, marks);
        }

        Assert.Empty(NodeTypes.Except(nodes));
        Assert.Empty(MarkTypes.Except(marks));
    }

    // Islands as their raw text, editable blocks as their visible content.
    private static List<string> Content(JsonElement doc, AdfDescription description) {
        var islands = description.Islands.Values.ToHashSet(StringComparer.Ordinal);
        var content = new List<string>();
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("content", out var blocks)) {
            return content;
        }
        foreach (var block in blocks.EnumerateArray()) {
            var raw = block.GetRawText();
            if (description.Islands.ContainsKey(AdfLossiness.IslandId(block)) || islands.Contains(raw)) {
                content.Add("island " + raw);
            } else if (AdfLossiness.Project([block]) is var projected && projected != "[]") {
                content.Add(projected);
            }
        }
        return content;
    }

    private static void Collect(JsonElement element, HashSet<string> nodes, HashSet<string> marks) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject()) {
                    if (member.Name == "marks" && member.Value.ValueKind == JsonValueKind.Array) {
                        foreach (var mark in member.Value.EnumerateArray()) {
                            if (mark.ValueKind == JsonValueKind.Object && mark.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) {
                                marks.Add(type.GetString()!);
                            }
                        }
                    } else if (member.Name == "type" && member.Value.ValueKind == JsonValueKind.String) {
                        nodes.Add(member.Value.GetString()!);
                    } else {
                        Collect(member.Value, nodes, marks);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) {
                    Collect(item, nodes, marks);
                }
                break;
        }
    }
}
