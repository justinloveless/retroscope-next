using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.PokerRoom;

[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class PersistenceTests(PostgresFixture postgres) {
    [Fact]
    public async Task Reveal_turns_unlocked_votes_into_abstains_in_sql_and_stores_the_winner() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var (alice, bob, carol) = (await scope.Seed.UserAsync("Alice"), await scope.Seed.UserAsync("Bob"), await scope.Seed.UserAsync("Carol"));
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice, bob, carol));
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await using var c = await host.JoinAsync(carol, room);
        await a.AckedAsync(room, new PokerVoteSet(r1, 8, Lock: true));
        await b.AckedAsync(room, new PokerVoteSet(r1, 13));
        await c.AckedAsync(room, new PokerAbstainSet(r1, true));

        await b.AckedAsync(room, new PokerRoundReveal(r1));

        var votes = await scope.Seed.RowsAsync(
            "select principal_id, points, locked, abstained from poker_votes where round_id = $1 order by principal_id", r1.Value);
        var expected = new[] {
            new object?[] { alice.Id.Value, (short)8, true, false },
            new object?[] { bob.Id.Value, null, true, true },
            new object?[] { carol.Id.Value, null, true, true },
        }.OrderBy(x => (Guid)x[0]!);
        Assert.Equal(expected, votes);
        var round = (await scope.Seed.RowsAsync(
            "select state, winner_points, revealed_by, revealed_at is not null from poker_rounds where id = $1", r1.Value)).Single();
        Assert.Equal(["revealed", (short)8, bob.Id.Value, true], round);
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task Reorder_rewrites_dense_sort_keys_under_the_deferred_unique_constraint() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice), "RS-1", "RS-2", "RS-3", "RS-4");
        var rounds = await scope.Seed.RoundsAsync(room);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);

        RoundId[] order = [rounds[2], rounds[0], rounds[3], rounds[1]];
        await a.AckedAsync(room, new PokerRoundReorder(order));
        await a.AckedAsync(room, new PokerRoundDelete(rounds[0]));
        var added = new RoundId(Guid.CreateVersion7());
        await a.AckedAsync(room, new PokerRoundCreate(added, "RS-9", ForceNew: true));

        var keys = await scope.Seed.RowsAsync("select id, sort_key from poker_rounds where session_id = $1 order by sort_key", room.Id);
        Assert.Equal([rounds[2].Value, rounds[3].Value, rounds[1].Value, added.Value], keys.Select(x => (Guid)x[0]!));
        Assert.Equal([0, 1, 2, 3], keys.Select(x => (int)x[1]!));
        var stale = await a.NackedAsync(room, new PokerRoundReorder([rounds[2], rounds[3]]));
        Assert.Equal(NackCode.Stale, stale.Code);
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task Deleting_the_spotlight_round_repairs_the_spotlight_in_the_same_transaction() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Admin), (bob, TeamRole.Member));
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1", "RS-2");
        var rounds = await scope.Seed.RoundsAsync(room);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await b.AckedAsync(room, new PokerSpotlightTake(rounds[1], false));

        var events = await a.AckedAsync(room, new PokerRoundDelete(rounds[1]));

        Assert.Contains(events, x => x is PokerSpotlight s && s.Holder == bob.Id && s.RoundId == rounds[0]);
        var spotlight = (await scope.Seed.RowsAsync(
            "select spotlight_holder_id, spotlight_round_id from poker_sessions where id = $1", room.Id)).Single();
        Assert.Equal([bob.Id.Value, rounds[0].Value], spotlight);
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task Clear_all_with_the_spotlight_held_keeps_the_holder_on_the_new_first_round() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice, bob), "RS-1", "RS-2", "RS-3");
        var rounds = await scope.Seed.RoundsAsync(room);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await b.AckedAsync(room, new PokerSpotlightTake(rounds[2], false));
        await b.AckedAsync(room, new PokerSpotlightMove(rounds[1]));
        await a.AckedAsync(room, new PokerVoteSet(rounds[1], 3));

        var fresh = new RoundId(Guid.CreateVersion7());
        var events = await a.AckedAsync(room, new PokerRoundsClearAll(fresh));

        Assert.Contains(events, x => x is PokerRoundsReset reset && reset.Round.Id == fresh && reset.Round.Number == 1);
        var session = (await scope.Seed.RowsAsync(
            "select spotlight_holder_id, spotlight_round_id, current_round_id, next_round_number from poker_sessions where id = $1",
            room.Id)).Single();
        Assert.Equal([bob.Id.Value, fresh.Value, fresh.Value, 2], session);
        Assert.Equal([fresh], await scope.Seed.RoundsAsync(room));
        Assert.Equal(0L, await scope.Seed.ScalarAsync<long>("select count(*) from poker_votes"));
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task A_moved_spotlight_is_persisted_once_it_settles_and_on_shutdown() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice), "RS-1", "RS-2", "RS-3");
        var rounds = await scope.Seed.RoundsAsync(room);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await a.AckedAsync(room, new PokerSpotlightTake(rounds[0], false));
        Task<object?> Persisted() => scope.Seed.ScalarAsync<object>("select spotlight_round_id from poker_sessions where id = $1", room.Id);

        await a.AckedAsync(room, new PokerSpotlightMove(rounds[1]));
        await host.AdvanceAsync(room, TimeSpan.FromMilliseconds(300));
        await a.AckedAsync(room, new PokerSpotlightMove(rounds[2]));
        await host.AdvanceAsync(room, TimeSpan.FromMilliseconds(300));
        Assert.Equal(rounds[0].Value, await Persisted());
        await host.AdvanceAsync(room, TimeSpan.FromMilliseconds(200));
        Assert.Equal(rounds[2].Value, await Persisted());
        Assert.Equal(a.Client.Value, await scope.Seed.ScalarAsync<object>("select spotlight_client_id from poker_sessions where id = $1", room.Id));

        await a.AckedAsync(room, new PokerSpotlightMove(rounds[1]));
        await scope.RestartAsync();

        Assert.Equal(rounds[1].Value, await Persisted());
        Assert.Equal(CloseCodes.Restart, await a.WaitForCloseAsync());
    }

    [Fact]
    public async Task A_crash_mid_round_loses_no_acked_vote_and_keeps_them_secret_after_reload() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice, bob));
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        var a = await host.JoinAsync(alice, room);
        var b = await host.JoinAsync(bob, room);
        await a.AckedAsync(room, new PokerVoteSet(r1, 5, Lock: true));
        await b.AckedAsync(room, new PokerVoteSet(r1, 2));
        var (epoch, seq) = a.Cursor(room);
        // Acked means committed: the rows are there before anything stops.
        Assert.Equal(2L, await scope.Seed.ScalarAsync<long>("select count(*) from poker_votes where round_id = $1", r1.Value));
        await a.DisposeAsync();
        await b.DisposeAsync();

        host = await scope.RestartAsync();
        await using var again = await PokerSocket.ConnectAsync(host, alice, a.Client);
        var snap = Assert.IsType<SnapFrame>(await again.SubscribeAsync(room, epoch, seq));
        await using var other = await host.JoinAsync(bob, room);
        var bobSnap = other.Frames().OfType<SnapFrame>().Single();

        Assert.NotEqual(epoch, snap.E);
        Assert.Equal(1, snap.LastQ);
        var own = Assert.Single(((PokerOverlay)snap.O!).Own);
        Assert.Equal(((short?)5, true), (own.Points, own.Locked));
        var bobSeesAlice = ((PokerSnapshot)bobSnap.D).Votes.Single(x => x.Pid == alice.Id);
        Assert.Equal((VoteStatus.Locked, (short?)null), (bobSeesAlice.Status, bobSeesAlice.Points));
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task The_snapshot_carries_the_sessions_created_at_in_unix_milliseconds_across_a_restart() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var room = await scope.Seed.SessionAsync(alice, await scope.Seed.TeamOfAsync(alice));
        // Not the time in the session's id, as for an imported session; Postgres keeps the microseconds.
        var created = new DateTimeOffset(2024, 3, 5, 14, 30, 15, 123, TimeSpan.Zero).AddTicks(4_560);
        await scope.Seed.ExecuteAsync("update poker_sessions set created_at = $2 where id = $1", room.Id, created);
        var host = await scope.StartAsync();

        await using (var a = await host.JoinAsync(alice, room)) {
            var snapshot = Assert.IsType<PokerSnapshot>(a.Frames().OfType<SnapFrame>().Single().D);
            Assert.Equal(created.ToUnixTimeMilliseconds(), snapshot.CreatedAt);
            Assert.Equal(created, await host.QueryAsync(room, x => x.CreatedAt));
        }
        await host.AssertPersistedAsync(room);

        host = await scope.RestartAsync();
        await using var again = await host.JoinAsync(alice, room);
        var reloaded = Assert.IsType<PokerSnapshot>(again.Frames().OfType<SnapFrame>().Single().D);
        Assert.Equal(1709649015123L, reloaded.CreatedAt);
    }
}
