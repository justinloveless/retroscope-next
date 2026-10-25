using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Authz;

/// <summary>
/// The rules behind <see cref="Policy"/> (§c.11, §d, R12, UX18). Every room belongs to a team, and every capability
/// needs a signed-in user who is a member of that team. Guests and external principals are history, never callers,
/// and being an app admin opens nothing. Roles come from <see cref="IAccessView"/> on each call, so a removal or a
/// demotion applies to the very next check. Anything not matched below is denied.
/// </summary>
internal static class PolicyRules {
    public static bool Evaluate(Principal? who, Capability capability, ResourceFacts facts, IAccessView access) {
        if (who is not { Kind: PrincipalKind.User } || facts.Deleted || facts.TeamDeleted) {
            return false;
        }
        // A team room belongs to itself; facts that say otherwise were gathered wrongly.
        if (facts.Room.Kind == RoomKind.Team && facts.Room.Id != facts.Team.Value) {
            return false;
        }
        if (access.RoleIn(facts.Team, who.Id) is not { } role) {
            return false;
        }
        return facts.Room.Kind switch {
            RoomKind.Poker => Poker(capability, facts, role),
            RoomKind.Board => Board(capability, facts, role),
            RoomKind.Team => Team(capability, facts, role),
            _ => false,
        };
    }

    private static bool Poker(Capability capability, ResourceFacts facts, TeamRole role) {
        return capability switch {
            Capability.Subscribe
                or Capability.PokerRevealOrReplay
                or Capability.PokerManageRounds
                or Capability.PokerUpdateSettings
                or Capability.PokerSetObserver
                or Capability.PokerSpotlight => true,
            Capability.PokerVote => !facts.IsObserver,
            Capability.PokerDeleteRound or Capability.PokerClearAllRounds or Capability.PokerDeleteSession => IsAdmin(role),
            Capability.PokerSubmitJiraPoints => CanEditJira(facts, role),
            _ => false,
        };
    }

    /// <summary>An archived board stays readable and manageable but takes no content (§c.4).</summary>
    private static bool Board(Capability capability, ResourceFacts facts, TeamRole role) {
        return capability switch {
            Capability.Subscribe => true,
            Capability.RetroAddCard
                or Capability.RetroVote
                or Capability.RetroComment
                or Capability.RetroReady
                or Capability.RetroFocus
                or Capability.RetroTimer
                or Capability.RetroFacilitate
                or Capability.RetroEndorse => !facts.Archived,
            Capability.RetroArchive or Capability.RetroDeleteBoard => IsAdmin(role),
            _ => false,
        };
    }

    private static bool Team(Capability capability, ResourceFacts facts, TeamRole role) {
        return capability switch {
            Capability.Subscribe or Capability.TeamUpdateActionItem => true,
            Capability.TeamDeleteActionItem
                or Capability.TeamManageMembers
                or Capability.TeamManageSettings
                or Capability.JiraConfigure => IsAdmin(role),
            Capability.TeamManageAdmins or Capability.TeamDelete => role == TeamRole.Owner,
            Capability.JiraEditIssues => CanEditJira(facts, role),
            _ => false,
        };
    }

    private static bool IsAdmin(TeamRole role) => role is TeamRole.Admin or TeamRole.Owner;

    private static bool CanEditJira(ResourceFacts facts, TeamRole role) {
        return facts.JiraConfigured && (facts.MembersCanEditJira || IsAdmin(role));
    }
}
