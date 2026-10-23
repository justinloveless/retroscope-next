using System.Text.Json.Nodes;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>The rich editor's server path (UX10): the edit view and whole-document saves through the real proxy.</summary>
[Trait("Category", "JiraHttp")]
[Collection(SharedPostgres.Name)]
public sealed class DescriptionTests(PostgresFixture postgres) {
    // Formatting, member order and escapes as Jira might send them; a save must hand each back unchanged.
    private const string Extension = """{ "type" : "extension", "attrs" : { "extensionKey" : "toc", "extensionType" : "com.atlassian.macro", "text" : "Table of contents" , "parameters": {"note": "café \/ <b>"} } }""";
    private const string InlineExtension = """{"attrs":{"extensionType":"y","extensionKey":"x","text":"inline chip"},"type":"inlineExtension"}""";
    private const string Annotation = """{"type":"annotation","attrs":{"annotationType":"inlineComment","id":"c-1"}}""";

    private static readonly string Raw = $$$"""
        {"version":1,"type":"doc","content":[
          {"type":"paragraph","content":[{"type":"text","text":"Pay with a saved card."}]},
          {{{Extension}}},
          {"type":"paragraph","content":[{"type":"text","text":"Card ","marks":[{{{Annotation}}}]},{{{InlineExtension}}}]},
          {"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"m-uuid-1","collection":"","alt":"shot.png","width":640,"height":360}}]}
        ]}
        """;

    [Fact]
    public async Task The_edit_view_replaces_what_the_editor_cannot_edit_with_tokens_and_describes_them() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);

        using var response = await host.GetAsync(ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var view = JsonNode.Parse(await response.Content.ReadAsStringAsync(JiraHttpHost.Token))!;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("RS-12", (string)view["key"]!);
        Assert.Equal((string)host.Jira.Issues["RS-12"]["fields"]!["updated"]!, (string)view["updated"]!);
        Assert.True((bool)view["editable"]!);
        var content = view["adf"]!["content"]!.AsArray();
        Assert.Equal(["paragraph", "rsOpaque", "paragraph", "mediaSingle"], content.Select(node => (string)node!["type"]!));
        Assert.Equal("rsMark", (string)content[2]!["content"]![0]!["marks"]![0]!["type"]!);
        Assert.Equal("rsOpaqueInline", (string)content[2]!["content"]![1]!["type"]!);
        Assert.Equal(
            JsonNode.Parse("""[{"id":"m-uuid-1","attachmentId":"30001","name":"shot.png","image":true,"w":640,"h":360}]"""),
            view["media"], JsonNode.DeepEquals);
        var opaque = view["opaque"]!.AsArray();
        Assert.Equal(["extension", "inlineExtension"], opaque.Select(part => (string)part!["kind"]!));
        Assert.Equal(["Table of contents", "inline chip"], opaque.Select(part => (string)part!["label"]!));
        Assert.Equal((string)content[1]!["attrs"]!["ref"]!, (string)opaque[0]!["ref"]!);
        Assert.Equal("doc", (string)opaque[0]!["preview"]!["t"]!);
        Assert.Equal("annotation", (string)view["marks"]![0]!["kind"]!);
        Assert.Equal(JsonNode.Parse("""{"nodes":5000,"depth":32,"text":32000,"bytes":1048576}"""), view["limits"], JsonNode.DeepEquals);
    }

    [Fact]
    public async Task A_save_rebuilds_the_document_and_hands_Jira_every_opaque_original_byte_for_byte() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var adf = view["adf"]!.DeepClone();
        adf["content"]![0]!["content"]![0]!["marks"] = JsonNode.Parse("""[{"type":"strong"}]""");
        adf["content"]![2]!["content"]![0]!["text"] = "Edited card ";
        adf["content"]!.AsArray().Add(JsonNode.Parse("""{"type":"panel","attrs":{"panelType":"info"},"content":[{"type":"paragraph","content":[{"type":"text","text":"New panel"}]}]}"""));

        var from = host.Jira.Requests.Count;
        var saved = await host.JsonAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            Body(adf, (string)view["updated"]!));

        var put = Assert.Single(host.JiraRequestsSince(from), r => r.Method == "PUT");
        Assert.Contains(Extension, put.Body, StringComparison.Ordinal);
        Assert.Contains(InlineExtension, put.Body, StringComparison.Ordinal);
        Assert.Contains(Annotation, put.Body, StringComparison.Ordinal);
        Assert.Contains("\"strong\"", put.Body, StringComparison.Ordinal);
        Assert.Contains("Edited card ", put.Body, StringComparison.Ordinal);
        Assert.Contains("\"panelType\":\"info\"", put.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("rsOpaque", put.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("rsMark", put.Body, StringComparison.Ordinal);
        Assert.NotEqual((string)view["updated"]!, (string)saved["updated"]!);
        Assert.Equal(["jira.update RS-12"], await host.AuditAsync(team));
    }

    [Fact]
    public async Task A_save_is_stale_when_Jira_moved_on_or_holds_no_such_token() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var updated = (string)view["updated"]!;
        var from = host.Jira.Requests.Count;

        var forged = view["adf"]!.DeepClone();
        forged["content"]!.AsArray().Add(JsonNode.Parse("""{"type":"rsOpaque","attrs":{"ref":"AAAAAAAAAAAAAAAA"}}"""));
        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(forged, updated));
        Assert.Equal((409, "stale", updated), (status, (string)problem["code"]!, (string)problem["current"]!["updated"]!));

        // Someone edits the issue in Jira after the editor opened.
        host.Jira.Issues["RS-12"]["fields"]!["updated"] = "2026-02-01T10:00:00.000+0000";
        (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(view["adf"]!, updated));
        Assert.Equal((409, "stale", "2026-02-01T10:00:00.000+0000"), (status, (string)problem["code"]!, (string)problem["current"]!["updated"]!));

        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Method == "PUT");
        Assert.Empty(await host.AuditAsync(team));
    }

    [Fact]
    public async Task Invalid_saves_answer_a_code_and_a_path_before_anything_reaches_Jira() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var updated = (string)view["updated"]!;
        var hostile = view["adf"]!.DeepClone();
        hostile["content"]![0]!["content"]![0]!["marks"] = JsonNode.Parse("""[{"type":"link","attrs":{"href":"javascript:alert(document.cookie)"}}]""");
        var from = host.Jira.Requests.Count;

        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(hostile, updated));
        Assert.Equal((400, "invalid"), (status, (string)problem["code"]!));
        Assert.Equal("invalid_href", (string)problem["fields"]!["descriptionAdf"]!);
        Assert.Equal("content.0.content.0.marks.0.attrs.href", (string)problem["fields"]!["descriptionAdf.at"]!);
        Assert.DoesNotContain("javascript", problem.ToJsonString(), StringComparison.Ordinal);

        (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new JsonObject { ["description"] = "markup", ["descriptionAdf"] = view["adf"]!.DeepClone(), ["descriptionBase"] = updated }.ToJsonString());
        Assert.Equal((400, "conflict"), (status, (string)problem["fields"]!["descriptionAdf"]!));

        (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new JsonObject { ["descriptionAdf"] = view["adf"]!.DeepClone() }.ToJsonString());
        Assert.Equal((400, "required"), (status, (string)problem["fields"]!["descriptionBase"]!));

        (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            new JsonObject { ["descriptionAdf"] = new JsonArray(), ["descriptionBase"] = updated }.ToJsonString());
        Assert.Equal((400, "not_doc"), (status, (string)problem["fields"]!["descriptionAdf"]!));

        Assert.DoesNotContain(host.JiraRequestsSince(from), r => r.Method == "PUT");
    }

    [Fact]
    public async Task A_body_over_the_route_limit_is_refused() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var text = new string('x', (int)Server.Endpoints.JiraEndpoints.MaxPatchBytes);
        var body = $$$"""{"descriptionBase":"x","descriptionAdf":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}]}}""";

        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), body);

        Assert.Equal((413, "too_large"), (status, (string)problem["code"]!));
    }

    [Fact]
    public async Task Null_clears_the_description() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var from = host.Jira.Requests.Count;

        await host.JsonAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            $$"""{"descriptionAdf":null,"descriptionBase":"{{(string)view["updated"]!}}"}""");

        var put = Assert.Single(host.JiraRequestsSince(from), r => r.Method == "PUT");
        Assert.Contains("""{"type":"doc","version":1,"content":[]}""", put.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_that_is_not_an_ADF_document_has_no_editable_view() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var issue = host.Jira.AddIssue("RS-12", "Saved card");
        issue["fields"]!["description"] = new JsonArray(new JsonObject { ["type"] = "paragraph" });

        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        Assert.False((bool)view["editable"]!);
        Assert.Null(view["adf"]);

        var (status, problem) = await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"),
            Body(JsonNode.Parse("""{"type":"doc","version":1,"content":[]}""")!, (string)view["updated"]!));
        Assert.Equal((409, "stale"), (status, (string)problem["code"]!));
    }

    [Fact]
    public async Task Only_members_who_may_edit_get_the_edit_view_or_save() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (member, team) = await BrowseTests.TeamAsync(host, membersCanEdit: false);
        var outsider = await host.UserAsync("Eve");
        host.Jira.AddIssue("RS-12", "Saved card");
        var adf = JsonNode.Parse("""{"type":"doc","version":1,"content":[]}""")!;
        var from = host.Jira.Requests.Count;

        var (status, problem) = await host.ProblemAsync(HttpMethod.Get, member, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        Assert.Equal((403, "forbidden"), (status, (string)problem["code"]!));
        (status, _) = await host.ProblemAsync(HttpMethod.Patch, member, JiraHttpHost.Url(team, "/issues/RS-12"), Body(adf, "x"));
        Assert.Equal(403, status);
        (status, _) = await host.ProblemAsync(HttpMethod.Get, outsider, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        Assert.Equal(404, status);
        (status, _) = await host.ProblemAsync(HttpMethod.Patch, outsider, JiraHttpHost.Url(team, "/issues/RS-12"), Body(adf, "x"));
        Assert.Equal(404, status);

        Assert.Empty(host.JiraRequestsSince(from));
    }

    [Fact]
    public async Task No_log_line_carries_description_text() {
        await using var host = await JiraHttpHost.StartAsync(postgres);
        var (ada, team) = await IssueAsync(host);
        var view = await host.JsonAsync(HttpMethod.Get, ada, JiraHttpHost.Url(team, "/issues/RS-12/description"));
        var updated = (string)view["updated"]!;
        var edited = view["adf"]!.DeepClone();
        edited["content"]![0]!["content"]![0]!["text"] = "Confidential roadmap line";
        var hostile = view["adf"]!.DeepClone();
        hostile["content"]![0]!["content"]![0]!["marks"] = JsonNode.Parse("""[{"type":"textColor","attrs":{"color":"Confidential colour"}}]""");

        await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(hostile, updated));
        await host.JsonAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(edited, updated));
        await host.ProblemAsync(HttpMethod.Patch, ada, JiraHttpHost.Url(team, "/issues/RS-12"), Body(edited, updated));

        Assert.NotEmpty(host.Logs);
        foreach (var secret in (string[])["Confidential", "saved card", "Table of contents", "inline chip", "shot.png"]) {
            Assert.DoesNotContain(host.Logs, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task<(Principal Ada, TeamId Team)> IssueAsync(JiraHttpHost host) {
        var (ada, team) = await BrowseTests.TeamAsync(host);
        var issue = host.Jira.AddIssue("RS-12", "Saved card");
        issue["fields"]!["description"] = JsonNode.Parse(Raw);
        host.Jira.RawDescriptions["RS-12"] = Raw;
        host.Jira.AddAttachment("RS-12", "30001", "shot.png", "image/png", [0x89, 0x50, 0x4E, 0x47]);
        return (ada, team);
    }

    private static string Body(JsonNode adf, string descriptionBase) =>
        new JsonObject { ["descriptionAdf"] = adf.DeepClone(), ["descriptionBase"] = descriptionBase }.ToJsonString();
}
