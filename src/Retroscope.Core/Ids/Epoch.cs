using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Core.Ids;

/// <summary>
/// 64 random bits per actor incarnation, written on the wire as 16 lower-case hex digits
/// (a JSON number would lose precision in the browser).
/// </summary>
[JsonConverter(typeof(EpochConverter))]
public readonly record struct Epoch(ulong Value) {
    public static Epoch NewRandom() {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return new Epoch(BitConverter.ToUInt64(bytes));
    }

    public static bool TryParse(ReadOnlySpan<char> text, out Epoch epoch) {
        epoch = default;
        if (text.Length != 16 || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) {
            return false;
        }
        epoch = new Epoch(value);
        return true;
    }

    public override string ToString() => Value.ToString("x16", CultureInfo.InvariantCulture);
}

public sealed class EpochConverter : JsonConverter<Epoch> {
    public override Epoch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String || !Epoch.TryParse(reader.GetString(), out var epoch)) {
            throw new JsonException("Expected an epoch of 16 hex digits.");
        }
        return epoch;
    }

    public override void Write(Utf8JsonWriter writer, Epoch value, JsonSerializerOptions options) {
        Span<byte> buffer = stackalloc byte[16];
        Utf8Formatter.TryFormat(value.Value, buffer, out _, new System.Buffers.StandardFormat('x', 16));
        writer.WriteStringValue(buffer);
    }
}
