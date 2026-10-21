using System.Text;
using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Timing;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed class AdfCapsTests {
    [Fact]
    public void Rendering_stops_below_the_depth_cap() {
        using var adf = Parse(Doc(NestedQuotes(AdfToDoc.MaxDepth + 10, "bottom")));

        var doc = AdfToDoc.Render(adf.RootElement, []);

        Assert.True(Depth(doc) <= AdfToDoc.MaxDepth + 2);
        Assert.DoesNotContain(doc.Descendants(), node => node is DocText { Value: "bottom" });
        AssertEndsWithNotice(doc.Blocks);
    }

    [Fact]
    public void A_block_past_the_depth_cap_is_an_island_kept_whole() {
        var deep = NestedQuotes(AdfToDoc.MaxDepth + 10, "bottom");
        using var adf = Parse(Doc(Paragraph("top"), deep));

        var description = AdfToDoc.Description(adf.RootElement, []);

        Assert.IsType<DocParagraph>(description.Doc.Blocks[0]);
        var island = Assert.IsType<DocIsland>(description.Doc.Blocks[1]);
        AssertEndsWithNotice(island.Blocks);
        Assert.Equal(deep, description.Islands[island.Id]);
    }

    [Fact]
    public void Rendering_stops_at_the_node_cap() {
        using var adf = Parse(Doc(Enumerable.Range(0, 3000).Select(i => Paragraph($"p{i}")).ToArray()));

        var doc = AdfToDoc.Render(adf.RootElement, []);

        Assert.True(doc.Descendants().Count() <= AdfToDoc.MaxNodes + 3);
        Assert.Equal(AdfToDoc.MaxNodes / 2 + 1, doc.Blocks.Count);
        AssertEndsWithNotice(doc.Blocks);
    }

    [Fact]
    public void Blocks_past_the_node_cap_become_islands_with_one_notice() {
        var paragraphs = Enumerable.Range(0, 3000).Select(i => Paragraph($"p{i}")).ToArray();
        using var adf = Parse(Doc(paragraphs));

        var description = AdfToDoc.Description(adf.RootElement, []);

        var islands = description.Doc.Blocks.OfType<DocIsland>().ToList();
        Assert.Equal(AdfToDoc.MaxNodes / 2, description.Doc.Blocks.Count(block => block is DocParagraph));
        Assert.Equal(3000 - AdfToDoc.MaxNodes / 2, islands.Count);
        AssertEndsWithNotice(islands[0].Blocks);
        Assert.All(islands.Skip(1), island => Assert.Empty(island.Blocks));

        var edited = MarkupParser.Parse(MarkupWriter.Write(description.Doc), MarkupDialect.JiraDescription);
        Assert.True(MarkupToAdf.TryWrite(edited, description.Islands, out var written, out _));
        using var reread = Parse(written);
        Assert.Equal(paragraphs.Length, reread.RootElement.GetProperty("content").GetArrayLength());
        Assert.Equal(paragraphs[^1], reread.RootElement.GetProperty("content")[paragraphs.Length - 1].GetRawText());
    }

    // Linear, not fast: a wall-clock budget fails on a loaded machine, so each shape is compared with itself at a
    // quarter of the size (see Scaling). The wide shape stays past the node cap at both sizes.
    [Theory]
    [InlineData("wide")]
    [InlineData("alternating marks")]
    public void Hostile_shapes_convert_quickly(string shape) {
        using var quarter = Parse(Hostile(shape, 4));
        using var full = Parse(Hostile(shape, 1));

        var ratio = Scaling.Ratio(() => Convert(quarter), () => Convert(full));

        Assert.True(ratio < Scaling.MaxLinearRatio, $"4x the input took {ratio:F1}x as long");
    }

    private static string Hostile(string shape, int divisor) {
        return shape switch {
            "wide" => Doc(Enumerable.Range(0, 20_000 / divisor)
                .Select(_ => """{"type":"text","text":"x","marks":[{"type":"strong"},{"type":"em"},{"type":"strike"}]}""")
                .ToArray()),
            _ => Doc($$"""{"type":"paragraph","content":[{{string.Join(',', Enumerable.Range(0, 4000 / divisor)
                .Select(i => $$"""{"type":"text","text":" {{i}} ","marks":[{"type":"{{(i % 2 == 0 ? "strong" : "em")}}"}]}"""))}}]}"""),
        };
    }

    private static void Convert(JsonDocument adf) {
        AdfToDoc.Render(adf.RootElement, []);
        AdfToDoc.Description(adf.RootElement, []);
    }

    private static void AssertEndsWithNotice(IReadOnlyList<DocBlock> blocks) {
        var notice = Assert.IsType<DocParagraph>(blocks[^1]);
        var italic = Assert.IsType<DocStyled>(Assert.Single(notice.Content));
        Assert.Equal(AdfToDoc.TruncatedText, Assert.IsType<DocText>(Assert.Single(italic.Content)).Value);
    }

    private static int Depth(Doc doc) => doc.Blocks.Select(block => Depth(block)).DefaultIfEmpty(0).Max();

    private static int Depth(DocNode node) => 1 + node.Children.Select(Depth).DefaultIfEmpty(0).Max();

    private static string NestedQuotes(int depth, string text) {
        var json = new StringBuilder();
        for (var i = 0; i < depth; i++) {
            json.Append("""{"type":"blockquote","content":[""");
        }
        json.Append(Paragraph(text));
        for (var i = 0; i < depth; i++) {
            json.Append("]}");
        }
        return json.ToString();
    }

    private static string Paragraph(string text) => $$"""{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}""";

    private static string Doc(params string[] blocks) => $$"""{"type":"doc","version":1,"content":[{{string.Join(',', blocks)}}]}""";

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
}
