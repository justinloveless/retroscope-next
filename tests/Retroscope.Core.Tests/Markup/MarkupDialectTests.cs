using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

[Trait("Category", "Markup")]
public sealed class MarkupDialectTests {
    private const string Island = "[[island:abc123def456]]";

    [Fact]
    public void Island_token_is_accepted_only_in_the_jira_description_dialect() {
        var islands = Enum.GetValues<MarkupDialect>()
            .Where(dialect => MarkupParser.Parse(Island, dialect).Blocks.Single() is DocIsland)
            .ToArray();

        Assert.Equal([MarkupDialect.JiraDescription], islands);
    }

    [Fact]
    public void Island_keeps_its_id_and_writes_back_unchanged() {
        var doc = MarkupParser.Parse($"intro\n{Island}\n\noutro", MarkupDialect.JiraDescription);

        var island = Assert.IsType<DocIsland>(doc.Blocks[1]);
        Assert.Equal("abc123def456", island.Id);
        Assert.Empty(island.Blocks);
        Assert.Equal($"intro\n\n{Island}\n\noutro", MarkupWriter.Write(doc));
    }

    [Fact]
    public void Island_outside_a_jira_description_is_plain_text() {
        foreach (var dialect in new[] { MarkupDialect.Retro, MarkupDialect.Jira }) {
            var paragraph = Assert.IsType<DocParagraph>(MarkupParser.Parse(Island, dialect).Blocks.Single());
            Assert.Equal(Island, Assert.IsType<DocText>(Assert.Single(paragraph.Content)).Value);
        }
    }

    [Fact]
    public void Mentions_and_images_exist_only_in_the_retro_dialect() {
        const string Body = "[[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:Ann]] ![image](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)";

        Assert.Equal(2, MarkupParser.Parse(Body, MarkupDialect.Retro).Descendants().Count(n => n is DocMention or DocImage));
        Assert.DoesNotContain(MarkupParser.Parse(Body, MarkupDialect.Jira).Descendants(), n => n is DocMention or DocImage);
        Assert.Empty(Mentions.Extract(MarkupParser.Parse(Body, MarkupDialect.JiraDescription)));
    }
}
