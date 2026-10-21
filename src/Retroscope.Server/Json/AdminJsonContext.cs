using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Placeholder until the module adds its own types: the generator emits nothing for an empty context.
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AdminJsonContext : JsonSerializerContext {
}
