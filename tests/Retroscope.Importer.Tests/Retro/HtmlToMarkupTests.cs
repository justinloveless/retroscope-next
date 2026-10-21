using Retroscope.Core.Markup;
using Retroscope.Importer.Html;

namespace Retroscope.Importer.Tests.Retro;

[Trait("Category", "RetroMap")]
public sealed class HtmlToMarkupTests {
    private static readonly Guid Board = new("0b0a0000-0000-4000-8000-000000000001");
    private static readonly Guid Alice = new("a11ce000-0000-4000-8000-000000000001");
    private static readonly Guid AliceMerged = new("a11ce000-0000-4000-8000-000000000002");
    private static readonly Guid Stranger = new("5a5a5a5a-0000-4000-8000-000000000001");

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public static TheoryData<string, string> Formatting => new() {
        { "<p>plain</p>", "plain" },
        { "<p><strong>b</strong> and <b>b</b></p>", "**b** and **b**" },
        { "<p><em>i</em> <i>i</i></p>", "_i_ _i_" },
        { "<p><s>gone</s> <strike>old</strike></p>", "~~gone~~ ~~old~~" },
        { "<p>run <code>x = 1</code></p>", "run `x = 1`" },
        { "<pre><code class=\"language-js\">let a = 1;\n  b();</code></pre>", "```js\nlet a = 1;\n  b();\n```" },
        { "<p>one</p><p>two</p>", "one\n\ntwo" },
        { "<div>one</div><div>two</div>", "one\n\ntwo" },
        { "<p>line<br>break</p>", "line\nbreak" },
        { "<ul><li><p>a</p></li><li><p>b</p></li></ul>", "- a\n- b" },
        { "<ol start=\"3\"><li>c</li><li>d</li></ol>", "3. c\n4. d" },
        { "<ul><li>outer<ul><li>inner</li></ul></li></ul>", "- outer\n  - inner" },
        { "<blockquote><p>quoted</p></blockquote>", "> quoted" },
        { "<p><b> spaced </b>word</p>", "**spaced** word" },
        { "<b><p>bold paragraph</p></b>", "**bold paragraph**" },
        { "<p>  lots   of\n spaces  </p>", "lots of spaces" },
        { "<p></p><p>after empty</p><p></p>", "after empty" },
        { "<h2>Heading</h2><p>body</p><hr><p>end</p>", "Heading\n\nbody\n\nend" },
        { "<p><u>under</u> <mark>mark</mark> <span>span</span> <sup>2</sup></p>", "under mark span 2" },
        { "<p>stars * and _under_ and [brackets]</p>", "stars \\* and \\_under\\_ and \\[brackets\\]" },
        { "<p>- not a list</p>", "\\- not a list" },
        { "plain text\nsecond line\n\nnew paragraph", "plain text\nsecond line\n\nnew paragraph" },
    };

    public static TheoryData<string, string> Hostile => new() {
        { "<p>hi<script>alert(1)</script></p>", "hi" },
        { "<style>p{color:red}</style><p>styled</p>", "styled" },
        { "<iframe src=\"https://evil.example\">fallback</iframe><p>x</p>", "x" },
        { "<p><a href=\"javascript:alert(1)\">click</a></p>", "click" },
        { "<p><a href=\"JaVaScRiPt:alert(1)\">click</a></p>", "click" },
        { "<p><a href=\"data:text/html,<script>alert(1)</script>\">data</a></p>", "data" },
        { "<p><a href=\"//evil.example/x\">proto</a></p>", "proto" },
        { "<p><a href=\"vbscript:msgbox\">vb</a></p>", "vb" },
        { "<p><img src=\"x\" onerror=\"alert(1)\">after</p>", "\\[image\\]after" },
        { "<p onclick=\"alert(1)\" style=\"x\">attrs</p>", "attrs" },
        { "<p><span onmouseover=\"alert(1)\">hover</span></p>", "hover" },
        { "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", "<script>alert(1)</script>" },
        { "<p>[x](javascript:alert(1))</p>", "\\[x\\](javascript:alert(1))" },
        { "<p><a href=\"https://ok.example/a b\">spaces</a></p>", "spaces" },
        { "<p><a href=\"https://ok.example/(x)\">parens</a></p>", "[parens](https://ok.example/%28x%29)" },
        { "<p><a href=\"mailto:a@example.com\">mail</a> <a href=\"http://x.example\">web</a></p>", "[mail](mailto:a@example.com) [web](http://x.example)" },
        { "<p>nul\u0000 and bell\u0007 removed</p>", "nul and bell removed" },
    };

    [Theory]
    [MemberData(nameof(Formatting))]
    public void Formatting_converts_to_markup(string html, string expected) => Assert.Equal(expected, Convert(html).Markup);

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_html_becomes_inert_markup(string html, string expected) {
        var result = Convert(html);

        Assert.Equal(expected, result.Markup);
        AssertAllowedNodes(result.Doc);
        Assert.All(result.Doc.Descendants().OfType<DocLink>(), x => Assert.Matches("^(https?|mailto):", x.Href));
    }

    [Fact]
    public void Escaped_tags_in_text_pass_the_DocNode_invariant() {
        var result = Convert("<p>pasted &lt;div class=\"x\"&gt;layout&lt;/div&gt; &amp; more</p>");

        Assert.Equal("pasted <div class=\"x\">layout</div> & more", result.Markup);
        var text = Assert.IsType<DocText>(Assert.Single(Assert.IsType<DocParagraph>(Assert.Single(result.Doc.Blocks)).Content));
        Assert.Contains("<div", text.Value, StringComparison.Ordinal);
        AssertAllowedNodes(result.Doc);
    }

    [Fact]
    public void Existing_markdown_links_and_mention_tokens_are_kept() {
        var result = Convert($"<p>see [the doc](https://docs.example/a) cc [[mention:{Alice}:Alice A]]</p>");

        Assert.Equal($"see [the doc](https://docs.example/a) cc [[mention:{AliceMerged}:Alice A]]", result.Markup);
        Assert.Equal([AliceMerged], Mentions.Extract(result.Doc));
    }

    [Fact]
    public void Markdown_mailto_links_are_kept() {
        var result = Convert("<p>write [the team](mailto:team@example.com)</p>");

        Assert.Equal("write [the team](mailto:team@example.com)", result.Markup);
        Assert.Equal("mailto:team@example.com", Assert.Single(result.Doc.Descendants().OfType<DocLink>()).Href);
    }

    [Theory]
    [InlineData(2100, "")]
    [InlineData(10, "\u007f")]
    public void Markdown_links_get_the_same_checks_as_anchors(int length, string tail) {
        var href = "https://e.example/" + new string('a', length) + tail + "b";

        var result = Convert($"<p>[label]({href})</p>");

        Assert.Empty(result.Doc.Descendants().OfType<DocLink>());
        Assert.StartsWith("\\[label\\](https://e.example/", result.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Mention_spans_become_tokens_and_unknown_people_become_text() {
        var result = Convert(
            $"<p><span data-mention-id=\"{Alice}\" data-user-id=\"{Alice}\" data-name=\"Alice\">Alice</span> and [[mention:{Stranger}:Sam]]</p>");

        Assert.Equal($"[[mention:{AliceMerged}:Alice]] and @Sam", result.Markup);
        Assert.Equal(1, result.Losses[HtmlLoss.MentionOfNonUser]);
    }

    [Fact]
    public void Base64_images_become_board_blobs() {
        var result = Convert($"<p>shot <img src=\"data:image/png;base64,{System.Convert.ToBase64String(Png)}\"></p>");

        var image = Assert.Single(result.Images);
        Assert.Equal($"shot ![image](blob:{image.BlobId})", result.Markup);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Png, image.Bytes);
        Assert.Equal(Board, image.BoardId);
        Assert.Equal(HtmlToMarkup.BlobId(Board, image.Sha256), image.BlobId);
    }

    [Fact]
    public void The_same_image_twice_is_one_blob() {
        var img = $"<img src=\"data:image/png;base64,{System.Convert.ToBase64String(Png)}\">";

        var result = Convert($"<p>{img}{img}</p>");

        Assert.Single(result.Images);
        Assert.Equal(2, result.Doc.Descendants().OfType<DocImage>().Count());
    }

    [Theory]
    [InlineData("data:image/png;base64,bm90IGFuIGltYWdl", HtmlLoss.ImageNotRecognised)]
    [InlineData("data:image/svg+xml,<svg onload=alert(1)>", HtmlLoss.ImageNotRecognised)]
    [InlineData("data:image/png;base64,!!!", HtmlLoss.ImageNotRecognised)]
    public void Unusable_images_are_removed_and_counted(string source, HtmlLoss loss) {
        var result = Convert($"<p><img src=\"{source}\"></p>");

        Assert.Equal("\\[image removed\\]", result.Markup);
        Assert.Equal(1, result.Losses[loss]);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void Oversize_images_are_removed() {
        var big = new byte[HtmlToMarkup.MaxImageBytes + 1];
        Png.CopyTo(big, 0);

        var result = Convert($"<p><img src=\"data:image/png;base64,{System.Convert.ToBase64String(big)}\"></p>");

        Assert.Equal("\\[image removed\\]", result.Markup);
        Assert.Equal(1, result.Losses[HtmlLoss.ImageOversize]);
    }

    [Fact]
    public void Images_without_a_board_are_removed() {
        var result = HtmlToMarkup.Convert(
            $"<p><img src=\"data:image/png;base64,{System.Convert.ToBase64String(Png)}\"></p>",
            new HtmlContext(MarkupField.ActionItem, null, _ => null));

        Assert.Equal(1, result.Losses[HtmlLoss.ImageWithoutBoard]);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void Images_beyond_the_per_body_limit_are_removed() {
        var html = string.Concat(Enumerable.Range(0, 10).Select(i => {
            var bytes = Png.Append((byte)i).ToArray();
            return $"<img src=\"data:image/png;base64,{System.Convert.ToBase64String(bytes)}\">";
        }));

        var result = Convert($"<p>{html}</p>");

        Assert.Equal(MarkupLimits.ImagesPerBody, result.Images.Count);
        Assert.Equal(2, result.Losses[HtmlLoss.ImageOverLimit]);
        Assert.True(MarkupLimits.Check(result.Markup, MarkupField.Card).IsValid);
    }

    [Fact]
    public void Linked_images_stay_links_and_are_never_hot_linked() {
        var result = Convert("<p><img src=\"https://cdn.example/cat.png\"></p>");

        Assert.Equal("[image](https://cdn.example/cat.png)", result.Markup);
        Assert.Empty(result.Doc.Descendants().OfType<DocImage>());
    }

    [Fact]
    public void Over_long_bodies_are_truncated_with_a_note() {
        var result = HtmlToMarkup.Convert($"<p>{new string('x', 5000)} *</p>", new HtmlContext(MarkupField.Comment, Board, _ => null));

        Assert.EndsWith(HtmlToMarkup.TruncationNote, result.Markup, StringComparison.Ordinal);
        Assert.True(MarkupLimits.Check(result.Markup, MarkupField.Comment).IsValid);
        Assert.Equal(1, result.Losses[HtmlLoss.Truncated]);
    }

    [Fact]
    public void Truncation_drops_the_blobs_it_cut_off() {
        var image = $"<img src=\"data:image/png;base64,{System.Convert.ToBase64String(Png)}\">";

        var result = HtmlToMarkup.Convert($"<p>{new string('y', 4100)}{image}</p>", new HtmlContext(MarkupField.Comment, Board, _ => null));

        Assert.Empty(result.Images);
        Assert.Empty(result.Doc.Descendants().OfType<DocImage>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p></p><p> </p>")]
    [InlineData("<script>only()</script>")]
    [InlineData(null)]
    public void Empty_bodies_become_a_placeholder(string? html) {
        var result = Convert(html);

        Assert.Equal(HtmlToMarkup.EmptyBody, result.Markup);
        Assert.Equal(1, result.Losses[HtmlLoss.Empty]);
    }

    [Fact]
    public void Unknown_tags_are_refused() =>
        Assert.Throws<InvalidOperationException>(() => Convert("<table><tr><td>x</td></tr></table>"));

    [Theory]
    [MemberData(nameof(Formatting))]
    public void Output_is_canonical(string html, string _) {
        var markup = Convert(html).Markup;

        Assert.Equal(markup, MarkupWriter.Write(MarkupParser.Parse(markup, MarkupDialect.Retro)));
    }

    [Fact]
    public void Deterministic_fuzz_never_throws_and_always_yields_valid_canonical_markup() {
        string[] pieces = [
            "<p>", "</p>", "<b>", "</b>", "<i>", "</i>", "<s>", "</s>", "<code>", "</code>", "<br>", "<ul><li>", "</li></ul>",
            "<ol><li>", "<blockquote>", "</blockquote>", "<a href=\"https://x.example/a\">", "</a>", "<pre>", "</pre>", "<span>",
            "text", " ", "*", "_", "~", "`", "[", "]", "(", ")", "!", "-", "1.", ">", "#", "\\", "&lt;", "&amp;", "\n",
            $"[[mention:{Alice}:Al]]", "[l](https://y.example)", "<script>x</script>", "<h1>", "</h1>", "<hr>",
        ];
        var random = new Random(20261020);
        for (var i = 0; i < 3000; i++) {
            var html = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => pieces[random.Next(pieces.Length)]));

            var markup = Convert(html).Markup;

            Assert.True(MarkupLimits.Check(markup, MarkupField.Card).IsValid, html);
            Assert.Equal(markup, MarkupWriter.Write(MarkupParser.Parse(markup, MarkupDialect.Retro)));
        }
    }

    private static HtmlConversion Convert(string? html) =>
        HtmlToMarkup.Convert(html, new HtmlContext(MarkupField.Card, Board, id => id == Alice ? AliceMerged : null));

    // The §h.5 S28 invariant: the parsed tree holds only node kinds the retro dialect can produce.
    private static void AssertAllowedNodes(Doc doc) =>
        Assert.All(doc.Descendants(), x => Assert.True(x is DocParagraph or DocBulletList or DocOrderedList or DocListItem or DocQuote
            or DocCodeBlock or DocText or DocStyled { Style: not DocStyle.Underline } or DocCode or DocLink or DocLineBreak or DocMention or DocImage,
            x.GetType().Name));
}
