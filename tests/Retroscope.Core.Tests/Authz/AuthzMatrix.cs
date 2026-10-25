using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

/// <summary>
/// The viewers and resources of <c>tests/authz-matrix.csv</c>. Each viewer is a relationship to the resource
/// (member of its team, admin, observer…), not a fixed identity. Every resource belongs to a team (UX18).
/// </summary>
internal static class AuthzMatrix {
    public static readonly TeamId TeamA = new(Guid.Parse("0199a000-0000-7000-8000-00000000000a"));

    private static readonly SessionId Session = new(Guid.Parse("0199a000-0000-7000-8000-000000000501"));
    private static readonly BoardId Board = new(Guid.Parse("0199a000-0000-7000-8000-000000000b01"));

    public static IReadOnlyList<MatrixViewer> Viewers { get; } = [
        new("anonymous", Kind: null),
        new("external", PrincipalKind.External),
        // A legacy guest principal: its history stays, but it is never a caller.
        new("guest", PrincipalKind.Guest),
        new("user", PrincipalKind.User),
        new("app_admin", PrincipalKind.User, AppAdmin: true),
        new("member", PrincipalKind.User, Role: TeamRole.Member),
        new("admin", PrincipalKind.User, Role: TeamRole.Admin),
        new("owner", PrincipalKind.User, Role: TeamRole.Owner),
        new("observer", PrincipalKind.User, Role: TeamRole.Member, Observer: true),
        // Admin when they subscribed, demoted to member mid-session.
        new("demoted", PrincipalKind.User, Role: TeamRole.Member, RoleBefore: TeamRole.Admin, Before: "admin"),
    ];

    public static IReadOnlyList<MatrixResource> Resources { get; } = [
        new("poker_team", new ResourceFacts(RoomKey.Poker(Session), TeamA)),
        new("poker_team_jira", new ResourceFacts(RoomKey.Poker(Session), TeamA, JiraConfigured: true)),
        new("poker_team_jira_admins", new ResourceFacts(RoomKey.Poker(Session), TeamA, JiraConfigured: true, MembersCanEditJira: false)),
        new("poker_deleted", new ResourceFacts(RoomKey.Poker(Session), TeamA, Deleted: true)),
        new("poker_team_deleted", new ResourceFacts(RoomKey.Poker(Session), TeamA, TeamDeleted: true)),
        new("board_team", new ResourceFacts(RoomKey.Board(Board), TeamA)),
        new("board_team_archived", new ResourceFacts(RoomKey.Board(Board), TeamA, Archived: true)),
        new("board_deleted", new ResourceFacts(RoomKey.Board(Board), TeamA, Deleted: true)),
        new("board_team_deleted", new ResourceFacts(RoomKey.Board(Board), TeamA, TeamDeleted: true)),
        new("team", new ResourceFacts(RoomKey.Team(TeamA), TeamA)),
        new("team_jira", new ResourceFacts(RoomKey.Team(TeamA), TeamA, JiraConfigured: true)),
        new("team_jira_admins", new ResourceFacts(RoomKey.Team(TeamA), TeamA, JiraConfigured: true, MembersCanEditJira: false)),
        new("team_deleted", new ResourceFacts(RoomKey.Team(TeamA), TeamA, TeamDeleted: true)),
    ];

    public static IReadOnlyList<Capability> Capabilities { get; } = Enum.GetValues<Capability>();

    /// <summary>The room kinds a capability is checked against; on any other kind it is always denied.</summary>
    public static IReadOnlyList<RoomKind> KindsOf(Capability capability) {
        var name = capability.ToString();
        if (capability == Capability.Subscribe) {
            return [RoomKind.Poker, RoomKind.Board, RoomKind.Team];
        }
        if (name.StartsWith("Poker", StringComparison.Ordinal)) {
            return [RoomKind.Poker];
        }
        if (name.StartsWith("Retro", StringComparison.Ordinal)) {
            return [RoomKind.Board];
        }
        if (name.StartsWith("Team", StringComparison.Ordinal) || name.StartsWith("Jira", StringComparison.Ordinal)) {
            return [RoomKind.Team];
        }
        throw new InvalidOperationException($"No room kind for {capability}");
    }

    /// <summary>
    /// Evaluates the cell. A viewer whose role changes mid-session (<see cref="MatrixViewer.Before"/>) is checked
    /// before and after the change through the same access view, as a live room sees it; the first answer must
    /// match the <c>Before</c> viewer's column.
    /// </summary>
    public static (bool? Before, bool After) Evaluate(Capability capability, MatrixResource resource, MatrixViewer viewer) {
        var principal = viewer.Kind is { } kind
            ? new Principal(new PrincipalId(Guid.CreateVersion7()), kind, viewer.Name, null, viewer.AppAdmin)
            : null;
        var facts = resource.Facts with { IsObserver = viewer.Observer };
        var access = new FakeAccess();
        if (principal is null) {
            return (null, Policy.Can(principal, capability, facts, access));
        }
        access.SetRole(TeamA, principal.Id, viewer.RoleBefore ?? viewer.Role);
        bool? before = null;
        if (viewer.RoleBefore is not null) {
            before = Policy.Can(principal, capability, facts, access);
            access.SetRole(TeamA, principal.Id, viewer.Role);
        }
        return (before, Policy.Can(principal, capability, facts, access));
    }
}

internal sealed record MatrixViewer(
    string Name,
    PrincipalKind? Kind,
    TeamRole? Role = null,
    TeamRole? RoleBefore = null,
    bool AppAdmin = false,
    bool Observer = false,
    string? Before = null);

internal sealed record MatrixResource(string Name, ResourceFacts Facts);
