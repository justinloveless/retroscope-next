using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;

namespace Retroscope.Core.Retro;

/// <summary>
/// The elevated rights inside a board the viewer may already use (DESIGN §c.4 "Facilitator", §c.11). Roles
/// come from <see cref="IAccessView"/> at decide time (R12), so a removed member loses them on the next command.
/// <see cref="Authz.Policy"/> asks the same questions, so the decider and the policy cannot drift.
/// </summary>
public static class RetroRoles {
    public static bool IsSignedIn(Principal who) => who.Kind == PrincipalKind.User;

    /// <summary>Whether <paramref name="principal"/> is a member of the board's team (mentions, endorsement targets).</summary>
    public static bool IsMember(TeamId? team, PrincipalId principal, IAccessView access) {
        return team is { } id && access.RoleIn(id, principal) is not null;
    }

    public static bool IsMember(RetroState state, PrincipalId principal, IAccessView access) {
        return IsMember(state.TeamId, principal, access);
    }

    /// <summary>The caller is a member of the board's team. Only users are members; a role reported for a guest is ignored.</summary>
    public static bool IsMember(TeamId? team, Principal who, IAccessView access) {
        return IsSignedIn(who) && IsMember(team, who.Id, access);
    }

    public static bool IsMember(RetroState state, Principal who, IAccessView access) => IsMember(state.TeamId, who, access);

    /// <summary>
    /// Team boards: team members only, whatever the visibility (S12). Quick boards: any signed-in principal.
    /// Guests never.
    /// </summary>
    public static bool IsFacilitator(TeamId? team, Principal who, IAccessView access) {
        return IsSignedIn(who) && (team is null || IsMember(team, who.Id, access));
    }

    public static bool IsFacilitator(RetroState state, Principal who, IAccessView access) {
        return IsFacilitator(state.TeamId, who, access);
    }

    /// <summary>
    /// Readiness and focus. Quick boards: everyone on the board, guests included. Team boards: members only, because
    /// a non-member may add, edit their own, vote and comment, "nothing more" (§c.4).
    /// </summary>
    public static bool MayPace(TeamId? team, Principal who, IAccessView access) {
        return team is null || IsMember(team, who, access);
    }

    public static bool MayPace(RetroState state, Principal who, IAccessView access) => MayPace(state.TeamId, who, access);

    /// <summary>The author, or the holder of a matching anonymous tag.</summary>
    public static bool Owns(RetroState state, Authorship authorship, PrincipalId principal, IAnonTagger tagger) {
        if (authorship.Author is { Pid: { } author }) {
            return author == principal;
        }
        return authorship.AnonTag is { } tag && tagger.Owns(tag, state.BoardId.Value, principal.Value);
    }
}
