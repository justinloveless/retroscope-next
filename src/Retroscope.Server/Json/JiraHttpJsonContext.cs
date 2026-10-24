using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

// Nulls are written, as the web fake server's Jira shapes have them (a row's "points": null means "no points").
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JiraConfigRequest))]
[JsonSerializable(typeof(JiraTestRequest))]
[JsonSerializable(typeof(JiraMetaRequest))]
[JsonSerializable(typeof(JiraIssuePatch))]
[JsonSerializable(typeof(JiraTransitionRequest))]
[JsonSerializable(typeof(JiraPointsRequest))]
[JsonSerializable(typeof(JiraSprintRequest))]
[JsonSerializable(typeof(JiraCommentRequest))]
[JsonSerializable(typeof(JiraLinkCreateRequest))]
[JsonSerializable(typeof(JiraCreateRequest))]
[JsonSerializable(typeof(JiraConfigView))]
[JsonSerializable(typeof(JiraTestView))]
[JsonSerializable(typeof(JiraBrowseView))]
[JsonSerializable(typeof(JiraMetaView))]
[JsonSerializable(typeof(JiraSprintsView))]
[JsonSerializable(typeof(JiraIssueView))]
[JsonSerializable(typeof(JiraDescriptionEditView))]
[JsonSerializable(typeof(JiraCommentPageView))]
[JsonSerializable(typeof(JiraCommentView))]
[JsonSerializable(typeof(JiraOptionsView))]
[JsonSerializable(typeof(JiraAssignableView))]
[JsonSerializable(typeof(JiraPointsView))]
[JsonSerializable(typeof(JiraLinkView))]
[JsonSerializable(typeof(JiraCreatedView))]
[JsonSerializable(typeof(JiraProblemBody))]
public sealed partial class JiraHttpJsonContext : JsonSerializerContext {
}
