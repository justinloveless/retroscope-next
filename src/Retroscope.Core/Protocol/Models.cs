using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Protocol;

// Shared shapes used inside events and snapshots. Timestamps are Unix milliseconds of server time.

public sealed record PrincipalInfo(PrincipalId Pid, PrincipalKind Kind, string Name, BlobId? Avatar = null);

public sealed record MemberInfo(PrincipalId Pid, string Name, TeamRole Role, BlobId? Avatar = null);

public sealed record JiraPointsInfo(decimal Points, long At, PrincipalId By);

public sealed record RoundInfo(
    RoundId Id,
    int Number,
    int Version,
    RoundState State,
    bool Active,
    bool Pending,
    bool AutoReveal,
    string? TicketKey = null,
    string? ParentKey = null,
    PrincipalId? PendingOwner = null,
    short? Winner = null,
    long? RevealedAt = null,
    PrincipalId? RevealedBy = null,
    JiraPointsInfo? Jira = null);

public sealed record RevealedVote(PrincipalId Pid, bool Abstained, short? Points = null, short? High = null);

public sealed record SpotlightInfo(PrincipalId Holder, RoundId RoundId);

public sealed record NewRound(RoundId RoundId, string? TicketKey = null);

/// <param name="Pid">Null for an imported label with no principal behind it.</param>
public sealed record AuthorInfo(string Name, bool Guest, PrincipalId? Pid = null, BlobId? Avatar = null);

/// <param name="Author">Null when anonymous or when the board hides author names.</param>
/// <param name="Mine">Per viewer; omitted in the shared snapshot, where the overlay carries it.</param>
public sealed record CardInfo(
    CardId Id,
    ColumnId ColumnId,
    string Body,
    bool Anonymous,
    int Version,
    long CreatedAt,
    AuthorInfo? Author = null,
    bool? Mine = null);

public sealed record CommentInfo(
    CommentId Id,
    CardId CardId,
    string Body,
    bool Anonymous,
    long CreatedAt,
    AuthorInfo? Author = null,
    bool? Mine = null);

public sealed record ColumnInfo(ColumnId Id, string Title, ColumnColor Color, bool ActionItems, int Version);

/// <param name="MaxVotes">Votes per principal; null means unlimited.</param>
public sealed record RetroConfig(
    bool AllowAnonymous,
    bool VotingEnabled,
    bool ShowAuthorNames,
    bool StagesEnabled,
    bool EnforceReadiness,
    bool AllowSelfVotes,
    string VoteEmoji,
    bool SortChronologically,
    int? MaxVotes = null);

/// <summary>
/// Only the fields present apply. A JSON null cannot mean "unlimited" here (absent and null look the same),
/// so <see cref="MaxVotes"/> uses 0 for unlimited.
/// </summary>
public sealed record RetroConfigPatch(
    bool? AllowAnonymous = null,
    bool? VotingEnabled = null,
    int? MaxVotes = null,
    bool? ShowAuthorNames = null,
    bool? StagesEnabled = null,
    bool? EnforceReadiness = null,
    bool? AllowSelfVotes = null,
    string? VoteEmoji = null,
    bool? SortChronologically = null);

public sealed record PokerSettingsValues(bool? PresenceEnabled = null, bool? FollowSpotlight = null);

public sealed record EndorsementInfo(
    EndorsementId Id,
    EndorsementTypeId TypeId,
    PrincipalId From,
    PrincipalId To,
    long CreatedAt);

public sealed record EndorsementTypeInfo(
    EndorsementTypeId Id,
    string Name,
    string Emoji,
    bool Archived,
    string? Description = null);

public sealed record ActionItemInfo(
    ActionItemId Id,
    string Text,
    int SourceVersion,
    long CreatedAt,
    int CommentCount,
    BoardId? SourceBoardId = null,
    CardId? SourceCardId = null,
    PrincipalId? AssignedTo = null,
    long? DoneAt = null,
    PrincipalId? DoneBy = null,
    PrincipalId? CreatedBy = null);
