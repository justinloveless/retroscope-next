using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Jira;

namespace Retroscope.Server.Json;

// Reads documents Jira writes, so unknown members are skipped rather than refused.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JiraErrorBody))]
[JsonSerializable(typeof(JiraUser))]
[JsonSerializable(typeof(IReadOnlyList<JiraUser>))]
[JsonSerializable(typeof(IReadOnlyList<JiraField>))]
[JsonSerializable(typeof(JiraBoard))]
[JsonSerializable(typeof(JiraValuesPage<JiraBoard>))]
[JsonSerializable(typeof(JiraValuesPage<JiraSprint>))]
[JsonSerializable(typeof(JiraIssue))]
[JsonSerializable(typeof(JiraIssuePage))]
[JsonSerializable(typeof(JiraSearchPage))]
[JsonSerializable(typeof(JiraCommentPage))]
[JsonSerializable(typeof(JiraEditMeta))]
[JsonSerializable(typeof(JiraTransitions))]
[JsonSerializable(typeof(JiraProject))]
[JsonSerializable(typeof(JiraLinkTypes))]
[JsonSerializable(typeof(JiraCreateMetaIssueTypes))]
[JsonSerializable(typeof(JiraCreatedIssue))]
[JsonSerializable(typeof(JiraCreatedComment))]
[JsonSerializable(typeof(JiraSearchRequest))]
[JsonSerializable(typeof(JiraTransitionRequest))]
[JsonSerializable(typeof(JiraMoveIssuesRequest))]
[JsonSerializable(typeof(JiraLinkRequest))]
[JsonSerializable(typeof(JiraCommentRequest))]
public sealed partial class JiraClientJsonContext : JsonSerializerContext {
}
