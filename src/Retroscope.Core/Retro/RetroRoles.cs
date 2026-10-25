using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;

namespace Retroscope.Core.Retro;

/// <summary>
/// Who may act on a board (DESIGN §c.4, §c.11, UX18): the members of its team, and only users are members. Every
/// member adds, votes, comments, paces and facilitates; archiving and deleting stay with team admins over HTTP. The
/// role comes from <see cref="IAccessView"/> at decide time (R12), so a removed member loses the board on the next
/// command. <see cref="Authz.Policy"/> answers every board capability from the same membership, so the decider and
/// the policy cannot drift.
/// </summary>
public static class RetroRoles {
    /// <summary>Whether <paramref name="principal"/> is a member of <paramref name="team"/> (mentions, endorsement targets).</summary>
    public static bool IsMember(TeamId team, PrincipalId principal, IAccessView access) {
        return access.RoleIn(team, principal) is not null;
    }

    public static bool IsMember(RetroState state, PrincipalId principal, IAccessView access) {
        return IsMember(state.TeamId, principal, access);
    }

    /// <summary>The caller is a member of the board's team. Only users are members; a role reported for a guest is ignored.</summary>
    public static bool IsMember(TeamId team, Principal who, IAccessView access) {
        return who.Kind == PrincipalKind.User && IsMember(team, who.Id, access);
    }

    public static bool IsMember(RetroState state, Principal who, IAccessView access) => IsMember(state.TeamId, who, access);

    /// <summary>The author, or the holder of a matching anonymous tag.</summary>
    public static bool Owns(RetroState state, Authorship authorship, PrincipalId principal, IAnonTagger tagger) {
        if (authorship.Author is { Pid: { } author }) {
            return author == principal;
        }
        return authorship.AnonTag is { } tag && tagger.Owns(tag, state.BoardId, principal);
    }
}
