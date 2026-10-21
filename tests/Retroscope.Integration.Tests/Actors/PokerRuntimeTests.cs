using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

/// <summary>The real poker domain on the runtime: the client command converts to <see cref="PokerInput"/>.</summary>
[Trait("Category", "Actors")]
public sealed class PokerRuntimeTests : IAsyncLifetime {
    private PokerRoom _room = null!;

    public async ValueTask InitializeAsync() => _room = await PokerRoom.StartAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Vote_values_reach_only_their_own_principal_live_and_on_replay() {
        var room = _room;
        var (alice, _) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);
        var from = await room.Registry.QueryAsync<PokerState, PokerInput, (Epoch, long)>(
            Room, view => ValueTask.FromResult((view.Epoch, view.Seq)), Token);

        room.Actor.Command(alice, Alice, Cmd(1, new PokerVoteSet(Round(1), 5)));
        var own = await VoteStateAsync(alice);
        await alice.UntilAsync<AckFrame>();
        var resumed = new TestPeer(Viewer(2, "Bob", connection: 1));
        room.Actor.Subscribe(resumed, resumed.Viewer, from.Item1, from.Item2);

        var other = await VoteStateAsync(bob);
        var replayed = (await resumed.NextAsync<ReplayFrame>()).Txs.SelectMany(x => x.Ev).OfType<PokerVoteState>().Single();
        Assert.Equal((short)5, own.Points);
        Assert.Null(other.Points);
        Assert.Equal(VoteStatus.Selected, other.Status);
        Assert.Equal(other, replayed);
    }

    [Fact]
    public async Task Auto_reveal_fires_once_after_everyone_locked() {
        var room = _room;
        var (alice, _) = await room.JoinAsync(Alice);
        var (bob, _) = await room.JoinAsync(Bob);

        room.Actor.Command(alice, Alice, Cmd(1, new PokerVoteSet(Round(1), 3, Lock: true)));
        room.Actor.Command(bob, Bob, Cmd(1, new PokerVoteSet(Round(1), 5, Lock: true)));
        await bob.UntilAsync<AckFrame>();
        await room.Registry.QueryAsync<PokerState, PokerInput, int>(Room, _ => ValueTask.FromResult(0), Token);
        room.Time.Advance(PokerTiming.AutoRevealDelay);
        var revealed = await alice.UntilAsync<TxFrame>();
        while (!revealed.Ev.OfType<PokerRoundRevealed>().Any()) {
            revealed = await alice.UntilAsync<TxFrame>();
        }
        room.Time.Advance(TimeSpan.FromSeconds(5));
        await room.Registry.QueryAsync<PokerState, PokerInput, int>(Room, _ => ValueTask.FromResult(0), Token);

        Assert.Equal((short)5, revealed.Ev.OfType<PokerRoundRevealed>().Single().Winner);
        Assert.Single(room.Store.Commits, x => x.Any(e => e is RoundRevealed));
        Assert.DoesNotContain(await alice.DrainAsync(), x => x is TxFrame { Ev: [PokerRoundRevealed, ..] });
    }

    private static async Task<PokerVoteState> VoteStateAsync(TestPeer peer) {
        while (true) {
            if (await peer.NextAsync() is TxFrame tx && tx.Ev.OfType<PokerVoteState>().FirstOrDefault() is { } vote) {
                return vote;
            }
        }
    }

    private sealed class PokerRoom {
        private PokerRoom() {
            Time = Clock();
            var state = new PokerState { SessionId = new SessionId(Room.Id), Code = "ABCD1234", WarmUntil = Start, NextRoundNumber = 2 };
            var round = new Round { Id = Round(1), Number = 1 };
            state.Rounds[round.Id] = round;
            state.Order.Add(round.Id);
            state.CurrentRoundId = round.Id;
            Store = new MemoryStore<PokerState, PokerEvent>(state, x => x.Clone(), PokerApply.Apply);
            Registry = new ActorRegistry(Runtime(Time), [RoomType.Of(new Binding(), Store, new TestAccess())]);
        }

        public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; }

        public MemoryStore<PokerState, PokerEvent> Store { get; }

        public ActorRegistry Registry { get; }

        public IRoomActor<PokerState, PokerInput> Actor { get; private set; } = null!;

        public static async Task<PokerRoom> StartAsync() {
            var room = new PokerRoom();
            room.Actor = (IRoomActor<PokerState, PokerInput>)await room.Registry.GetOrStartAsync(Room, Token);
            return room;
        }

        public async Task<(TestPeer Peer, SnapFrame Snap)> JoinAsync(Viewer viewer) {
            var peer = new TestPeer(viewer);
            Actor.Subscribe(peer, viewer);
            var snap = await peer.NextAsync<SnapFrame>();
            await Registry.QueryAsync<PokerState, PokerInput, int>(Room, _ => ValueTask.FromResult(0), Token);
            return (peer, snap);
        }
    }

    /// <summary>The shape U21's poker binding takes; enough of it to run the domain on the runtime.</summary>
    private sealed class Binding : IRoomBinding<PokerState, PokerInput, PokerEvent> {
        public RoomKind Kind => RoomKind.Poker;

        public IRoomDomain<PokerState, PokerInput, PokerEvent> Domain { get; } = new PokerDomain();

        public PokerInput? FromClient(Command command) => command is PokerCommand poker ? (PokerInput)poker : null;

        public EventDurability DurabilityOf(PokerEvent evt) => evt.Durability;

        public bool CanSubscribe(PokerState state, Viewer viewer, IAccessView access) => true;

        public RoomSnapshot Snapshot(PokerState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
            return PokerProjection.Snapshot(state, presence);
        }

        public RoomOverlay? Overlay(PokerState state, Viewer viewer) => PokerProjection.Overlay(state, viewer.Who.Id, viewer.Client);

        public IReadOnlyList<WireEvent> Project(PokerState state, IReadOnlyList<PokerEvent> events, PrincipalId viewer, bool viewerIsMember) {
            return PokerProjection.Events(events, viewer);
        }

        public bool Supersedes(IReadOnlyList<PokerEvent> latest, IReadOnlyList<PokerEvent> previous) {
            return latest.All(x => x is SpotlightMoved) && previous.All(x => x is SpotlightMoved);
        }

        public DateTimeOffset? WarmUntil(PokerState state) => state.WarmUntil;

        public bool IsDeleted(PokerState state) => state.Deleted;

        public PokerInput? OnShutdown(PokerState state) => state.Spotlight is { Dirty: true } ? new SpotlightSettleDue() : null;
    }
}
