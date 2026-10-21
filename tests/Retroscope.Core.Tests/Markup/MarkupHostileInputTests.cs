using System.Diagnostics;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

// Bodies are parsed on the room actor, so a card-sized hostile body must not stall it.
[Trait("Category", "Markup")]
public sealed class MarkupHostileInputTests {
    public static TheoryData<string> Bodies() {
        return [
            Repeat("*a ", MarkupLimits.CardLength),
            Repeat("**a ", MarkupLimits.CardLength),
            Repeat("_", MarkupLimits.CardLength),
            Repeat("[", MarkupLimits.CardLength),
            Repeat("*[", MarkupLimits.CardLength),
            Repeat("*`", MarkupLimits.CardLength),
            Repeat("*` ``", MarkupLimits.CardLength),
            Repeat("[a](", MarkupLimits.CardLength),
            Repeat("[[mention:", MarkupLimits.CardLength),
            Repeat("> ", MarkupLimits.CardLength),
            Repeat("- ", MarkupLimits.CardLength),
            Repeat("**_~~", MarkupLimits.CardLength),
            Repeat("a\n", MarkupLimits.CardLength),
        ];
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Card_sized_hostile_body_parses_and_writes_quickly(string body) {
        var stopwatch = Stopwatch.StartNew();

        var doc = MarkupParser.Parse(body, MarkupDialect.Retro);
        MarkupWriter.Write(doc);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    private static string Repeat(string piece, int length) {
        return string.Concat(Enumerable.Repeat(piece, length / piece.Length + 1))[..length];
    }
}
