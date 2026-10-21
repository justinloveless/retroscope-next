using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;

namespace Retroscope.Core.Tests.Team;

[Trait("Category", "Team")]
public sealed class MembershipAndProjectionTests {
    private readonly TeamFixture _team = new();

    [Fact]
    public void Adding_a_member_carries_the_whole_ordered_roster() {
        var zed = new MemberInfo(new PrincipalId(Guid.CreateVersion7()), "Zed", TeamRole.Member);

        var added = TeamFixture.Single<TeamEvent.MemberAdded>(_team.Run(new TeamInput.MemberAdded(zed)));

        Assert.Equal(zed, added.Member);
        Assert.Equal(["Olga", "Ada", "Mo", "Zed"], added.Members.Select(x => x.Name));
        Assert.Equal(added.Members, TeamProjection.Snapshot(_team.State, TeamFixture.Now).Members);
    }

    [Fact]
    public void Re_adding_an_existing_member_keeps_their_role() {
        var again = new MemberInfo(_team.Admin.Id, _team.Admin.DisplayName, TeamRole.Member);

        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.MemberAdded(again)));
    }

    [Fact]
    public void Role_changes_and_removals_update_the_roster() {
        var promoted = TeamFixture.Single<TeamEvent.MemberRoleSet>(_team.Run(new TeamInput.MemberRoleSet(_team.Member.Id, TeamRole.Admin)));
        Assert.Equal(["Olga", "Ada", "Mo"], promoted.Members.Select(x => x.Name));
        Assert.Equal(TeamRole.Admin, _team.State.Members[_team.Member.Id].Role);
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.MemberRoleSet(_team.Member.Id, TeamRole.Admin)));

        var removed = TeamFixture.Single<TeamEvent.MemberRemoved>(_team.Run(new TeamInput.MemberRemoved(_team.Admin.Id)));
        Assert.Equal(["Olga", "Mo"], removed.Members.Select(x => x.Name));
        Assert.False(_team.State.Members.ContainsKey(_team.Admin.Id));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.MemberRemoved(_team.Admin.Id)));
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.MemberRoleSet(_team.Outsider.Id, TeamRole.Admin)));
    }

    [Fact]
    public void Settings_carry_only_what_changed() {
        var descriptions = new Dictionary<string, string> { ["5"] = "A few days" };

        var changed = TeamFixture.Single<TeamEvent.SettingsChanged>(
            _team.Run(new TeamInput.SettingsChanged(descriptions, _team.State.MaxEndorsements)));

        Assert.Same(descriptions, changed.PointDescriptions);
        Assert.Null(changed.MaxEndorsements);
        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.SettingsChanged(
            new Dictionary<string, string> { ["5"] = "A few days" })));

        var max = TeamFixture.Single<TeamEvent.SettingsChanged>(_team.Run(new TeamInput.SettingsChanged(MaxEndorsements: 7)));
        Assert.Equal(new TeamSettingsChanged(null, 7), TeamProjection.ToWire(max));
        Assert.Equal(7, _team.State.MaxEndorsements);
        Assert.Same(descriptions, _team.State.PointDescriptions);
    }

    [Fact]
    public void The_snapshot_holds_open_items_and_those_closed_in_the_last_30_days_newest_first() {
        var now = TeamFixture.Now;
        var recent = TeamFixture.NewItem(new CardId(Guid.CreateVersion7())) with {
            CreatedAt = now.AddDays(-40).ToUnixTimeMilliseconds(),
            DoneAt = now.AddDays(-30).ToUnixTimeMilliseconds(),
        };
        var old = TeamFixture.NewItem(new CardId(Guid.CreateVersion7())) with {
            CreatedAt = now.AddDays(-50).ToUnixTimeMilliseconds(),
            DoneAt = now.AddDays(-30).AddMilliseconds(-1).ToUnixTimeMilliseconds(),
        };
        var openOld = TeamFixture.NewItem(new CardId(Guid.CreateVersion7())) with { CreatedAt = now.AddDays(-400).ToUnixTimeMilliseconds() };
        foreach (var item in new[] { recent, old, openOld }) {
            _team.State.ActionItems[item.Id] = item;
        }

        var snapshot = TeamProjection.Snapshot(_team.State, now);

        Assert.Equal([_team.Item.Id, recent.Id, openOld.Id], snapshot.ActionItems.Select(x => x.Id));
        Assert.Equal(_team.TeamId, snapshot.TeamId);
        Assert.Equal("Platform", snapshot.Name);
    }

    [Fact]
    public void Transferring_ownership_demotes_and_promotes_in_one_event() {
        var transferred = TeamFixture.Single<TeamEvent.OwnershipTransferred>(
            _team.Run(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Member.Id)));

        Assert.Equal((_team.Owner.Id, _team.Member.Id), (transferred.From, transferred.To));
        Assert.Equal(["Mo", "Ada", "Olga"], transferred.Members.Select(x => x.Name));
        Assert.Equal(TeamRole.Owner, _team.State.Members[_team.Member.Id].Role);
        Assert.Equal(TeamRole.Admin, _team.State.Members[_team.Owner.Id].Role);
        Assert.Single(_team.State.Members.Values, x => x.Role == TeamRole.Owner);
    }

    [Fact]
    public void A_transfer_needs_the_owner_and_a_member_and_a_retry_is_a_no_op() {
        TeamFixture.Rejected(_team.Decide(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Owner.Id)), NackCode.Invalid);
        TeamFixture.Rejected(_team.Decide(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Outsider.Id)), NackCode.NotFound);
        TeamFixture.Rejected(_team.Decide(new TeamInput.TransferOwnership(_team.Admin.Id, _team.Member.Id)), NackCode.Forbidden);

        _team.Run(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Member.Id));

        Assert.IsType<Decision<TeamEvent>.NoOp>(_team.Decide(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Member.Id)));
        TeamFixture.Rejected(_team.Decide(new TeamInput.TransferOwnership(_team.Owner.Id, _team.Admin.Id)), NackCode.Forbidden);
    }

    [Fact]
    public void Every_event_projects_to_its_wire_kind() {
        var item = _team.Item;
        var board = new BoardId(Guid.CreateVersion7());
        var session = new SessionId(Guid.CreateVersion7());
        var roster = TeamProjection.Roster(_team.State.Members.Values);
        var cases = new (TeamEvent Event, WireEvent? Wire)[] {
            (new TeamEvent.ActionItemAssigned(item), new TeamActionUpdated(item)),
            (new TeamEvent.ActionItemDoneSet(item), new TeamActionUpdated(item)),
            (new TeamEvent.ActionItemSourceDetached(item), new TeamActionUpdated(item)),
            (new TeamEvent.ActionItemAdded(item), new TeamActionAdded(item)),
            (new TeamEvent.ActionItemDeleted(item.Id), new TeamActionDeleted(item.Id)),
            (new TeamEvent.ActionItemTextSynced(item.Id, "t", 4), new TeamActionText(item.Id, "t", 4)),
            (new TeamEvent.ActionItemCommentsCounted(item.Id, 3), new TeamActionComments(item.Id, 3)),
            (new TeamEvent.MemberAdded(roster[0], roster), new TeamMembers(roster)),
            (new TeamEvent.MemberRoleSet(roster[0].Pid, TeamRole.Owner, roster), new TeamMembers(roster)),
            (new TeamEvent.MemberRemoved(_team.Outsider.Id, roster), new TeamMembers(roster)),
            (new TeamEvent.OwnershipTransferred(roster[0].Pid, roster[1].Pid, roster), new TeamMembers(roster)),
            (new TeamEvent.BoardsChanged(board), new TeamBoardsChanged(board)),
            (new TeamEvent.PokerChanged(session, 1, 2), new TeamPokerChanged(session, 1, 2)),
            (new TeamEvent.SettingsChanged(null, 4), new TeamSettingsChanged(null, 4)),
            (new TeamEvent.TeamDeleted(), null),
        };

        var eventTypes = typeof(TeamEvent).GetNestedTypes().Where(x => x.IsSubclassOf(typeof(TeamEvent))).ToHashSet();
        Assert.Equal(eventTypes.OrderBy(x => x.Name), cases.Select(x => x.Event.GetType()).Distinct().OrderBy(x => x.Name));
        foreach (var (evt, wire) in cases) {
            Assert.Equal(wire, TeamProjection.ToWire(evt));
        }
    }

    [Fact]
    public void A_replayed_tx_projects_as_it_was_committed() {
        var assigned = TeamFixture.Single<TeamEvent.ActionItemAssigned>(
            _team.Run(TeamInput.From(new TeamActionAssign(_team.Item.Id, _team.Member.Id)), _team.Member));
        _team.Run(TeamInput.From(new TeamActionAssign(_team.Item.Id, _team.Admin.Id)), _team.Member);
        _team.Run(TeamInput.From(new TeamActionDelete(_team.Item.Id)), _team.Admin);

        var wire = Assert.IsType<TeamActionUpdated>(TeamProjection.ToWire(assigned));

        Assert.Equal(_team.Member.Id, wire.Item.AssignedTo);
    }
}
