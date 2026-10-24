using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck.Xunit;
using Retroscope.Core.Adf;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// The rich editor's save invariants (UX10): a save of the unedited projection writes back the same document, with
/// every opaque part byte for byte; random edits the editor may make still validate and keep every opaque part's bytes.
/// </summary>
[Trait("Category", "Adf")]
public sealed class AdfWritePropertyTests {
    [Property(MaxTest = 300)]
    public void Saving_the_unedited_projection_writes_the_same_document(int seed) => NoOp(new AdfGen(seed).Doc());

    [Fact]
    public void Saving_any_unedited_fixture_description_writes_the_same_document() {
        foreach (var (_, testCase) in AdfFixtures.All().Where(entry => AdfFixtures.IsDescription(entry.Case))) {
            NoOp(testCase.GetProperty("adf").GetRawText(), AdfFixtures.Attachments(testCase));
        }
    }

    [Property(MaxTest = 300)]
    public void Random_editor_edits_validate_and_keep_every_opaque_part(int seed) {
        var adf = new AdfGen(seed).Doc();
        var view = AdfEditProjectionTests.Project(adf);
        var edited = JsonNode.Parse(view.Adf)!;
        var random = new Random(seed ^ 0x5eed);
        for (var i = random.Next(1, 6); i > 0; i--) {
            Edit(edited, random);
        }

        var ok = AdfWrite.TryWrite(Encoding.UTF8.GetBytes(edited.ToJsonString()), new AdfWriteContext(view), out var written, out var errors, out var missing);

        Assert.True(ok, $"{Describe(errors)} missing [{string.Join(',', missing)}]\nedited {edited.ToJsonString()}");
        var output = Encoding.UTF8.GetString(written!);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in Tokens(edited)) {
            var raws = view.Base.Opaque[token].Raws;
            var index = used[token] = used.GetValueOrDefault(token) + 1;
            Assert.Contains(raws[Math.Min(index, raws.Count) - 1], output, StringComparison.Ordinal);
        }
    }

    internal static void NoOp(string adf, IReadOnlyList<AdfAttachment>? attachments = null) {
        var view = AdfEditProjectionTests.Project(adf, attachments);
        // No description projects to an empty document, which is not the same JSON as null.
        if (!view.Editable || adf.Trim() == "null") {
            return;
        }

        var ok = AdfWrite.TryWrite(Encoding.UTF8.GetBytes(view.Adf), new AdfWriteContext(view), out var written, out var errors, out var missing);

        Assert.True(ok, $"{Describe(errors)} missing [{string.Join(',', missing)}]\nbase      {adf}\nprojected {view.Adf}");
        var output = Encoding.UTF8.GetString(written!);
        using var original = JsonDocument.Parse(adf, new JsonDocumentOptions { MaxDepth = 256 });
        using var rewritten = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 256 });
        Assert.True(JsonElement.DeepEquals(original.RootElement, rewritten.RootElement), $"base   {adf}\noutput {output}");
        foreach (var raw in view.Opaque.SelectMany(part => view.Base.Opaque[part.Ref].Raws)) {
            Assert.Contains(raw, output, StringComparison.Ordinal);
        }
        foreach (var mark in view.Base.Marks.Values.SelectMany(entry => entry.Raws)) {
            Assert.Contains(mark, output, StringComparison.Ordinal);
        }
    }

    // Edits a client may make without breaking the schema: rewrite a text, add bold, drop, swap or add a top-level block.
    private static void Edit(JsonNode doc, Random random) {
        var root = doc["content"]!.AsArray();
        var texts = Texts(doc, inCode: false).ToList();
        switch (random.Next(5)) {
            case 0 when texts.Count > 0:
                texts[random.Next(texts.Count)]["text"] = $"edited {random.Next(100)} é \"q\"";
                break;
            case 1 when texts.Count > 0:
                var text = texts[random.Next(texts.Count)];
                var marks = text["marks"] as JsonArray;
                var types = marks?.Select(mark => (string?)mark!["type"]).ToList() ?? [];
                if (!types.Contains("code") && !types.Contains("strong")) {
                    if (marks is null) {
                        text["marks"] = marks = [];
                    }
                    marks.Add(new JsonObject { ["type"] = "strong" });
                }
                break;
            case 2 when root.Count > 1:
                root.RemoveAt(random.Next(root.Count));
                break;
            case 3 when root.Count > 1:
                var a = random.Next(root.Count);
                var b = random.Next(root.Count);
                var first = root[a]!.DeepClone();
                root[a] = root[b]!.DeepClone();
                root[b] = first;
                break;
            default:
                root.Insert(random.Next(root.Count + 1), JsonNode.Parse("""{"type":"paragraph","content":[{"type":"text","text":"new"}]}"""));
                break;
        }
    }

    private static IEnumerable<JsonObject> Texts(JsonNode? node, bool inCode) {
        if (node is not JsonObject obj) {
            yield break;
        }
        var type = (string?)obj["type"];
        if (type == "text" && !inCode) {
            yield return obj;
        }
        if (obj["content"] is JsonArray content) {
            foreach (var child in content) {
                foreach (var text in Texts(child, inCode || type == "codeBlock")) {
                    yield return text;
                }
            }
        }
    }

    private static IEnumerable<string> Tokens(JsonNode? node) {
        if (node is JsonArray array) {
            return array.SelectMany(Tokens);
        }
        if (node is not JsonObject obj) {
            return [];
        }
        if ((string?)obj["type"] is AdfSchema.OpaqueBlock or AdfSchema.OpaqueInline) {
            return [(string)obj["attrs"]!["ref"]!];
        }
        return Tokens(obj["content"]);
    }

    private static string Describe(IReadOnlyList<AdfWriteError> errors) => string.Join(", ", errors.Select(e => $"{e.Code}@{e.Path}"));
}
