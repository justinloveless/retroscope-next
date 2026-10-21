using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Retroscope.Core.Protocol;

/// <summary>
/// Writes frames with <c>t</c> first. The kind of every payload comes from <see cref="Catalog"/>, so a type
/// missing from the catalog fails loudly instead of reaching the wire without a kind.
/// </summary>
public static class WireWriter {
    private static readonly WireJsonContext Json = WireJsonContext.Default;

    public static byte[] ToUtf8(ClientFrame frame) {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) {
            Write(writer, frame);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] ToUtf8(ServerFrame frame) {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) {
            Write(writer, frame);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static void Write(Utf8JsonWriter writer, ClientFrame frame) {
        writer.WriteStartObject();
        switch (frame) {
            case HelloFrame hello:
                writer.WriteString("t"u8, "hello"u8);
                writer.WriteNumber("v"u8, hello.V);
                Member(writer, "cid"u8, hello.Cid, Json.ClientId);
                writer.WritePropertyName("subs"u8);
                JsonSerializer.Serialize(writer, hello.Subs, Json.IReadOnlyListSubCursor);
                break;
            case SubFrame sub:
                writer.WriteString("t"u8, "sub"u8);
                Member(writer, "r"u8, sub.R, Json.RoomKey);
                if (sub.E is { } epoch) {
                    Member(writer, "e"u8, epoch, Json.Epoch);
                }
                if (sub.S is { } seq) {
                    writer.WriteNumber("s"u8, seq);
                }
                break;
            case UnsubFrame unsub:
                writer.WriteString("t"u8, "unsub"u8);
                Member(writer, "r"u8, unsub.R, Json.RoomKey);
                break;
            case CmdFrame cmd: {
                var entry = Catalog.EntryOf(cmd.D);
                writer.WriteString("t"u8, "cmd"u8);
                Member(writer, "r"u8, cmd.R, Json.RoomKey);
                writer.WriteNumber("q"u8, cmd.Q);
                writer.WriteString("k"u8, entry.Kind);
                writer.WritePropertyName("d"u8);
                JsonSerializer.Serialize(writer, cmd.D, entry.TypeInfo);
                break;
            }
            case PingFrame ping:
                writer.WriteString("t"u8, "ping"u8);
                writer.WriteNumber("c"u8, ping.C);
                break;
            default:
                throw new ArgumentException($"Unknown client frame {frame.GetType().Name}.", nameof(frame));
        }
        writer.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter writer, ServerFrame frame) {
        writer.WriteStartObject();
        switch (frame) {
            case WelcomeFrame welcome:
                writer.WriteString("t"u8, "welcome"u8);
                Member(writer, "conn"u8, welcome.Conn, Json.ConnectionId);
                writer.WritePropertyName("me"u8);
                JsonSerializer.Serialize(writer, welcome.Me, Json.PrincipalInfo);
                writer.WriteNumber("n"u8, welcome.N);
                break;
            case SnapFrame snap:
                writer.WriteString("t"u8, "snap"u8);
                Member(writer, "r"u8, snap.R, Json.RoomKey);
                Member(writer, "e"u8, snap.E, Json.Epoch);
                writer.WriteNumber("s"u8, snap.S);
                writer.WriteNumber("lastQ"u8, snap.LastQ);
                writer.WritePropertyName("d"u8);
                JsonSerializer.Serialize(writer, snap.D, Catalog.SnapshotTypeInfo(snap.R.Kind));
                if (snap.O is { } overlay) {
                    var overlayType = Catalog.OverlayTypeInfo(snap.R.Kind)
                        ?? throw new ArgumentException("This room has no overlay.", nameof(frame));
                    writer.WritePropertyName("o"u8);
                    JsonSerializer.Serialize(writer, overlay, overlayType);
                }
                break;
            case ReplayFrame replay:
                writer.WriteString("t"u8, "replay"u8);
                Member(writer, "r"u8, replay.R, Json.RoomKey);
                Member(writer, "e"u8, replay.E, Json.Epoch);
                writer.WriteNumber("from"u8, replay.From);
                writer.WriteNumber("lastQ"u8, replay.LastQ);
                writer.WriteStartArray("txs"u8);
                foreach (var tx in replay.Txs) {
                    writer.WriteStartObject();
                    writer.WriteNumber("s"u8, tx.S);
                    WriteEvents(writer, tx.Ev);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case TxFrame tx:
                writer.WriteString("t"u8, "tx"u8);
                Member(writer, "r"u8, tx.R, Json.RoomKey);
                writer.WriteNumber("s"u8, tx.S);
                WriteEvents(writer, tx.Ev);
                break;
            case AckFrame ack:
                writer.WriteString("t"u8, "ack"u8);
                Member(writer, "r"u8, ack.R, Json.RoomKey);
                writer.WriteNumber("q"u8, ack.Q);
                writer.WriteNumber("s"u8, ack.S);
                if (ack.Dup) {
                    writer.WriteBoolean("dup"u8, true);
                }
                break;
            case NackFrame nack:
                writer.WriteString("t"u8, "nack"u8);
                Member(writer, "r"u8, nack.R, Json.RoomKey);
                writer.WriteNumber("q"u8, nack.Q);
                Member(writer, "code"u8, nack.Code, Json.NackCode);
                writer.WriteBoolean("retry"u8, nack.Retry);
                if (nack.Msg is { } msg) {
                    writer.WriteString("msg"u8, msg);
                }
                if (nack.D is { } data) {
                    writer.WritePropertyName("d"u8);
                    data.WriteTo(writer);
                }
                break;
            case XFrame x: {
                var entry = Catalog.EntryOf(x.D);
                writer.WriteString("t"u8, "x"u8);
                Member(writer, "r"u8, x.R, Json.RoomKey);
                writer.WriteString("k"u8, entry.Kind);
                writer.WritePropertyName("d"u8);
                JsonSerializer.Serialize(writer, x.D, entry.TypeInfo);
                break;
            }
            case PongFrame pong:
                writer.WriteString("t"u8, "pong"u8);
                writer.WriteNumber("c"u8, pong.C);
                writer.WriteNumber("n"u8, pong.N);
                break;
            case KickFrame kick:
                writer.WriteString("t"u8, "kick"u8);
                Member(writer, "r"u8, kick.R, Json.RoomKey);
                Member(writer, "code"u8, kick.Code, Json.KickCode);
                break;
            case ByeFrame bye:
                writer.WriteString("t"u8, "bye"u8);
                Member(writer, "code"u8, bye.Code, Json.ByeCode);
                writer.WriteNumber("retryMs"u8, bye.RetryMs);
                break;
            default:
                throw new ArgumentException($"Unknown server frame {frame.GetType().Name}.", nameof(frame));
        }
        writer.WriteEndObject();
    }

    /// <summary>Writes <c>"ev":[{"k":…,"d":…},…]</c>.</summary>
    public static void WriteEvents(Utf8JsonWriter writer, IReadOnlyList<WireEvent> events) {
        writer.WriteStartArray("ev"u8);
        foreach (var evt in events) {
            WriteEvent(writer, evt);
        }
        writer.WriteEndArray();
    }

    /// <summary>Writes one <c>{"k":…,"d":…}</c> entry.</summary>
    public static void WriteEvent(Utf8JsonWriter writer, WireEvent evt) {
        var entry = Catalog.EntryOf(evt);
        writer.WriteStartObject();
        writer.WriteString("k"u8, entry.Kind);
        writer.WritePropertyName("d"u8);
        JsonSerializer.Serialize(writer, evt, entry.TypeInfo);
        writer.WriteEndObject();
    }

    private static void Member<T>(Utf8JsonWriter writer, ReadOnlySpan<byte> name, T value, JsonTypeInfo<T> typeInfo) {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, typeInfo);
    }
}
