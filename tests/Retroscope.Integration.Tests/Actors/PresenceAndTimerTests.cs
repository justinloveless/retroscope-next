using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class PresenceAndTimerTests {
    private static readonly TimeSpan Grace = ActorOptions.Default.PresenceGrace;

    [Fact]
    public async Task Presence_joins_once_per_principal_and_leaves_only_after_the_grace_period() {
        var room = new CounterRoom();
        var (bob, _) = await room.JoinAsync(Bob);
        await bob.DrainAsync();
        var actor = await room.ActorAsync();
        var aliceTab0 = Viewer(1, "Alice");
        var aliceTab1 = Viewer(1, "Alice", tab: 1);

        await room.JoinAsync(aliceTab0);
        Assert.IsType<PresenceJoin>(Assert.Single((await bob.NextAsync<TxFrame>()).Ev));
        await room.JoinAsync(aliceTab1);
        actor.ConnectionClosed(aliceTab1.Conn);
        actor.ConnectionClosed(aliceTab0.Conn);
        await room.SettleAsync();
        room.Time.Advance(Grace / 2);
        var (reconnected, snap) = await room.JoinAsync(Viewer(1, "Alice", connection: 1));
        await room.SettleAsync();
        bob.AssertIdle();
        Assert.Equal(2, Assert.IsType<PokerSnapshot>(snap.D).Presence.Count);

        actor.ConnectionClosed(reconnected.Id);
        await room.SettleAsync();
        room.Time.Advance(Grace);
        await room.SettleAsync();

        var leave = Assert.IsType<PresenceLeave>(Assert.Single((await bob.NextAsync<TxFrame>()).Ev));
        Assert.Equal(Alice.Who.Id, leave.Pid);
        Assert.Equal(
            [
                PresenceChange.Joined,
                PresenceChange.ClientConnected,
                PresenceChange.ClientConnected,
                PresenceChange.ClientGone,
                PresenceChange.Left,
                PresenceChange.ClientGone,
            ],
            room.Domain.Deltas.Where(x => x.Principal == Alice.Who.Id).Select(x => x.Change));
    }

    [Fact]
    public async Task Presence_view_lists_only_connected_tabs_while_the_principal_is_still_present() {
        var room = new CounterRoom();
        var actor = await room.ActorAsync();
        var tab0 = Viewer(1, "Alice");
        var tab1 = Viewer(1, "Alice", tab: 1);
        await room.JoinAsync(tab0);
        await room.JoinAsync(tab1);

        actor.ConnectionClosed(tab1.Conn);
        await room.SettleAsync();
        var view = await room.QueryAsync(x => x.Presence);
        room.Time.Advance(Grace);
        await room.SettleAsync();

        Assert.True(view.IsPresent(Alice.Who.Id));
        Assert.True(view.IsConnected(Alice.Who.Id, tab0.Client));
        Assert.False(view.IsConnected(Alice.Who.Id, tab1.Client));
        var gone = Assert.Single(room.Domain.Deltas, x => x.Change == PresenceChange.ClientGone);
        Assert.Equal(tab1.Client, gone.Client);
    }

    [Fact]
    public async Task Presence_reaction_events_commit_in_their_own_transaction() {
        var room = new CounterRoom();
        room.Domain.PresenceReaction = delta => delta.Change == PresenceChange.Left
            ? [new CounterEvent("add", 100, Core.Poker.EventDurability.Committed)]
            : [];
        var (bob, _) = await room.JoinAsync(Bob);
        await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();

        actor.ConnectionClosed(Alice.Conn);
        await room.SettleAsync();
        room.Time.Advance(Grace);
        await room.SettleAsync();

        Assert.Equal(100, room.Store.Persisted!.Value);
        var tx = await bob.UntilAsync<TxFrame>();
        while (tx.Ev is not [PresenceLeave, ..]) {
            tx = await bob.UntilAsync<TxFrame>();
        }
        Assert.Equal("add:100:100", Assert.IsType<PresenceUpdate>(tx.Ev[1]).Name);
    }

    [Fact]
    public async Task Presence_updates_reach_everyone_in_the_room() {
        var room = new CounterRoom();
        var (bob, _) = await room.JoinAsync(Bob);
        await room.JoinAsync(Alice);
        await room.SettleAsync();
        await bob.DrainAsync();

        room.Registry.PrincipalUpdated(Room, new PrincipalInfo(Alice.Who.Id, PrincipalKind.User, "Alicia"));
        await room.SettleAsync();

        var update = Assert.IsType<PresenceUpdate>(Assert.Single((await bob.NextAsync<TxFrame>()).Ev));
        Assert.Equal((Alice.Who.Id, "Alicia"), (update.Pid, update.Name));
    }

    [Fact]
    public async Task Rooms_without_presence_send_no_presence_events() {
        var room = new CounterRoom(tracksPresence: false);
        var (bob, snap) = await room.JoinAsync(Bob);
        await room.JoinAsync(Alice);
        await room.SettleAsync();

        bob.AssertIdle();
        Assert.Equal(0, snap.S);
    }

    [Fact]
    public async Task Domain_timers_fire_once_for_their_latest_generation() {
        var room = new CounterRoom();
        var actor = await room.ActorAsync();
        var now = room.Time.GetUtcNow();

        await actor.AskAsync(new CounterInput.SetTimer("t", now.AddSeconds(5), new CounterInput.Add(1)), null, Token);
        await actor.AskAsync(new CounterInput.SetTimer("t", now.AddSeconds(10), new CounterInput.Add(100)), null, Token);
        await actor.AskAsync(new CounterInput.SetTimer("u", now.AddSeconds(5), new CounterInput.Add(7)), null, Token);
        await actor.AskAsync(new CounterInput.ClearTimer("u"), null, Token);
        room.Time.Advance(TimeSpan.FromSeconds(5));
        await room.SettleAsync();
        var early = room.Store.Persisted!.Value;
        room.Time.Advance(TimeSpan.FromSeconds(5));
        await room.SettleAsync();

        Assert.Equal(0, early);
        Assert.Equal(100, room.Store.Persisted!.Value);
    }

    [Fact]
    public void Timer_fires_once_per_request_and_stale_generations_are_ignored() {
        var time = Clock();
        var fired = new List<(string Key, long Generation)>();
        var timers = new ActorTimers<string, int>(time, (key, generation) => fired.Add((key, generation)));

        timers.Arm("a", time.GetUtcNow().AddSeconds(1), 1);
        timers.Arm("a", time.GetUtcNow().AddSeconds(1), 1);
        var due = time.GetUtcNow().AddSeconds(2);
        timers.Arm("a", due, 2);
        time.Advance(TimeSpan.FromSeconds(2));

        var (key, generation) = Assert.Single(fired);
        Assert.False(timers.TryTake(key, generation - 1, out _));
        Assert.True(timers.TryTake(key, generation, out var payload));
        Assert.Equal(2, payload);
        Assert.Equal(0, timers.Count);
        timers.Arm("a", due, 2);
        Assert.Equal(0, timers.Count);
        timers.Arm("a", time.GetUtcNow().AddSeconds(1), 3);
        Assert.Equal(1, timers.Count);
    }

    [Fact]
    public async Task Warm_up_runs_once_at_its_deadline() {
        var room = new CounterRoom(new Counter { WarmUntil = Start.AddSeconds(15) });
        await room.JoinAsync(Alice);
        await room.SettleAsync();
        var before = room.Domain.Warms;

        room.Time.Advance(TimeSpan.FromSeconds(15));
        await room.SettleAsync();

        Assert.Equal(0, before);
        Assert.Equal(1, room.Domain.Warms);
    }

    [Fact]
    public async Task Idle_actor_retires_after_the_eviction_delay_unless_a_timer_is_armed() {
        var room = new CounterRoom();
        var actor = (RoomActor<Counter, CounterInput, CounterEvent>)await room.ActorAsync();
        await room.JoinAsync(Alice);
        actor.ConnectionClosed(Alice.Conn);
        await room.SettleAsync();
        room.Time.Advance(Grace);
        await room.SettleAsync();
        await actor.AskAsync(new CounterInput.SetTimer("later", Start.AddHours(1), new CounterInput.Add(1)), null, Token);

        room.Time.Advance(ActorOptions.Default.IdleEviction);
        await room.SettleAsync();
        Assert.False(actor.Completion.IsCompleted);
        await actor.AskAsync(new CounterInput.ClearTimer("later"), null, Token);
        room.Time.Advance(ActorOptions.Default.IdleEviction);

        await actor.Completion.WaitAsync(Wait, Token);
        Assert.Equal(0, room.Registry.Count);
    }
}
