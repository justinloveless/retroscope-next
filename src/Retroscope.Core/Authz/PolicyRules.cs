using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;

namespace Retroscope.Core.Authz;

/// <summary>
/// The rules behind <see cref="Policy"/> (§c.11, §d, S12, R12). Every answer is computed from the arguments:
/// roles and grants come from <see cref="IAccessView"/> on each call, so a demotion or a password change applies
/// to the very next check. Anything not matched below is denied.
/// </summary>
internal static class PolicyRules {
    public static bool Evaluate(Principal? who, Capability capability, ResourceFacts facts, IAccessView access) {
        if (who is null || who.Kind == PrincipalKind.External || facts.Deleted || facts.TeamDeleted) {
            return false;
        }
        var subject = new Subject(who, facts, access);
        return facts.Room.Kind switch {
            RoomKind.Poker => Poker(subject, capability),
            RoomKind.Board => Board(subject, capability),
            RoomKind.Team => Team(subject, capability),
            _ => false,
        };
    }

    private static bool Poker(Subject s, Capability capability) {
        if (!s.CanJoinSession) {
            return false;
        }
        return capability switch {
            Capability.Subscribe
                or Capability.PokerRevealOrReplay
                or Capability.PokerManageRounds
                or Capability.PokerUpdateSettings
                or Capability.PokerSetObserver
                or Capability.PokerSpotlight => true,
            Capability.PokerVote => !s.Facts.IsObserver,
            Capability.PokerDeleteRound or Capability.PokerClearAllRounds => s.IsTeamRoom ? s.IsTeamAdmin : s.IsUser,
            Capability.PokerDeleteSession => s.IsTeamRoom ? s.IsTeamAdmin : s.IsCreator,
            Capability.PokerSubmitJiraPoints => s.IsTeamRoom && s.CanEditJira,
            _ => false,
        };
    }

    private static bool Board(Subject s, Capability capability) {
        if (!s.CanOpenBoard) {
            return false;
        }
        var open = !s.Facts.Archived;
        return capability switch {
            Capability.Subscribe => true,
            Capability.RetroAddCard or Capability.RetroVote or Capability.RetroComment => open,
            Capability.RetroReady or Capability.RetroFocus => open && RetroRoles.MayPace(s.Team, s.Who, s.Access),
            Capability.RetroTimer or Capability.RetroFacilitate => open && RetroRoles.IsFacilitator(s.Team, s.Who, s.Access),
            Capability.RetroEndorse => open && RetroRoles.IsMember(s.Team, s.Who, s.Access),
            Capability.RetroViewRoster => RetroRoles.IsMember(s.Team, s.Who, s.Access),
            Capability.RetroArchive or Capability.RetroManageAccess or Capability.RetroDeleteBoard => s.CanManageBoard,
            _ => false,
        };
    }

    private static bool Team(Subject s, Capability capability) {
        if (!s.IsMember) {
            return false;
        }
        return capability switch {
            Capability.Subscribe or Capability.TeamUpdateActionItem => true,
            Capability.TeamDeleteActionItem
                or Capability.TeamManageMembers
                or Capability.TeamManageSettings
                or Capability.JiraConfigure => s.IsTeamAdmin,
            Capability.TeamManageAdmins or Capability.TeamDelete => s.Role == TeamRole.Owner,
            Capability.JiraEditIssues => s.CanEditJira,
            _ => false,
        };
    }

    private readonly struct Subject {
        public Subject(Principal who, ResourceFacts facts, IAccessView access) {
            Who = who;
            Facts = facts;
            Access = access;
            Team = facts.Room.Kind == RoomKind.Team ? new TeamId(facts.Room.Id) : facts.Team;
            // Only users can be team members; a role reported for a guest is ignored.
            Role = Team is { } team && who.Kind == PrincipalKind.User ? access.RoleIn(team, who.Id) : null;
        }

        public Principal Who { get; }

        public ResourceFacts Facts { get; }

        public IAccessView Access { get; }

        public TeamId? Team { get; }

        public TeamRole? Role { get; }

        public bool IsTeamRoom => Team is not null;

        public bool IsUser => Who.Kind == PrincipalKind.User;

        public bool IsMember => Role is not null;

        public bool IsTeamAdmin => Role is TeamRole.Admin or TeamRole.Owner;

        public bool IsCreator => Facts.CreatedBy == Who.Id;

        public bool CanEditJira => IsMember && Facts.JiraConfigured && (Facts.MembersCanEditJira || IsTeamAdmin);

        /// <summary>Team sessions: members. Quick sessions: anyone with the code.</summary>
        public bool CanJoinSession => !IsTeamRoom || IsMember;

        public bool CanOpenBoard {
            get {
                if (IsMember) {
                    return true;
                }
                return Facts.Visibility switch {
                    BoardVisibility.Link => true,
                    BoardVisibility.Password => (!IsTeamRoom && IsCreator) || HasCurrentGrant,
                    _ => false,
                };
            }
        }

        /// <summary>BoardManage (§d): team admin or owner on team boards, the creator on quick boards.</summary>
        public bool CanManageBoard => IsTeamRoom ? IsTeamAdmin : IsCreator;

        private bool HasCurrentGrant =>
            Facts.PasswordSetAt is not null
            && Access.HasCurrentGrant(new BoardId(Facts.Room.Id), Who.Id, Facts.PasswordSetAt);
    }
}
