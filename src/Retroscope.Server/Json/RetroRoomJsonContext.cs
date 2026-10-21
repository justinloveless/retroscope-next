using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Server.Json;

// Reads documents this module does not fully own, so unknown members are skipped rather than refused.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Placeholder until the module adds its own types: the generator emits nothing for an empty context.
[JsonSerializable(typeof(JsonElement))]
public sealed partial class RetroRoomJsonContext : JsonSerializerContext {
}
