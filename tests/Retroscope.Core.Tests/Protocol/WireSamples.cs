using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Protocol;

/// <summary>
/// Builds sample JSON for any wire type from its source-generated metadata, so every DTO (including ones
/// added later) is covered without hand-written fixtures.
/// </summary>
internal static class WireSamples {
    public static JsonTypeInfo TypeInfo(Type type) {
        return WireJsonContext.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException($"{type.Name} is not registered in WireJsonContext");
    }

    public static bool IsRequired(JsonPropertyInfo property) {
        return property.IsRequired || property.AssociatedParameter is { HasDefaultValue: false };
    }

    /// <param name="full">Every member when true; only the required ones when false.</param>
    public static string Json(Type type, bool full, int seed = 1) {
        var builder = new StringBuilder();
        Append(builder, type, full, seed, depth: 0);
        return builder.ToString();
    }

    public static object Payload(Type type, bool full, int seed = 1) {
        return JsonSerializer.Deserialize(Json(type, full, seed), TypeInfo(type))
            ?? throw new InvalidOperationException($"{type.Name} sample deserialized to null");
    }

    /// <summary>Re-emits the JSON with the members of every object in a seeded random order.</summary>
    public static string Shuffle(string json, int seed) {
        var random = new Random(seed);
        return Shuffle(JsonNode.Parse(json), random)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Shuffle(JsonNode? node, Random random) {
        switch (node) {
            case JsonObject obj: {
                var members = obj.Select(x => (x.Key, Value: Shuffle(x.Value?.DeepClone(), random))).ToArray();
                random.Shuffle(members);
                var shuffled = new JsonObject();
                foreach (var (key, value) in members) {
                    shuffled[key] = value;
                }
                return shuffled;
            }
            case JsonArray array:
                return new JsonArray(array.Select(x => Shuffle(x?.DeepClone(), random)).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static void Append(StringBuilder builder, Type type, bool full, int seed, int depth) {
        if (depth > 8) {
            throw new InvalidOperationException($"{type.Name} nests too deeply for a sample");
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Leaf(type, seed) is { } leaf) {
            builder.Append(leaf);
            return;
        }
        var info = TypeInfo(type);
        switch (info.Kind) {
            case JsonTypeInfoKind.Enumerable:
                builder.Append('[');
                Append(builder, info.ElementType!, full, seed + 1, depth + 1);
                builder.Append(',');
                Append(builder, info.ElementType!, full, seed + 2, depth + 1);
                builder.Append(']');
                return;
            case JsonTypeInfoKind.Dictionary:
                builder.Append("{\"3\":");
                Append(builder, info.ElementType!, full, seed + 1, depth + 1);
                builder.Append('}');
                return;
            case JsonTypeInfoKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in info.Properties) {
                    if (!full && !IsRequired(property)) {
                        continue;
                    }
                    if (!first) {
                        builder.Append(',');
                    }
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name, WireJsonContext.Default.String)).Append(':');
                    Append(builder, property.PropertyType, full, seed * 31 + property.Name.Length, depth + 1);
                }
                builder.Append('}');
                return;
            default:
                throw new InvalidOperationException($"No sample for {type.Name}");
        }
    }

    private static string? Leaf(Type type, int seed) {
        var n = Math.Abs(seed % 1000) + 1;
        if (type == typeof(string)) {
            return $"\"s{n} \\u00e9 \\\"q\\\"\"";
        }
        if (type == typeof(bool)) {
            return n % 2 == 0 ? "true" : "false";
        }
        if (type == typeof(short) || type == typeof(int) || type == typeof(long)) {
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (type == typeof(decimal)) {
            return $"{n}.5";
        }
        if (type == typeof(JsonElement)) {
            return "{\"current\":{\"version\":2}}";
        }
        if (type == typeof(RoomKey)) {
            return $"\"board:{GuidFor(seed)}\"";
        }
        if (type == typeof(Epoch)) {
            return $"\"{new Epoch((ulong)n * 0x9E3779B97F4A7C15UL)}\"";
        }
        if (type.GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IGuidId<>))) {
            return $"\"{GuidFor(seed)}\"";
        }
        if (type.IsEnum) {
            var values = Enum.GetValues(type);
            return JsonSerializer.Serialize(values.GetValue(n % values.Length), TypeInfo(type));
        }
        return null;
    }

    private static Guid GuidFor(int seed) {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new Guid(bytes);
    }
}
