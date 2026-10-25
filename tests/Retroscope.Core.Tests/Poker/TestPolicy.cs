using Retroscope.Core.Authz;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// The poker rows of §c.11, standing in for the real policy (U9) so these tests pin the decider, not the matrix.
/// Members (users) of the session's team only; deleting rounds, clearing and deleting the session need admin or
/// owner; Jira submits need Jira configured and <c>members_can_edit</c> or admin.
/// </summary>
internal static class TestPolicy {
    public static bool Can(Principal who, Capability capability, ResourceFacts facts, IAccessView access) {
        if (who.Kind != PrincipalKind.User || access.RoleIn(facts.Team, who.Id) is not { } role) {
            return false;
        }
        var admin = role is TeamRole.Admin or TeamRole.Owner;
        return capability switch {
            Capability.PokerDeleteRound or Capability.PokerClearAllRounds or Capability.PokerDeleteSession => admin,
            Capability.PokerSubmitJiraPoints => facts.JiraConfigured && (facts.MembersCanEditJira || admin),
            _ => true,
        };
    }
}
