using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>Who may subscribe, what a kick does to the connection, presence, deletion and the team notices.</summary>
[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RoomTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_non_member_is_kicked_from_a_team_session_and_its_commands_are_not_subscribed() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var mallory = await scope.Seed.UserAsync("Mallory");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner));
        var room = await scope.Seed.SessionAsync(alice, team);
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var m = await PokerSocket.ConnectAsync(host, mallory);

        var kick = Assert.IsType<KickFrame>(await m.SubscribeAsync(room));
        var nack = await m.NackedAsync(room, new PokerVoteSet(r1, 3));

        Assert.Equal(KickCode.Forbidden, kick.Code);
        Assert.Equal(NackCode.NotSubscribed, nack.Code);
        Assert.Equal(0L, await scope.Seed.ScalarAsync<long>("select count(*) from poker_votes"));
    }

    [Fact]
    public async Task Missing_sessions_soft_deleted_teams_and_unserved_room_kinds_are_not_found() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner));
        var gone = await scope.Seed.SessionAsync(alice, team);
        await scope.Seed.ExecuteAsync("update teams set deleted_at = now() where id = $1", team.Value);
        var host = await scope.StartAsync();
        await using var a = await PokerSocket.ConnectAsync(host, alice);

        var missing = await a.SubscribeAsync(RoomKey.Poker(new SessionId(Guid.CreateVersion7())));
        var deletedTeam = await a.SubscribeAsync(gone);
        var board = await a.SubscribeAsync(RoomKey.Board(new BoardId(Guid.CreateVersion7())));

        Assert.All([missing, deletedTeam, board], x => Assert.Equal(KickCode.NotFound, Assert.IsType<KickFrame>(x).Code));
    }

    [Fact]
    public async Task A_kicked_room_no_longer_counts_against_the_connection_room_cap() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var room = await scope.Seed.SessionAsync(alice);
        var host = await scope.StartAsync();
        await using var a = await PokerSocket.ConnectAsync(host, alice);

        for (var i = 0; i < 12; i++) {
            var kick = Assert.IsType<KickFrame>(await a.SubscribeAsync(RoomKey.Poker(new SessionId(Guid.CreateVersion7()))));
            Assert.Equal(KickCode.NotFound, kick.Code);
        }

        Assert.IsType<SnapFrame>(await a.SubscribeAsync(room));
    }

    [Fact]
    public async Task A_restarted_room_gives_the_spotlight_holder_the_warm_up_to_come_back() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice, null, "RS-1", "RS-2");
        var rounds = await scope.Seed.RoundsAsync(room);
        var host = await scope.StartAsync();
        await using (var a = await host.JoinAsync(alice, room)) {
            await a.AckedAsync(room, new PokerSpotlightTake(rounds[1], false));
        }

        host = await scope.RestartAsync();
        await using var b = await host.JoinAsync(bob, room);
        await host.AdvanceAsync(room, PokerTiming.WarmUp - TimeSpan.FromSeconds(1));
        Assert.Equal(alice.Id, await host.QueryAsync(room, x => x.Spotlight?.Holder));
        await host.AdvanceAsync(room, TimeSpan.FromSeconds(1));

        await b.WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PokerSpotlight { Holder: null }));
        Assert.Null(await scope.Seed.ScalarAsync<object>("select spotlight_holder_id from poker_sessions where id = $1", room.Id));
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task Presence_joins_and_leaves_after_the_grace_period() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        var b = await host.JoinAsync(bob, room);
        await a.WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PresenceJoin join && join.Pid == bob.Id));

        await b.DisposeAsync();
        while (await host.Registry.QueryAsync<PokerState, PokerInput, int>(
            room, view => ValueTask.FromResult(view.Presence.ClientsOf(bob.Id).Count), PokerHost.Token) > 0) {
            await Task.Delay(10, PokerHost.Token);
        }
        await host.AdvanceAsync(room, TimeSpan.FromSeconds(10));

        await a.WaitForAsync<TxFrame>(x => x.Ev.Any(e => e is PresenceLeave leave && leave.Pid == bob.Id));
    }

    [Fact]
    public async Task Deleting_the_session_tells_everyone_kicks_them_and_removes_the_rows() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await b.AckedAsync(room, new PokerVoteSet((await scope.Seed.RoundsAsync(room))[0], 2));
        var bobViewer = new Viewer(bob, b.Client, new ConnectionId(Guid.CreateVersion7()));
        var aliceViewer = new Viewer(alice, a.Client, new ConnectionId(Guid.CreateVersion7()));

        var refused = await host.Registry.AskAsync<PokerState, PokerInput>(room, new DeleteSession(), bobViewer, PokerHost.Token);
        var deleted = await host.Registry.AskAsync<PokerState, PokerInput>(room, new DeleteSession(), aliceViewer, PokerHost.Token);

        Assert.Equal((AskStatus.Rejected, NackCode.Forbidden), (refused.Status, refused.Code));
        Assert.Equal(AskStatus.Accepted, deleted.Status);
        foreach (var tab in new[] { a, b }) {
            await tab.WaitForEventAsync<PokerSessionDeleted>();
            Assert.Equal(KickCode.Deleted, (await tab.WaitForAsync<KickFrame>()).Code);
        }
        Assert.Equal(0L, await scope.Seed.ScalarAsync<long>("select count(*) from poker_sessions"));
        Assert.Equal(0L, await scope.Seed.ScalarAsync<long>("select count(*) from client_cursors"));
        Assert.Equal(KickCode.NotFound, Assert.IsType<KickFrame>(await a.SubscribeAsync(room)).Code);
    }

    [Fact]
    public async Task The_team_hears_when_the_session_tallies_change() {
        var notices = new FakeTeamNotices();
        await using var scope = await PokerScope.CreateAsync(postgres, notices: notices);
        var alice = await scope.Seed.UserAsync("Alice");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner));
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);

        await a.AckedAsync(room, new PokerVoteSet(r1, 5, Lock: true));
        await a.AckedAsync(room, new PokerRoundReveal(r1));
        await a.AckedAsync(room, new PokerRoundCreate(new RoundId(Guid.CreateVersion7()), "RS-2", ForceNew: true));
        await a.AckedAsync(room, new PokerSpotlightTake(r1, false));
        await a.AckedAsync(room, new PokerRoundCreate(new RoundId(Guid.CreateVersion7()), "RS-3", Pending: true));
        // The tallies are compared after the message that changed them, which ends after its ack went out.
        await host.QueryAsync(room, _ => 0);

        Assert.Equal([(new SessionId(room.Id), 1, 1), (new SessionId(room.Id), 1, 2)], notices.Poker);
    }
}
