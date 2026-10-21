using Retroscope.Core.Authz;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// The poker rows of §c.11, standing in for the real policy (U9) so these tests pin the decider, not the matrix.
/// Team sessions: members (users) only; deleting rounds, clearing and deleting the session need admin or owner;
/// Jira submits need Jira configured and <c>members_can_edit</c> or admin. Quick sessions: anyone with the code;
/// signed-in users delete rounds and clear; only the creator deletes the session; no Jira.
/// </summary>
internal static class TestPolicy {
    public static bool Can(Principal who, Capability capability, ResourceFacts facts, IAccessView access) {
        if (facts.Team is { } team) {
            if (who.Kind != PrincipalKind.User || access.RoleIn(team, who.Id) is not { } role) {
                return false;
            }
            var admin = role is TeamRole.Admin or TeamRole.Owner;
            return capability switch {
                Capability.PokerDeleteRound or Capability.PokerClearAllRounds or Capability.PokerDeleteSession => admin,
                Capability.PokerSubmitJiraPoints => facts.JiraConfigured && (facts.MembersCanEditJira || admin),
                _ => true,
            };
        }
        return capability switch {
            Capability.PokerDeleteRound or Capability.PokerClearAllRounds => who.Kind == PrincipalKind.User,
            Capability.PokerDeleteSession => who.Id == facts.CreatedBy,
            Capability.PokerSubmitJiraPoints => false,
            _ => true,
        };
    }
}
