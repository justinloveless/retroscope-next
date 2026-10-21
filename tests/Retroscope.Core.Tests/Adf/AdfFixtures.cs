using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

// tests/fixtures/adf: {"cases": [{name, mode: render|description, adf, attachments?, doc, islands?, editable?, markup?}]}.
// The adf member is read as a JsonElement over the file's own bytes, so island splice-back can be checked byte for byte.
internal static class AdfFixtures {
    public static readonly string Directory = Path.Combine(RepoRoot.Path, "tests", "fixtures", "adf");

    private static readonly Dictionary<string, JsonDocument> Files = System.IO.Directory.GetFiles(Directory, "*.json")
        .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => JsonDocument.Parse(File.ReadAllBytes(path)), StringComparer.Ordinal);

    public static IEnumerable<(string File, JsonElement Case)> All() {
        return Files.OrderBy(file => file.Key, StringComparer.Ordinal)
            .SelectMany(file => file.Value.RootElement.GetProperty("cases").EnumerateArray().Select(testCase => (file.Key, testCase)));
    }

    public static JsonElement Find(string file, string name) {
        return Files[file].RootElement.GetProperty("cases").EnumerateArray().Single(testCase => Name(testCase) == name);
    }

    public static string Name(JsonElement testCase) => testCase.GetProperty("name").GetString()!;

    public static bool IsDescription(JsonElement testCase) => testCase.GetProperty("mode").GetString() == "description";

    public static List<AdfAttachment> Attachments(JsonElement testCase) {
        if (!testCase.TryGetProperty("attachments", out var attachments)) {
            return [];
        }
        return attachments.EnumerateArray()
            .Select(attachment => new AdfAttachment(
                attachment.GetProperty("id").GetString()!,
                attachment.GetProperty("filename").GetString()!,
                attachment.GetProperty("mimeType").GetString()!,
                DateTimeOffset.ParseExact(attachment.GetProperty("created").GetString()!, "yyyy-MM-dd'T'HH:mm:ss.fffzzzz", CultureInfo.InvariantCulture)))
            .ToList();
    }

    public static void AssertDoc(JsonElement expected, Doc actual) {
        var expectedJson = JsonNode.Parse(expected.GetRawText());
        var actualJson = JsonNode.Parse(DocJson.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(expectedJson, actualJson), $"expected {expectedJson!.ToJsonString()}\nactual   {actualJson!.ToJsonString()}");
    }
}
