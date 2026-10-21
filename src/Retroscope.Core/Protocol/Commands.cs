using System.Text.Json.Serialization;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

// Command payloads (DESIGN §c.4). A parameter without a default is required on the wire; a nullable
// parameter without a default must be present and may be null, so it is always written (JsonIgnore Never).

public sealed record PokerVoteSet(RoundId RoundId, short Points, short? High = null, bool? Lock = null) : PokerCommand;

public sealed record PokerLockSet(RoundId RoundId, bool Locked) : PokerCommand;

public sealed record PokerAbstainSet(RoundId RoundId, bool Abstain) : PokerCommand;

public sealed record PokerRoundReveal(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundReplay(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundCreate(
    RoundId RoundId,
    string? TicketKey = null,
    bool? Pending = null,
    bool? ForceNew = null) : PokerCommand;

public sealed record PokerRoundCreateMany(IReadOnlyList<NewRound> Rounds) : PokerCommand;

public sealed record PokerRoundNext(RoundId FromRoundId, RoundId NewRoundId, string? TicketKey = null) : PokerCommand;

public sealed record PokerRoundEnd(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundActivate(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundSetTicket(
    RoundId RoundId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TicketKey,
    int BaseVersion) : PokerCommand;

public sealed record PokerRoundCommitPending(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundDiscardPending(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundDelete(RoundId RoundId) : PokerCommand;

public sealed record PokerRoundReorder(IReadOnlyList<RoundId> Order) : PokerCommand;

public sealed record PokerRoundsClearAll(RoundId NewRoundId) : PokerCommand;

/// <param name="Expected">For every field being changed, the value the client last saw (R5).</param>
public sealed record PokerSettingsUpdate(
    PokerSettingsValues Expected,
    bool? PresenceEnabled = null,
    bool? FollowSpotlight = null) : PokerCommand;

public sealed record PokerObserverSet(PrincipalId PrincipalId, bool Observer) : PokerCommand;

public sealed record PokerSpotlightTake(RoundId RoundId, bool Force) : PokerCommand;

public sealed record PokerSpotlightMove(RoundId RoundId) : PokerCommand;

public sealed record PokerSpotlightGive(PrincipalId PrincipalId) : PokerCommand;

public sealed record PokerSpotlightRelease : PokerCommand;

public sealed record PokerJiraSubmitPoints(RoundId RoundId, decimal Points) : PokerCommand;

public sealed record RetroCardAdd(CardId CardId, ColumnId ColumnId, string Body, bool Anonymous) : RetroCommand;

public sealed record RetroCardEdit(CardId CardId, string Body, int BaseVersion) : RetroCommand;

public sealed record RetroCardDelete(CardId CardId) : RetroCommand;

public sealed record RetroVoteSet(CardId CardId, bool On) : RetroCommand;

public sealed record RetroCommentAdd(CommentId CommentId, CardId CardId, string Body, bool Anonymous) : RetroCommand;

public sealed record RetroCommentDelete(CommentId CommentId) : RetroCommand;

public sealed record RetroColumnAdd(ColumnId ColumnId, string Title, ColumnColor Color) : RetroCommand;

public sealed record RetroColumnUpdate(
    ColumnId ColumnId,
    int BaseVersion,
    string? Title = null,
    ColumnColor? Color = null) : RetroCommand;

public sealed record RetroColumnDelete(ColumnId ColumnId) : RetroCommand;

public sealed record RetroColumnReorder(IReadOnlyList<ColumnId> Order) : RetroCommand;

public sealed record RetroColumnSetActionItems(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ColumnId? ColumnId) : RetroCommand;

/// <param name="Expected">The title the client last saw.</param>
public sealed record RetroBoardRename(string Title, string Expected) : RetroCommand;

/// <param name="Expected">The current value of every field in <paramref name="Patch"/> as the client last saw it.</param>
public sealed record RetroConfigUpdate(RetroConfigPatch Patch, RetroConfigPatch Expected) : RetroCommand;

public sealed record RetroStageSet(RetroStage From, RetroStage To) : RetroCommand;

public sealed record RetroReadySet(RetroStage Stage, bool Ready) : RetroCommand;

/// <param name="From">The focus the client last saw (compare-and-set).</param>
public sealed record RetroFocusSet(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CardId? CardId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CardId? From) : RetroCommand;

public sealed record RetroTimerStart(int DurationMs, bool Alarm, bool Music, TimerState From) : RetroCommand;

public sealed record RetroTimerPause(TimerState From) : RetroCommand;

public sealed record RetroTimerResume(TimerState From) : RetroCommand;

public sealed record RetroTimerReset(TimerState From) : RetroCommand;

public sealed record RetroTimerSetFlags(bool? Alarm = null, bool? Music = null) : RetroCommand;

public sealed record RetroEndorseGive(EndorsementId EndorsementId, PrincipalId ToUserId, EndorsementTypeId TypeId) : RetroCommand;

public sealed record RetroEndorseRevoke(EndorsementId EndorsementId) : RetroCommand;

public sealed record TeamActionAssign(
    ActionItemId ItemId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PrincipalId? UserId) : TeamCommand;

public sealed record TeamActionDone(ActionItemId ItemId, bool Done) : TeamCommand;

public sealed record TeamActionDelete(ActionItemId ItemId) : TeamCommand;
