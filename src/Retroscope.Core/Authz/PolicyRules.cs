using Retroscope.Core.Room;

namespace Retroscope.Core.Authz;

/// <summary>The rules behind <see cref="Policy"/>. Deny-all stub until U9 supplies the matrix.</summary>
internal static class PolicyRules {
    public static bool Evaluate(Principal? who, Capability capability, ResourceFacts facts, IAccessView access) {
        return false;
    }
}
