using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Core.Protocol;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateTeamRequest))]
[JsonSerializable(typeof(TeamPatchRequest))]
[JsonSerializable(typeof(DeleteTeamRequest))]
[JsonSerializable(typeof(MemberRoleRequest))]
[JsonSerializable(typeof(TransferOwnershipRequest))]
[JsonSerializable(typeof(InviteRequest))]
[JsonSerializable(typeof(InviteTokenRequest))]
[JsonSerializable(typeof(TemplateRequest))]
[JsonSerializable(typeof(EndorsementTypeRequest))]
[JsonSerializable(typeof(IReadOnlyList<TeamSummary>))]
[JsonSerializable(typeof(TeamCreated))]
[JsonSerializable(typeof(TeamResponse))]
[JsonSerializable(typeof(IReadOnlyList<MemberInfo>))]
[JsonSerializable(typeof(ActionItemsPage))]
[JsonSerializable(typeof(InvitesPage))]
[JsonSerializable(typeof(InviteInfo))]
[JsonSerializable(typeof(InvitePreview))]
[JsonSerializable(typeof(InviteAccepted))]
[JsonSerializable(typeof(TemplatesPage))]
[JsonSerializable(typeof(TemplateInfo))]
[JsonSerializable(typeof(EndorsementTypesPage))]
[JsonSerializable(typeof(EndorsementTypeInfo))]
[JsonSerializable(typeof(Leaderboard))]
[JsonSerializable(typeof(IReadOnlyList<ReceivedEndorsement>))]
public sealed partial class TeamHttpJsonContext : JsonSerializerContext {
}
