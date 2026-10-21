using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Core.Ids;

/// <summary>
/// Writes an id as its bare canonical UUID string (lower-case, hyphenated), both as a value and as a
/// dictionary key. Reading accepts only that 36-character form, in either case.
/// </summary>
public sealed class GuidIdConverter<T> : JsonConverter<T> where T : struct, IGuidId<T> {
    private const int Length = 36;
    private const int MaxEscapedLength = 6 * Length;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException($"Expected a UUID string for {typeof(T).Name}.");
        }
        return T.From(ParseGuid(ref reader));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) {
        Span<byte> buffer = stackalloc byte[Length];
        Utf8Formatter.TryFormat(value.Value, buffer, out _, 'D');
        writer.WriteStringValue(buffer);
    }

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return T.From(ParseGuid(ref reader));
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) {
        Span<byte> buffer = stackalloc byte[Length];
        Utf8Formatter.TryFormat(value.Value, buffer, out _, 'D');
        writer.WritePropertyName(buffer);
    }

    internal static Guid ParseGuid(ref Utf8JsonReader reader) {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped) {
            return Parse(reader.ValueSpan);
        }
        var escapedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (escapedLength > MaxEscapedLength) {
            throw new JsonException($"Expected a 36-character UUID for {typeof(T).Name}.");
        }
        Span<byte> buffer = stackalloc byte[MaxEscapedLength];
        var written = reader.CopyString(buffer);
        return Parse(buffer[..written]);
    }

    private static Guid Parse(ReadOnlySpan<byte> utf8) {
        if (utf8.Length != Length || !Utf8Parser.TryParse(utf8, out Guid value, out var consumed, 'D') || consumed != Length) {
            throw new JsonException($"Expected a 36-character UUID for {typeof(T).Name}.");
        }
        return value;
    }
}
