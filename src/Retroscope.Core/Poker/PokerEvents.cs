using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Poker;

/// <summary>How an event travels once decided.</summary>
public enum EventDurability : byte {
    /// <summary>Written in the command's transaction (the store plans SQL for it), then applied and projected.</summary>
    Committed,

    /// <summary>Applied, sequenced and projected, never written (spotlight moves, Jira submit status, roster, §c.8).</summary>
    Ephemeral,

    /// <summary>Applied to actor memory only: no SQL and no wire event (auto-reveal arming, holder absence).</summary>
    MemoryOnly,
}

/// <summary>
/// Domain events of a poker room. <see cref="PokerApply"/> folds them into <see cref="PokerState"/>,
/// <see cref="PokerProjection"/> turns them into wire events per viewer, and the store plans SQL for the
/// <see cref="EventDurability.Committed"/> ones. A decision whose events are all non-committed needs no transaction.
/// </summary>
public abstract record PokerEvent {
    public virtual EventDurability Durability => EventDurability.Committed;
}

/// <param name="Vote">Null deletes the vote row (the seat is empty again).</param>
public sealed record VoteChanged(RoundId RoundId, PrincipalId Principal, Vote? Vote) : PokerEvent;

/// <param name="Votes">Every vote after the reveal: unlocked votes became abstains.</param>
/// <param name="By">Null for an auto-reveal.</param>
public sealed record RoundRevealed(
    RoundId RoundId,
    IReadOnlyList<RevealedVote> Votes,
    short Winner,
    DateTimeOffset At,
    PrincipalId? By) : PokerEvent;

/// <param name="Round">The round after the replay.</param>
/// <param name="Votes">Every vote after the replay, all locked.</param>
public sealed record RoundReplayed(RoundInfo Round, IReadOnlyList<VoteEntry> Votes) : PokerEvent;

public sealed record VoteEntry(PrincipalId Principal, Vote Vote);

/// <param name="SortKey">Its index in the display order: new rounds are appended.</param>
public sealed record RoundCreated(RoundInfo Round, int SortKey) : PokerEvent;

/// <summary>Carries the whole round after the change (ticket, parent, activity, pending flag, Jira points).</summary>
public sealed record RoundUpdated(RoundInfo Round) : PokerEvent;

/// <summary>The round, its votes and its Jira submit status go; the sort keys after it close the gap.</summary>
public sealed record RoundDeleted(RoundId RoundId) : PokerEvent;

/// <summary>Sort keys are rewritten densely in this order.</summary>
public sealed record RoundsReordered(IReadOnlyList<RoundId> Order) : PokerEvent;

/// <summary>Every round is deleted and <paramref name="Round"/> (number 1) is created in their place.</summary>
public sealed record RoundsReset(RoundInfo Round) : PokerEvent;

public sealed record PointerMoved(RoundId RoundId, PointerCause Cause, bool Nav) : PokerEvent;

/// <summary>A durable spotlight change (take, give, release, repair, unbind). All three members null means released.</summary>
public sealed record SpotlightChanged(PrincipalId? Holder, ClientId? HolderClient, RoundId? RoundId) : PokerEvent;

/// <summary>Ephemeral-durable (§c.8): persisted later by <see cref="SpotlightSettled"/>.</summary>
public sealed record SpotlightMoved(PrincipalId Holder, ClientId HolderClient, RoundId RoundId, DateTimeOffset At)
    : PokerEvent {
    public override EventDurability Durability => EventDurability.Ephemeral;
}

/// <summary>Persists the moved spotlight position (round and bound tab). Not projected.</summary>
public sealed record SpotlightSettled(RoundId RoundId, ClientId? HolderClient) : PokerEvent;

public sealed record SettingsChanged(bool PresenceEnabled, bool FollowSpotlight) : PokerEvent;

/// <param name="Observers">Every observer after the change, for the wire event.</param>
public sealed record ObserverSet(PrincipalId Principal, bool Observer, IReadOnlyList<PrincipalId> Observers)
    : PokerEvent;

public sealed record RosterSet(IReadOnlyList<PrincipalInfo> Seats) : PokerEvent {
    public override EventDurability Durability => EventDurability.Ephemeral;
}

public sealed record PointDescriptionsSet(IReadOnlyDictionary<string, string> PointDescriptions) : PokerEvent {
    public override EventDurability Durability => EventDurability.Ephemeral;
}

/// <summary>A Jira submit started, failed, or succeeded for a ticket the round no longer carries.</summary>
public sealed record JiraSubmitChanged(
    RoundId RoundId,
    string Key,
    decimal Points,
    PrincipalId By,
    JiraSubmitStatus Status,
    string? Code = null) : PokerEvent {
    public override EventDurability Durability => EventDurability.Ephemeral;
}

/// <summary>Jira accepted the points: persists <c>jira_points*</c> of the round.</summary>
public sealed record JiraPointsRecorded(RoundInfo Round, string Key, decimal Points, PrincipalId By) : PokerEvent;

public sealed record SessionDeleted : PokerEvent;

public sealed record AutoRevealArmed(RoundId RoundId, DateTimeOffset At) : PokerEvent {
    public override EventDurability Durability => EventDurability.MemoryOnly;
}

/// <param name="Since">Null when the holder is present again.</param>
public sealed record HolderAbsence(DateTimeOffset? Since) : PokerEvent {
    public override EventDurability Durability => EventDurability.MemoryOnly;
}

public sealed record JiraSettingsSet(TeamJiraSettings Settings) : PokerEvent {
    public override EventDurability Durability => EventDurability.MemoryOnly;
}
