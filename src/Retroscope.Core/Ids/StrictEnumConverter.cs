using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Core.Ids;

/// <summary>
/// Reads and writes an enum by its <see cref="JsonStringEnumMemberNameAttribute"/> name only.
/// <see cref="JsonStringEnumConverter{TEnum}"/> also accepts integers, numeric strings and names in any case,
/// which would let values outside the enum reach a decider.
/// </summary>
public sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum {
    private static readonly T[] Values = Enum.GetValues<T>();
    private static readonly JsonEncodedText[] Names = Values.Select(WireName).ToArray();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.String) {
            for (var i = 0; i < Names.Length; i++) {
                if (reader.ValueTextEquals(Names[i].EncodedUtf8Bytes)) {
                    return Values[i];
                }
            }
        }
        throw new JsonException($"Expected one of the {typeof(T).Name} names.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) {
        var i = Array.IndexOf(Values, value);
        if (i < 0) {
            throw new JsonException($"{typeof(T).Name} has no member with this value.");
        }
        writer.WriteStringValue(Names[i]);
    }

    private static JsonEncodedText WireName(T value) {
        var options = new JsonSerializerOptions();
        var inner = (JsonConverter<T>)new JsonStringEnumConverter<T>().CreateConverter(typeof(T), options)!;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            inner.Write(writer, value, options);
        }
        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        reader.Read();
        return JsonEncodedText.Encode(reader.GetString()!);
    }
}
