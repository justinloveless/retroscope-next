using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Auth;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

// Reads documents the identity provider writes, so unknown members are skipped rather than refused. The link request
// refuses them through the attribute on its type.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OidcFlow))]
[JsonSerializable(typeof(OidcTokenResponse))]
[JsonSerializable(typeof(IdTokenClaims))]
[JsonSerializable(typeof(OidcLinkRequest))]
[JsonSerializable(typeof(OidcLinkResponse))]
[JsonSerializable(typeof(string[]))]
public sealed partial class OidcJsonContext : JsonSerializerContext {
}
