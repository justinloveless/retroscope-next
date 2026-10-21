using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdminUserPatch))]
[JsonSerializable(typeof(AdminMemberRequest))]
[JsonSerializable(typeof(AdminUser))]
[JsonSerializable(typeof(IReadOnlyList<AdminUser>))]
[JsonSerializable(typeof(IReadOnlyList<AdminTeam>))]
[JsonSerializable(typeof(AdminStats))]
public sealed partial class AdminJsonContext : JsonSerializerContext {
}
