using System.Text.Json;
using System.Text.Json.Nodes;
using Retroscope.Core.Adf;
using Retroscope.Core.Markup;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// The web fake's Jira serves the read view from markup and islands, and the rich editor's edit view from the
/// issue's <c>adf</c> as it is. These checks keep the two in step: the ADF renders, through the real pipeline, to
/// exactly the Doc the fake serves, and it projects to itself (nothing in it is opaque to the editor).
/// </summary>
[Trait("Category", "Adf")]
public sealed class WebFakeAdfTests {
    public static TheoryData<string, string> Issues() => new() {
        { "tests/web-fake-server/fixtures/jira.json", "RS-12" },
        { "tests/web-fake-server/demo/jira.json", "DEMO-123" },
    };

    [Theory]
    [MemberData(nameof(Issues))]
    public void The_fixture_ADF_renders_to_the_Doc_the_fake_serves(string file, string key) {
        var issue = Issue(file, key);
        var description = AdfToDoc.Description(issue.GetProperty("adf"), Attachments(issue));

        var expected = FakeDoc(issue);
        var actual = Normalize(JsonNode.Parse(DocJson.Serialize(description.Doc))!);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"fake   {expected.ToJsonString()}\nserver {actual.ToJsonString()}");
        Assert.True(description.Editable);
    }

    [Theory]
    [MemberData(nameof(Issues))]
    public void The_fixture_ADF_is_its_own_edit_view(string file, string key) {
        var issue = Issue(file, key);
        var view = AdfEditProjection.Project(issue.GetProperty("adf"), Attachments(issue));

        Assert.True(view.Editable);
        Assert.Empty(view.Opaque);
        Assert.Empty(view.Marks);
        AdfEditProjectionTests.AssertCanonical(issue.GetProperty("adf").GetRawText(), view.Adf);
        Assert.All(view.Media, media => Assert.NotNull(media.AttachmentId));
    }

    // What tests/web-fake-server/jira.js descriptionDoc() serves: the markup parsed, each island's Doc spliced in.
    private static JsonNode FakeDoc(JsonElement issue) {
        var doc = JsonNode.Parse(DocJson.Serialize(MarkupParser.Parse(issue.GetProperty("description").GetString()!, MarkupDialect.JiraDescription)))!;
        foreach (var block in doc["c"]!.AsArray()) {
            if ((string?)block!["t"] == "island") {
                block["c"] = new JsonArray(JsonNode.Parse(issue.GetProperty("islands").GetProperty((string)block["id"]!).GetRawText()));
            }
        }
        return Normalize(doc);
    }

    // Island ids differ by design: the fake's are hand-written, the server's are content hashes.
    private static JsonNode Normalize(JsonNode doc) {
        foreach (var block in doc["c"]!.AsArray()) {
            if ((string?)block!["t"] == "island") {
                block["id"] = "island";
            }
        }
        return doc;
    }

    private static List<AdfAttachment> Attachments(JsonElement issue) =>
        [.. issue.GetProperty("attachments").EnumerateArray().Select(a => new AdfAttachment(
            a.GetProperty("id").GetString()!, a.GetProperty("filename").GetString()!, a.GetProperty("mimeType").GetString()!, DateTimeOffset.MinValue))];

    private static JsonElement Issue(string file, string key) {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot.Path, file)));
        return document.RootElement.GetProperty("issues").EnumerateArray().Single(issue => issue.GetProperty("key").GetString() == key).Clone();
    }
}
