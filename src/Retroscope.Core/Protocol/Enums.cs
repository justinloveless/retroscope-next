using System.Text.Json.Serialization;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

[JsonConverter(typeof(StrictEnumConverter<RoundState>))]
public enum RoundState : byte {
    [JsonStringEnumMemberName("voting")] Voting,
    [JsonStringEnumMemberName("revealed")] Revealed,
}

/// <summary>What other principals see of a vote while its round is voting; abstain shows as locked.</summary>
[JsonConverter(typeof(StrictEnumConverter<VoteStatus>))]
public enum VoteStatus : byte {
    [JsonStringEnumMemberName("empty")] Empty,
    [JsonStringEnumMemberName("selected")] Selected,
    [JsonStringEnumMemberName("locked")] Locked,
}

[JsonConverter(typeof(StrictEnumConverter<PointerCause>))]
public enum PointerCause : byte {
    [JsonStringEnumMemberName("next")] Next,
    [JsonStringEnumMemberName("create")] Create,
    [JsonStringEnumMemberName("createMany")] CreateMany,
    [JsonStringEnumMemberName("commit")] Commit,
    [JsonStringEnumMemberName("reset")] Reset,
    [JsonStringEnumMemberName("repair")] Repair,
}

[JsonConverter(typeof(StrictEnumConverter<JiraSubmitStatus>))]
public enum JiraSubmitStatus : byte {
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("failed")] Failed,
}

[JsonConverter(typeof(StrictEnumConverter<RetroStage>))]
public enum RetroStage : byte {
    [JsonStringEnumMemberName("thinking")] Thinking,
    [JsonStringEnumMemberName("voting")] Voting,
    [JsonStringEnumMemberName("discussing")] Discussing,
    [JsonStringEnumMemberName("closed")] Closed,
}

[JsonConverter(typeof(StrictEnumConverter<TimerState>))]
public enum TimerState : byte {
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("paused")] Paused,
}

[JsonConverter(typeof(StrictEnumConverter<BoardVisibility>))]
public enum BoardVisibility : byte {
    [JsonStringEnumMemberName("link")] Link,
    [JsonStringEnumMemberName("team")] Team,
    [JsonStringEnumMemberName("password")] Password,
}

[JsonConverter(typeof(StrictEnumConverter<ColumnColor>))]
public enum ColumnColor : byte {
    [JsonStringEnumMemberName("green")] Green,
    [JsonStringEnumMemberName("red")] Red,
    [JsonStringEnumMemberName("blue")] Blue,
    [JsonStringEnumMemberName("yellow")] Yellow,
    [JsonStringEnumMemberName("purple")] Purple,
    [JsonStringEnumMemberName("pink")] Pink,
    [JsonStringEnumMemberName("indigo")] Indigo,
    [JsonStringEnumMemberName("orange")] Orange,
    [JsonStringEnumMemberName("gray")] Gray,
}

[JsonConverter(typeof(StrictEnumConverter<KickCode>))]
public enum KickCode : byte {
    [JsonStringEnumMemberName("access_revoked")] AccessRevoked,
    [JsonStringEnumMemberName("deleted")] Deleted,
    [JsonStringEnumMemberName("not_found")] NotFound,
    [JsonStringEnumMemberName("forbidden")] Forbidden,
    [JsonStringEnumMemberName("too_many_rooms")] TooManyRooms,
}

[JsonConverter(typeof(StrictEnumConverter<ByeCode>))]
public enum ByeCode : byte {
    [JsonStringEnumMemberName("restart")] Restart,
}
