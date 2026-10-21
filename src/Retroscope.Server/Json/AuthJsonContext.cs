using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignupRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(GuestRequest))]
[JsonSerializable(typeof(GuestResponse))]
[JsonSerializable(typeof(ProfileRequest))]
[JsonSerializable(typeof(PasswordRequest))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(IReadOnlyList<SessionInfo>))]
public sealed partial class AuthJsonContext : JsonSerializerContext {
}
