using System.Text.Json.Nodes;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Markup;

// tests/fixtures/markup is the grammar's shared corpus: web/src/markup/parse.js checks the same files.
[Trait("Category", "Markup")]
public sealed class MarkupCorpusTests {
    private static readonly string FixtureDirectory = Path.Combine(RepoRoot.Path, "tests", "fixtures", "markup");

    public static TheoryData<string, string> Cases() {
        var data = new TheoryData<string, string>();
        foreach (var file in Directory.GetFiles(FixtureDirectory, "*.json").Order(StringComparer.Ordinal)) {
            foreach (var testCase in LoadCases(file)) {
                data.Add(Path.GetFileNameWithoutExtension(file), testCase["name"]!.GetValue<string>());
            }
        }
        return data;
    }

    [Fact]
    public void Corpus_covers_every_dialect() {
        var dialects = Directory.GetFiles(FixtureDirectory, "*.json")
            .SelectMany(LoadCases)
            .Select(testCase => testCase["dialect"]!.GetValue<string>())
            .ToHashSet();

        Assert.Equal(["jira", "jiraDescription", "retro"], dialects.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses_to_the_expected_doc(string file, string name) {
        var testCase = Find(file, name);

        var doc = MarkupParser.Parse(testCase["input"]!.GetValue<string>(), Dialect(testCase));

        AssertDoc(testCase["doc"]!, doc);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Writes_canonical_markup_that_parses_back_to_the_same_doc(string file, string name) {
        var testCase = Find(file, name);
        var doc = MarkupParser.Parse(testCase["input"]!.GetValue<string>(), Dialect(testCase));

        var markup = MarkupWriter.Write(doc);

        Assert.Equal(testCase["markup"]!.GetValue<string>(), markup);
        AssertDoc(testCase["doc"]!, MarkupParser.Parse(markup, Dialect(testCase)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Extracts_the_expected_mentions(string file, string name) {
        var testCase = Find(file, name);
        var expected = testCase["mentions"]?.AsArray().Select(id => Guid.Parse(id!.GetValue<string>())) ?? [];

        var mentions = Mentions.Extract(MarkupParser.Parse(testCase["input"]!.GetValue<string>(), Dialect(testCase)));

        Assert.Equal(expected, mentions);
    }

    private static void AssertDoc(JsonNode expected, Doc actual) {
        var actualJson = JsonNode.Parse(DocJson.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(expected, actualJson), $"expected {expected.ToJsonString()}\nactual   {actualJson!.ToJsonString()}");
    }

    private static MarkupDialect Dialect(JsonObject testCase) {
        return testCase["dialect"]!.GetValue<string>() switch {
            "retro" => MarkupDialect.Retro,
            "jira" => MarkupDialect.Jira,
            "jiraDescription" => MarkupDialect.JiraDescription,
            var other => throw new InvalidOperationException($"Unknown dialect {other}"),
        };
    }

    private static JsonObject Find(string file, string name) {
        return LoadCases(Path.Combine(FixtureDirectory, file + ".json"))
            .Single(testCase => testCase["name"]!.GetValue<string>() == name);
    }

    private static IEnumerable<JsonObject> LoadCases(string path) {
        return JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray().Select(testCase => testCase!.AsObject());
    }
}
