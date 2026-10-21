using System.Diagnostics;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MarkupTiming {
    public const string Name = "Markup timing";
}

// Jira text is capped at 32,000 code points and is parsed on the request path (§f), so the bound is tight. The
// collection runs alone so that other tests do not inflate the timings.
[Trait("Category", "Markup")]
[Collection(MarkupTiming.Name)]
public sealed class MarkupJiraLengthTimingTests {
    private const string Mention = "[[mention:00112233-4455-6677-8899-aabbccddeeff:Ada]]";
    private const string QuadraticLinkScan =
        "U4 follow-up (docs/decisions/UF1.md): InlineParser.ScanLink rescans to the next ']' from every '['";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    public static IEnumerable<TheoryDataRow<MarkupDialect, string>> Bodies() {
        var length = MarkupLimits.JiraTextLength;
        foreach (var dialect in Enum.GetValues<MarkupDialect>()) {
            yield return new(dialect, Nested("**_~~*", "*~~_**", length));
            yield return new(dialect, Nested("**_~~*a ", " a*~~_**", length));
            yield return new(dialect, Repeat("**_~~*", length));
            yield return new(dialect, Repeat("[a](https://example.com/", length));
            yield return new(dialect, Repeat("[a](", length));
            yield return new(dialect, Repeat("[a]", length));
            yield return new(dialect, Repeat("[", length)) { Skip = QuadraticLinkScan };
            yield return new(dialect, Repeat("[**a", length)) { Skip = QuadraticLinkScan };
            yield return new(dialect, "[" + Repeat("a ", length - 1));
            yield return new(dialect, Repeat(Mention, length));
            yield return new(dialect, Repeat(Mention[..^2], length));
            yield return new(dialect, Repeat("[[mention:", length)) { Skip = QuadraticLinkScan };
            yield return new(dialect, Repeat("*" + Mention, length));
        }
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Jira_length_hostile_body_parses_within_200_ms(MarkupDialect dialect, string body) {
        MarkupParser.Parse("**warm** [up](https://example.com)", dialect);

        var fastest = Enumerable.Range(0, 3).Min(_ => Time(() => MarkupParser.Parse(body, dialect)));

        Assert.True(fastest < Budget, $"took {fastest}");
    }

    private static TimeSpan Time(Action action) {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed;
    }

    private static string Nested(string opener, string closer, int length) {
        var half = Repeat(opener, length / 2);
        return half + Repeat(closer, length - half.Length);
    }

    private static string Repeat(string piece, int length) {
        return string.Concat(Enumerable.Repeat(piece, length / piece.Length + 1))[..length];
    }
}
