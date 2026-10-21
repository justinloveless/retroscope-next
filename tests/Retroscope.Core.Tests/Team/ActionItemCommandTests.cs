using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;

namespace Retroscope.Core.Tests.Team;

[Trait("Category", "Team")]
public sealed class ActionItemCommandTests {
    private readonly TeamFixture _team = new();

    [Fact]
    public void A_member_assigns_an_item_to_another_member() {
        var decision = _team.Command(new TeamActionAssign(_team.Item.Id, _team.Admin.Id), _team.Member);

        var assigned = TeamFixture.Single<TeamEvent.ActionItemAssigned>(decision);
        Assert.Equal(_team.Item with { AssignedTo = _team.Admin.Id }, assigned.Item);
    }

    [Fact]
    public void Unassigning_clears_the_assignee() {
        _team.Run(TeamInput.From(new TeamActionAssign(_team.Item.Id, _team.Member.Id)), _team.Member);

        var decision = _team.Command(new TeamActionAssign(_team.Item.Id, null), _team.Member);

        Assert.Null(TeamFixture.Single<TeamEvent.ActionItemAssigned>(decision).Item.AssignedTo);
    }

    [Fact]
    public void The_assignee_must_be_a_member_now() {
        TeamFixture.Rejected(_team.Command(new TeamActionAssign(_team.Item.Id, _team.Outsider.Id), _team.Member), NackCode.Invalid);

        _team.Access.SetRole(_team.TeamId, _team.Admin.Id, null);

        TeamFixture.Rejected(_team.Command(new TeamActionAssign(_team.Item.Id, _team.Admin.Id), _team.Member), NackCode.Invalid);
    }

    [Fact]
    public void Assign_and_done_are_set_semantics() {
        _team.Run(TeamInput.From(new TeamActionAssign(_team.Item.Id, _team.Member.Id)), _team.Member);
        _team.Run(TeamInput.From(new TeamActionDone(_team.Item.Id, true)), _team.Member);

        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Command(new TeamActionAssign(_team.Item.Id, _team.Member.Id), _team.Admin));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Command(new TeamActionDone(_team.Item.Id, true), _team.Admin));
    }

    [Fact]
    public void Done_records_who_and_when_and_undone_clears_both() {
        var done = TeamFixture.Single<TeamEvent.ActionItemDoneSet>(_team.Run(TeamInput.From(new TeamActionDone(_team.Item.Id, true)), _team.Member));
        Assert.Equal(TeamFixture.Now.ToUnixTimeMilliseconds(), done.Item.DoneAt);
        Assert.Equal(_team.Member.Id, done.Item.DoneBy);

        var undone = TeamFixture.Single<TeamEvent.ActionItemDoneSet>(_team.Command(new TeamActionDone(_team.Item.Id, false), _team.Admin));
        Assert.Null(undone.Item.DoneAt);
        Assert.Null(undone.Item.DoneBy);
    }

    [Fact]
    public void Only_admins_and_the_owner_delete_items() {
        TeamFixture.Rejected(_team.Command(new TeamActionDelete(_team.Item.Id), _team.Member), NackCode.Forbidden);

        foreach (var who in new[] { _team.Admin, _team.Owner }) {
            Assert.Equal(_team.Item.Id, TeamFixture.Single<TeamEvent.ActionItemDeleted>(_team.Command(new TeamActionDelete(_team.Item.Id), who)).ItemId);
        }
    }

    [Fact]
    public void A_demoted_admin_cannot_delete_on_the_next_command() {
        Assert.IsType<Decision<TeamEvent>.Accept>(_team.Command(new TeamActionDelete(_team.Item.Id), _team.Admin));

        _team.Access.SetRole(_team.TeamId, _team.Admin.Id, TeamRole.Member);

        TeamFixture.Rejected(_team.Command(new TeamActionDelete(_team.Item.Id), _team.Admin), NackCode.Forbidden);
    }

    [Fact]
    public void Non_members_and_callers_without_a_viewer_are_forbidden() {
        var guest = new Principal(new(Guid.CreateVersion7()), PrincipalKind.Guest, "Gus", null, false);
        foreach (var command in new TeamCommand[] {
            new TeamActionAssign(_team.Item.Id, null),
            new TeamActionDone(_team.Item.Id, true),
            new TeamActionDelete(_team.Item.Id),
        }) {
            TeamFixture.Rejected(_team.Command(command, _team.Outsider), NackCode.Forbidden);
            TeamFixture.Rejected(_team.Command(command, guest), NackCode.Forbidden);
            TeamFixture.Rejected(_team.Decide(TeamInput.From(command)), NackCode.Forbidden);
        }
    }

    [Fact]
    public void Unknown_items_are_not_found() {
        var missing = TeamFixture.NewItem(new(Guid.CreateVersion7())).Id;

        TeamFixture.Rejected(_team.Command(new TeamActionAssign(missing, null), _team.Member), NackCode.NotFound);
        TeamFixture.Rejected(_team.Command(new TeamActionDone(missing, true), _team.Member), NackCode.NotFound);
        TeamFixture.Rejected(_team.Command(new TeamActionDelete(missing), _team.Admin), NackCode.NotFound);
    }

    [Fact]
    public void After_the_team_is_deleted_every_command_is_forbidden() {
        _team.Run(new TeamInput.TeamDeleted());

        TeamFixture.Rejected(_team.Command(new TeamActionDone(_team.Item.Id, true), _team.Owner), NackCode.Forbidden);
        TeamFixture.Rejected(_team.Command(new TeamActionDelete(_team.Item.Id), _team.Owner), NackCode.Forbidden);
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.TeamDeleted()));
    }

    [Fact]
    public void Deciding_never_mutates_the_state() {
        var before = _team.State.ActionItems[_team.Item.Id];

        _team.Command(new TeamActionAssign(_team.Item.Id, _team.Admin.Id), _team.Member);
        _team.Command(new TeamActionDone(_team.Item.Id, true), _team.Member);
        _team.Command(new TeamActionDelete(_team.Item.Id), _team.Admin);

        Assert.Same(before, _team.State.ActionItems[_team.Item.Id]);
        Assert.Single(_team.State.ActionItems);
    }

    [Fact]
    public void Applying_a_delete_removes_the_item() {
        _team.Run(TeamInput.From(new TeamActionDelete(_team.Item.Id)), _team.Admin);

        Assert.Empty(_team.State.ActionItems);
    }
}
