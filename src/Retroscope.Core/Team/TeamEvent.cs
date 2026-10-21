using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Team;

/// <summary>
/// What the team decider emits. Each event carries everything its projection needs, so a tx re-projected
/// from the ring later (replay) does not depend on the state at that time.
/// </summary>
public abstract record TeamEvent {
    private protected TeamEvent() {
    }

    /// <summary>Changes <c>assigned_to</c> only.</summary>
    public sealed record ActionItemAssigned(ActionItemInfo Item) : TeamEvent;

    /// <summary>Changes <c>done_at</c> and <c>done_by</c> only.</summary>
    public sealed record ActionItemDoneSet(ActionItemInfo Item) : TeamEvent;

    public sealed record ActionItemDeleted(ActionItemId ItemId) : TeamEvent;

    /// <summary>The row already exists (inserted by the board actor); nothing to write.</summary>
    public sealed record ActionItemAdded(ActionItemInfo Item) : TeamEvent;

    /// <summary>Changes <c>text</c> and <c>source_version</c>, guarded by <c>source_version &lt; SourceVersion</c>.</summary>
    public sealed record ActionItemTextSynced(ActionItemId ItemId, string Text, int SourceVersion) : TeamEvent;

    /// <summary>The FK was already set to null by the board actor's delete; nothing to write.</summary>
    public sealed record ActionItemSourceDetached(ActionItemInfo Item) : TeamEvent;

    /// <summary>Not persisted: comments live on the source board.</summary>
    public sealed record ActionItemCommentsCounted(ActionItemId ItemId, int Count) : TeamEvent;

    /// <param name="Members">The whole roster after the change.</param>
    public sealed record MemberAdded(MemberInfo Member, IReadOnlyList<MemberInfo> Members) : TeamEvent;

    public sealed record MemberRoleSet(PrincipalId Member, TeamRole Role, IReadOnlyList<MemberInfo> Members) : TeamEvent;

    public sealed record MemberRemoved(PrincipalId Member, IReadOnlyList<MemberInfo> Members) : TeamEvent;

    /// <summary>Not persisted: a list page refetches.</summary>
    public sealed record BoardsChanged(BoardId BoardId) : TeamEvent;

    /// <summary>Not persisted: a list page refetches.</summary>
    public sealed record PokerChanged(SessionId SessionId, int Pointed, int Total) : TeamEvent;

    /// <summary>Not persisted here: the HTTP handler wrote the settings before posting.</summary>
    public sealed record SettingsChanged(IReadOnlyDictionary<string, string>? PointDescriptions, int? MaxEndorsements) : TeamEvent;

    /// <summary>Not persisted or projected: subscribers are kicked by the access-change path.</summary>
    public sealed record TeamDeleted : TeamEvent;
}
