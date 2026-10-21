using System.Text.Json.Serialization;

namespace Retroscope.Core.Ids;

[JsonConverter(typeof(GuidIdConverter<PrincipalId>))]
public readonly record struct PrincipalId(Guid Value) : IGuidId<PrincipalId> {
    public static PrincipalId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<TeamId>))]
public readonly record struct TeamId(Guid Value) : IGuidId<TeamId> {
    public static TeamId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<SessionId>))]
public readonly record struct SessionId(Guid Value) : IGuidId<SessionId> {
    public static SessionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<RoundId>))]
public readonly record struct RoundId(Guid Value) : IGuidId<RoundId> {
    public static RoundId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<BoardId>))]
public readonly record struct BoardId(Guid Value) : IGuidId<BoardId> {
    public static BoardId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<ColumnId>))]
public readonly record struct ColumnId(Guid Value) : IGuidId<ColumnId> {
    public static ColumnId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<CardId>))]
public readonly record struct CardId(Guid Value) : IGuidId<CardId> {
    public static CardId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<CommentId>))]
public readonly record struct CommentId(Guid Value) : IGuidId<CommentId> {
    public static CommentId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<ActionItemId>))]
public readonly record struct ActionItemId(Guid Value) : IGuidId<ActionItemId> {
    public static ActionItemId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<EndorsementId>))]
public readonly record struct EndorsementId(Guid Value) : IGuidId<EndorsementId> {
    public static EndorsementId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<EndorsementTypeId>))]
public readonly record struct EndorsementTypeId(Guid Value) : IGuidId<EndorsementTypeId> {
    public static EndorsementTypeId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(GuidIdConverter<BlobId>))]
public readonly record struct BlobId(Guid Value) : IGuidId<BlobId> {
    public static BlobId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

/// <summary>One per browser tab (sessionStorage); the unit of command sequencing.</summary>
[JsonConverter(typeof(GuidIdConverter<ClientId>))]
public readonly record struct ClientId(Guid Value) : IGuidId<ClientId> {
    public static ClientId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

/// <summary>One per WebSocket.</summary>
[JsonConverter(typeof(GuidIdConverter<ConnectionId>))]
public readonly record struct ConnectionId(Guid Value) : IGuidId<ConnectionId> {
    public static ConnectionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}
