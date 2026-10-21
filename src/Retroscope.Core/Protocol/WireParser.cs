using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

public enum FrameError : byte {
    None,

    /// <summary>Over <see cref="WireParser.MaxFrameBytes"/>; the transport closes with 4413.</summary>
    TooLarge,

    /// <summary>Not a JSON object, a duplicate, missing or unknown envelope member, or a bad envelope value.</summary>
    Malformed,

    /// <summary>The <c>t</c> member names no frame of this direction.</summary>
    UnknownType,

    /// <summary>The <c>k</c> member names no catalog kind of this direction.</summary>
    UnknownKind,

    /// <summary>The kind exists but does not belong to the room in <c>r</c>.</summary>
    WrongRoom,

    /// <summary><c>d</c> does not match the payload of its kind.</summary>
    InvalidData,
}

/// <param name="Room">For a rejected <c>cmd</c>, the room it named (when readable), so the caller can nack it.</param>
/// <param name="Q">For a rejected <c>cmd</c>, its sequence number (when readable).</param>
/// <param name="Message">Names the failing member or kind; never echoes payload values (S33).</param>
public readonly record struct ParseResult<TFrame>(
    TFrame? Frame,
    FrameError Error = FrameError.None,
    string? Message = null,
    RoomKey? Room = null,
    long? Q = null) where TFrame : class {
    public bool Ok => Error == FrameError.None;
}

/// <summary>
/// The two-phase frame parser (DESIGN §a). Phase one walks the envelope with <see cref="Utf8JsonReader"/>
/// in any property order and records each member's raw slice; phase two deserializes <c>d</c> with the
/// <see cref="WireJsonContext"/> type that <see cref="Catalog"/> names for the sibling <c>k</c>.
/// </summary>
public static class WireParser {
    public const int MaxFrameBytes = 64 * 1024;

    private const int FieldCount = 24;

    private static readonly WireJsonContext Json = WireJsonContext.Default;

    public static ParseResult<ClientFrame> ParseClient(ReadOnlySpan<byte> utf8) {
        if (utf8.Length > MaxFrameBytes) {
            return new(null, FrameError.TooLarge, $"frame exceeds {MaxFrameBytes} bytes");
        }
        if (!Envelope.TryRead(utf8, out var env, out var problem)) {
            return new(null, FrameError.Malformed, problem);
        }
        try {
            return ParseClient(utf8, env);
        } catch (JsonException) {
            return new(null, FrameError.Malformed, "an envelope member has the wrong shape");
        } catch (FrameException e) {
            return new(null, e.Error, e.Message);
        }
    }

    public static ParseResult<ServerFrame> ParseServer(ReadOnlySpan<byte> utf8) {
        if (!Envelope.TryRead(utf8, out var env, out var problem)) {
            return new(null, FrameError.Malformed, problem);
        }
        try {
            return ParseServer(utf8, env);
        } catch (JsonException) {
            return new(null, FrameError.Malformed, "a member has the wrong shape");
        } catch (FrameException e) {
            return new(null, e.Error, e.Message);
        }
    }

    private static ParseResult<ClientFrame> ParseClient(ReadOnlySpan<byte> utf8, in Envelope env) {
        switch (env.Type(utf8)) {
            case "hello":
                Require(env.Check(utf8, Field.V | Field.Cid | Field.Subs));
                return new(new HelloFrame(
                    env.Required(utf8, Field.V, Json.Int32),
                    env.Required(utf8, Field.Cid, Json.ClientId),
                    env.RequiredRef(utf8, Field.Subs, Json.IReadOnlyListSubCursor)));
            case "sub":
                Require(env.Check(utf8, Field.R, Field.E | Field.S));
                return new(new SubFrame(
                    env.Required(utf8, Field.R, Json.RoomKey),
                    env.Optional(utf8, Field.E, Json.Epoch),
                    env.Optional(utf8, Field.S, Json.Int64)));
            case "unsub":
                Require(env.Check(utf8, Field.R));
                return new(new UnsubFrame(env.Required(utf8, Field.R, Json.RoomKey)));
            case "cmd":
                return ParseCommand(utf8, env);
            case "ping":
                Require(env.Check(utf8, Field.C));
                return new(new PingFrame(env.Required(utf8, Field.C, Json.Int64)));
            case null:
                return new(null, FrameError.Malformed, "t is missing or not a string");
            default:
                return new(null, FrameError.UnknownType, "t names no client frame");
        }
    }

    private static ParseResult<ClientFrame> ParseCommand(ReadOnlySpan<byte> utf8, in Envelope env) {
        var problem = env.Check(utf8, Field.R | Field.Q | Field.K | Field.D);
        if (problem != null) {
            return new(null, FrameError.Malformed, problem);
        }
        var room = env.Required(utf8, Field.R, Json.RoomKey);
        var q = env.Required(utf8, Field.Q, Json.Int64);
        var kind = env.RequiredRef(utf8, Field.K, Json.String);
        if (!Catalog.TryGetCommand(kind, out var entry)) {
            return new(null, FrameError.UnknownKind, "k names no command", room, q);
        }
        if (!entry.AllowedIn(room.Kind)) {
            return new(null, FrameError.WrongRoom, $"{entry.Kind} is not a {RoomKey.Prefix(room.Kind)} command", room, q);
        }
        var data = env.Slice(utf8, Field.D);
        Command? command;
        try {
            command = JsonSerializer.Deserialize(data, entry.TypeInfo) as Command;
        } catch (JsonException) {
            command = null;
        }
        if (command is null) {
            return new(null, FrameError.InvalidData, $"d does not match {entry.Kind}", room, q);
        }
        return new(new CmdFrame(room, q, command) { DataHash = Fnv1a(data) });
    }

    private static ParseResult<ServerFrame> ParseServer(ReadOnlySpan<byte> utf8, in Envelope env) {
        switch (env.Type(utf8)) {
            case "welcome":
                Require(env.Check(utf8, Field.Conn | Field.Me | Field.N));
                return new(new WelcomeFrame(
                    env.Required(utf8, Field.Conn, Json.ConnectionId),
                    env.RequiredRef(utf8, Field.Me, Json.PrincipalInfo),
                    env.Required(utf8, Field.N, Json.Int64)));
            case "snap": {
                Require(env.Check(utf8, Field.R | Field.E | Field.S | Field.LastQ | Field.D, Field.O));
                var room = env.Required(utf8, Field.R, Json.RoomKey);
                var overlayType = Catalog.OverlayTypeInfo(room.Kind);
                if (env.Has(Field.O) && overlayType is null) {
                    throw new FrameException(FrameError.Malformed, "o is not allowed for this room");
                }
                var snapshot = Deserialize<RoomSnapshot>(env.Slice(utf8, Field.D), Catalog.SnapshotTypeInfo(room.Kind), "d");
                var overlay = env.Has(Field.O) ? Deserialize<RoomOverlay>(env.Slice(utf8, Field.O), overlayType!, "o") : null;
                return new(new SnapFrame(
                    room,
                    env.Required(utf8, Field.E, Json.Epoch),
                    env.Required(utf8, Field.S, Json.Int64),
                    env.Required(utf8, Field.LastQ, Json.Int64),
                    snapshot,
                    overlay));
            }
            case "replay": {
                Require(env.Check(utf8, Field.R | Field.E | Field.From | Field.LastQ | Field.Txs));
                var room = env.Required(utf8, Field.R, Json.RoomKey);
                return new(new ReplayFrame(
                    room,
                    env.Required(utf8, Field.E, Json.Epoch),
                    env.Required(utf8, Field.From, Json.Int64),
                    env.Required(utf8, Field.LastQ, Json.Int64),
                    ReadTxRecords(env.Slice(utf8, Field.Txs), room.Kind)));
            }
            case "tx": {
                Require(env.Check(utf8, Field.R | Field.S | Field.Ev));
                var room = env.Required(utf8, Field.R, Json.RoomKey);
                return new(new TxFrame(
                    room,
                    env.Required(utf8, Field.S, Json.Int64),
                    ReadEvents(env.Slice(utf8, Field.Ev), room.Kind)));
            }
            case "ack":
                Require(env.Check(utf8, Field.R | Field.Q | Field.S, Field.Dup));
                return new(new AckFrame(
                    env.Required(utf8, Field.R, Json.RoomKey),
                    env.Required(utf8, Field.Q, Json.Int64),
                    env.Required(utf8, Field.S, Json.Int64),
                    env.Optional(utf8, Field.Dup, Json.Boolean) ?? false));
            case "nack": {
                Require(env.Check(utf8, Field.R | Field.Q | Field.Code | Field.Retry, Field.Msg | Field.D));
                var code = env.Required(utf8, Field.Code, Json.NackCode);
                if (env.Required(utf8, Field.Retry, Json.Boolean) != code.IsRetryable()) {
                    throw new FrameException(FrameError.Malformed, "retry disagrees with code");
                }
                return new(new NackFrame(
                    env.Required(utf8, Field.R, Json.RoomKey),
                    env.Required(utf8, Field.Q, Json.Int64),
                    code,
                    env.OptionalRef(utf8, Field.Msg, Json.String),
                    env.Has(Field.D) ? env.Required(utf8, Field.D, Json.JsonElement) : null));
            }
            case "x": {
                Require(env.Check(utf8, Field.R | Field.K | Field.D));
                var room = env.Required(utf8, Field.R, Json.RoomKey);
                var kind = env.RequiredRef(utf8, Field.K, Json.String);
                if (!Catalog.TryGetUnsequenced(kind, out var entry)) {
                    throw new FrameException(FrameError.UnknownKind, "k names no unsequenced kind");
                }
                RequireRoom(entry, room.Kind);
                return new(new XFrame(room, Deserialize<Unsequenced>(env.Slice(utf8, Field.D), entry.TypeInfo, "d")));
            }
            case "pong":
                Require(env.Check(utf8, Field.C | Field.N));
                return new(new PongFrame(env.Required(utf8, Field.C, Json.Int64), env.Required(utf8, Field.N, Json.Int64)));
            case "kick":
                Require(env.Check(utf8, Field.R | Field.Code));
                return new(new KickFrame(env.Required(utf8, Field.R, Json.RoomKey), env.Required(utf8, Field.Code, Json.KickCode)));
            case "bye":
                Require(env.Check(utf8, Field.Code | Field.RetryMs));
                return new(new ByeFrame(env.Required(utf8, Field.Code, Json.ByeCode), env.Required(utf8, Field.RetryMs, Json.Int32)));
            case null:
                return new(null, FrameError.Malformed, "t is missing or not a string");
            default:
                return new(null, FrameError.UnknownType, "t names no server frame");
        }
    }

    private static void Require(string? problem) {
        if (problem != null) {
            throw new FrameException(FrameError.Malformed, problem);
        }
    }

    private static void RequireRoom(CatalogEntry entry, RoomKind room) {
        if (!entry.AllowedIn(room)) {
            throw new FrameException(FrameError.WrongRoom, $"{entry.Kind} does not belong to a {RoomKey.Prefix(room)} room");
        }
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> json, JsonTypeInfo typeInfo, string member) where T : class {
        try {
            return JsonSerializer.Deserialize(json, typeInfo) as T
                ?? throw new FrameException(FrameError.InvalidData, $"{member} is null");
        } catch (JsonException) {
            throw new FrameException(FrameError.InvalidData, $"{member} does not match {typeInfo.Type.Name}");
        }
    }

    private static List<TxRecord> ReadTxRecords(ReadOnlySpan<byte> json, RoomKind room) {
        var records = new List<TxRecord>();
        var reader = new Utf8JsonReader(json);
        Expect(ref reader, JsonTokenType.StartArray, "txs");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (reader.TokenType != JsonTokenType.StartObject) {
                throw new FrameException(FrameError.Malformed, "txs entries must be objects");
            }
            long? seq = null;
            List<WireEvent>? events = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                if (reader.ValueTextEquals("s"u8) && seq is null) {
                    reader.Read();
                    seq = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s)
                        ? s
                        : throw new FrameException(FrameError.Malformed, "txs[].s must be an integer");
                } else if (reader.ValueTextEquals("ev"u8) && events is null) {
                    reader.Read();
                    var start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    events = ReadEvents(json[start..(int)reader.BytesConsumed], room);
                } else {
                    throw new FrameException(FrameError.Malformed, "txs entries hold exactly s and ev");
                }
            }
            if (seq is null || events is null) {
                throw new FrameException(FrameError.Malformed, "txs entries hold exactly s and ev");
            }
            records.Add(new TxRecord(seq.Value, events));
        }
        return records;
    }

    private static List<WireEvent> ReadEvents(ReadOnlySpan<byte> json, RoomKind room) {
        var events = new List<WireEvent>();
        var reader = new Utf8JsonReader(json);
        Expect(ref reader, JsonTokenType.StartArray, "ev");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (reader.TokenType != JsonTokenType.StartObject) {
                throw new FrameException(FrameError.Malformed, "ev entries must be objects");
            }
            string? kind = null;
            (int Start, int End)? data = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                if (reader.ValueTextEquals("k"u8) && kind is null) {
                    reader.Read();
                    kind = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new FrameException(FrameError.Malformed, "ev[].k must be a string");
                } else if (reader.ValueTextEquals("d"u8) && data is null) {
                    reader.Read();
                    var start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    data = (start, (int)reader.BytesConsumed);
                } else {
                    throw new FrameException(FrameError.Malformed, "ev entries hold exactly k and d");
                }
            }
            if (kind is null || data is null) {
                throw new FrameException(FrameError.Malformed, "ev entries hold exactly k and d");
            }
            if (!Catalog.TryGetEvent(kind, out var entry)) {
                throw new FrameException(FrameError.UnknownKind, "ev[].k names no event");
            }
            RequireRoom(entry, room);
            events.Add(Deserialize<WireEvent>(json[data.Value.Start..data.Value.End], entry.TypeInfo, $"d of {entry.Kind}"));
        }
        return events;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token, string member) {
        if (!reader.Read() || reader.TokenType != token) {
            throw new FrameException(FrameError.Malformed, $"{member} has the wrong shape");
        }
    }

    private static uint Fnv1a(ReadOnlySpan<byte> data) {
        var hash = 2166136261u;
        foreach (var b in data) {
            hash = (hash ^ b) * 16777619u;
        }
        return hash;
    }

    private sealed class FrameException(FrameError error, string message) : Exception(message) {
        public FrameError Error { get; } = error;
    }

    [Flags]
    private enum Field : uint {
        None = 0,
        T = 1u << 0,
        V = 1u << 1,
        Cid = 1u << 2,
        Subs = 1u << 3,
        R = 1u << 4,
        E = 1u << 5,
        S = 1u << 6,
        Q = 1u << 7,
        K = 1u << 8,
        D = 1u << 9,
        C = 1u << 10,
        Conn = 1u << 11,
        Me = 1u << 12,
        N = 1u << 13,
        LastQ = 1u << 14,
        O = 1u << 15,
        From = 1u << 16,
        Txs = 1u << 17,
        Ev = 1u << 18,
        Dup = 1u << 19,
        Code = 1u << 20,
        Retry = 1u << 21,
        Msg = 1u << 22,
        RetryMs = 1u << 23,
    }

    [InlineArray(FieldCount)]
    private struct Slots {
        private (int Start, int End) _element;
    }

    /// <summary>Phase one: the raw slice of every top-level member, keyed by its envelope field.</summary>
    private struct Envelope {
        private Slots _slots;
        private Field _present;

        public static bool TryRead(ReadOnlySpan<byte> utf8, out Envelope env, out string? problem) {
            env = default;
            problem = null;
            try {
                var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) {
                    problem = "a frame is one JSON object";
                    return false;
                }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                    var field = FieldOf(ref reader);
                    if (field == Field.None) {
                        problem = "unknown envelope member";
                        return false;
                    }
                    if ((env._present & field) != 0) {
                        problem = "duplicate envelope member";
                        return false;
                    }
                    reader.Read();
                    var start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    env._present |= field;
                    env._slots[IndexOf(field)] = (start, (int)reader.BytesConsumed);
                }
                if (reader.Read()) {
                    problem = "trailing content after the frame";
                    return false;
                }
                return true;
            } catch (JsonException) {
                problem = "not valid JSON";
                return false;
            }
        }

        public readonly bool Has(Field field) => (_present & field) != 0;

        public readonly ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, Field field) {
            var (start, end) = _slots[IndexOf(field)];
            return utf8[start..end];
        }

        public readonly string? Type(ReadOnlySpan<byte> utf8) {
            if (!Has(Field.T)) {
                return null;
            }
            var reader = new Utf8JsonReader(Slice(utf8, Field.T));
            reader.Read();
            return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        }

        /// <summary>Returns a problem when a required field is missing or a field outside both sets is present.</summary>
        public readonly string? Check(ReadOnlySpan<byte> utf8, Field required, Field optional = Field.None) {
            required |= Field.T;
            if ((_present & required) != required) {
                return $"{Type(utf8)} is missing {Describe(required & ~_present)}";
            }
            var extra = _present & ~(required | optional);
            return extra == Field.None ? null : $"{Type(utf8)} does not take {Describe(extra)}";
        }

        public readonly T Required<T>(ReadOnlySpan<byte> utf8, Field field, JsonTypeInfo<T> typeInfo) where T : struct {
            return JsonSerializer.Deserialize(Slice(utf8, field), typeInfo);
        }

        public readonly T RequiredRef<T>(ReadOnlySpan<byte> utf8, Field field, JsonTypeInfo<T> typeInfo) where T : class {
            return JsonSerializer.Deserialize(Slice(utf8, field), typeInfo)
                ?? throw new JsonException($"{Describe(field)} is null");
        }

        public readonly T? Optional<T>(ReadOnlySpan<byte> utf8, Field field, JsonTypeInfo<T> typeInfo) where T : struct {
            return Has(field) ? JsonSerializer.Deserialize(Slice(utf8, field), typeInfo) : null;
        }

        public readonly T? OptionalRef<T>(ReadOnlySpan<byte> utf8, Field field, JsonTypeInfo<T> typeInfo) where T : class {
            return Has(field) ? RequiredRef(utf8, field, typeInfo) : null;
        }

        private static Field FieldOf(ref Utf8JsonReader reader) {
            for (var i = 0; i < FieldCount; i++) {
                if (reader.ValueTextEquals(Names[i])) {
                    return (Field)(1u << i);
                }
            }
            return Field.None;
        }

        private static int IndexOf(Field field) => System.Numerics.BitOperations.TrailingZeroCount((uint)field);

        private static string Describe(Field fields) {
            var names = new List<string>();
            for (var i = 0; i < FieldCount; i++) {
                if (((uint)fields & (1u << i)) != 0) {
                    names.Add(NameStrings[i]);
                }
            }
            return string.Join(", ", names);
        }

        private static readonly string[] NameStrings = [
            "t", "v", "cid", "subs", "r", "e", "s", "q", "k", "d", "c", "conn",
            "me", "n", "lastQ", "o", "from", "txs", "ev", "dup", "code", "retry", "msg", "retryMs",
        ];

        private static readonly byte[][] Names = NameStrings.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
    }
}
