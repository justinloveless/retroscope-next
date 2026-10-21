using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;
using static Retroscope.Integration.Tests.TeamRoom.TeamRoomScope;

namespace Retroscope.Integration.Tests.TeamRoom;

[Trait("Category", "TeamRoom")]
[Collection(SharedPostgres.Name)]
public sealed class TeamRoomTests(PostgresFixture postgres) {
    private static readonly Viewer Dave = Viewer(4, "Dave");

    [Fact]
    public async Task Snapshot_holds_members_open_and_recent_items_types_and_settings() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob, TeamRole.Member);
        var open = await scope.AddActionItemAsync(1, null, "open");
        var recent = await scope.AddActionItemAsync(2, null, "recent");
        var old = await scope.AddActionItemAsync(3, null, "old");
        await scope.ExecAsync("update team_action_items set done_at = $2 where id = $1", recent.Id.Value, Start.AddDays(-29));
        await scope.ExecAsync("update team_action_items set done_at = $2 where id = $1", old.Id.Value, Start.AddDays(-31));
        await scope.ExecAsync("""
            insert into endorsement_types (id, team_id, name, emoji, sort_key) values ($1, $2, 'Helper', '🤝', 0)
            """, Id(0x7600), Team.Value);

        var snapshot = await JoinAsync(await scope.SubscribeAsync(Alice));

        Assert.Equal("Platform", snapshot.Name);
        Assert.Equal([Alice.Who.Id, Bob.Who.Id], snapshot.Members.Select(x => x.Pid));
        Assert.Equal([recent.Id, open.Id], snapshot.ActionItems.Select(x => x.Id));
        Assert.Equal("Helper", Assert.Single(snapshot.EndorsementTypes).Name);
        Assert.Equal("tiny", snapshot.PointDescriptions["1"]);
        Assert.Equal(3, snapshot.MaxEndorsements);
    }

    [Fact]
    public async Task Non_members_and_deleted_teams_cannot_subscribe() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        await scope.AddUserAsync(Bob);

        var outsider = await scope.SubscribeAsync(Bob);
        Assert.Equal(KickCode.Forbidden, (await outsider.NextAsync<KickFrame>()).Code);

        await scope.ExecAsync("update teams set deleted_at = now() where id = $1", Team.Value);
        scope.Restart();
        var member = await scope.SubscribeAsync(Alice);
        Assert.Equal(KickCode.NotFound, (await member.NextAsync<KickFrame>()).Code);
    }

    [Fact]
    public async Task Board_action_item_notices_reach_the_team_room_and_text_sync_is_monotonic() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        var alice = await scope.SubscribeAsync(Alice);
        await JoinAsync(alice);
        var card = await scope.AddCardAsync(1, "Fix CI");
        var item = await scope.AddActionItemAsync(1, card, "Fix CI");

        scope.Notices.ActionItemCreated(Team, item);
        var added = Assert.IsType<TeamActionAdded>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        scope.Notices.ActionItemTextChanged(Team, card, "Fix CI today", 3);
        var text = Assert.IsType<TeamActionText>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        scope.Notices.ActionItemTextChanged(Team, card, "stale edit", 2);
        scope.Notices.ActionItemCommentsChanged(Team, item.Id, 2);
        var comments = Assert.IsType<TeamActionComments>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        scope.Notices.SourceCardDeleted(Team, card);
        var detached = Assert.IsType<TeamActionUpdated>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        await scope.SettleAsync();

        Assert.Equal(item, added.Item);
        Assert.Equal(new TeamActionText(item.Id, "Fix CI today", 3), text);
        Assert.Equal(2, comments.Count);
        Assert.Null(detached.Item.SourceCardId);
        Assert.Equal("Fix CI today", await scope.ScalarAsync<string>("select text from team_action_items where id = $1", item.Id.Value));
        Assert.Equal(3, await scope.ScalarAsync<int>("select source_version from team_action_items where id = $1", item.Id.Value));
        alice.AssertIdle();
    }

    [Fact]
    public async Task Action_item_created_twice_or_after_a_reload_is_applied_once() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        var first = await scope.AddActionItemAsync(1, await scope.AddCardAsync(1, "one"), "one");
        var alice = await scope.SubscribeAsync(Alice);
        Assert.Single((await JoinAsync(alice)).ActionItems);
        var seq = await scope.QueryAsync(_ => 0);

        Assert.Equal(AskStatus.NoOp, (await scope.AskAsync(new TeamInput.ActionItemCreated(first))).Status);
        var second = await scope.AddActionItemAsync(2, await scope.AddCardAsync(2, "two"), "two");
        Assert.Equal(AskStatus.Accepted, (await scope.AskAsync(new TeamInput.ActionItemCreated(second))).Status);
        Assert.Equal(AskStatus.NoOp, (await scope.AskAsync(new TeamInput.ActionItemCreated(second))).Status);
        Assert.Equal(AskStatus.NoOp, (await scope.AskAsync(new TeamInput.ActionItemCreated(second with { Id = new ActionItemId(Id(0x7599)) }))).Status);

        Assert.Single((await alice.NextAsync<TxFrame>()).Ev.OfType<TeamActionAdded>());
        alice.AssertIdle();
        Assert.Equal(2, await scope.QueryAsync(x => x.ActionItems.Count));
    }

    [Fact]
    public async Task Assign_and_done_write_only_their_columns_so_a_detached_source_stays_detached() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        await scope.AddUserAsync(Bob, TeamRole.Member);
        var card = await scope.AddCardAsync(1, "Write docs");
        var item = await scope.AddActionItemAsync(1, card, "Write docs");
        var alice = await scope.SubscribeAsync(Alice);
        await JoinAsync(alice);
        // The board actor deleted the card; the team actor has not heard yet and still holds the FK in memory.
        await scope.ExecAsync("delete from retro_cards where id = $1", card.Value);

        await scope.CommandAsync(alice, 1, new TeamActionAssign(item.Id, Bob.Who.Id));
        await scope.CommandAsync(alice, 2, new TeamActionDone(item.Id, true));
        var assigned = Assert.IsType<TeamActionUpdated>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        await alice.NextAsync<AckFrame>();
        var done = Assert.IsType<TeamActionUpdated>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        await alice.NextAsync<AckFrame>();

        Assert.Equal(card, assigned.Item.SourceCardId);
        Assert.Equal(Bob.Who.Id, done.Item.AssignedTo);
        Assert.Null(await scope.ScalarAsync<Guid?>("select source_card_id from team_action_items where id = $1", item.Id.Value));
        Assert.Equal(Bob.Who.Id.Value, await scope.ScalarAsync<Guid>("select assigned_to from team_action_items where id = $1", item.Id.Value));
        Assert.Equal(Alice.Who.Id.Value, await scope.ScalarAsync<Guid>("select done_by from team_action_items where id = $1", item.Id.Value));
        Assert.Equal(Start.UtcDateTime, await scope.ScalarAsync<DateTime>("select done_at from team_action_items where id = $1", item.Id.Value));
        Assert.Equal("Write docs", await scope.ScalarAsync<string>("select text from team_action_items where id = $1", item.Id.Value));
    }

    [Fact]
    public async Task Text_sync_sql_never_moves_the_copy_backwards() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        var item = await scope.AddActionItemAsync(1, await scope.AddCardAsync(1, "v1"), "v1");
        var state = await scope.QueryAsync(x => x);
        await scope.ExecAsync("update team_action_items set text = 'v5', source_version = 5 where id = $1", item.Id.Value);

        await scope.Store.CommitAsync(TeamKey, state, [new TeamEvent.ActionItemTextSynced(item.Id, "v3", 3)], null, Token);

        Assert.Equal("v5", await scope.ScalarAsync<string>("select text from team_action_items where id = $1", item.Id.Value));
        Assert.Equal(5, await scope.ScalarAsync<int>("select source_version from team_action_items where id = $1", item.Id.Value));
    }

    [Fact]
    public async Task Load_repairs_copies_left_stale_by_a_crash_between_the_two_commits() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Member);
        var card = await scope.AddCardAsync(1, "edited after the copy", version: 4);
        var item = await scope.AddActionItemAsync(1, card, "original", sourceVersion: 2);
        var newer = await scope.AddActionItemAsync(2, await scope.AddCardAsync(2, "card behind", version: 1), "copy ahead", sourceVersion: 3);

        var snapshot = await JoinAsync(await scope.SubscribeAsync(Alice));

        var repaired = snapshot.ActionItems.Single(x => x.Id == item.Id);
        Assert.Equal(("edited after the copy", 4), (repaired.Text, repaired.SourceVersion));
        Assert.Equal("edited after the copy", await scope.ScalarAsync<string>("select text from team_action_items where id = $1", item.Id.Value));
        Assert.Equal("copy ahead", snapshot.ActionItems.Single(x => x.Id == newer.Id).Text);
    }

    [Fact]
    public async Task Member_added_writes_the_row_bumps_the_cache_and_broadcasts_the_roster() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob);
        var alice = await scope.SubscribeAsync(Alice);
        await JoinAsync(alice);
        Assert.True(scope.Cache.TryGet(Team, out var before));
        Assert.Null(scope.Cache.RoleIn(Team, Bob.Who.Id));

        var result = await scope.AskAsync(new TeamInput.MemberAdded(new MemberInfo(Bob.Who.Id, "Bob", TeamRole.Member)));

        Assert.Equal(AskStatus.Accepted, result.Status);
        Assert.True(scope.Cache.TryGet(Team, out var after));
        Assert.True(after.Version > before.Version);
        Assert.Equal(TeamRole.Member, scope.Cache.RoleIn(Team, Bob.Who.Id));
        Assert.Equal("member", await scope.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", Team.Value, Bob.Who.Id.Value));
        var members = Assert.IsType<TeamMembers>(Assert.Single((await alice.NextAsync<TxFrame>()).Ev));
        Assert.Equal([Alice.Who.Id, Bob.Who.Id], members.Members.Select(x => x.Pid));
        var bob = await scope.SubscribeAsync(Bob);
        Assert.Equal(2, (await JoinAsync(bob)).Members.Count);
    }

    [Fact]
    public async Task Removing_a_member_kicks_their_subscription() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob, TeamRole.Member);
        var alice = await scope.SubscribeAsync(Alice);
        await JoinAsync(alice);
        var bob = await scope.SubscribeAsync(Bob);
        await JoinAsync(bob);

        await scope.AskAsync(new TeamInput.MemberRemoved(Bob.Who.Id));
        await scope.SettleAsync();

        Assert.IsType<TeamMembers>(Assert.Single((await bob.NextAsync<TxFrame>()).Ev));
        Assert.Equal(KickCode.AccessRevoked, (await bob.NextAsync<KickFrame>()).Code);
        Assert.Single((await alice.NextAsync<TxFrame>()).Ev.OfType<TeamMembers>());
        Assert.Null(scope.Cache.RoleIn(Team, Bob.Who.Id));
        Assert.Equal(0L, await scope.ScalarAsync<long>("select count(*) from team_members where user_id = $1", Bob.Who.Id.Value));
    }

    [Fact]
    public async Task A_demoted_admin_is_refused_on_the_very_next_command() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob, TeamRole.Admin);
        var first = await scope.AddActionItemAsync(1, null, "one");
        var second = await scope.AddActionItemAsync(2, null, "two");
        var bob = await scope.SubscribeAsync(Bob);
        await JoinAsync(bob);

        await scope.CommandAsync(bob, 1, new TeamActionDelete(first.Id));
        Assert.IsType<TeamActionDeleted>(Assert.Single((await bob.NextAsync<TxFrame>()).Ev));
        await bob.NextAsync<AckFrame>();
        await scope.AskAsync(new TeamInput.MemberRoleSet(Bob.Who.Id, TeamRole.Member));
        await scope.CommandAsync(bob, 2, new TeamActionDelete(second.Id));

        await bob.UntilAsync<TxFrame>();
        Assert.Equal(NackCode.Forbidden, (await bob.UntilAsync<NackFrame>()).Code);
        Assert.Equal("member", await scope.ScalarAsync<string>("select role from team_members where user_id = $1", Bob.Who.Id.Value));
        Assert.Equal(1L, await scope.ScalarAsync<long>("select count(*) from team_action_items where id = $1", second.Id.Value));
        Assert.Equal(0L, await scope.ScalarAsync<long>("select count(*) from team_action_items where id = $1", first.Id.Value));
    }

    [Fact]
    public async Task Membership_changes_refresh_the_roster_of_live_poker_sessions() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob);
        var poker = await scope.SubscribeAsync(Alice, RoomKey.Poker(Session));
        await poker.NextAsync<SnapFrame>();
        await scope.SettleAsync();
        await poker.DrainAsync();

        await scope.AskAsync(new TeamInput.MemberAdded(new MemberInfo(Bob.Who.Id, "Bob", TeamRole.Member)));
        await scope.SettleAsync();

        var roster = await UntilEventAsync<PokerRoster>(poker);
        Assert.Contains(roster.Seats, x => x.Pid == Bob.Who.Id && x.Name == "Bob" && x.Kind == PrincipalKind.User);
        Assert.Contains(roster.Seats, x => x.Pid == Alice.Who.Id);
    }

    [Fact]
    public async Task Membership_changes_give_a_board_viewer_the_variant_for_their_new_role() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Dave);
        var board = await scope.SubscribeAsync(Dave, RoomKey.Board(Board));
        Assert.Empty(Assert.IsType<RetroSnapshot>((await board.NextAsync<SnapFrame>()).D).Members ?? []);
        await scope.SettleAsync();
        await board.DrainAsync();

        await scope.AskAsync(new TeamInput.MemberAdded(new MemberInfo(Dave.Who.Id, "Dave", TeamRole.Member)));
        await scope.SettleAsync();
        var asMember = Assert.IsType<RetroSnapshot>((await board.UntilAsync<SnapFrame>()).D);
        await scope.AskAsync(new TeamInput.MemberRemoved(Dave.Who.Id));
        await scope.SettleAsync();
        var asOutsider = Assert.IsType<RetroSnapshot>((await board.UntilAsync<SnapFrame>()).D);

        Assert.Contains(asMember.Members!, x => x.Pid == Dave.Who.Id);
        Assert.Empty(asOutsider.Members ?? []);
    }

    [Fact]
    public async Task Settings_changes_fan_out_to_the_team_room_and_its_live_rooms() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        var team = await scope.SubscribeAsync(Alice);
        await JoinAsync(team);
        var poker = await scope.SubscribeAsync(Alice, RoomKey.Poker(Session));
        var board = await scope.SubscribeAsync(Viewer(1, "Alice", connection: 2), RoomKey.Board(Board));
        await poker.NextAsync<SnapFrame>();
        await board.NextAsync<SnapFrame>();
        await scope.SettleAsync();
        await poker.DrainAsync();
        await board.DrainAsync();
        var descriptions = new Dictionary<string, string> { ["3"] = "a day" };

        await scope.AskAsync(new TeamInput.SettingsChanged(descriptions, 5));
        await scope.SettleAsync();

        var teamSettings = Assert.IsType<TeamSettingsChanged>(Assert.Single((await team.NextAsync<TxFrame>()).Ev));
        var pokerSettings = await UntilEventAsync<TeamSettingsChanged>(poker);
        var boardSettings = await UntilEventAsync<TeamSettingsChanged>(board);
        Assert.Equal(("a day", 5), (teamSettings.PointDescriptions!["3"], teamSettings.MaxEndorsements));
        Assert.Equal("a day", pokerSettings.PointDescriptions!["3"]);
        Assert.Equal(5, boardSettings.MaxEndorsements);
    }

    [Fact]
    public async Task Rooms_that_are_not_live_are_left_alone() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob);

        await scope.AskAsync(new TeamInput.MemberAdded(new MemberInfo(Bob.Who.Id, "Bob", TeamRole.Member)));
        await scope.AskAsync(new TeamInput.SettingsChanged(MaxEndorsements: 4));

        Assert.False(scope.Registry.TryGet(RoomKey.Poker(Session), out _));
        Assert.False(scope.Registry.TryGet(RoomKey.Board(Board), out _));
    }

    [Fact]
    public async Task Loading_the_team_room_hands_the_roster_it_read_to_live_sessions() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        await scope.AddUserAsync(Bob);
        var poker = await scope.SubscribeAsync(Alice, RoomKey.Poker(Session));
        await poker.NextAsync<SnapFrame>();
        await scope.Registry.QueryAsync<PokerState, PokerInput, int>(RoomKey.Poker(Session), _ => ValueTask.FromResult(0), Token);
        await poker.DrainAsync();
        Assert.False(scope.Registry.TryGet(TeamKey, out _));
        // A member change whose commit landed although its outcome was unknown: the rows moved, nothing fanned out.
        await scope.ExecAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'member')", Team.Value, Bob.Who.Id.Value);

        await scope.SettleAsync();

        var roster = await UntilEventAsync<PokerRoster>(poker);
        Assert.Contains(roster.Seats, x => x.Pid == Bob.Who.Id);
        Assert.Equal(TeamRole.Member, scope.Cache.RoleIn(Team, Bob.Who.Id));
    }

    [Fact]
    public async Task Deleting_the_team_drops_every_role_and_rechecks_its_live_boards() {
        await using var scope = await StartAsync(postgres);
        await scope.AddUserAsync(Alice, TeamRole.Owner);
        var board = await scope.SubscribeAsync(Alice, RoomKey.Board(Board));
        Assert.NotNull(Assert.IsType<RetroSnapshot>((await board.NextAsync<SnapFrame>()).D).Members);
        await scope.SettleAsync();
        await board.DrainAsync();

        // The member variant of a board's snapshot ends with the roles: Alice is sent the outsiders' snapshot.
        await scope.AskAsync(new TeamInput.TeamDeleted());
        await scope.SettleAsync();

        Assert.Null(scope.Cache.RoleIn(Team, Alice.Who.Id));
        await board.UntilAsync<SnapFrame>();
    }

    private static async Task<T> UntilEventAsync<T>(TestPeer peer) where T : WireEvent {
        while (true) {
            if (await peer.NextAsync() is TxFrame tx && tx.Ev.OfType<T>().FirstOrDefault() is { } evt) {
                return evt;
            }
        }
    }
}
