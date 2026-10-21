using System.Text;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

[Trait("Category", "Markup")]
public sealed class MarkupFuzzTests {
    private const int Iterations = 100_000;
    private const int Seed = 20261020;

    private static readonly string[] Pieces = [
        "*", "**", "***", "_", "__", "~", "~~", "`", "``", "```", "\\", "[", "]", "(", ")", "!", "!",
        "-", "- ", "1. ", "10. ", "> ", ">", "#", "## ", "---", "\n", "\n", "\n\n", "\r\n", "\r", " ", "  ", "   ", "\t",
        "a", "b", "word", "x1", "é", "😀", "\ud83d", ":", ".", "7", "999999999. ", "\0", "<b>", "&lt;div&gt;",
        "](https://example.com/a_b)", "](mailto:a@b.c)", "](javascript:alert(1))", "](http://x y)",
        "[[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:Ann]]", "[[mention:", "[[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:",
        "![image](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)", "](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)", "![",
        "[[island:abc123def456]]", "[[island:", "```js", "```\n", "\n```\n",
    ];

    [Fact]
    public void Parsing_never_throws_and_writing_is_a_fixed_point() {
        var random = new Random(Seed);
        for (var i = 0; i < Iterations; i++) {
            var input = Generate(random);
            var dialect = (MarkupDialect)(i % 3);
            string? failure;
            try {
                failure = RoundTripFailure(input, dialect);
            } catch (Exception e) {
                failure = e.ToString();
            }
            Assert.True(failure == null, $"iteration {i} ({dialect}) input {Quote(input)}: {failure}");
        }
    }

    private static string? RoundTripFailure(string input, MarkupDialect dialect) {
        var doc = MarkupParser.Parse(input, dialect);
        var kinds = DocKinds.DisallowedIn(doc, dialect);
        if (kinds != null) {
            return $"produced {kinds}";
        }
        var written = MarkupWriter.Write(doc);
        var reparsed = MarkupParser.Parse(written, dialect);
        if (DocJson.Serialize(reparsed) != DocJson.Serialize(doc)) {
            return $"written {Quote(written)} parsed as {DocJson.Serialize(reparsed)}, expected {DocJson.Serialize(doc)}";
        }
        var rewritten = MarkupWriter.Write(reparsed);
        return rewritten == written ? null : $"rewritten {Quote(rewritten)} differs from {Quote(written)}";
    }

    private static string Generate(Random random) {
        var builder = new StringBuilder();
        var pieces = random.Next(0, 80);
        for (var i = 0; i < pieces; i++) {
            builder.Append(Pieces[random.Next(Pieces.Length)]);
        }
        return builder.ToString();
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
