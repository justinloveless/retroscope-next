using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

// Domain events: what the store persists and RetroApply folds. RetroProjection turns them into wire events per
// viewer. Each carries what its SQL needs, so the store never has to diff state.

public abstract record RetroEvent;

/// <param name="Mentions">Mentioned team members, for the <c>mentions</c> table.</param>
/// <param name="Images">Board images the body references; the store marks them attached (and ownerless when anonymous).</param>
/// <param name="ActionItem">Set when the card lands in the action-items column of a team board (create in place).</param>
public sealed record CardAdded(
    CardId CardId,
    ColumnId ColumnId,
    string Body,
    Authorship Authorship,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PrincipalId> Mentions,
    IReadOnlyList<BlobId> Images,
    ActionItemInfo? ActionItem) : RetroEvent;

/// <param name="Version">The card's new version.</param>
/// <param name="Anonymous">Whether newly attached images must be stored without an owner.</param>
public sealed record CardEdited(
    CardId CardId,
    string Body,
    int Version,
    bool Anonymous,
    IReadOnlyList<PrincipalId> Mentions,
    IReadOnlyList<BlobId> Images) : RetroEvent;

/// <param name="Comments">The card's comments, which cascade with it.</param>
public sealed record CardDeleted(CardId CardId, IReadOnlyList<CommentId> Comments) : RetroEvent;

public sealed record VoteSet(CardId CardId, PrincipalId Voter, bool On) : RetroEvent;

public sealed record CommentAdded(
    CommentId CommentId,
    CardId CardId,
    string Body,
    Authorship Authorship,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PrincipalId> Mentions,
    IReadOnlyList<BlobId> Images) : RetroEvent;

public sealed record CommentDeleted(CommentId CommentId, CardId CardId) : RetroEvent;

/// <param name="SortKey">One past the highest sort key on the board.</param>
public sealed record ColumnAdded(ColumnId ColumnId, string Title, ColumnColor Color, int SortKey) : RetroEvent;

public sealed record ColumnUpdated(ColumnId ColumnId, string Title, ColumnColor Color, int Version) : RetroEvent;

/// <param name="Cards">Every card that cascades with the column.</param>
/// <param name="Comments">Every comment on those cards.</param>
public sealed record ColumnDeleted(
    ColumnId ColumnId,
    IReadOnlyList<CardId> Cards,
    IReadOnlyList<CommentId> Comments) : RetroEvent;

/// <summary>The new order; the sort keys become the dense positions 0..n-1.</summary>
public sealed record ColumnsReordered(IReadOnlyList<ColumnId> Order) : RetroEvent;

public sealed record ActionItemsColumnSet(ColumnId? ColumnId) : RetroEvent;

public sealed record BoardRenamed(string Title) : RetroEvent;

/// <param name="Previous">The configuration it replaced; projections need it to tell what became visible.</param>
public sealed record ConfigChanged(RetroConfig Config, RetroConfig Previous) : RetroEvent;

/// <summary>The store also deletes the readiness rows of <paramref name="From"/>.</summary>
public sealed record StageChanged(RetroStage From, RetroStage To) : RetroEvent;

public sealed record ReadySet(RetroStage Stage, PrincipalId Principal, bool Ready) : RetroEvent;

public sealed record FocusChanged(CardId? CardId) : RetroEvent;

/// <param name="ExpiredAt">The end that was reached, when this change is the expiry (sent once, R16).</param>
public sealed record TimerChanged(TimerValue Timer, DateTimeOffset? ExpiredAt = null) : RetroEvent;

public sealed record EndorsementAdded(EndorsementInfo Endorsement) : RetroEvent;

public sealed record EndorsementRemoved(EndorsementId EndorsementId) : RetroEvent;

public sealed record MaxEndorsementsChanged(int MaxEndorsements) : RetroEvent;

/// <summary>Memory only; membership is persisted by the team actor.</summary>
public sealed record MembersReplaced(IReadOnlyList<MemberInfo> Members) : RetroEvent;

public sealed record ArchivedChanged(bool Archived, PrincipalId? By, DateTimeOffset At) : RetroEvent;

public sealed record AccessChanged(BoardVisibility Visibility, DateTimeOffset? PasswordSetAt) : RetroEvent;
