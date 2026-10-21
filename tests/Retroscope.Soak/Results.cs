using System.Text.Json;

namespace Retroscope.Soak;

// What the client process writes when it stops (client/soak.mjs `results()`).

internal sealed record ClientResults(
    IReadOnlyList<RoomResult> Rooms,
    IReadOnlyList<TabResult> Tabs,
    LatencyResult Latency,
    IReadOnlyDictionary<string, int> NackCodes,
    IReadOnlyList<ReconnectResult> Reconnects,
    IReadOnlyList<JsonElement> Violations) {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <param name="Reveals">Round id → distinct (epoch, seq) of the <c>poker.round.revealed</c> frames any tab folded.</param>
internal sealed record RoomResult(
    string Key,
    string Kind,
    string Role,
    Guid Id,
    IReadOnlyList<Guid>? InitialRounds,
    IReadOnlyDictionary<Guid, int> Reveals,
    IReadOnlyList<TimerResult> Timers);

/// <param name="Expired">Distinct (epoch, seq) of the expiry frames for this <c>endsAt</c>.</param>
internal sealed record TimerResult(long EndsAt, long? StartedAt, int Expired);

internal sealed record TabResult(string Room, string Label, Guid? Pid, bool Twin, IReadOnlyList<IntentResult> Intents);

/// <param name="Via">
/// <c>ack</c> (applied, seq <paramref name="S"/>), <c>dup</c> or <c>cursor</c> (the server's cursor had passed it: applied
/// unless its answer was a nack that got lost with a socket), <c>nack</c>, <c>dropped</c> (ephemeral, queued over 10 s) or
/// <c>pending</c> (never answered).
/// </param>
internal sealed record IntentResult(int Intent, string K, JsonElement D, string Via, string? Code = null, long? S = null) {
    public bool Certain => Via == "ack";

    public bool Uncertain => Via is "dup" or "cursor";

    public bool MaybeApplied => Certain || Uncertain;

    public Guid Id(string member) => D.GetProperty(member).GetGuid();

    public string Text(string member) => D.GetProperty(member).GetString()!;
}

internal sealed record LatencyResult(int Acks, double P50, double P95, double P99, double Max);

internal sealed record ReconnectResult(int Code, long ClosedAt, long Ms, string Tab);
