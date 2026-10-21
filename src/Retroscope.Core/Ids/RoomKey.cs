using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Core.Ids;

[JsonConverter(typeof(JsonStringEnumConverter<RoomKind>))]
public enum RoomKind : byte {
    [JsonStringEnumMemberName("poker")] Poker,
    [JsonStringEnumMemberName("board")] Board,
    [JsonStringEnumMemberName("team")] Team,
}

/// <summary>A room on the wire: <c>poker:&lt;sessionId&gt;</c>, <c>board:&lt;boardId&gt;</c> or <c>team:&lt;teamId&gt;</c>.</summary>
[JsonConverter(typeof(RoomKeyConverter))]
public readonly record struct RoomKey(RoomKind Kind, Guid Id) {
    public static RoomKey Poker(SessionId id) => new(RoomKind.Poker, id.Value);

    public static RoomKey Board(BoardId id) => new(RoomKind.Board, id.Value);

    public static RoomKey Team(TeamId id) => new(RoomKind.Team, id.Value);

    public static string Prefix(RoomKind kind) => kind switch {
        RoomKind.Poker => "poker",
        RoomKind.Board => "board",
        RoomKind.Team => "team",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse([NotNullWhen(true)] string? text, out RoomKey key) {
        key = default;
        if (text is null) {
            return false;
        }
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) {
            return false;
        }
        RoomKind kind;
        switch (text.AsSpan(0, colon)) {
            case "poker": kind = RoomKind.Poker; break;
            case "board": kind = RoomKind.Board; break;
            case "team": kind = RoomKind.Team; break;
            default: return false;
        }
        var id = text.AsSpan(colon + 1);
        if (id.Length != 36 || !Guid.TryParseExact(id, "D", out var guid)) {
            return false;
        }
        key = new RoomKey(kind, guid);
        return true;
    }

    public override string ToString() => $"{Prefix(Kind)}:{Id:D}";
}

public sealed class RoomKeyConverter : JsonConverter<RoomKey> {
    public override RoomKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String || !RoomKey.TryParse(reader.GetString(), out var key)) {
            throw new JsonException("Expected a room key such as \"poker:<uuid>\".");
        }
        return key;
    }

    public override void Write(Utf8JsonWriter writer, RoomKey value, JsonSerializerOptions options) {
        writer.WriteStringValue(value.ToString());
    }

    public override RoomKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (!RoomKey.TryParse(reader.GetString(), out var key)) {
            throw new JsonException("Expected a room key such as \"poker:<uuid>\".");
        }
        return key;
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, RoomKey value, JsonSerializerOptions options) {
        writer.WritePropertyName(value.ToString());
    }
}
