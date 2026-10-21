using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
public sealed class HtmlHistogramTests {
    [Fact]
    public void Tags_and_attributes_are_counted() {
        var histogram = new HtmlHistogram();

        histogram.Add("""<p>Hi <span data-mention-id="u1" class="mention-link">Ana</span></p><p><img src="data:image/png;base64,AA=="></p>""", "card#1");
        histogram.Add("<p>again</p>", "card#2");

        Assert.Equal(new Dictionary<string, long> { ["img"] = 1, ["p"] = 3, ["span"] = 1 }, histogram.Tags);
        Assert.Equal(new Dictionary<string, long> { ["img[src]"] = 1, ["span[class]"] = 1, ["span[data-mention-id]"] = 1 }, histogram.Attributes);
        histogram.ThrowIfUnknown();
    }

    [Fact]
    public void Escaped_markup_is_text() {
        var histogram = new HtmlHistogram();

        histogram.Add("<p>we pasted &lt;div&gt; and &lt;marquee&gt; as text</p>", "card#1");
        histogram.Add("plain text with [[mention:u1:Ana]] and 2 < 3", "card#2");

        Assert.Equal(["p"], histogram.Tags.Keys);
        histogram.ThrowIfUnknown();
    }

    [Fact]
    public void An_unknown_tag_fails_with_samples() {
        var histogram = new HtmlHistogram();
        for (var i = 0; i < 7; i++) {
            histogram.Add("<table><tr><td>cell</td></tr></table>", $"card#{i}");
        }

        var failure = Assert.Throws<UnknownHtmlTagsException>(histogram.ThrowIfUnknown);

        Assert.Equal(["table", "tbody", "td", "tr"], histogram.UnknownTags);
        Assert.Equal("<table> × 7 (e.g. card#0, card#1, card#2, card#3, card#4)", failure.Errors[0]);
        Assert.Contains("html-textonly.json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropped_converted_and_text_only_tags_are_known() {
        var histogram = new HtmlHistogram();

        histogram.Add("<script>alert(1)</script><style>p{}</style><iframe src=x></iframe><h2>t</h2><u>u</u><pre><code>c</code></pre>", "card#1");

        histogram.ThrowIfUnknown();
        Assert.Contains("span", (IReadOnlySet<string>)HtmlTagPolicy.TextOnly);
        Assert.DoesNotContain("table", (IReadOnlySet<string>)HtmlTagPolicy.TextOnly);
    }
}
