using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;

namespace Retroscope.Core.Retro;

/// <summary>
/// The elevated rights inside a board the viewer may already use (DESIGN §c.4 "Facilitator", §c.11). Roles
/// come from <see cref="IAccessView"/> at decide time (R12), so a removed member loses them on the next command.
/// </summary>
public static class RetroRoles {
    public static bool IsSignedIn(Principal who) => who.Kind == PrincipalKind.User;

    public static bool IsMember(RetroState state, PrincipalId principal, IAccessView access) {
        return state.TeamId is { } team && access.RoleIn(team, principal) is not null;
    }

    /// <summary>
    /// Team boards: team members only, whatever the visibility (S12). Quick boards: any signed-in principal.
    /// Guests never.
    /// </summary>
    public static bool IsFacilitator(RetroState state, Principal who, IAccessView access) {
        if (!IsSignedIn(who)) {
            return false;
        }
        return state.TeamId is null || IsMember(state, who.Id, access);
    }

    /// <summary>The author, or the holder of a matching anonymous tag.</summary>
    public static bool Owns(RetroState state, Authorship authorship, PrincipalId principal, IAnonTagger tagger) {
        if (authorship.Author is { Pid: { } author }) {
            return author == principal;
        }
        return authorship.AnonTag is { } tag && tagger.Owns(tag, state.BoardId.Value, principal.Value);
    }
}
