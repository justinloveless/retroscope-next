using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

[Trait("Category", "Authz")]
public sealed class PolicyScenarioTests {
    private static readonly TeamId Team = new(Guid.CreateVersion7());
    private static readonly BoardId Board = new(Guid.CreateVersion7());

    private readonly FakeAccess _access = new();

    [Fact]
    public void A_demoted_admin_loses_admin_rights_on_the_next_check() {
        var alice = User();
        var team = new ResourceFacts(RoomKey.Team(Team), Team);
        var session = new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), Team);
        _access.SetRole(Team, alice.Id, TeamRole.Admin);
        Assert.True(Policy.Can(alice, Capability.TeamDeleteActionItem, team, _access));
        Assert.True(Policy.Can(alice, Capability.PokerClearAllRounds, session, _access));

        _access.SetRole(Team, alice.Id, TeamRole.Member);

        Assert.False(Policy.Can(alice, Capability.TeamDeleteActionItem, team, _access));
        Assert.False(Policy.Can(alice, Capability.PokerClearAllRounds, session, _access));
        Assert.True(Policy.Can(alice, Capability.TeamUpdateActionItem, team, _access));
        Assert.True(Policy.Can(alice, Capability.Subscribe, session, _access));
    }

    [Fact]
    public void A_removed_member_loses_the_team_board_on_the_next_check() {
        var alice = User();
        var board = BoardFacts();
        _access.SetRole(Team, alice.Id, TeamRole.Member);
        Assert.True(Policy.Can(alice, Capability.RetroAddCard, board, _access));

        _access.SetRole(Team, alice.Id, null);

        Assert.False(Policy.Can(alice, Capability.Subscribe, board, _access));
        Assert.False(Policy.Can(alice, Capability.RetroAddCard, board, _access));
    }

    [Fact]
    public void Non_members_get_nothing_from_any_room_of_the_team() {
        var rooms = new[] {
            new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), Team),
            BoardFacts(),
            new ResourceFacts(RoomKey.Team(Team), Team),
        };
        foreach (var outsider in new[] { User(), Guest() }) {
            foreach (var room in rooms) {
                foreach (var capability in Enum.GetValues<Capability>()) {
                    Assert.False(Policy.Can(outsider, capability, room, _access), $"{outsider.Kind} {capability} on {room.Room.Kind}");
                }
            }
        }
    }

    [Fact]
    public void Guests_are_never_callers_even_with_a_role() {
        var guest = Guest();
        // Membership is for users only: a role reported for a legacy guest id must not open anything.
        _access.SetRole(Team, guest.Id, TeamRole.Owner);

        Assert.False(Policy.Can(guest, Capability.Subscribe, new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), Team), _access));
        Assert.False(Policy.Can(guest, Capability.Subscribe, BoardFacts(), _access));
        Assert.False(Policy.Can(guest, Capability.RetroAddCard, BoardFacts(), _access));
        Assert.False(Policy.Can(guest, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team), _access));
    }

    [Fact]
    public void App_admins_get_no_room_rights_from_being_app_admins() {
        var admin = new Principal(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "Root", null, IsAppAdmin: true);

        Assert.False(Policy.Can(admin, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team), _access));
        Assert.False(Policy.Can(admin, Capability.Subscribe, BoardFacts(), _access));
        Assert.False(Policy.Can(admin, Capability.RetroDeleteBoard, BoardFacts(), _access));
    }

    [Fact]
    public void Archived_boards_stay_readable_and_manageable_but_take_no_content() {
        var admin = User();
        var member = User();
        _access.SetRole(Team, admin.Id, TeamRole.Admin).SetRole(Team, member.Id, TeamRole.Member);
        var board = BoardFacts() with { Archived = true };

        Assert.True(Policy.Can(member, Capability.Subscribe, board, _access));
        Assert.True(Policy.Can(admin, Capability.RetroArchive, board, _access));
        Assert.False(Policy.Can(member, Capability.RetroArchive, board, _access));
        Assert.False(Policy.Can(admin, Capability.RetroAddCard, board, _access));
        Assert.False(Policy.Can(admin, Capability.RetroFacilitate, board, _access));
        Assert.False(Policy.Can(admin, Capability.RetroTimer, board, _access));
    }

    [Fact]
    public void Soft_deleted_teams_close_every_room_they_own() {
        var owner = User();
        _access.SetRole(Team, owner.Id, TeamRole.Owner);

        Assert.False(Policy.Can(owner, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team, TeamDeleted: true), _access));
        Assert.False(Policy.Can(owner, Capability.RetroFacilitate, BoardFacts() with { TeamDeleted = true }, _access));
    }

    [Fact]
    public void A_team_room_belongs_to_the_team_it_is() {
        var owner = User();
        var other = new TeamId(Guid.CreateVersion7());
        _access.SetRole(other, owner.Id, TeamRole.Owner);

        Assert.True(Policy.Can(owner, Capability.Subscribe, new ResourceFacts(RoomKey.Team(other), other), _access));
        Assert.False(Policy.Can(owner, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), other), _access));
    }

    [Fact]
    public void Observers_may_do_everything_in_a_session_except_vote() {
        var observer = User();
        _access.SetRole(Team, observer.Id, TeamRole.Member);
        var session = new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), Team, IsObserver: true);

        Assert.False(Policy.Can(observer, Capability.PokerVote, session, _access));
        Assert.True(Policy.Can(observer, Capability.PokerRevealOrReplay, session, _access));
        Assert.True(Policy.Can(observer, Capability.PokerSetObserver, session, _access));
    }

    [Fact]
    public void The_viewer_overload_decides_like_the_principal_overload() {
        var member = User();
        _access.SetRole(Team, member.Id, TeamRole.Member);
        var viewer = new Viewer(member, new ClientId(Guid.CreateVersion7()), new ConnectionId(Guid.CreateVersion7()));
        var team = new ResourceFacts(RoomKey.Team(Team), Team);

        Assert.True(Policy.Can(viewer, Capability.TeamUpdateActionItem, team, _access));
        Assert.False(Policy.Can(viewer, Capability.TeamDeleteActionItem, team, _access));
    }

    [Fact]
    public void Only_the_owner_manages_admins_while_admins_manage_members() {
        var team = new ResourceFacts(RoomKey.Team(Team), Team);
        var owner = User();
        var admin = User();
        var member = User();
        _access.SetRole(Team, owner.Id, TeamRole.Owner).SetRole(Team, admin.Id, TeamRole.Admin).SetRole(Team, member.Id, TeamRole.Member);

        Assert.True(Policy.Can(owner, Capability.TeamManageAdmins, team, _access));
        Assert.False(Policy.Can(admin, Capability.TeamManageAdmins, team, _access));
        Assert.False(Policy.Can(member, Capability.TeamManageAdmins, team, _access));
        Assert.True(Policy.Can(admin, Capability.TeamManageMembers, team, _access));
        Assert.True(Policy.Can(owner, Capability.TeamManageMembers, team, _access));
        Assert.False(Policy.Can(member, Capability.TeamManageMembers, team, _access));

        _access.SetRole(Team, owner.Id, TeamRole.Admin);
        Assert.False(Policy.Can(owner, Capability.TeamManageAdmins, team, _access));
    }

    /// <summary>The retro decider admits members through <see cref="RetroRoles"/>; the policy must agree on every board capability.</summary>
    [Fact]
    public void Board_roles_answer_like_the_retro_decider() {
        var board = BoardFacts();
        var member = User();
        var outsider = User();
        var guest = Guest();
        var guestWithRole = Guest();
        _access.SetRole(Team, member.Id, TeamRole.Member).SetRole(Team, guestWithRole.Id, TeamRole.Admin);
        Capability[] contributions = [
            Capability.Subscribe, Capability.RetroAddCard, Capability.RetroVote, Capability.RetroComment, Capability.RetroReady,
            Capability.RetroFocus, Capability.RetroTimer, Capability.RetroFacilitate, Capability.RetroEndorse,
        ];

        foreach (var who in new[] { member, outsider, guest, guestWithRole }) {
            var isMember = RetroRoles.IsMember(Team, who, _access);
            foreach (var capability in contributions) {
                Assert.Equal(isMember, Policy.Can(who, capability, board, _access));
            }
        }
        Assert.True(RetroRoles.IsMember(Team, member, _access));
        Assert.False(RetroRoles.IsMember(Team, guestWithRole, _access));
    }

    private static Principal User() => new(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "User", null, false);

    private static Principal Guest() => new(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.Guest, "Guest", null, false);

    private static ResourceFacts BoardFacts() => new(RoomKey.Board(Board), Team);
}
