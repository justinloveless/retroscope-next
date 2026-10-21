using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

/// <summary>
/// Everything a board actor decides: a client command, or an internal transition with no wire kind (timers,
/// team fan-out, HTTP-originated changes that the HTTP handler has already authorized).
/// </summary>
public abstract record RetroInput {
    private RetroInput() {
    }

    public static implicit operator RetroInput(RetroCommand command) => new Client(command);

    public sealed record Client(RetroCommand Command) : RetroInput;

    /// <summary>Fires for the running timer that ends at <paramref name="EndsAt"/>; a stale one is ignored.</summary>
    public sealed record TimerDue(DateTimeOffset EndsAt) : RetroInput;

    /// <summary><c>max_endorsements_per_board</c> changed on the team.</summary>
    public sealed record TeamSettingsChanged(int MaxEndorsements) : RetroInput;

    /// <summary>A new version of the team's membership; the board shows it to members only.</summary>
    public sealed record MembersChanged(IReadOnlyList<MemberInfo> Members) : RetroInput;

    public sealed record ArchiveSet(bool Archived, PrincipalId? By) : RetroInput;

    /// <summary>
    /// A comment posted from the team page on an action item's source card (<c>POST
    /// /api/teams/{id}/action-items/{itemId}/comments</c>, routed here by Ask). <see cref="Room.CommandContext.Viewer"/>
    /// is the HTTP caller.
    /// </summary>
    public sealed record ActionItemCommentAdd(CommentId CommentId, CardId CardId, string Body) : RetroInput;

    /// <summary>Visibility or password changed; <paramref name="PasswordSetAt"/> is null without a password.</summary>
    public sealed record AccessSet(BoardVisibility Visibility, DateTimeOffset? PasswordSetAt) : RetroInput;

    /// <summary>The board's row was soft-deleted (<c>DELETE /api/retro/{id}</c>); the actor kicks every subscriber.</summary>
    public sealed record Delete : RetroInput;
}
