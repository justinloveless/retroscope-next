using System.Text;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class RingAndResumeTests {
    private static readonly Audience AliceAudience = new(Alice.Who.Id, false);

    [Fact]
    public void Ring_is_bounded_in_bytes_and_drops_its_oldest_entries() {
        var budget = new RingBudget(1_000_000);
        var ring = new ByteBoundedRing(1000, budget);

        for (var seq = 1; seq <= 20; seq++) {
            ring.Append(seq, Entry(AliceAudience, 150));
        }

        Assert.True(ring.Bytes <= 1000);
        Assert.True(ring.FirstSeq > 1);
        Assert.Equal(ring.Bytes, budget.TotalBytes);
        var txs = new List<(long, byte[])>();
        Assert.True(ring.TryReplay(ring.FirstSeq - 1, 20, AliceAudience, txs));
        Assert.Equal(20 - ring.FirstSeq + 1, txs.Count);
        Assert.False(ring.TryReplay(ring.FirstSeq - 2, 20, AliceAudience, txs));
        Assert.Empty(txs);
    }

    [Fact]
    public void Replay_needs_the_audience_in_every_entry() {
        var ring = new ByteBoundedRing(10_000, new RingBudget(1_000_000));
        var bob = new Audience(Bob.Who.Id, false);
        ring.Append(1, Entry(AliceAudience, 10));
        ring.Append(2, new Dictionary<Audience, byte[]> { [AliceAudience] = Bytes(10), [bob] = Bytes(10) });

        var txs = new List<(long, byte[])>();
        Assert.True(ring.TryReplay(1, 2, bob, txs));
        Assert.False(ring.TryReplay(0, 2, bob, txs));
        Assert.False(ring.TryReplay(0, 2, AliceAudience with { Member = true }, txs));
    }

    [Fact]
    public void Global_budget_trims_the_coldest_rings_first() {
        var budget = new RingBudget(1100);
        var cold = new ByteBoundedRing(800, budget);
        var warm = new ByteBoundedRing(800, budget);
        var hot = new ByteBoundedRing(800, budget);
        cold.Append(1, Entry(AliceAudience, 300));
        warm.Append(1, Entry(AliceAudience, 300));
        hot.Append(1, Entry(AliceAudience, 300));

        hot.Append(2, Entry(AliceAudience, 300));

        Assert.Equal(0, cold.Bytes);
        Assert.True(warm.Bytes > 0);
        Assert.Equal(1, hot.FirstSeq);
        Assert.True(budget.TotalBytes <= 1100);
        Assert.Equal(cold.Bytes + warm.Bytes + hot.Bytes, budget.TotalBytes);
        Assert.Equal(2, cold.FirstSeq);
        warm.Release();
        Assert.Equal(cold.Bytes + hot.Bytes, budget.TotalBytes);
    }

    [Fact]
    public void Blanked_entry_keeps_its_seq_with_no_events() {
        var ring = new ByteBoundedRing(10_000, new RingBudget(1_000_000));
        ring.Append(1, Entry(AliceAudience, 100));
        var before = ring.Bytes;

        ring.Blank(1);

        var txs = new List<(long Seq, byte[] Events)>();
        Assert.True(ring.TryReplay(0, 1, AliceAudience, txs));
        Assert.Equal("[]", Encoding.UTF8.GetString(Assert.Single(txs).Events));
        Assert.True(ring.Bytes < before);
    }

    [Fact]
    public async Task Resume_at_the_current_seq_gets_an_empty_replay_with_lastQ() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        actor.Command(alice, Alice, Cmd(3, new PokerVoteSet(Round(1), 2)));
        var ack = await alice.UntilAsync<AckFrame>();
        var epoch = await room.QueryAsync(view => view.Epoch);

        var again = new TestPeer(Viewer(1, "Alice", connection: 1));
        actor.Subscribe(again, again.Viewer, epoch, ack.S);

        var replay = await again.NextAsync<ReplayFrame>();
        Assert.Equal((ack.S + 1, 3L), (replay.From, replay.LastQ));
        Assert.Empty(replay.Txs);
    }

    [Fact]
    public async Task Resume_from_an_older_seq_replays_what_this_viewer_would_have_seen() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        var actor = await room.ActorAsync();
        await room.SettleAsync();
        var from = await room.QueryAsync(view => (view.Epoch, view.Seq));
        actor.Command(alice, Alice, Cmd(1, new PokerVoteSet(Round(1), 2)));
        actor.Command(bob, Bob, Cmd(1, new PokerVoteSet(Round(1), 3)));
        await bob.UntilAsync<AckFrame>();
        var bobLive = new List<TxFrame>();
        foreach (var frame in await alice.DrainAsync()) {
            if (frame is TxFrame tx && tx.S > from.Seq) {
                bobLive.Add(tx);
            }
        }

        var again = new TestPeer(Viewer(1, "Alice", connection: 1));
        actor.Subscribe(again, again.Viewer, from.Epoch, from.Seq);

        var replay = await again.NextAsync<ReplayFrame>();
        Assert.Equal(from.Seq + 1, replay.From);
        Assert.Equal(1, replay.LastQ);
        Assert.Equal(
            ["add:2:2:mine", "add:3:5"],
            replay.Txs.Select(x => Assert.IsType<PresenceUpdate>(Assert.Single(x.Ev)).Name));
        Assert.Equal(bobLive.Select(x => x.S), replay.Txs.Select(x => x.S));
    }

    [Fact]
    public async Task Another_epoch_or_a_seq_the_ring_no_longer_holds_gets_a_snapshot() {
        var room = new CounterRoom(options: ActorOptions.Default with { RingBytesPerRoom = 400 });
        var (alice, first) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        for (var q = 1; q <= 10; q++) {
            actor.Command(alice, Alice, Cmd(q, new PokerVoteSet(Round(1), 1)));
        }
        while ((await alice.UntilAsync<AckFrame>()).Q < 10) {
        }

        var stale = new TestPeer(Viewer(1, "Alice", connection: 1));
        actor.Subscribe(stale, stale.Viewer, first.E, first.S);
        var foreign = new TestPeer(Viewer(1, "Alice", connection: 2));
        actor.Subscribe(foreign, foreign.Viewer, new Epoch(first.E.Value + 1), 3);

        Assert.Equal("v10", Assert.IsType<PokerSnapshot>((await stale.NextAsync<SnapFrame>()).D).Code);
        Assert.Equal(10, (await foreign.NextAsync<SnapFrame>()).LastQ);
    }

    [Fact]
    public async Task A_principal_absent_when_a_tx_was_built_resumes_with_a_snapshot() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        await room.SettleAsync();
        var from = await room.QueryAsync(view => (view.Epoch, view.Seq));
        actor.Command(alice, Alice, Cmd(1, new PokerVoteSet(Round(1), 2)));
        await alice.UntilAsync<AckFrame>();

        var bob = new TestPeer(Bob);
        actor.Subscribe(bob, Bob, from.Epoch, from.Seq);

        Assert.IsType<SnapFrame>(await bob.NextAsync());
    }

    [Fact]
    public async Task Consecutive_superseding_moves_coalesce_in_the_ring() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var actor = await room.ActorAsync();
        await room.SettleAsync();
        var from = await room.QueryAsync(view => (view.Epoch, view.Seq));
        for (var q = 1; q <= 3; q++) {
            actor.Command(alice, Alice, Cmd(q, new PokerSpotlightMove(Round(1))));
        }
        await alice.UntilAsync<AckFrame>();
        await alice.UntilAsync<AckFrame>();
        await alice.UntilAsync<AckFrame>();

        var again = new TestPeer(Viewer(1, "Alice", connection: 1));
        actor.Subscribe(again, again.Viewer, from.Epoch, from.Seq);

        var replay = await again.NextAsync<ReplayFrame>();
        Assert.Equal([0, 0, 1], replay.Txs.Select(x => x.Ev.Count));
    }

    [Fact]
    public async Task Snapshot_is_one_shared_document_per_seq_and_variant_plus_a_per_viewer_overlay() {
        var builds = 0;
        var room = new CounterRoom(tracksPresence: false, slowSnapshot: state => {
            Interlocked.Increment(ref builds);
            return new PokerSnapshot(new SessionId(Room.Id), $"v{state.Value}", true, true, [], [], [], [], [], RoomTeam);
        });
        var actor = await room.ActorAsync();
        await actor.AskAsync(new CounterInput.Add(4), Alice, Token);
        await actor.AskAsync(new CounterInput.Join(Carol.Who.Id), null, Token);

        var (alice, aliceSnap) = await room.JoinAsync(Alice);
        var (bob, bobSnap) = await room.JoinAsync(Bob);
        var (carol, carolSnap) = await room.JoinAsync(Carol);

        Assert.Equal(2, builds);
        Assert.Equal(Json(aliceSnap.D), Json(bobSnap.D));
        Assert.True(Assert.IsType<PokerOverlay>(aliceSnap.O).HeldByThisTab);
        Assert.False(Assert.IsType<PokerOverlay>(bobSnap.O).HeldByThisTab);
        Assert.Equal(aliceSnap.S, carolSnap.S);
        Assert.NotNull(alice);
        Assert.NotNull(bob);
        Assert.NotNull(carol);
    }

    [Fact]
    public async Task Members_get_the_members_variant_of_the_shared_snapshot() {
        var room = new CounterRoom();
        var actor = await room.ActorAsync();
        await actor.AskAsync(new CounterInput.Join(Carol.Who.Id), null, Token);

        var (_, bobSnap) = await room.JoinAsync(Bob);
        var (_, carolSnap) = await room.JoinAsync(Carol);

        Assert.Equal("v0", Assert.IsType<PokerSnapshot>(bobSnap.D).Code);
        Assert.Equal("v0-members", Assert.IsType<PokerSnapshot>(carolSnap.D).Code);
    }

    [Fact]
    public async Task Snapshot_construction_is_gated_process_wide() {
        var current = 0;
        var peak = 0;
        var room = new CounterRoom(options: ActorOptions.Default with { SnapshotConcurrency = 2 }, slowSnapshot: _ => {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            Thread.Sleep(50);
            Interlocked.Decrement(ref current);
            return new PokerSnapshot(new SessionId(Room.Id), "slow", true, true, [], [], [], [], [], RoomTeam);
        });
        var peers = new List<TestPeer>();
        for (var n = 0; n < 8; n++) {
            var key = RoomKey.Poker(new SessionId(Id(0xC00 + n)));
            var peer = new TestPeer(Viewer(10 + n, $"P{n}"));
            peers.Add(peer);
            await room.Registry.SubscribeAsync(key, peer, peer.Viewer, null, null, Token);
        }

        foreach (var peer in peers) {
            await peer.NextAsync<SnapFrame>();
        }
        Assert.InRange(peak, 1, 2);
    }

    private static Dictionary<Audience, byte[]> Entry(Audience audience, int size) => new() { [audience] = Bytes(size) };

    private static byte[] Bytes(int size) => Encoding.UTF8.GetBytes("[" + new string(' ', size - 2) + "]");

    private static string Json(RoomSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Catalog.SnapshotTypeInfo(RoomKind.Poker));

    private static void InterlockedMax(ref int target, int value) {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) {
        }
    }
}
