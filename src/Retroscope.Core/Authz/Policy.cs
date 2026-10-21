using Retroscope.Core.Room;

namespace Retroscope.Core.Authz;

/// <summary>Every permission decision goes through here; deciders call it first. Default deny.</summary>
public static class Policy {
    /// <param name="who">Null for an unauthenticated caller (HTTP only).</param>
    public static bool Can(Principal? who, Capability capability, ResourceFacts facts, IAccessView access) {
        return PolicyRules.Evaluate(who, capability, facts, access);
    }

    public static bool Can(Viewer viewer, Capability capability, ResourceFacts facts, IAccessView access) {
        return PolicyRules.Evaluate(viewer.Who, capability, facts, access);
    }
}
