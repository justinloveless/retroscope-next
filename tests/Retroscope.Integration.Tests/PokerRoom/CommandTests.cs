using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>
/// Every poker command over real WebSockets, each followed by the Plan/Load round trip: the actor's state and a
/// fresh load from the database describe the same session.
/// </summary>
[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class CommandTests(PostgresFixture postgres) {
    [Fact]
    public async Task Every_team_session_command_persists_what_the_actor_applied() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var carol = await scope.Seed.UserAsync("Carol");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner), (bob, TeamRole.Member), (carol, TeamRole.Member));
        await scope.Seed.JiraAsync(team);
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        await using var c = await host.JoinAsync(carol, room);

        async Task<IReadOnlyList<WireEvent>> Step(PokerSocket tab, Command command) {
            var events = await tab.AckedAsync(room, command);
            await host.AssertPersistedAsync(room);
            return events;
        }

        Assert.Contains(await Step(a, new PokerVoteSet(r1, 5)), x => x is PokerVoteState { Points: 5 });
        Assert.Contains(await Step(b, new PokerVoteSet(r1, 3, High: 5, Lock: true)), x => x is PokerVoteState { High: 5, Locked: true });
        await Step(a, new PokerLockSet(r1, true));
        await Step(c, new PokerAbstainSet(r1, true));
        await Step(c, new PokerAbstainSet(r1, false));
        Assert.Contains(await Step(a, new PokerRoundReveal(r1)), x => x is PokerRoundRevealed { Winner: 5 });
        Assert.Contains(await Step(b, new PokerRoundReplay(r1)), x => x is PokerRoundReplayed);
        await Step(a, new PokerRoundReveal(r1));

        var r2 = NewRound();
        Assert.Contains(await Step(a, new PokerRoundCreate(r2, "RS-2")), x => x is PokerRoundCreated);
        var (r3, r4) = (NewRound(), NewRound());
        await Step(b, new PokerRoundCreateMany([new NewRound(r3, "RS-3"), new NewRound(r4, "RS-4")]));
        Assert.Contains(await Step(c, new PokerRoundSetTicket(r2, "12", 1)), x => x is PokerRoundUpdated { Round.TicketKey: "RS-12" });
        await Step(a, new PokerRoundEnd(r2));
        await Step(a, new PokerRoundActivate(r2));
        var r5 = NewRound();
        Assert.Contains(await Step(b, new PokerRoundNext(r4, r5, "RS-5")), x => x is PokerPointer { Nav: true });

        await Step(a, new PokerSpotlightTake(r5, false));
        await a.AckedAsync(room, new PokerSpotlightMove(r3));
        await host.AdvanceAsync(room, TimeSpan.FromMilliseconds(500));
        await host.AssertPersistedAsync(room);
        await Step(a, new PokerSpotlightGive(bob.Id));
        await Step(b, new PokerSpotlightRelease());

        await Step(a, new PokerSpotlightTake(r5, false));
        var r6 = NewRound();
        await Step(a, new PokerRoundCreate(r6, "RS-6", Pending: true));
        await Step(a, new PokerRoundCommitPending(r6));
        var r7 = NewRound();
        await Step(a, new PokerRoundCreate(r7, "RS-7", Pending: true));
        Assert.Contains(await Step(a, new PokerRoundDiscardPending(r7)), x => x is PokerRoundDeleted);

        var order = await host.QueryAsync(room, x => x.Order.ToArray());
        Assert.Contains(await Step(c, new PokerRoundReorder([.. order.Reverse()])), x => x is PokerRoundOrder);
        await Step(b, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: true), PresenceEnabled: false));
        await Step(b, new PokerObserverSet(carol.Id, true));
        await Step(a, new PokerRoundDelete(r3));
        Assert.Contains(await Step(a, new PokerRoundsClearAll(NewRound())), x => x is PokerRoundsReset);
    }

    [Fact]
    public async Task Quick_session_seats_voters_and_reloads_them_from_their_votes() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var gus = await scope.Seed.GuestAsync("Gus");
        var room = await scope.Seed.SessionAsync(alice);
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var g = await host.JoinAsync(gus, room);

        Assert.Contains(await g.AckedAsync(room, new PokerVoteSet(r1, 8)), x => x is PokerRoster);
        await host.AssertPersistedAsync(room);
        var r2 = NewRound();
        await g.AckedAsync(room, new PokerRoundCreate(r2, ForceNew: true));
        Assert.Equal(NackCode.Forbidden, (await g.NackedAsync(room, new PokerRoundDelete(r2))).Code);
        await a.AckedAsync(room, new PokerRoundDelete(r2));
        await host.AssertPersistedAsync(room);

        var loaded = await host.Store.LoadAsync(room, scope.Time.GetUtcNow(), PokerHost.Token);
        Assert.Equal([gus.Id], loaded!.Roster.Keys);
        Assert.Equal("Gus", loaded.Roster[gus.Id].Name);
        Assert.Equal(PrincipalKind.Guest, loaded.Roster[gus.Id].Kind);
    }

    [Fact]
    public async Task Rejections_reach_the_client_as_nacks_and_change_nothing() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var bob = await scope.Seed.UserAsync("Bob");
        var team = await scope.Seed.TeamAsync((alice, TeamRole.Owner), (bob, TeamRole.Member));
        var room = await scope.Seed.SessionAsync(alice, team, "RS-1");
        var r1 = (await scope.Seed.RoundsAsync(room))[0];
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);
        await using var b = await host.JoinAsync(bob, room);
        var before = await host.QueryAsync(room, PokerHost.Describe);

        var stale = await b.NackedAsync(room, new PokerRoundSetTicket(r1, "RS-2", 7));
        var forbidden = await b.NackedAsync(room, new PokerRoundsClearAll(NewRound()));
        await a.AckedAsync(room, new PokerObserverSet(bob.Id, true));
        var observer = await b.NackedAsync(room, new PokerVoteSet(r1, 3));

        Assert.Equal(NackCode.Stale, stale.Code);
        Assert.Equal("RS-1", stale.D!.Value.GetProperty("current").GetProperty("ticketKey").GetString());
        Assert.Equal(NackCode.Forbidden, forbidden.Code);
        Assert.Equal(NackCode.Observer, observer.Code);
        Assert.Equal(before.Replace("observers=", $"observers={bob.Id}", StringComparison.Ordinal), await host.QueryAsync(room, PokerHost.Describe));
        await host.AssertPersistedAsync(room);
    }

    [Fact]
    public async Task A_resent_command_is_acked_as_a_duplicate_and_applied_once() {
        await using var scope = await PokerScope.CreateAsync(postgres);
        var alice = await scope.Seed.UserAsync("Alice");
        var room = await scope.Seed.SessionAsync(alice);
        var host = await scope.StartAsync();
        await using var a = await host.JoinAsync(alice, room);

        var round = NewRound();
        Assert.IsType<AckFrame>(await a.CommandAsync(room, new PokerRoundCreate(round, ForceNew: true), 5));
        var again = Assert.IsType<AckFrame>(await a.CommandAsync(room, new PokerRoundCreate(round, ForceNew: true), 5));

        Assert.True(again.Dup);
        Assert.Equal(2, (await scope.Seed.RoundsAsync(room)).Length);
        Assert.Equal(5L, await scope.Seed.ScalarAsync<long>("select last_cseq from client_cursors where client_id = $1", a.Client.Value));
    }

    private static RoundId NewRound() => new(Guid.CreateVersion7());
}
