using System.Buffers;
using System.Text.Json;

namespace Retroscope.Core.Poker;

/// <summary>Builds a nack's <c>d</c> without a serializer context (the shapes are per command, §c.6).</summary>
internal static class NackData {
    /// <summary><c>{"current":{…}}</c> for <c>stale</c>; <paramref name="write"/> writes the members.</summary>
    public static JsonElement Current(Action<Utf8JsonWriter> write) {
        return Build(writer => {
            writer.WriteStartObject("current");
            write(writer);
            writer.WriteEndObject();
        });
    }

    public static JsonElement Build(Action<Utf8JsonWriter> write) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
