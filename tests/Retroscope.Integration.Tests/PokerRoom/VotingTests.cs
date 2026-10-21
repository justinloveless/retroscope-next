using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.PokerRoom;

[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class VotingTests(PostgresFixture postgres) {
    [Fact]
    public async Task Auto_reveal_waits_for_warm_up_and_then_fires_once_for_five_clients() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var people = new List<Principal>();
        for (var i = 0; i < 5; i++) {
            people.Add(await scope.Seed.UserAsync($"Voter {i}"));
        }
        var room = await scope.Seed.SessionAsync(people[0]);
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        var tabs = new List<PokerSocket>();
        foreach (var who in people) {
            tabs.Add(await host.JoinAsync(who, room));
        }
        try {
            for (var i = 0; i < tabs.Count; i++) {
                await tabs[i].AckedAsync(room, new PokerVoteSet(r1, Deck.Values[i], Lock: true));
            }
            await host.AdvanceAsync(room, TimeSpan.FromSeconds(5));
            Assert.Equal(RoundState.Voting, await host.QueryAsync(room, x => x.Rounds[r1].State));

            await host.AdvanceAsync(room, PokerTiming.WarmUp);
            await host.AdvanceAsync(room, PokerTiming.AutoRevealDelay);
            foreach (var tab in tabs) {
                await tab.WaitForEventAsync<PokerRoundRevealed>();
            }
            await host.AdvanceAsync(room, TimeSpan.FromSeconds(30));

            foreach (var tab in tabs) {
                var revealed = Assert.Single(tab.Events().OfType<PokerRoundRevealed>());
                Assert.Null(revealed.By);
                Assert.Equal(5, revealed.Votes.Count);
            }
            var round = (await scope.Seed.RowsAsync(
                "select state, revealed_by, revealed_at is not null from poker_rounds where id = $1", r1.Value)).Single();
            Assert.Equal(["revealed", null, true], round);
            await host.AssertPersistedAsync(room);
        } finally {
            foreach (var tab in tabs) {
                await tab.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Auto_reveal_after_warm_up_fires_once_the_last_present_voter_locks() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice);
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await host.AdvanceAsync(room, PokerTiming.WarmUp);

        await a.AckedAsync(room, new PokerVoteSet(r1, 3, Lock: true));
        await host.AdvanceAsync(room, PokerTiming.AutoRevealDelay);
        Assert.Empty(a.Events().OfType<PokerRoundRevealed>());
        await b.AckedAsync(room, new PokerAbstainSet(r1, true));
        await host.AdvanceAsync(room, PokerTiming.AutoRevealDelay);

        var revealed = (PokerRoundRevealed)await b.WaitForEventAsync<PokerRoundRevealed>();
        Assert.Equal((short)3, revealed.Winner);
        Assert.Single(a.Events().OfType<PokerRoundRevealed>());
    }

    [Fact]
    public async Task No_frame_shows_a_vote_value_to_another_principal_while_the_round_is_voting() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var room = await scope.Seed.SessionAsync(alice);
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var secondTab = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);

        await a.AckedAsync(room, new PokerVoteSet(r1, 13));
        await a.AckedAsync(room, new PokerVoteSet(r1, 21, Lock: true));
        await b.AckedAsync(room, new PokerVoteSet(r1, 1));
        var (epoch, seq) = b.Cursor(room);
        await using var late = await host.JoinAsync(bob, room);
        await using var resumed = await PokerSocket.ConnectAsync(host, bob);
        var replay = await resumed.SubscribeAsync(room, epoch, seq - 2);

        Assert.IsType<ReplayFrame>(replay);
        foreach (var tab in new[] { b, late, resumed }) {
            Assert.DoesNotContain(VotesOf(tab), x => x.Pid == alice.Id && (x.Points is not null || x.High is not null));
            Assert.Contains(VotesOf(tab), x => x.Pid == alice.Id && x.Status == VoteStatus.Locked);
            Assert.DoesNotContain(tab.Raw, x => x.Contains("\"points\":21", StringComparison.Ordinal));
        }
        Assert.Contains(VotesOf(secondTab), x => x.Pid == alice.Id && x.Points == 21);
        var lateOwn = ((PokerOverlay)late.Frames().OfType<SnapFrame>().Single().O!).Own;
        Assert.Equal([(bob.Id, (short?)1)], lateOwn.Select(x => (x.Pid, x.Points)));

        await b.AckedAsync(room, new PokerRoundReveal(r1));
        var revealed = (PokerRoundRevealed)await late.WaitForEventAsync<PokerRoundRevealed>();
        Assert.Contains(revealed.Votes, x => x.Pid == alice.Id && x.Points == 21);
    }

    private static IEnumerable<PokerVoteState> VotesOf(PokerSocket tab) {
        foreach (var frame in tab.Frames()) {
            switch (frame) {
                case SnapFrame { D: PokerSnapshot snapshot }:
                    foreach (var vote in snapshot.Votes) {
                        yield return vote;
                    }
                    break;
                case ReplayFrame replay:
                    foreach (var vote in replay.Txs.SelectMany(x => x.Ev).OfType<PokerVoteState>()) {
                        yield return vote;
                    }
                    break;
                case TxFrame tx:
                    foreach (var vote in tx.Ev.OfType<PokerVoteState>()) {
                        yield return vote;
                    }
                    break;
            }
        }
    }
}
