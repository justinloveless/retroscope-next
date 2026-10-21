using Retroscope.Core.Adf;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed class MarkupToAdfTests {
    private const string Head = """{"type":"doc","version":1,"content":[""";
    private const string Tail = "]}";
    private const string EmptyParagraph = """{"type":"paragraph","content":[]}""";

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", """{"type":"paragraph","content":[{"type":"text","text":"plain"}]}""")]
    [InlineData(
        "**bold _both_** ~~gone~~",
        """{"type":"paragraph","content":[{"type":"text","text":"bold ","marks":[{"type":"strong"}]},{"type":"text","text":"both","marks":[{"type":"strong"},{"type":"em"}]},{"type":"text","text":" "},{"type":"text","text":"gone","marks":[{"type":"strike"}]}]}""")]
    [InlineData(
        "a\nb",
        """{"type":"paragraph","content":[{"type":"text","text":"a"},{"type":"hardBreak"},{"type":"text","text":"b"}]}""")]
    [InlineData(
        "[**go** now](https://example.com/x%28y%29)",
        """{"type":"paragraph","content":[{"type":"text","text":"go","marks":[{"type":"link","attrs":{"href":"https://example.com/x%28y%29"}},{"type":"strong"}]},{"type":"text","text":" now","marks":[{"type":"link","attrs":{"href":"https://example.com/x%28y%29"}}]}]}""")]
    [InlineData(
        "**`code` text**",
        """{"type":"paragraph","content":[{"type":"text","text":"code","marks":[{"type":"code"}]},{"type":"text","text":" text","marks":[{"type":"strong"}]}]}""")]
    [InlineData(
        "[`run()`](mailto:a@b.c)",
        """{"type":"paragraph","content":[{"type":"text","text":"run()","marks":[{"type":"link","attrs":{"href":"mailto:a@b.c"}},{"type":"code"}]}]}""")]
    [InlineData("## Title", """{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Title"}]}""")]
    [InlineData("---", """{"type":"rule"}""")]
    [InlineData(
        "```js\nx()\n```\n\n```\n```",
        """{"type":"codeBlock","attrs":{"language":"js"},"content":[{"type":"text","text":"x()"}]},{"type":"codeBlock","content":[]}""")]
    [InlineData(
        "3. three\n4. four",
        """{"type":"orderedList","attrs":{"order":3},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"three"}]}]},{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"four"}]}]}]}""")]
    public void Writes_jira_markup_as_adf(string markup, string content) {
        Assert.Equal(Head + content + Tail, MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.Jira)));
    }

    // ADF list items and quotes hold paragraphs, lists and code blocks only, at least one, and a list item starts
    // with a paragraph or code block.
    [Theory]
    [InlineData("-", """{"type":"bulletList","content":[{"type":"listItem","content":[""" + EmptyParagraph + "]}]}")]
    [InlineData(
        "- - deep",
        """{"type":"bulletList","content":[{"type":"listItem","content":[""" + EmptyParagraph
        + """,{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"deep"}]}]}]}]}]}""")]
    [InlineData(
        "- # Heading\n\n  ---\n\n  > quoted",
        """{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Heading"}]},{"type":"paragraph","content":[{"type":"text","text":"quoted"}]}]}]}""")]
    [InlineData(">", """{"type":"blockquote","content":[""" + EmptyParagraph + "]}")]
    [InlineData(
        "> > inner\n> ## h",
        """{"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"inner"}]},{"type":"paragraph","content":[{"type":"text","text":"h"}]}]}""")]
    public void Lowers_shapes_adf_does_not_allow(string markup, string content) {
        Assert.Equal(Head + content + Tail, MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.Jira)));
    }

    [Fact]
    public void Retro_mentions_and_images_become_text() {
        var doc = MarkupParser.Parse(
            "cc [[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:Ann]] ![chart](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e) ![](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)",
            MarkupDialect.Retro);

        Assert.Equal(
            Head + """{"type":"paragraph","content":[{"type":"text","text":"cc @Ann chart image"}]}""" + Tail,
            MarkupToAdf.Write(doc));
    }

    [Fact]
    public void Adf_only_nodes_are_refused() {
        var panel = new Doc([new DocPanel(DocPanelKind.Info, [])]);
        var status = new Doc([new DocParagraph([new DocStatus("OK", DocStatusColor.Green)])]);

        Assert.Throws<ArgumentException>(() => MarkupToAdf.Write(panel));
        Assert.Throws<ArgumentException>(() => MarkupToAdf.Write(status));
    }

    [Fact]
    public void A_link_inside_a_link_keeps_the_outer_href() {
        var doc = new Doc([new DocParagraph([new DocLink("https://outer.example", [new DocLink("https://inner.example", [new DocText("x")])])])]);

        Assert.Equal(
            Head + """{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"https://outer.example"}}]}]}""" + Tail,
            MarkupToAdf.Write(doc));
    }
}
