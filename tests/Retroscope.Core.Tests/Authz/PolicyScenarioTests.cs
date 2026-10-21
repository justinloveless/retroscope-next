using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Authz;

[Trait("Category", "Authz")]
public sealed class PolicyScenarioTests {
    private static readonly TeamId Team = new(Guid.CreateVersion7());
    private static readonly BoardId Board = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

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
        var board = BoardFacts(BoardVisibility.Team, Team);
        _access.SetRole(Team, alice.Id, TeamRole.Member);
        Assert.True(Policy.Can(alice, Capability.RetroAddCard, board, _access));

        _access.SetRole(Team, alice.Id, null);

        Assert.False(Policy.Can(alice, Capability.Subscribe, board, _access));
        Assert.False(Policy.Can(alice, Capability.RetroAddCard, board, _access));
    }

    [Fact]
    public void Changing_the_board_password_invalidates_existing_grants() {
        var guest = Guest();
        _access.Grant(Board, guest.Id, T0);
        var before = BoardFacts(BoardVisibility.Password, passwordSetAt: T0);
        Assert.True(Policy.Can(guest, Capability.Subscribe, before, _access));
        Assert.True(Policy.Can(guest, Capability.RetroVote, before, _access));

        var after = before with { PasswordSetAt = T0.AddMinutes(5) };

        Assert.False(Policy.Can(guest, Capability.Subscribe, after, _access));
        Assert.False(Policy.Can(guest, Capability.RetroVote, after, _access));

        _access.Grant(Board, guest.Id, T0.AddMinutes(5));
        Assert.True(Policy.Can(guest, Capability.Subscribe, after, _access));
    }

    [Fact]
    public void A_quick_board_creator_needs_no_grant_but_a_team_board_creator_does() {
        var creator = User();
        var quick = BoardFacts(BoardVisibility.Password, passwordSetAt: T0) with { CreatedBy = creator.Id };
        var team = quick with { Team = Team };

        Assert.True(Policy.Can(creator, Capability.Subscribe, quick, _access));
        Assert.False(Policy.Can(creator, Capability.Subscribe, team, _access));
    }

    [Fact]
    public void A_non_member_on_a_link_team_board_may_contribute_but_not_facilitate() {
        foreach (var outsider in new[] { User(), Guest() }) {
            var board = BoardFacts(BoardVisibility.Link, Team);

            Assert.True(Policy.Can(outsider, Capability.Subscribe, board, _access));
            Assert.True(Policy.Can(outsider, Capability.RetroAddCard, board, _access));
            Assert.True(Policy.Can(outsider, Capability.RetroVote, board, _access));
            Assert.True(Policy.Can(outsider, Capability.RetroComment, board, _access));
            Assert.False(Policy.Can(outsider, Capability.RetroFacilitate, board, _access));
            Assert.False(Policy.Can(outsider, Capability.RetroViewRoster, board, _access));
            Assert.False(Policy.Can(outsider, Capability.RetroEndorse, board, _access));
            Assert.False(Policy.Can(outsider, Capability.RetroArchive, board, _access));
        }
    }

    [Fact]
    public void Guests_never_join_team_sessions_or_team_visibility_boards() {
        var guest = Guest();
        // Membership is for users only: a role reported for a guest id must not open anything.
        _access.SetRole(Team, guest.Id, TeamRole.Owner);

        Assert.False(Policy.Can(guest, Capability.Subscribe, new ResourceFacts(RoomKey.Poker(new SessionId(Guid.CreateVersion7())), Team), _access));
        Assert.False(Policy.Can(guest, Capability.Subscribe, BoardFacts(BoardVisibility.Team, Team), _access));
        Assert.False(Policy.Can(guest, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team), _access));
    }

    [Fact]
    public void App_admins_get_no_room_rights_from_being_app_admins() {
        var admin = new Principal(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "Root", null, IsAppAdmin: true);

        Assert.False(Policy.Can(admin, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team), _access));
        Assert.False(Policy.Can(admin, Capability.RetroDeleteBoard, BoardFacts(BoardVisibility.Link), _access));
    }

    [Fact]
    public void Archived_boards_stay_readable_and_manageable_but_take_no_content() {
        var creator = User();
        var board = BoardFacts(BoardVisibility.Link) with { CreatedBy = creator.Id, Archived = true };

        Assert.True(Policy.Can(creator, Capability.Subscribe, board, _access));
        Assert.True(Policy.Can(creator, Capability.RetroArchive, board, _access));
        Assert.False(Policy.Can(creator, Capability.RetroAddCard, board, _access));
        Assert.False(Policy.Can(creator, Capability.RetroFacilitate, board, _access));
        Assert.False(Policy.Can(creator, Capability.RetroTimer, board, _access));
    }

    [Fact]
    public void Soft_deleted_teams_close_every_room_they_own() {
        var owner = User();
        _access.SetRole(Team, owner.Id, TeamRole.Owner);

        Assert.False(Policy.Can(owner, Capability.Subscribe, new ResourceFacts(RoomKey.Team(Team), Team, TeamDeleted: true), _access));
        Assert.False(Policy.Can(owner, Capability.RetroFacilitate, BoardFacts(BoardVisibility.Link, Team) with { TeamDeleted = true }, _access));
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

    private static Principal User() => new(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, "User", null, false);

    private static Principal Guest() => new(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.Guest, "Guest", null, false);

    private static ResourceFacts BoardFacts(BoardVisibility visibility, TeamId? team = null, DateTimeOffset? passwordSetAt = null) {
        return new ResourceFacts(RoomKey.Board(Board), team, Visibility: visibility, PasswordSetAt: passwordSetAt);
    }
}
