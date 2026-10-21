using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

/// <summary>
/// One board, owned by its actor thread. <see cref="RetroDecider"/> only reads it; <see cref="RetroApply"/>
/// mutates it after the events committed.
/// </summary>
public sealed class RetroState {
    public BoardId BoardId { get; set; }

    public string Code { get; set; } = "";

    public TeamId? TeamId { get; set; }

    public PrincipalId? CreatedBy { get; set; }

    public string Title { get; set; } = "";

    public BoardVisibility Visibility { get; set; } = BoardVisibility.Link;

    public DateTimeOffset? PasswordSetAt { get; set; }

    public bool Archived { get; set; }

    public bool Deleted { get; set; }

    public RetroConfig Config { get; set; } = RetroDefaults.Config;

    public RetroStage Stage { get; set; } = RetroStage.Thinking;

    public CardId? Focus { get; set; }

    public TimerValue Timer { get; set; } = TimerValue.Initial;

    /// <summary>In display order.</summary>
    public List<Column> Columns { get; } = [];

    public Dictionary<CardId, Card> Cards { get; } = [];

    public Dictionary<CommentId, Comment> Comments { get; } = [];

    /// <summary>Ready principals for <see cref="Stage"/>; a stage change clears it.</summary>
    public HashSet<PrincipalId> ReadyThisStage { get; } = [];

    public Dictionary<EndorsementId, EndorsementInfo> Endorsements { get; } = [];

    public List<EndorsementTypeInfo> EndorsementTypes { get; } = [];

    public int MaxEndorsements { get; set; } = RetroDefaults.MaxEndorsements;

    /// <summary>The team's members from the membership cache, for the members-only roster (S12).</summary>
    public List<MemberInfo> Members { get; } = [];

    public Column? FindColumn(ColumnId id) => Columns.Find(column => column.Id == id);

    public Column? ActionItemsColumn => Columns.Find(column => column.ActionItems);

    public int VotesBy(PrincipalId voter) => Cards.Values.Count(card => card.Voters.Contains(voter));
}

public sealed class Column {
    public ColumnId Id { get; init; }

    public string Title { get; set; } = "";

    public ColumnColor Color { get; set; }

    public bool ActionItems { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>The persisted <c>sort_key</c>; unique per board, not necessarily dense after a delete.</summary>
    public int SortKey { get; set; }

    public ColumnInfo ToInfo() => new(Id, Title, Color, ActionItems, Version);
}

/// <summary>
/// A card or comment's authorship. <see cref="Author"/> is null for anonymous posts, which carry only the
/// board-scoped <see cref="AnonTag"/>; an imported label has an author without a principal.
/// </summary>
public sealed record Authorship(AuthorInfo? Author, byte[]? AnonTag) {
    public bool Anonymous => Author is null;
}

public sealed class Card {
    public CardId Id { get; init; }

    public ColumnId ColumnId { get; set; }

    public string Body { get; set; } = "";

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public Authorship Authorship { get; init; } = new(null, null);

    public HashSet<PrincipalId> Voters { get; } = [];
}

public sealed class Comment {
    public CommentId Id { get; init; }

    public CardId CardId { get; init; }

    public string Body { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public Authorship Authorship { get; init; } = new(null, null);
}

/// <summary>
/// The timer on the server clock (§b <c>timer_*</c> columns): running holds <see cref="EndsAt"/>, paused holds
/// <see cref="RemainingMs"/>, idle holds neither.
/// </summary>
public sealed record TimerValue(
    TimerState State,
    int DurationMs,
    bool Alarm,
    bool Music,
    DateTimeOffset? EndsAt = null,
    int? RemainingMs = null) {
    public static readonly TimerValue Initial = new(TimerState.Idle, RetroDefaults.TimerDurationMs, Alarm: true, Music: false);
}

public static class RetroDefaults {
    public const int TimerDurationMs = 900_000;
    public const int MaxEndorsements = 3;

    /// <summary>The §b column defaults.</summary>
    public static readonly RetroConfig Config = new(
        AllowAnonymous: true,
        VotingEnabled: true,
        ShowAuthorNames: true,
        StagesEnabled: false,
        EnforceReadiness: false,
        AllowSelfVotes: true,
        VoteEmoji: "👍",
        SortChronologically: false);
}
