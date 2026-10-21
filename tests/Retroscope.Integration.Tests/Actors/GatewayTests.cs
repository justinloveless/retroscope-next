using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Transport;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class GatewayTests {
    private static PokerVoteSet Vote(short points) => new(Round(1), points);

    [Fact]
    public async Task After_a_server_busy_refusal_later_commands_are_refused_until_the_room_is_subscribed_again() {
        var room = new CounterRoom(options: ActorOptions.Default with { ExternalQueueCapacity = 1 });
        var gateway = new RoomGateway(room.Registry, [RoomType.Of(room.Binding, room.Store, room.Access)], [], NullLogger<RoomGateway>.Instance);
        var alice = new FakeConnection(Alice);
        gateway.Subscribe(alice, Room, null, null);
        var snap = await alice.NextAsync<SnapFrame>();
        await room.SettleAsync();
        var release = room.Store.HoldNextCommit();

        gateway.Command(alice, Cmd(1, Vote(1)));
        await room.Store.CommitEntered;
        gateway.Command(alice, Cmd(2, Vote(2)));
        gateway.Command(alice, Cmd(3, Vote(3)));
        gateway.Command(alice, Cmd(4, Vote(4)));

        var refused = new[] { await alice.UntilAsync<NackFrame>(), await alice.UntilAsync<NackFrame>() };
        Assert.Equal([(3L, NackCode.ServerBusy, true), (4L, NackCode.ServerBusy, true)], refused.Select(x => (x.Q, x.Code, x.Retry)));
        release.SetResult();
        Assert.Equal(1, (await alice.UntilAsync<AckFrame>()).Q);
        Assert.Equal(2, (await alice.UntilAsync<AckFrame>()).Q);

        gateway.Command(alice, Cmd(5, Vote(5)));
        var later = await alice.UntilAsync<NackFrame>();
        Assert.Equal((5L, NackCode.ServerBusy), (later.Q, later.Code));
        Assert.Equal(2, room.Store.Commits.Count);
        Assert.Equal(2, room.Store.CursorOf(Alice.Client));

        gateway.Subscribe(alice, Room, snap.E, snap.S + 2);
        await alice.UntilAsync<ReplayFrame>();
        var acks = new List<AckFrame>();
        foreach (var q in new long[] { 3, 4, 5 }) {
            gateway.Command(alice, Cmd(q, Vote((short)q)));
            acks.Add(await alice.UntilAsync<AckFrame>());
        }

        Assert.Equal([(3L, false), (4L, false), (5L, false)], acks.Select(x => (x.Q, x.Dup)));
        Assert.Equal(1 + 2 + 3 + 4 + 5, room.Store.Persisted!.Value);
    }

    [Fact]
    public async Task A_kick_makes_the_connection_forget_the_room_and_later_commands_are_not_subscribed() {
        var room = new CounterRoom();
        var gateway = new RoomGateway(room.Registry, [RoomType.Of(room.Binding, room.Store, room.Access)], [], NullLogger<RoomGateway>.Instance);
        var alice = new FakeConnection(Alice);
        gateway.Subscribe(alice, Room, null, null);
        await alice.NextAsync<SnapFrame>();
        await room.SettleAsync();

        room.Registry.Post<Counter, CounterInput>(Room, new CounterInput.Ban(Alice.Who.Id));
        room.Registry.AccessChanged(Room);

        Assert.Equal(KickCode.AccessRevoked, (await alice.UntilAsync<KickFrame>()).Code);
        Assert.Equal([Room], alice.Kicked);
        gateway.Command(alice, Cmd(1, Vote(1)));
        Assert.Equal(NackCode.NotSubscribed, (await alice.UntilAsync<NackFrame>()).Code);
    }

    [Fact]
    public async Task A_room_kind_nobody_registered_is_not_found() {
        var room = new CounterRoom();
        var gateway = new RoomGateway(room.Registry, [RoomType.Of(room.Binding, room.Store, room.Access)], [], NullLogger<RoomGateway>.Instance);
        var alice = new FakeConnection(Alice);
        var team = RoomKey.Team(new TeamId(Id(77)));

        gateway.Subscribe(alice, team, null, null);

        Assert.Equal(KickCode.NotFound, (await alice.UntilAsync<KickFrame>()).Code);
    }

    /// <summary>A socket as the gateway sees it: frames are parsed and kept in order; kicks are recorded.</summary>
    private sealed class FakeConnection(Viewer viewer) : IClientConnection {
        private readonly Channel<ServerFrame> _frames = Channel.CreateUnbounded<ServerFrame>();

        public Viewer Viewer { get; } = viewer;

        public Guid? AuthSessionId => null;

        public List<RoomKey> Kicked { get; } = [];

        public bool Send(ServerFrame frame) => _frames.Writer.TryWrite(frame);

        public bool Send(ReadOnlyMemory<byte> utf8) => Send(WireParser.ParseServer(utf8.Span).Frame!);

        public bool Send(IReadOnlyList<ReadOnlyMemory<byte>> segments) => Send(segments.SelectMany(x => x.ToArray()).ToArray().AsMemory());

        public void Kick(RoomKey room, KickCode code) {
            lock (Kicked) {
                Kicked.Add(room);
            }
            Send(new KickFrame(room, code));
        }

        public void Restart() => Send(new ByeFrame(ByeCode.Restart, 500));

        public async Task<T> NextAsync<T>() where T : ServerFrame {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(Wait);
            return Assert.IsType<T>(await _frames.Reader.ReadAsync(timeout.Token));
        }

        public async Task<T> UntilAsync<T>() where T : ServerFrame {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(Wait);
            while (true) {
                if (await _frames.Reader.ReadAsync(timeout.Token) is T frame) {
                    return frame;
                }
            }
        }
    }
}
