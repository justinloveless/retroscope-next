using System.Text;
using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

// markup → ADF → markup. Markup always comes back fully editable (no islands) with the same content, and markup that
// ADF can express comes back unchanged. The few
// shapes ADF cannot hold (headings, rules and quotes inside list items or quotes, emphasis around code, an
// emphasis inside the same emphasis, neighbours with the same mark, two marks nested in the other order, emphasis
// starting or ending with a space) are normalised once, and the result is a fixed point.
[Trait("Category", "Adf")]
public sealed class AdfRoundTripTests {
    private const int Iterations = 20_000;
    private const int Seed = 20261021;

    private static readonly string[] Pieces = [
        "*", "**", "_", "~~", "`", "```", "\\", "[", "]", "(", ")", "!", "-", "- ", "1. ", "7. ", "> ", ">", "# ", "## ",
        "---", "\n", "\n", "\n\n", " ", "  ", "\t", "a", "b", "word", "x1", "é", "😀", ":", ".", "<b>",
        "](https://example.com/a_b)", "](mailto:a@b.c)", "](javascript:alert(1))", "[link](https://example.com)",
        "```js\n", "\n```\n", "[[island:abc]]",
    ];

    public static TheoryData<string> JiraCorpus() {
        var data = new TheoryData<string>();
        foreach (var testCase in MarkupCorpus()) {
            data.Add(testCase.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(JiraCorpus))]
    public void Markup_corpus_survives_adf(string name) {
        var markup = MarkupCorpus().Single(testCase => testCase.Name == name).Markup;

        var failure = RoundTripFailure(markup, expectExact: !NeedsLowering(markup));

        Assert.True(failure == null, failure);
    }

    [Fact]
    public void Random_markup_survives_adf() {
        var random = new Random(Seed);
        for (var i = 0; i < Iterations; i++) {
            var markup = MarkupWriter.Write(MarkupParser.Parse(Generate(random), MarkupDialect.Jira));
            string? failure;
            try {
                failure = RoundTripFailure(markup, expectExact: false);
            } catch (Exception e) {
                failure = e.ToString();
            }
            Assert.True(failure == null, $"iteration {i} markup {JsonSerializer.Serialize(markup)}: {failure}");
        }
    }

    [Theory]
    [InlineData("- # Heading", "- Heading")]
    [InlineData("> > nested", "> nested")]
    [InlineData("- a\n\n  ---\n\n  b", "- a\n\n  b")]
    [InlineData("**`x`**", "`x`")]
    public void Shapes_adf_cannot_hold_are_lowered(string markup, string lowered) {
        var adf = MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.Jira));
        using var document = JsonDocument.Parse(adf);

        Assert.Equal(lowered, MarkupWriter.Write(AdfToDoc.Description(document.RootElement, []).Doc));
    }

    // Null when markup → ADF → markup keeps the content, needs no island, is a fixed point and (when expected) is
    // unchanged.
    private static string? RoundTripFailure(string markup, bool expectExact) {
        var once = Through(markup, out var islands);
        if (islands > 0) {
            return $"{islands} island(s)";
        }
        if (expectExact && once != markup) {
            return $"changed to {JsonSerializer.Serialize(once)}";
        }
        if (Content(once) != Content(markup)) {
            return $"content changed: {JsonSerializer.Serialize(once)}";
        }
        var twice = Through(once, out _);
        return twice == once ? null : $"not a fixed point: {JsonSerializer.Serialize(once)} then {JsonSerializer.Serialize(twice)}";
    }

    private static string Through(string markup, out int islands) {
        using var adf = JsonDocument.Parse(MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.Jira)));
        var description = AdfToDoc.Description(adf.RootElement, []);
        islands = description.Islands.Count;
        return MarkupWriter.Write(description.Doc);
    }

    private static string Content(string markup) {
        using var adf = JsonDocument.Parse(MarkupToAdf.Write(MarkupParser.Parse(markup, MarkupDialect.Jira)));
        return AdfLossiness.Project(adf.RootElement.GetProperty("content").EnumerateArray());
    }

    // Conservative: any of these may need lowering, in which case only stability is checked.
    private static bool NeedsLowering(string markup) {
        var doc = MarkupParser.Parse(markup, MarkupDialect.Jira);
        return doc.Descendants().Any(node => node switch {
            DocListItem item => item.Blocks.Any(block => block is DocHeading or DocRule or DocQuote),
            DocQuote quote => quote.Blocks.Any(block => block is DocHeading or DocRule or DocQuote),
            DocStyled styled when Below(styled).Any(inner => inner is DocCode || inner is DocStyled nested && nested.Style == styled.Style) => true,
            DocStyled or DocLink when node.Children is [var only] && Rank(only) < Rank(node) => true,
            DocStyled styled when Text(styled) is var text && (text.StartsWith(' ') || text.EndsWith(' ')) => true,
            _ => HasAdjacentTwins(node.Children),
        });
    }

    private static string Text(DocNode node) {
        return node is DocText text ? text.Value : string.Concat(node.Children.Select(Text));
    }

    // Two marks over the same text nest in one order: link, bold, italic, strike, underline from the outside in.
    private static int Rank(DocNode node) {
        return node switch {
            DocLink => 0,
            DocStyled styled => 1 + (int)styled.Style,
            _ => int.MaxValue,
        };
    }

    // ADF marks are flat, so "_a_*b*" and "**a**\n**b**"-style neighbours with the same mark merge into one.
    private static bool HasAdjacentTwins(IReadOnlyList<DocNode> children) {
        var marked = children.Where(child => child is not DocLineBreak).ToList();
        for (var i = 0; i + 1 < marked.Count; i++) {
            var twins = (marked[i], marked[i + 1]) switch {
                (DocStyled a, DocStyled b) => a.Style == b.Style,
                (DocLink a, DocLink b) => a.Href == b.Href,
                _ => false,
            };
            if (twins) {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<DocNode> Below(DocNode node) {
        return node.Children.SelectMany(child => Below(child).Prepend(child));
    }

    // Every case of the shared markup corpus, re-read in the Jira dialect (ADF has no retro mentions or images).
    private static IEnumerable<(string Name, string Markup)> MarkupCorpus() {
        var directory = Path.Combine(RepoRoot.Path, "tests", "fixtures", "markup");
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal)) {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray()) {
                var input = testCase.GetProperty("input").GetString()!;
                if (input.Any(c => c < ' ' && c is not ('\t' or '\n' or '\r'))) {
                    continue;
                }
                var name = $"{Path.GetFileNameWithoutExtension(file)}: {testCase.GetProperty("name").GetString()}";
                yield return (name, MarkupWriter.Write(MarkupParser.Parse(input, MarkupDialect.Jira)));
            }
        }
    }

    private static string Generate(Random random) {
        var builder = new StringBuilder();
        var pieces = random.Next(0, 40);
        for (var i = 0; i < pieces; i++) {
            builder.Append(Pieces[random.Next(Pieces.Length)]);
        }
        return builder.ToString();
    }
}
