using System.Buffers;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// Builds server frames from pieces serialized once and shared: a tx frame around an audience's <c>ev</c> array, a
/// replay around ring entries, a snapshot around the shared public document plus the viewer's overlay (R11).
/// The output is byte-for-byte what <see cref="WireWriter"/> writes for the same frame.
/// </summary>
public static class FrameBytes {
    public static byte[] Frame(ServerFrame frame) => WireWriter.ToUtf8(frame);

    /// <summary>The <c>ev</c> array of a tx: <c>[{"k":…,"d":…},…]</c>.</summary>
    public static byte[] Events(IReadOnlyList<WireEvent> events) {
        return Write(writer => {
            writer.WriteStartArray();
            foreach (var evt in events) {
                WireWriter.WriteEvent(writer, evt);
            }
            writer.WriteEndArray();
        });
    }

    public static byte[] SharedSnapshot(RoomKind kind, RoomSnapshot snapshot) {
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, Catalog.SnapshotTypeInfo(kind));
    }

    public static byte[] Tx(RoomKey room, long seq, byte[] events) {
        return Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("t"u8, "tx"u8);
            writer.WriteString("r"u8, room.ToString());
            writer.WriteNumber("s"u8, seq);
            writer.WritePropertyName("ev"u8);
            writer.WriteRawValue(events, skipInputValidation: true);
            writer.WriteEndObject();
        });
    }

    public static byte[] Replay(RoomKey room, Epoch epoch, long from, long lastQ, IReadOnlyList<(long Seq, byte[] Events)> txs) {
        return Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("t"u8, "replay"u8);
            writer.WriteString("r"u8, room.ToString());
            writer.WriteString("e"u8, epoch.ToString());
            writer.WriteNumber("from"u8, from);
            writer.WriteNumber("lastQ"u8, lastQ);
            writer.WriteStartArray("txs"u8);
            foreach (var (seq, events) in txs) {
                writer.WriteStartObject();
                writer.WriteNumber("s"u8, seq);
                writer.WritePropertyName("ev"u8);
                writer.WriteRawValue(events, skipInputValidation: true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static byte[] Snap(RoomKey room, Epoch epoch, long seq, long lastQ, byte[] shared, RoomOverlay? overlay) {
        return Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("t"u8, "snap"u8);
            writer.WriteString("r"u8, room.ToString());
            writer.WriteString("e"u8, epoch.ToString());
            writer.WriteNumber("s"u8, seq);
            writer.WriteNumber("lastQ"u8, lastQ);
            writer.WritePropertyName("d"u8);
            writer.WriteRawValue(shared, skipInputValidation: true);
            if (overlay is not null) {
                var overlayType = Catalog.OverlayTypeInfo(room.Kind)
                    ?? throw new ArgumentException($"{room.Kind} rooms have no overlay.", nameof(overlay));
                writer.WritePropertyName("o"u8);
                JsonSerializer.Serialize(writer, overlay, overlayType);
            }
            writer.WriteEndObject();
        });
    }

    private static byte[] Write(Action<Utf8JsonWriter> write) {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }
}
