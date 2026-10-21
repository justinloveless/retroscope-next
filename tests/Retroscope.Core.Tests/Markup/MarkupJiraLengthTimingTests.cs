using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Timing;

namespace Retroscope.Core.Tests.Markup;

// Jira text is capped at 32,000 code points and is parsed on the request path (§f), so a hostile body must parse in
// linear time. Each row compares the work of parsing the body at a quarter of the cap with the body at the cap (see
// Scaling), so the test holds on a loaded machine and still fails when a scan turns quadratic.
[Trait("Category", "Markup")]
[Collection(TimedAlone.Name)]
public sealed class MarkupJiraLengthTimingTests {
    private const string Mention = "[[mention:00112233-4455-6677-8899-aabbccddeeff:Ada]]";

    private static readonly Dictionary<string, Func<int, string>> Shapes = new() {
        ["nested emphasis"] = length => Nested("**_~~*", "*~~_**", length),
        ["nested emphasis with words"] = length => Nested("**_~~*a ", " a*~~_**", length),
        ["emphasis openers"] = length => Repeat("**_~~*", length),
        ["unclosed links"] = length => Repeat("[a](https://example.com/", length),
        ["unclosed link targets"] = length => Repeat("[a](", length),
        ["labels"] = length => Repeat("[a]", length),
        ["brackets"] = length => Repeat("[", length),
        ["brackets with emphasis"] = length => Repeat("[**a", length),
        ["one bracket"] = length => "[" + Repeat("a ", length - 1),
        ["mentions"] = length => Repeat(Mention, length),
        ["unclosed mentions"] = length => Repeat(Mention[..^2], length),
        ["mention openers"] = length => Repeat("[[mention:", length),
        ["emphasised mentions"] = length => Repeat("*" + Mention, length),
    };

    public static IEnumerable<TheoryDataRow<MarkupDialect, string>> Bodies() {
        foreach (var dialect in Enum.GetValues<MarkupDialect>()) {
            foreach (var shape in Shapes.Keys) {
                yield return new(dialect, shape);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Jira_length_hostile_body_parses_in_linear_time(MarkupDialect dialect, string shape) {
        var quarter = Shapes[shape](MarkupLimits.JiraTextLength / 4);
        var full = Shapes[shape](MarkupLimits.JiraTextLength);

        var ratio = Scaling.Ratio(() => MarkupParser.Parse(quarter, dialect), () => MarkupParser.Parse(full, dialect));

        Assert.True(ratio < Scaling.MaxLinearRatio, $"4x the input took {ratio:F1}x as long");
    }

    private static string Nested(string opener, string closer, int length) {
        var half = Repeat(opener, length / 2);
        return half + Repeat(closer, length - half.Length);
    }

    private static string Repeat(string piece, int length) {
        return string.Concat(Enumerable.Repeat(piece, length / piece.Length + 1))[..length];
    }
}
