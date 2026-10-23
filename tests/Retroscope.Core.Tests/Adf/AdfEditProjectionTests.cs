using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Retroscope.Core.Adf;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed class AdfEditProjectionTests {
    private const string Hello = """{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}""";

    [Fact]
    public void A_document_the_allow_list_covers_passes_through_unchanged() {
        var adf = Doc(
            """{"type":"heading","attrs":{"level":2,"localId":"h1"},"marks":[{"type":"alignment","attrs":{"align":"center"}}],"content":[{"type":"text","text":"Title"}]}""",
            """{"type":"paragraph","content":[{"type":"text","text":"bold ","marks":[{"type":"strong"},{"type":"textColor","attrs":{"color":"#FF5630"}}]},{"type":"text","text":"link","marks":[{"type":"link","attrs":{"href":"https://example.com/a","title":"A"}}]},{"type":"hardBreak"},{"type":"mention","attrs":{"id":"5b10ac8d82e05b22cc7d4ef5","text":"@Ada","accessLevel":""}},{"type":"emoji","attrs":{"shortName":":rocket:","id":"1f680","text":"🚀"}},{"type":"date","attrs":{"timestamp":"1761955200000"}},{"type":"status","attrs":{"text":"OPEN","color":"blue","localId":"s1"}},{"type":"inlineCard","attrs":{"url":"https://acme.atlassian.net/browse/RS-1"}}]}""",
            """{"type":"panel","attrs":{"panelType":"custom","panelColor":"#DEEBFF","panelIconText":"🙂"},"content":[{"type":"paragraph","content":[]}]}""",
            """{"type":"table","attrs":{"isNumberColumnEnabled":true,"layout":"center","localId":"t1"},"content":[{"type":"tableRow","content":[{"type":"tableHeader","attrs":{"colwidth":[120,80],"colspan":2,"background":"#F4F5F7"},"content":[{"type":"paragraph","content":[{"type":"text","text":"H"}]}]}]},{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]},{"type":"tableCell","content":[{"type":"nestedExpand","attrs":{"title":"More"},"content":[{"type":"paragraph"}]}]}]}]}""",
            """{"type":"layoutSection","content":[{"type":"layoutColumn","attrs":{"width":50},"content":[{"type":"taskList","attrs":{"localId":"tl"},"content":[{"type":"taskItem","attrs":{"localId":"t-1","state":"DONE"},"content":[{"type":"text","text":"done"}]},{"type":"taskList","attrs":{"localId":"tl2"},"content":[{"type":"taskItem","attrs":{"localId":"t-2","state":"TODO"}}]}]}]},{"type":"layoutColumn","attrs":{"width":50},"content":[{"type":"decisionList","attrs":{"localId":"dl"},"content":[{"type":"decisionItem","attrs":{"localId":"d-1","state":"DECIDED"},"content":[{"type":"text","text":"yes"}]}]}]}]}""",
            """{"type":"expand","attrs":{"title":"Details"},"content":[{"type":"codeBlock","attrs":{"language":"csharp"},"content":[{"type":"text","text":"var x = 1;\nvar y = 2;"}]}]}""",
            """{"type":"mediaSingle","attrs":{"layout":"center","width":640,"widthType":"pixel"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"","alt":"shot.png","width":1280,"height":720}},{"type":"caption","content":[{"type":"text","text":"Shot"}]}]}""",
            """{"type":"orderedList","attrs":{"order":3},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"code"},{"type":"link","attrs":{"href":"mailto:a@b.example"}}]}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"}]}]}]}]}""",
            """{"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"q","marks":[{"type":"subsup","attrs":{"type":"sup"}},{"type":"backgroundColor","attrs":{"color":"#F8E6A0"}},{"type":"underline"},{"type":"strike"},{"type":"em"}]}]}]}""",
            """{"type":"rule"}""",
            """{"type":"blockCard","attrs":{"url":"https://example.com/card"}}""",
            """{"type":"embedCard","attrs":{"url":"https://example.com/embed","layout":"center","width":80}}""",
            """{"type":"mediaGroup","content":[{"type":"media","attrs":{"type":"file","id":"m-2","collection":""}}]}""");

        var view = Project(adf);

        Assert.True(view.Editable);
        AssertCanonical(adf, view.Adf);
        Assert.Empty(view.Opaque);
        Assert.Empty(view.Marks);
        Assert.DoesNotContain("rsKeep", view.Adf, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_and_preserve_only_nodes_become_tokens_whose_ref_is_the_island_id() {
        const string Extension = """{"type":"extension","attrs":{"extensionKey":"toc","extensionType":"com.atlassian.macro","text":"Table of contents"}}""";
        const string Inline = """{"type":"inlineExtension","attrs":{"extensionKey":"x","extensionType":"y"}}""";
        const string Mystery = """{"type":"sparkle","content":[]}""";
        var view = Project(Doc(Extension, $$"""{"type":"paragraph","content":[{"type":"text","text":"a"},{{Inline}}]}""", Mystery, Extension));
        var content = Content(view);

        AssertNode(Token("rsOpaque", Id(Extension)), content[0]);
        AssertNode(Token("rsOpaqueInline", Id(Inline)), content[1]!["content"]![1]);
        AssertNode(Token("rsOpaque", Id(Mystery)), content[2]);
        AssertNode(Token("rsOpaque", Id(Extension)), content[3]);
        Assert.Equal([Id(Extension), Id(Inline), Id(Mystery)], view.Opaque.Select(part => part.Ref));
        Assert.Equal(("extension", false, Extension), (view.Opaque[0].Kind, view.Opaque[0].Inline, view.Opaque[0].Raw));
        Assert.Equal(("inlineExtension", true), (view.Opaque[1].Kind, view.Opaque[1].Inline));
        Assert.Equal(16, view.Opaque[0].Ref.Length);
    }

    [Fact]
    public void Token_ids_are_content_hashes_independent_of_formatting() {
        var compact = Project(Doc("""{"type":"extension","attrs":{"extensionKey":"a","extensionType":"b"}}"""));
        var spaced = Project(Doc("""{ "attrs" : { "extensionType":"b", "extensionKey":"a" }, "type" : "extension" }"""));

        Assert.Equal(compact.Opaque[0].Ref, spaced.Opaque[0].Ref);
        Assert.NotEqual(compact.Opaque[0].Raw, spaced.Opaque[0].Raw);
    }

    [Fact]
    public void Unknown_and_invalid_marks_become_rsMark_tokens_that_travel_with_their_text() {
        const string Annotation = """{"type":"annotation","attrs":{"id":"c1","annotationType":"inlineComment"}}""";
        const string BadLink = """{"type":"link","attrs":{"href":"javascript:alert(1)"}}""";
        const string MediaLink = """{"type":"link","attrs":{"href":"https://ok.example","collection":"c"}}""";
        var view = Project(Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"strong"},{{Annotation}},{{BadLink}},{{MediaLink}}]}]}"""));
        var marks = Content(view)[0]!["content"]![0]!["marks"]!.AsArray();

        AssertNode(JsonNode.Parse("""{"type":"strong"}""")!, marks[0]);
        AssertNode(Token("rsMark", Id(Annotation)), marks[1]);
        AssertNode(Token("rsMark", Id(BadLink)), marks[2]);
        AssertNode(Token("rsMark", Id(MediaLink)), marks[3]);
        Assert.Equal(["annotation", "link", "link"], view.Marks.Select(mark => mark.Kind));
    }

    [Fact]
    public void Code_text_keeps_only_link_and_a_second_block_mark_is_kept_opaque() {
        var view = Project(Doc(
            """{"type":"paragraph","marks":[{"type":"alignment","attrs":{"align":"end"}},{"type":"indentation","attrs":{"level":2}}],"content":[{"type":"text","text":"x","marks":[{"type":"code"},{"type":"strong"},{"type":"link","attrs":{"href":"https://ok.example"}}]}]}"""));
        var paragraph = Content(view)[0]!;

        Assert.Equal("alignment", (string)paragraph["marks"]![0]!["type"]!);
        Assert.Equal("rsMark", (string)paragraph["marks"]![1]!["type"]!);
        Assert.Equal(["code", "rsMark", "link"], paragraph["content"]![0]!["marks"]!.AsArray().Select(m => (string)m!["type"]!));
    }

    [Fact]
    public void Unknown_attributes_and_invalid_optional_values_move_into_one_stash() {
        var view = Project(Doc(
            """{"type":"codeBlock","attrs":{"language":"Protocol Buffers","uniqueId":"u1","futureAttr":{"x":1}},"content":[{"type":"text","text":"x"}]}""",
            """{"type":"panel","attrs":{"panelType":"info","panelColor":"#DEEBFF"},"content":[{"type":"paragraph"}]}"""));
        var code = Content(view)[0]!["attrs"]!.AsObject();
        var panel = Content(view)[1]!["attrs"]!.AsObject();

        Assert.Equal(["uniqueId", "rsKeep"], code.Select(attr => attr.Key));
        Assert.Equal(["panelType", "rsKeep"], panel.Select(attr => attr.Key));
        var stash = view.Base.Keeps[(string)code["rsKeep"]!];
        Assert.Equal(["language", "futureAttr"], stash.Members.Select(member => member.Key));
        Assert.Equal(["\"Protocol Buffers\"", """{"x":1}"""], stash.Members.Select(member => member.Value));
        Assert.Equal(["codeBlock"], stash.NodeTypes);
        Assert.Equal(["panelColor"], view.Base.Keeps[(string)panel["rsKeep"]!].Members.Select(member => member.Key));
    }

    [Fact]
    public void Invalid_required_or_structural_values_make_the_node_opaque() {
        var view = Project(Doc(
            """{"type":"heading","attrs":{"level":7},"content":[]}""",
            """{"type":"paragraph","content":[{"type":"status","attrs":{"text":"","color":"blue"}},{"type":"inlineCard","attrs":{"url":"javascript:alert(1)"}},{"type":"inlineCard","attrs":{"data":{"name":"x"}}}]}""",
            """{"type":"blockCard","attrs":{"datasource":{"id":"d","parameters":{},"views":[{"type":"table"}]},"url":"https://ok.example"}}"""));
        var content = Content(view);

        Assert.Equal("rsOpaque", (string)content[0]!["type"]!);
        Assert.All(content[1]!["content"]!.AsArray(), node => Assert.Equal("rsOpaqueInline", (string)node!["type"]!));
        Assert.Equal("rsOpaque", (string)content[2]!["type"]!);
    }

    [Fact]
    public void A_child_that_cannot_stand_alone_takes_its_structural_parent_with_it() {
        var view = Project(Doc(
            """{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"}]},{"type":"extension","attrs":{"extensionKey":"a","extensionType":"b"}}]}""",
            """{"type":"codeBlock","content":[{"type":"text","text":"x","marks":[{"type":"strong"}]}]}""",
            """{"type":"panel","attrs":{"panelType":"info"},"content":[{"type":"paragraph"},{"type":"extension","attrs":{"extensionKey":"a","extensionType":"b"}}]}"""));
        var content = Content(view);

        Assert.Equal("rsOpaque", (string)content[0]!["type"]!);
        Assert.Equal("rsOpaque", (string)content[1]!["type"]!);
        Assert.Equal("panel", (string)content[2]!["type"]!);
        Assert.Equal("rsOpaque", (string)content[2]!["content"]![1]!["type"]!);
    }

    [Fact]
    public void Nodes_in_a_place_the_schema_does_not_allow_become_tokens_where_they_are() {
        var view = Project(Doc("""{"type":"blockquote","content":[{"type":"heading","attrs":{"level":1}}]}""", """{"type":"text","text":"loose"}"""));
        var content = Content(view);

        Assert.Equal("rsOpaque", (string)content[0]!["content"]![0]!["type"]!);
        Assert.Equal("rsOpaque", (string)content[1]!["type"]!);
        Assert.Equal(["blockquote"], view.Base.Opaque[(string)content[0]!["content"]![0]!["attrs"]!["ref"]!].Contexts);
    }

    [Fact]
    public void Text_with_a_line_break_or_control_outside_code_is_kept_opaque() {
        var view = Project(Doc("""{"type":"paragraph","content":[{"type":"text","text":"a\nb"},{"type":"text","text":"ok"},{"type":"text","text":"\u0007"}]}"""));
        var inline = Content(view)[0]!["content"]!.AsArray();

        Assert.Equal(["rsOpaqueInline", "text", "rsOpaqueInline"], inline.Select(node => (string)node!["type"]!));
    }

    [Fact]
    public void Ragged_tables_stay_editable_only_with_a_unique_localId() {
        const string Ragged = """{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]},{"type":"tableCell","content":[{"type":"paragraph"}]}]},{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]}]}""";
        var view = Project(Doc(
            $$"""{"type":"table","attrs":{"localId":"keep"},"content":[{{Ragged}}]}""",
            $$"""{"type":"table","content":[{{Ragged}}]}""",
            $$"""{"type":"table","attrs":{"localId":"twice"},"content":[{{Ragged}}]}""",
            $$"""{"type":"table","attrs":{"localId":"twice"},"content":[{{Ragged}}]}""",
            """{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"colspan":2,"colwidth":[100]},"content":[{"type":"paragraph"}]}]}]}"""));

        Assert.Equal(["table", "rsOpaque", "rsOpaque", "rsOpaque", "rsOpaque"], Content(view).Select(node => (string)node!["type"]!));
        Assert.Equal(["keep"], view.Base.IrregularTables);
    }

    [Fact]
    public void A_repeated_task_localId_keeps_only_its_first_item_editable() {
        var view = Project(Doc("""{"type":"taskList","attrs":{"localId":"l"},"content":[{"type":"taskItem","attrs":{"localId":"same","state":"TODO"}},{"type":"taskItem","attrs":{"localId":"same","state":"DONE"}}]}"""));

        Assert.Equal(["taskItem", "rsOpaque"], Content(view)[0]!["content"]!.AsArray().Select(node => (string)node!["type"]!));
    }

    [Fact]
    public void Nodes_past_the_depth_cap_go_opaque_and_the_limits_grow_to_fit() {
        var nested = """{"type":"paragraph"}""";
        for (var i = 0; i < AdfSchema.MaxDepth + 3; i++) {
            nested = $$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{nested}}]}]}""";
        }
        var view = Project(Doc(nested));

        // The list item at depth 32 keeps its children as tokens: its paragraph and the rest of the list.
        Assert.Equal(["paragraph", "bulletList"], view.Opaque.Select(part => part.Kind));
        Assert.Equal(AdfSchema.MaxDepth + 1, view.Limits.Depth);
        Assert.True(view.Limits.Depth >= AdfSchema.MaxDepth);
        Assert.Contains("rsOpaque", view.Adf, StringComparison.Ordinal);
        Assert.StartsWith("""{"type":"doc","version":1,"content":[{"type":"bulletList""", view.Adf, StringComparison.Ordinal);
    }

    [Fact]
    public void Nodes_past_the_node_cap_go_opaque() {
        var blocks = Enumerable.Repeat(Hello, AdfSchema.MaxNodes).ToArray();
        var view = Project(Doc(blocks));
        var content = Content(view);

        // Each paragraph is two nodes, so the cap falls halfway through.
        Assert.Equal("paragraph", (string)content[AdfSchema.MaxNodes / 2 - 1]!["type"]!);
        Assert.Equal("rsOpaque", (string)content[AdfSchema.MaxNodes / 2 + 1]!["type"]!);
        Assert.Single(view.Opaque);
        Assert.Equal(AdfSchema.MaxNodes / 2, content.Count(node => (string)node!["type"]! == "rsOpaque"));
        Assert.True(view.Limits.Nodes >= AdfSchema.MaxNodes);
    }

    [Fact]
    public void Limits_are_the_defaults_or_the_base_measure_when_larger() {
        var small = Project(Doc(Hello));
        Assert.Equal(new AdfEditLimits(AdfSchema.MaxNodes, AdfSchema.MaxDepth, AdfSchema.MaxText, AdfSchema.MaxBytes), small.Limits);

        var longText = new string('x', AdfSchema.MaxText + 10);
        var large = Project(Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"{{longText}}"}]}"""));
        Assert.Equal(AdfSchema.MaxText + 10, large.Limits.Text);
    }

    [Fact]
    public void The_media_table_resolves_media_ids_to_attachments_like_the_read_view() {
        var attachments = new List<AdfAttachment> {
            new("10001", "shot.png", "image/png", DateTimeOffset.UnixEpoch),
            new("10002", "notes.pdf", "application/pdf", DateTimeOffset.UnixEpoch),
        };
        var view = Project(Doc(
            """{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"","alt":"shot.png","width":64,"height":32}}]}""",
            """{"type":"mediaGroup","content":[{"type":"media","attrs":{"type":"file","id":"m-2","collection":"","alt":"notes.pdf"}},{"type":"media","attrs":{"type":"file","id":"m-3","collection":""}}]}"""), attachments);

        Assert.Equal(
            [
                new AdfEditMedia("m-1", "10001", "shot.png", true, 64, 32),
                new AdfEditMedia("m-2", "10002", "notes.pdf", false, null, null),
                new AdfEditMedia("m-3", null, null, false, null, null),
            ],
            view.Media);
    }

    [Fact]
    public void Not_a_document_is_not_editable_and_no_description_is_an_empty_one() {
        using var array = JsonDocument.Parse("""[{"type":"paragraph"}]""");
        Assert.False(AdfEditProjection.Project(array.RootElement, []).Editable);
        using var extra = JsonDocument.Parse("""{"type":"doc","version":1,"content":[],"extra":true}""");
        Assert.False(AdfEditProjection.Project(extra.RootElement, []).Editable);
        using var version = JsonDocument.Parse("""{"type":"doc","version":2,"content":[]}""");
        Assert.False(AdfEditProjection.Project(version.RootElement, []).Editable);

        var none = AdfEditProjection.Project(null, []);
        Assert.True(none.Editable);
        Assert.Equal("""{"type":"doc","version":1,"content":[]}""", none.Adf);
    }

    [Fact]
    public void Reserved_names_from_Jira_are_preserved_as_opaque() {
        var view = Project(Doc("""{"type":"rsOpaque","attrs":{"ref":"forged"}}"""));

        Assert.Equal("rsOpaque", view.Opaque[0].Kind);
        Assert.NotEqual("forged", (string)Content(view)[0]!["attrs"]!["ref"]!);
    }

    [Fact]
    public void Every_fixture_description_projects_to_a_valid_document_or_is_read_only() {
        foreach (var (_, testCase) in AdfFixtures.All().Where(entry => AdfFixtures.IsDescription(entry.Case))) {
            var view = AdfEditProjection.Project(testCase.GetProperty("adf"), AdfFixtures.Attachments(testCase));
            if (!view.Editable) {
                continue;
            }
            var projected = JsonNode.Parse(view.Adf)!;
            Assert.Equal("doc", (string)projected["type"]!);
            foreach (var part in view.Opaque) {
                Assert.Contains(part.Raw, testCase.GetProperty("adf").GetRawText(), StringComparison.Ordinal);
            }
        }
    }

    internal static string Doc(params string[] blocks) => $$"""{"type":"doc","version":1,"content":[{{string.Join(',', blocks)}}]}""";

    internal static AdfEditView Project(string adf, IReadOnlyList<AdfAttachment>? attachments = null) {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(adf), new JsonDocumentOptions { MaxDepth = 256 });
        return AdfEditProjection.Project(document.RootElement, attachments ?? []);
    }

    internal static string Id(string json) {
        using var document = JsonDocument.Parse(json);
        return AdfLossiness.IslandId(document.RootElement);
    }

    internal static void AssertCanonical(string expected, string actual) {
        var left = JsonNode.Parse(expected);
        var right = JsonNode.Parse(actual);
        Assert.True(JsonNode.DeepEquals(left, right), $"expected {left!.ToJsonString()}\nactual   {right!.ToJsonString()}");
    }

    internal static void AssertNode(JsonNode expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"expected {expected.ToJsonString()}\nactual   {actual?.ToJsonString()}");

    private static JsonArray Content(AdfEditView view) => JsonNode.Parse(view.Adf)!["content"]!.AsArray();

    private static JsonObject Token(string type, string id) => new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["ref"] = id } };
}
