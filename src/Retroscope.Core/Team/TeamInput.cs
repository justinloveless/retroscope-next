using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Team;

/// <summary>
/// What the team actor decides: a client command from the wire, or an internal post from another actor or an
/// HTTP handler. Internal inputs carry no caller (<see cref="CommandContext.Viewer"/> is null); whoever posts
/// them has already authorized the change.
/// </summary>
public abstract record TeamInput {
    private protected TeamInput() {
    }

    public static TeamInput From(TeamCommand command) => new Client(command);

    /// <summary>A <c>team.*</c> command from a subscribed client.</summary>
    public sealed record Client(TeamCommand Command) : TeamInput;

    /// <summary>The board actor inserted the row in its own transaction (create in place). Idempotent by id.</summary>
    public sealed record ActionItemCreated(ActionItemInfo Item) : TeamInput;

    /// <summary>The source card was edited; applied only when <paramref name="CardVersion"/> is newer than the copy.</summary>
    public sealed record SyncActionText(CardId SourceCard, string Text, int CardVersion) : TeamInput;

    /// <summary>The source card is gone (its FK was set to null in the board actor's transaction).</summary>
    public sealed record SourceCardDeleted(CardId SourceCard) : TeamInput;

    public sealed record ActionItemCommentsChanged(ActionItemId ItemId, int Count) : TeamInput;

    public sealed record BoardsChanged(BoardId BoardId) : TeamInput;

    public sealed record PokerChanged(SessionId SessionId, int Pointed, int Total) : TeamInput;

    /// <summary>
    /// From invite accept or admin repair. An existing member keeps their role: accepting a member link must not
    /// demote an admin.
    /// </summary>
    public sealed record MemberAdded(MemberInfo Member) : TeamInput;

    public sealed record MemberRoleSet(PrincipalId Member, TeamRole Role) : TeamInput;

    public sealed record MemberRemoved(PrincipalId Member) : TeamInput;

    /// <summary>Only the settings present changed.</summary>
    public sealed record SettingsChanged(
        IReadOnlyDictionary<string, string>? PointDescriptions = null,
        int? MaxEndorsements = null) : TeamInput;

    public sealed record TeamDeleted : TeamInput;
}
