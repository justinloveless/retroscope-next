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

        // Writers can outrun the clock, so they keep going until the actor has retired a few times (or time runs out).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var asked = 0;
        var writers = Enumerable.Range(0, Writers).Select(_ => Task.Run(async () => {
            for (var n = 0; n < PerWriter || (room.Store.Loads < 3 && DateTime.UtcNow < deadline); n++) {
                var result = await room.Registry.AskAsync<Counter, CounterInput>(Room, new CounterInput.Add(1), null, Token);
                Assert.Equal(AskStatus.Accepted, result.Status);
                Interlocked.Increment(ref asked);
            }
        }, Token));
        await Task.WhenAll(writers);
        await done.CancelAsync();
        await clock;

        Assert.Equal(asked, room.Store.Persisted!.Value);
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
    public async Task Purge_is_refused_while_a_message_is_queued_behind_it() {
        var room = new CounterRoom();
        var actor = await room.ActorAsync();
        var release = room.Store.HoldNextCommit();
        var first = actor.AskAsync(new CounterInput.Add(1), null, Token);
        await room.Store.CommitEntered;
        var purged = false;

        var purge = room.Registry.TryPurgeAsync(Room, _ => {
            purged = true;
            return Task.CompletedTask;
        }, Token);
        var second = actor.AskAsync(new CounterInput.Add(1), null, Token);
        release.SetResult();

        Assert.False(await purge);
        Assert.False(purged);
        Assert.Equal(AskStatus.Accepted, (await first).Status);
        Assert.Equal(AskStatus.Accepted, (await second).Status);
        Assert.Equal(2, room.Store.Persisted!.Value);
        Assert.Equal(1, room.Store.Loads);
        Assert.Same(actor, await room.ActorAsync());
    }

    [Fact]
    public async Task Purge_is_refused_while_a_resume_waits_on_the_internal_queue() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Add(1));
        var (epoch, seq) = await room.QueryAsync(view => (view.Epoch, view.Seq));
        var actor = await room.ActorAsync();
        var release = room.Store.HoldNextCommit();
        var held = actor.AskAsync(new CounterInput.Add(1), null, Token);
        await room.Store.CommitEntered;

        var purge = room.Registry.TryPurgeAsync(Room, _ => Task.CompletedTask, Token);
        var resumed = new TestPeer(Alice);
        Assert.Equal(PostResult.Accepted, actor.Subscribe(resumed, Alice, epoch, seq));
        release.SetResult();

        Assert.False(await purge);
        await held;
        Assert.IsType<SnapFrame>(await resumed.NextAsync());
        Assert.Equal(1, room.Store.Loads);
    }

    [Fact]
    public async Task An_ask_the_actor_accepted_is_never_served_by_the_purged_room() {
        for (var i = 0; i < 300; i++) {
            var room = new CounterRoom();
            await Ask(room, new CounterInput.Add(1));
            var actor = await room.ActorAsync();
            using var start = new Barrier(2);

            var purge = Task.Run(() => {
                start.SignalAndWait(Token);
                return room.Registry.TryPurgeAsync(Room, _ => {
                    room.Store.Erase();
                    return Task.CompletedTask;
                }, Token);
            }, Token);
            var ask = Task.Run(async () => {
                start.SignalAndWait(Token);
                try {
                    return await actor.AskAsync(new CounterInput.Add(1), Alice, Token);
                } catch (ActorClosedException) {
                    return null;
                }
            }, Token);

            // Accepted by the live actor: served before the purge, or the purge was refused, whether the ask was
            // queued before the purge checked the queues or in the instant between that check and the close.
            var purged = await purge;
            if (await ask is { } accepted) {
                Assert.Equal(AskStatus.Accepted, accepted.Status);
            } else {
                Assert.True(purged);
            }
        }
    }

    [Fact]
    public async Task A_message_for_a_key_still_being_purged_is_answered_server_busy_after_the_wait() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Add(1));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var purge = room.Registry.TryPurgeAsync(Room, async _ => {
            entered.SetResult();
            await release.Task;
        }, Token);
        await entered.Task;

        var ask = Ask(room, new CounterInput.Add(1));
        var commander = new TestPeer(Bob);
        var command = room.Registry.CommandAsync(Room, commander, Bob, Cmd(1, new PokerVoteSet(Round(1), 1)), Token).AsTask();
        var subscriber = new TestPeer(Carol);
        var subscribe = room.Registry.SubscribeAsync(Room, subscriber, Carol, null, null, Token).AsTask();
        var query = room.QueryAsync(view => view.Seq);
        Task[] waiting = [ask, command, subscribe, query];
        while (!waiting.All(x => x.IsCompleted)) {
            room.Time.Advance(ActorOptions.Default.IncarnationWait);
            await Task.Delay(10, Token);
        }

        var busy = await ask;
        Assert.Equal((AskStatus.Unavailable, NackCode.ServerBusy), (busy.Status, busy.Code));
        Assert.Equal(PostResult.Busy, await command);
        var nack = await commander.NextAsync<NackFrame>();
        Assert.Equal((1L, NackCode.ServerBusy), (nack.Q, nack.Code));
        Assert.Equal(PostResult.Busy, await subscribe);
        await Assert.ThrowsAsync<RoomUnavailableException>(() => query);
        release.SetResult();
        Assert.True(await purge);
        Assert.Equal(AskStatus.Accepted, (await Ask(room, new CounterInput.Add(1))).Status);
    }

    [Fact]
    public async Task A_message_waiting_on_a_purge_reaches_the_next_incarnation_once_it_finished() {
        var room = new CounterRoom();
        var first = await room.ActorAsync();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var purge = room.Registry.TryPurgeAsync(Room, async _ => {
            entered.SetResult();
            await release.Task;
        }, Token);
        await entered.Task;

        var ask = Ask(room, new CounterInput.Add(2));
        await Task.Delay(20, Token);
        Assert.False(ask.IsCompleted);
        release.SetResult();

        Assert.True(await purge);
        Assert.Equal(AskStatus.Accepted, (await ask).Status);
        Assert.NotSame(first, await room.ActorAsync());
    }

    [Fact]
    public async Task Query_of_a_missing_room_sees_a_clean_not_found_view() {
        var room = new CounterRoom();
        room.Store.Erase();

        var view = await room.QueryAsync(view => (view.Found, view.State, view.CanSubscribe(Alice)));
        var snap = room.Registry.QueryAsync<Counter, CounterInput, byte[]>(Room, view => view.SnapFrameAsync(Alice), Token);

        Assert.Equal((false, null, false), view);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snap);
    }

    [Fact]
    public async Task Query_of_a_deleted_room_sees_a_clean_not_found_view() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Delete());

        var view = await room.QueryAsync(view => (view.Found, view.State, view.CanSubscribe(Alice)));

        Assert.Equal((false, null, false), view);
    }

    [Fact]
    public async Task A_view_kept_past_a_poison_still_answers_from_the_state_it_was_built_for() {
        var room = new CounterRoom();
        await Ask(room, new CounterInput.Add(4));
        var view = await room.QueryAsync(view => view);

        room.Store.FailNextCommit(new TimeoutException("outcome unknown"));
        Assert.Equal(AskStatus.Unavailable, (await Ask(room, new CounterInput.Add(1))).Status);

        Assert.True(view.CanSubscribe(Alice));
        var snap = Assert.IsType<SnapFrame>(WireParser.ParseServer(await view.SnapFrameAsync(Alice)).Frame);
        Assert.Equal("v4", Assert.IsType<PokerSnapshot>(snap.D).Code);
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
    public async Task A_cursor_that_cannot_be_loaded_on_an_access_change_restarts_only_that_peer() {
        var room = new CounterRoom(options: ActorOptions.Default with { CursorTtl = TimeSpan.FromSeconds(1) });
        var (alice, _) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        await Ask(room, new CounterInput.Join(Alice.Who.Id));
        await alice.DrainAsync();
        await bob.DrainAsync();
        room.Time.Advance(TimeSpan.FromSeconds(2));
        room.Store.FailCursorLoads(new InvalidOperationException("database down"));

        room.Registry.AccessChanged(Room);
        await room.SettleAsync();
        room.Store.FailCursorLoads(null);
        await Ask(room, new CounterInput.Add(1));

        await alice.Restarted;
        Assert.Equal(0, bob.Restarts);
        Assert.Equal(1, room.Store.Loads);
        var tx = await bob.NextAsync<TxFrame>();
        Assert.StartsWith("add:1:1", Assert.IsType<PresenceUpdate>(Assert.Single(tx.Ev)).Name, StringComparison.Ordinal);
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

        Assert.Equal(KickCode.TooManyConnections, (await third.NextAsync<KickFrame>()).Code);
    }

    [Fact]
    public async Task A_seventh_tab_of_one_principal_is_kicked_too_many_connections_not_forbidden_and_gets_in_once_a_tab_closes() {
        var room = new CounterRoom();
        Assert.Equal(6, ActorOptions.Default.ConnectionsPerPrincipal);
        var tabs = new List<TestPeer>();
        for (var tab = 0; tab < 6; tab++) {
            tabs.Add((await room.JoinAsync(Viewer(1, "Alice", tab: tab))).Peer);
        }
        var actor = await room.ActorAsync();
        var seventh = new TestPeer(Viewer(1, "Alice", tab: 6));

        actor.Subscribe(seventh, seventh.Viewer);

        // Alice is still a member: the kick must not read as lost access (UX18 decision 2).
        var kick = await seventh.NextAsync<KickFrame>();
        Assert.Equal((Room, KickCode.TooManyConnections), (kick.R, kick.Code));
        await room.JoinAsync(Bob);

        actor.ConnectionClosed(tabs[0].Id);
        await room.SettleAsync();
        var retry = new TestPeer(seventh.Viewer);
        actor.Subscribe(retry, retry.Viewer);

        await retry.NextAsync<SnapFrame>();
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
