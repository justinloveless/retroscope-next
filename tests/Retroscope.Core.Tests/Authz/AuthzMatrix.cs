using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

/// <summary>
/// The viewers and resources of <c>tests/authz-matrix.csv</c>. Each viewer is a relationship to the resource
/// (member of its team, its creator, holder of a password grant…), not a fixed identity.
/// </summary>
internal static class AuthzMatrix {
    public static readonly TeamId TeamA = new(Guid.Parse("0199a000-0000-7000-8000-00000000000a"));
    public static readonly PrincipalId Someone = new(Guid.Parse("0199a000-0000-7000-8000-0000000000ff"));

    /// <summary>The board password before and after a change (<c>password_set_at</c>).</summary>
    public static readonly DateTimeOffset OldPassword = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset CurrentPassword = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SessionId Session = new(Guid.Parse("0199a000-0000-7000-8000-000000000501"));
    private static readonly BoardId Board = new(Guid.Parse("0199a000-0000-7000-8000-000000000b01"));

    public static IReadOnlyList<MatrixViewer> Viewers { get; } = [
        new("anonymous", Kind: null),
        new("external", PrincipalKind.External),
        new("guest", PrincipalKind.Guest),
        new("user", PrincipalKind.User),
        new("app_admin", PrincipalKind.User, AppAdmin: true),
        new("member", PrincipalKind.User, Role: TeamRole.Member),
        new("admin", PrincipalKind.User, Role: TeamRole.Admin),
        new("owner", PrincipalKind.User, Role: TeamRole.Owner),
        new("observer", PrincipalKind.User, Role: TeamRole.Member, Observer: true),
        new("creator", PrincipalKind.User, Creator: true),
        new("guest_creator", PrincipalKind.Guest, Creator: true),
        new("grant_holder", PrincipalKind.User, Grant: CurrentPassword),
        // Unlocked the board, then the password was changed mid-session.
        new("stale_grant", PrincipalKind.User, Grant: OldPassword, Before: "grant_holder"),
        // Admin when they subscribed, demoted to member mid-session.
        new("demoted", PrincipalKind.User, Role: TeamRole.Member, RoleBefore: TeamRole.Admin, Before: "admin"),
    ];

    public static IReadOnlyList<MatrixResource> Resources { get; } = [
        new("poker_quick", new ResourceFacts(RoomKey.Poker(Session))),
        new("poker_team", new ResourceFacts(RoomKey.Poker(Session), TeamA)),
        new("poker_team_jira", new ResourceFacts(RoomKey.Poker(Session), TeamA, JiraConfigured: true)),
        new("poker_team_jira_admins", new ResourceFacts(RoomKey.Poker(Session), TeamA, JiraConfigured: true, MembersCanEditJira: false)),
        new("poker_quick_deleted", new ResourceFacts(RoomKey.Poker(Session), Deleted: true)),
        new("poker_team_deleted", new ResourceFacts(RoomKey.Poker(Session), TeamA, TeamDeleted: true)),
        new("board_quick_link", BoardFacts(null, BoardVisibility.Link)),
        new("board_quick_password", BoardFacts(null, BoardVisibility.Password)),
        new("board_team_team", BoardFacts(TeamA, BoardVisibility.Team)),
        new("board_team_link", BoardFacts(TeamA, BoardVisibility.Link)),
        new("board_team_password", BoardFacts(TeamA, BoardVisibility.Password)),
        new("board_quick_link_archived", BoardFacts(null, BoardVisibility.Link) with { Archived = true }),
        new("board_team_team_archived", BoardFacts(TeamA, BoardVisibility.Team) with { Archived = true }),
        new("board_quick_deleted", BoardFacts(null, BoardVisibility.Link) with { Deleted = true }),
        new("board_team_deleted", BoardFacts(TeamA, BoardVisibility.Team) with { TeamDeleted = true }),
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
    /// Evaluates the cell. A viewer whose access changes mid-session (<see cref="MatrixViewer.Before"/>) is checked
    /// before and after the change through the same access view, as a live room sees it; the first answer must
    /// match the <c>Before</c> viewer's column.
    /// </summary>
    public static (bool? Before, bool After) Evaluate(Capability capability, MatrixResource resource, MatrixViewer viewer) {
        var principal = viewer.Kind is { } kind
            ? new Principal(new PrincipalId(Guid.CreateVersion7()), kind, viewer.Name, null, viewer.AppAdmin)
            : null;
        var facts = resource.Facts with {
            CreatedBy = viewer.Creator && principal is not null ? principal.Id : Someone,
            IsObserver = viewer.Observer,
        };
        var access = new FakeAccess();
        if (principal is null) {
            return (null, Policy.Can(principal, capability, facts, access));
        }
        access.SetRole(TeamA, principal.Id, viewer.RoleBefore ?? viewer.Role);
        if (viewer.Grant is { } grant) {
            access.Grant(Board, principal.Id, grant);
        }
        bool? before = null;
        if (viewer.RoleBefore is not null) {
            before = Policy.Can(principal, capability, facts, access);
            access.SetRole(TeamA, principal.Id, viewer.Role);
        } else if (viewer.Before is not null) {
            before = Policy.Can(principal, capability, facts.PasswordSetAt is null ? facts : facts with { PasswordSetAt = OldPassword }, access);
        }
        return (before, Policy.Can(principal, capability, facts, access));
    }

    private static ResourceFacts BoardFacts(TeamId? team, BoardVisibility visibility) {
        return new ResourceFacts(
            RoomKey.Board(Board),
            team,
            Visibility: visibility,
            PasswordSetAt: visibility == BoardVisibility.Password ? CurrentPassword : null);
    }
}

internal sealed record MatrixViewer(
    string Name,
    PrincipalKind? Kind,
    TeamRole? Role = null,
    TeamRole? RoleBefore = null,
    bool AppAdmin = false,
    bool Observer = false,
    bool Creator = false,
    DateTimeOffset? Grant = null,
    string? Before = null);

internal sealed record MatrixResource(string Name, ResourceFacts Facts);
