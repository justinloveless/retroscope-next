using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProblemBody))]
public sealed partial class HostingJsonContext : JsonSerializerContext {
}
