using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed class AdfIslandTests {
    // Formatting, member order and escapes as Jira might send them; none of it may change on the way back.
    private const string Table = """
        {"type": "table",  "attrs":{"layout":"default","isNumberColumnEnabled":false},
          "content":[{"content":[{"type":"tableCell","attrs":{"colwidth":[120]},"content":[{"type":"paragraph","content":[{"text":"café <b>","type":"text"}]}]}],"type":"tableRow"}]}
        """;

    private const string Panel = """{"type":"panel","attrs":{"panelType":"info"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Hi"}]}]}""";

    private const string Status = """{"type":"paragraph","content":[{"type":"status","attrs":{"text":"OPEN","color":"blue","localId":"9f1"}}]}""";

    private static readonly string Adf = $$"""
        {"version":1,"type":"doc","content":[
          {"type":"paragraph","content":[{"type":"text","text":"Intro"}]},
          {{Table}},
          {{Panel}},
          {{Status}},
          {"type":"paragraph","content":[{"type":"text","text":"Outro"}]}
        ]}
        """;

    [Fact]
    public void Island_ids_hash_the_canonical_subtree() {
        Assert.Equal("vptt64Wcf4xd6Xdj", AdfLossiness.IslandId(Parse("""{"type":"rule"}""")));
        Assert.Equal("W6OCeDlwcDm0amKu", AdfLossiness.IslandId(Parse(Panel)));
        Assert.Equal(
            AdfLossiness.IslandId(Parse(Panel)),
            AdfLossiness.IslandId(Parse("""{ "content" : [{"content":[{"text":"Hi","type":"text"}],"type":"paragraph"}], "attrs":{"panelType":"info"}, "type":"panel" }""")));
        Assert.NotEqual(AdfLossiness.IslandId(Parse(Panel)), AdfLossiness.IslandId(Parse(Panel.Replace("Hi", "Ho", StringComparison.Ordinal))));
    }

    [Fact]
    public void Island_ids_are_valid_markup_island_refs() {
        var description = Describe(Adf);

        foreach (var id in description.Islands.Keys) {
            var doc = MarkupParser.Parse($"[[island:{id}]]", MarkupDialect.JiraDescription);
            Assert.Equal(id, Assert.IsType<DocIsland>(Assert.Single(doc.Blocks)).Id);
        }
    }

    [Fact]
    public void Unedited_islands_splice_back_byte_for_byte() {
        var description = Describe(Adf);
        Assert.Equal([Table.Trim(), Panel, Status], description.Islands.Values);

        var adf = Save(description, MarkupWriter.Write(description.Doc));

        Assert.Contains(Table.Trim(), adf, StringComparison.Ordinal);
        Assert.Contains(Panel, adf, StringComparison.Ordinal);
        Assert.Contains(Status, adf, StringComparison.Ordinal);
    }

    [Fact]
    public void Islands_can_be_moved_and_deleted_around_edited_text() {
        var description = Describe(Adf);
        var ids = description.Islands.Keys.ToList();

        var adf = Save(description, $"[[island:{ids[2]}]]\n\nRewritten **intro**\n\n[[island:{ids[0]}]]");

        Assert.Equal(
            $$"""{"type":"doc","version":1,"content":[{{Status}},{"type":"paragraph","content":[{"type":"text","text":"Rewritten "},{"type":"text","text":"intro","marks":[{"type":"strong"}]}]},{{Table.Trim()}}]}""",
            adf);
    }

    [Fact]
    public void An_unknown_island_is_reported_instead_of_written() {
        var description = Describe(Adf);
        var edited = MarkupParser.Parse("text\n\n[[island:notAnIsland]]", MarkupDialect.JiraDescription);

        Assert.False(MarkupToAdf.TryWrite(edited, description.Islands, out var adf, out var missing));
        Assert.Null(adf);
        Assert.Equal("notAnIsland", missing);
        Assert.Throws<ArgumentException>(() => MarkupToAdf.Write(edited));
    }

    [Fact]
    public void Island_renderings_are_read_only_content_of_the_island() {
        var description = Describe(Adf);

        var islands = description.Doc.Blocks.OfType<DocIsland>().ToList();

        Assert.Equal(3, islands.Count);
        Assert.IsType<DocTable>(Assert.Single(islands[0].Blocks));
        Assert.IsType<DocPanel>(Assert.Single(islands[1].Blocks));
        Assert.IsType<DocStatus>(Assert.Single(Assert.IsType<DocParagraph>(Assert.Single(islands[2].Blocks)).Content));
    }

    private static AdfDescription Describe(string adf) => AdfToDoc.Description(Parse(adf), []);

    private static string Save(AdfDescription description, string markup) {
        var edited = MarkupParser.Parse(markup, MarkupDialect.JiraDescription);
        Assert.True(MarkupToAdf.TryWrite(edited, description.Islands, out var adf, out var missing), missing);
        return adf;
    }

    private static JsonElement Parse(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
