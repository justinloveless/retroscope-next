using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

/// <param name="Votes">Every vote as other principals see it (status while voting, values once revealed).</param>
/// <param name="Rounds">In display order.</param>
/// <param name="CreatedAt">When the session was created, which the toolbar names as its day (UX18).</param>
public sealed record PokerSnapshot(
    SessionId SessionId,
    string Code,
    bool PresenceEnabled,
    bool FollowSpotlight,
    IReadOnlyList<PrincipalId> Observers,
    IReadOnlyList<RoundInfo> Rounds,
    IReadOnlyList<PokerVoteState> Votes,
    IReadOnlyList<PrincipalInfo> Roster,
    IReadOnlyList<PrincipalInfo> Presence,
    TeamId TeamId,
    long CreatedAt,
    SpotlightInfo? Spotlight = null,
    RoundId? CurrentRoundId = null,
    IReadOnlyDictionary<string, string>? PointDescriptions = null,
    IReadOnlyList<PokerJiraPoints>? JiraSubmits = null) : RoomSnapshot;

/// <param name="Own">The viewer's own votes with values.</param>
/// <param name="HeldByThisTab">The spotlight is bound to this viewer's tab.</param>
public sealed record PokerOverlay(IReadOnlyList<PokerVoteState> Own, bool HeldByThisTab) : RoomOverlay;

/// <param name="Members">The board's team, for mentions and endorsements.</param>
public sealed record RetroSnapshot(
    BoardId BoardId,
    string Code,
    RetroBoardMeta Meta,
    RetroConfig Config,
    RetroStage Stage,
    RetroTimer Timer,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<CardInfo> Cards,
    IReadOnlyList<CommentInfo> Comments,
    IReadOnlyList<RetroVoteChanged> Votes,
    RetroReadiness Readiness,
    IReadOnlyList<PrincipalInfo> Presence,
    TeamId TeamId,
    IReadOnlyList<MemberInfo> Members,
    IReadOnlyList<EndorsementInfo> Endorsements,
    IReadOnlyList<EndorsementTypeInfo> EndorsementTypes,
    int MaxEndorsements,
    CardId? Focus = null) : RoomSnapshot;

public sealed record RetroOverlay(
    IReadOnlyList<CardId> MineCards,
    IReadOnlyList<CommentId> MineComments,
    IReadOnlyList<CardId> MyVotes) : RoomOverlay;

/// <param name="ActionItems">Open items plus those closed in the last 30 days.</param>
public sealed record TeamSnapshot(
    TeamId TeamId,
    string Name,
    IReadOnlyList<MemberInfo> Members,
    IReadOnlyList<ActionItemInfo> ActionItems,
    IReadOnlyList<EndorsementTypeInfo> EndorsementTypes,
    IReadOnlyDictionary<string, string> PointDescriptions,
    int MaxEndorsements) : RoomSnapshot;
