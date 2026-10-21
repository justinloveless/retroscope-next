using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class RegistryTests {
    [Fact]
    public async Task Ask_reports_accept_reject_noop_and_missing_rooms() {
        var room = new CounterRoom();
        var missing = RoomKey.Poker(new SessionId(Id(0xDEAD)));

        var accepted = await Ask(room, new CounterInput.Add(3));
        var rejected = await Ask(room, new CounterInput.Client(new PokerRoundReveal(Round(1))));
        var noop = await Ask(room, new CounterInput.Client(new PokerSpotlightRelease()));
        room.Store.Erase();
        var notFound = await room.Registry.AskAsync<Counter, CounterInput>(missing, new CounterInput.Add(1), Alice, Token);

        Assert.Equal((AskStatus.Accepted, 1L), (accepted.Status, accepted.Seq));
        Assert.Equal((AskStatus.Rejected, NackCode.RoundRevealed), (rejected.Status, rejected.Code));
        Assert.Equal((AskStatus.NoOp, 1L), (noop.Status, noop.Seq));
        Assert.Equal(AskStatus.NotFound, notFound.Status);
    }

    [Fact]
    public async Task Query_builds_the_boot_snapshot_on_the_actor() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Add(2));

        var bytes = await room.Registry.QueryAsync<Counter, CounterInput, byte[]>(
            Room,
            view => view.CanSubscribe(Alice) ? view.SnapFrameAsync(Alice) : ValueTask.FromResult<byte[]>([]),
            Token);

        var snap = Assert.IsType<SnapFrame>(WireParser.ParseServer(bytes).Frame);
        Assert.Equal((1L, 0L, "v2"), (snap.S, snap.LastQ, Assert.IsType<PokerSnapshot>(snap.D).Code));
    }

    [Fact]
    public async Task Retiring_actor_loses_no_message_posted_while_it_closes() {
        var room = new CounterRoom(options: ActorOptions.Default with { IdleEviction = TimeSpan.FromMilliseconds(1) });
        const int Writers = 4;
        const int PerWriter = 200;
        using var done = new CancellationTokenSource();
        var clock = Task.Run(async () => {
            while (!done.IsCancellationRequested) {
                room.Time.Advance(TimeSpan.FromMilliseconds(1));
                await Task.Yield();
            }
        }, Token);

        var writers = Enumerable.Range(0, Writers).Select(_ => Task.Run(async () => {
            for (var n = 0; n < PerWriter; n++) {
                var result = await room.Registry.AskAsync<Counter, CounterInput>(Room, new CounterInput.Add(1), null, Token);
                Assert.Equal(AskStatus.Accepted, result.Status);
            }
        }, Token));
        await Task.WhenAll(writers);
        await done.CancelAsync();
        await clock;

        Assert.Equal(Writers * PerWriter, room.Store.Persisted!.Value);
        Assert.True(room.Store.Loads > 1, "the actor never retired during the run");
    }

    [Fact]
    public async Task Purge_is_refused_while_anyone_is_subscribed() {
        var room = new CounterRoom();
        await room.JoinAsync(Alice);
        var purged = false;

        var result = await room.Registry.TryPurgeAsync(Room, _ => {
            purged = true;
            return Task.CompletedTask;
        }, Token);

        Assert.False(result);
        Assert.False(purged);
    }

    [Fact]
    public async Task Purge_closes_the_key_so_nothing_loads_the_room_until_it_finished() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Add(1));
        var loadsBefore = room.Store.Loads;
        var release = new TaskCompletionSource();
        Task<IRoomActor>? waiting = null;

        var purge = room.Registry.TryPurgeAsync(Room, async _ => {
            waiting = room.Registry.GetOrStartAsync(Room, Token).AsTask();
            await Task.Delay(50, Token);
            Assert.False(waiting.IsCompleted);
            room.Store.Erase();
            await release.Task;
        }, Token);
        await Task.Delay(20, Token);
        release.SetResult();

        Assert.True(await purge);
        var actor = await waiting!;
        var peer = new TestPeer(Alice);
        actor.Subscribe(peer, Alice);
        Assert.Equal(KickCode.NotFound, (await peer.NextAsync<KickFrame>()).Code);
        Assert.Equal(loadsBefore + 1, room.Store.Loads);
    }

    [Fact]
    public async Task Messages_queued_behind_a_purge_go_to_the_next_incarnation() {
        var room = new CounterRoom();
        var actor = await room.ActorAsync();
        var release = room.Store.HoldNextCommit();
        var first = actor.AskAsync(new CounterInput.Add(1), null, Token);
        await room.Store.CommitEntered;

        var purge = room.Registry.TryPurgeAsync(Room, _ => Task.CompletedTask, Token);
        var second = actor.AskAsync(new CounterInput.Add(1), null, Token);
        release.SetResult();

        Assert.True(await purge);
        Assert.Equal(AskStatus.Accepted, (await first).Status);
        Assert.Equal(AskStatus.Accepted, (await second).Status);
        Assert.Equal(2, room.Store.Persisted!.Value);
        Assert.Equal(2, room.Store.Loads);
        Assert.NotSame(actor, await room.ActorAsync());
    }

    [Fact]
    public async Task Shutdown_runs_the_last_input_and_restarts_every_subscriber() {
        var room = new CounterRoom(new Counter { OnShutdown = new CounterInput.Add(5) });
        var (alice, _) = await room.JoinAsync(Alice);

        await room.Registry.ShutdownAsync(Token);

        await alice.Restarted;
        Assert.Equal(5, room.Store.Persisted!.Value);
        var late = new TestPeer(Bob);
        await room.Registry.SubscribeAsync(Room, late, Bob, null, null, Token);
        await late.Restarted;
    }

    [Fact]
    public async Task Access_change_kicks_who_lost_access_and_resnaps_who_changed_variant() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        await Ask(room, new CounterInput.Ban(Bob.Who.Id));
        await Ask(room, new CounterInput.Join(Alice.Who.Id));
        await alice.DrainAsync();

        room.Registry.AccessChanged(Room);
        await room.SettleAsync();

        Assert.Equal(KickCode.AccessRevoked, (await bob.UntilAsync<KickFrame>()).Code);
        Assert.Equal("v0-members", Assert.IsType<PokerSnapshot>((await alice.NextAsync<SnapFrame>()).D).Code);
        var actor = await room.ActorAsync();
        actor.Command(bob, Bob, Cmd(1, new PokerVoteSet(Round(1), 1)));
        Assert.Equal(NackCode.NotSubscribed, (await bob.NextAsync<NackFrame>()).Code);
    }

    [Fact]
    public async Task Deleted_room_kicks_everyone_and_refuses_new_subscriptions() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);

        await Ask(room, new CounterInput.Delete());
        var late = new TestPeer(Bob);
        (await room.ActorAsync()).Subscribe(late, Bob);

        Assert.Equal(KickCode.Deleted, (await alice.UntilAsync<KickFrame>()).Code);
        Assert.Equal(KickCode.NotFound, (await late.NextAsync<KickFrame>()).Code);
    }

    [Fact]
    public async Task A_principal_may_hold_only_so_many_connections_in_one_room() {
        var room = new CounterRoom(options: ActorOptions.Default with { ConnectionsPerPrincipal = 2 });
        await room.JoinAsync(Viewer(1, "Alice", connection: 0));
        await room.JoinAsync(Viewer(1, "Alice", connection: 1));
        var third = new TestPeer(Viewer(1, "Alice", connection: 2));

        (await room.ActorAsync()).Subscribe(third, third.Viewer);

        Assert.Equal(KickCode.Forbidden, (await third.NextAsync<KickFrame>()).Code);
    }

    [Fact]
    public async Task Room_without_a_registered_type_cannot_start() {
        var room = new CounterRoom();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => room.Registry.GetOrStartAsync(RoomKey.Team(new TeamId(Id(1))), Token).AsTask());
    }

    private static Task<AskResult> Ask(CounterRoom room, CounterInput input) {
        return room.Registry.AskAsync<Counter, CounterInput>(Room, input, Alice, Token);
    }
}
