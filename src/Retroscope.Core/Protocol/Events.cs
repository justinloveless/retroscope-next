using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Protocol;

// Tx-frame events (DESIGN §c.4), as projected for one viewer. Optional members are omitted when null.

/// <summary>
/// To the voter's own principal: points, high, locked, abstained. To everyone else while voting: status only.
/// </summary>
public sealed record PokerVoteState(
    RoundId RoundId,
    PrincipalId Pid,
    short? Points = null,
    short? High = null,
    bool? Locked = null,
    bool? Abstained = null,
    VoteStatus? Status = null) : WireEvent;

/// <param name="By">Null for an auto-reveal.</param>
public sealed record PokerRoundRevealed(
    RoundId RoundId,
    IReadOnlyList<RevealedVote> Votes,
    short Winner,
    long At,
    PrincipalId? By = null) : WireEvent;

public sealed record PokerRoundReplayed(RoundId RoundId) : WireEvent;

public sealed record PokerRoundCreated(RoundInfo Round) : WireEvent;

/// <summary>Carries the whole round, so absent and cleared fields cannot be confused.</summary>
public sealed record PokerRoundUpdated(RoundInfo Round) : WireEvent;

public sealed record PokerRoundDeleted(RoundId RoundId) : WireEvent;

public sealed record PokerRoundOrder(IReadOnlyList<RoundId> Order) : WireEvent;

public sealed record PokerRoundsReset(RoundInfo Round) : WireEvent;

/// <param name="Nav">True: every client navigates to the round. False: only clients whose round vanished.</param>
public sealed record PokerPointer(RoundId RoundId, PointerCause Cause, bool Nav) : WireEvent;

/// <summary>Both members absent means the spotlight is released.</summary>
public sealed record PokerSpotlight(PrincipalId? Holder = null, RoundId? RoundId = null) : WireEvent;

public sealed record PokerSettingsChanged(bool PresenceEnabled, bool FollowSpotlight) : WireEvent;

public sealed record PokerObservers(IReadOnlyList<PrincipalId> Ids) : WireEvent;

public sealed record PokerRoster(IReadOnlyList<PrincipalInfo> Seats) : WireEvent;

public sealed record PokerJiraPoints(
    RoundId RoundId,
    string Key,
    decimal Points,
    JiraSubmitStatus Status,
    PrincipalId By,
    string? Code = null) : WireEvent;

public sealed record PokerSessionDeleted : WireEvent;

public sealed record RetroCardAdded(CardInfo Card) : WireEvent;

public sealed record RetroCardEdited(CardId CardId, string Body, int Version) : WireEvent;

public sealed record RetroCardDeleted(CardId CardId) : WireEvent;

/// <param name="Voters">Present only when the stage rule shows voter identities.</param>
/// <param name="Mine">Per viewer: whether this viewer's vote is on the card.</param>
public sealed record RetroVoteChanged(
    CardId CardId,
    int Count,
    IReadOnlyList<PrincipalId>? Voters = null,
    bool? Mine = null) : WireEvent;

public sealed record RetroCommentAdded(CommentInfo Comment) : WireEvent;

public sealed record RetroCommentDeleted(CommentId CommentId, CardId CardId) : WireEvent;

public sealed record RetroColumnAdded(ColumnInfo Column) : WireEvent;

public sealed record RetroColumnUpdated(ColumnInfo Column) : WireEvent;

public sealed record RetroColumnDeleted(ColumnId ColumnId) : WireEvent;

public sealed record RetroColumnOrder(IReadOnlyList<ColumnId> Order) : WireEvent;

public sealed record RetroActionItemsColumn(ColumnId? ColumnId = null) : WireEvent;

public sealed record RetroBoardMeta(string Title, BoardVisibility Visibility, bool Archived) : WireEvent;

public sealed record RetroConfigChanged(RetroConfig Config) : WireEvent;

public sealed record RetroStageChanged(RetroStage Stage) : WireEvent;

public sealed record RetroReadiness(RetroStage Stage, IReadOnlyList<PrincipalId> Ready) : WireEvent;

public sealed record RetroFocus(CardId? CardId = null) : WireEvent;

/// <param name="Expired">Set once when a running timer reaches its end (also after a restart, R16).</param>
public sealed record RetroTimer(
    TimerState State,
    int DurationMs,
    bool Alarm,
    bool Music,
    long? EndsAt = null,
    int? RemainingMs = null,
    bool? Expired = null) : WireEvent;

/// <param name="Celebrate">Set only in the recipient's projection.</param>
public sealed record RetroEndorsementAdded(EndorsementInfo Endorsement, bool? Celebrate = null) : WireEvent;

public sealed record RetroEndorsementRemoved(EndorsementId EndorsementId) : WireEvent;

public sealed record TeamActionAdded(ActionItemInfo Item) : WireEvent;

public sealed record TeamActionUpdated(ActionItemInfo Item) : WireEvent;

public sealed record TeamActionText(ActionItemId ItemId, string Text, int SourceVersion) : WireEvent;

public sealed record TeamActionDeleted(ActionItemId ItemId) : WireEvent;

/// <summary>The client refetches the item's comments over HTTP (R22).</summary>
public sealed record TeamActionComments(ActionItemId ItemId, int Count) : WireEvent;

public sealed record TeamMembers(IReadOnlyList<MemberInfo> Members) : WireEvent;

public sealed record TeamBoardsChanged(BoardId BoardId) : WireEvent;

public sealed record TeamPokerChanged(SessionId SessionId, int Pointed, int Total) : WireEvent;

/// <summary>Emitted in team, poker and board rooms; each carries only the settings that room uses.</summary>
public sealed record TeamSettingsChanged(
    IReadOnlyDictionary<string, string>? PointDescriptions = null,
    int? MaxEndorsements = null) : WireEvent;

public sealed record PresenceJoin(PrincipalId Pid, PrincipalKind Kind, string Name, BlobId? Avatar = null) : WireEvent;

public sealed record PresenceLeave(PrincipalId Pid) : WireEvent;

public sealed record PresenceUpdate(PrincipalId Pid, string Name, BlobId? Avatar = null) : WireEvent;

/// <summary>Jira titles live only in memory and in this unsequenced fill (no Jira titles in Postgres).</summary>
public sealed record JiraMeta(
    string Key,
    string? Summary = null,
    string? IssueType = null,
    string? Status = null,
    string? ParentKey = null,
    string? ParentSummary = null) : Unsequenced;
