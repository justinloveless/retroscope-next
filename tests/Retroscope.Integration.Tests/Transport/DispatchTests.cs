using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

[Trait("Category", "Transport")]
public sealed class DispatchTests {
    [Fact]
    public async Task Hello_resumes_every_listed_room_with_its_cursor() {
        await using var app = await TransportApp.StartAsync();
        var first = new SubCursor(TransportApp.NewRoom(), new Epoch(0xabc), 42);
        var second = new SubCursor(TransportApp.NewRoom());
        var client = TransportApp.NewClient();

        await using var socket = await app.HelloAsync("user", client, first, second);

        await Wait.UntilAsync(() => app.Gateway.Of("sub").Count == 2);
        var subs = app.Gateway.Of("sub");
        Assert.Equal([first, second], subs.Select(x => x.Detail));
        Assert.All(subs, x => Assert.Equal(client, x.Connection.Viewer.Client));
    }

    [Fact]
    public async Task Sub_and_unsub_reach_the_gateway() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();
        var room = TransportApp.NewRoom();

        await client.SendAsync(new SubFrame(room, new Epoch(1), 3));
        await client.SendAsync(new UnsubFrame(room));

        var unsub = await app.Gateway.WaitForAsync("unsub");
        Assert.Equal(room, unsub.Room);
        Assert.Equal(new SubCursor(room, new Epoch(1), 3), app.Gateway.Of("sub").Single().Detail);
    }

    [Fact]
    public async Task Unsub_of_a_room_never_subscribed_is_ignored() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendAsync(new UnsubFrame(TransportApp.NewRoom()));
        await client.SendAsync(new PingFrame(1));

        Assert.IsType<PongFrame>(await client.ReceiveAsync());
        Assert.Empty(app.Gateway.Of("unsub"));
    }

    [Fact]
    public async Task A_command_for_a_subscribed_room_reaches_the_gateway_with_its_payload_hash() {
        await using var app = await TransportApp.StartAsync();
        var room = TransportApp.NewRoom();
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));
        var command = new CmdFrame(room, 7, new PokerRoundReveal(new RoundId(Guid.CreateVersion7())));

        await client.SendAsync(command);

        var call = await app.Gateway.WaitForAsync("cmd");
        var received = Assert.IsType<CmdFrame>(call.Detail);
        Assert.Equal(7, received.Q);
        Assert.Equal(command.D, received.D);
        Assert.NotEqual(0u, received.DataHash);
    }

    [Fact]
    public async Task A_command_without_a_subscription_is_nacked_not_subscribed() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();
        var room = TransportApp.NewRoom();

        await client.SendAsync(new CmdFrame(room, 1, new PokerRoundReveal(new RoundId(Guid.CreateVersion7()))));
        var nack = Assert.IsType<NackFrame>(await client.ReceiveAsync());

        Assert.Equal((room, 1L, NackCode.NotSubscribed, false), (nack.R, nack.Q, nack.Code, nack.Retry));
        Assert.Empty(app.Gateway.Of("cmd"));
    }

    [Fact]
    public async Task A_command_with_a_bad_payload_is_nacked_invalid_and_the_socket_stays_open() {
        await using var app = await TransportApp.StartAsync();
        var room = TransportApp.NewRoom();
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));

        await client.SendRawAsync($$$"""{"t":"cmd","r":"{{{room}}}","q":5,"k":"poker.round.reveal","d":{"roundId":17}}""");
        var nack = Assert.IsType<NackFrame>(await client.ReceiveAsync());
        await client.SendRawAsync($$$"""{"t":"cmd","r":"{{{room}}}","q":6,"k":"poker.from.the.future","d":{}}""");
        var unknown = Assert.IsType<NackFrame>(await client.ReceiveAsync());
        await client.SendAsync(new PingFrame(1));

        Assert.Equal((5L, NackCode.Invalid), (nack.Q, nack.Code));
        Assert.Equal((6L, NackCode.Invalid), (unknown.Q, unknown.Code));
        Assert.IsType<PongFrame>(await client.ReceiveAsync());
        Assert.Empty(app.Gateway.Of("cmd"));
    }

    [Fact]
    public async Task An_eleventh_room_is_kicked_too_many_rooms_but_a_resubscribe_is_not() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();
        var rooms = Enumerable.Range(0, 10).Select(_ => TransportApp.NewRoom()).ToList();
        foreach (var room in rooms) {
            await client.SendAsync(new SubFrame(room));
        }
        var extra = TransportApp.NewRoom();

        await client.SendAsync(new SubFrame(rooms[0], new Epoch(9), 1));
        await client.SendAsync(new SubFrame(extra));
        var kick = Assert.IsType<KickFrame>(await client.ReceiveAsync());

        Assert.Equal((extra, KickCode.TooManyRooms), (kick.R, kick.Code));
        Assert.Equal(11, app.Gateway.Of("sub").Count);
        Assert.DoesNotContain(app.Gateway.Of("sub"), x => x.Room == extra);
    }

    [Fact]
    public async Task A_kick_from_the_actor_drops_the_room() {
        await using var app = await TransportApp.StartAsync();
        var room = TransportApp.NewRoom();
        app.Gateway.OnSubscribe = (connection, key) => connection.Kick(key, KickCode.Forbidden);
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));

        var kick = Assert.IsType<KickFrame>(await client.ReceiveAsync());
        await client.SendAsync(new CmdFrame(room, 1, new PokerRoundReveal(new RoundId(Guid.CreateVersion7()))));
        var nack = Assert.IsType<NackFrame>(await client.ReceiveAsync());

        Assert.Equal(KickCode.Forbidden, kick.Code);
        Assert.Equal(NackCode.NotSubscribed, nack.Code);
    }

    [Fact]
    public async Task Frames_from_the_actor_arrive_in_order_and_segments_form_one_frame() {
        await using var app = await TransportApp.StartAsync();
        var room = TransportApp.NewRoom();
        app.Gateway.OnCommand = (connection, command) => {
            connection.Send(new TxFrame(command.R, 1, []));
            var ack = Encoding.UTF8.GetString(WireWriter.ToUtf8(new AckFrame(command.R, command.Q, 1)));
            var split = ack.Length / 2;
            connection.Send([Encoding.UTF8.GetBytes(ack[..split]), Encoding.UTF8.GetBytes(ack[split..])]);
        };
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));

        await client.SendAsync(new CmdFrame(room, 3, new PokerRoundReveal(new RoundId(Guid.CreateVersion7()))));

        Assert.IsType<TxFrame>(await client.ReceiveAsync());
        var ack = Assert.IsType<AckFrame>(await client.ReceiveAsync());
        Assert.Equal((room, 3L, 1L), (ack.R, ack.Q, ack.S));
    }

    [Fact]
    public async Task Disconnect_reports_the_rooms_still_subscribed() {
        await using var app = await TransportApp.StartAsync();
        var kept = TransportApp.NewRoom();
        var left = TransportApp.NewRoom();
        var client = await app.HelloAsync("user", null, new SubCursor(kept), new SubCursor(left));
        await client.SendAsync(new UnsubFrame(left));
        await app.Gateway.WaitForAsync("unsub");

        await client.DisposeAsync();

        var gone = await app.Gateway.WaitForAsync("gone");
        Assert.Equal([kept], Assert.IsAssignableFrom<IReadOnlyCollection<RoomKey>>(gone.Detail));
    }

    [Fact]
    public async Task Without_the_actor_runtime_rooms_are_not_found() {
        var gateway = new UnavailableRoomGateway();
        var connection = new FakeConnection();
        var room = TransportApp.NewRoom();

        gateway.Subscribe(connection, room, null, null);
        gateway.Command(connection, new CmdFrame(room, 1, new PokerRoundReveal(new RoundId(Guid.CreateVersion7()))));

        Assert.Equal(KickCode.NotFound, Assert.IsType<KickFrame>(connection.Sent[0]).Code);
        Assert.Equal(NackCode.NotSubscribed, Assert.IsType<NackFrame>(connection.Sent[1]).Code);
        await Task.CompletedTask;
    }

    private sealed class FakeConnection : IClientConnection {
        public List<ServerFrame> Sent { get; } = [];

        public Retroscope.Core.Room.Viewer Viewer => throw new NotSupportedException();

        public Guid? AuthSessionId => null;

        public bool Send(ServerFrame frame) {
            Sent.Add(frame);
            return true;
        }

        public bool Send(ReadOnlyMemory<byte> utf8) => throw new NotSupportedException();

        public bool Send(IReadOnlyList<ReadOnlyMemory<byte>> segments) => throw new NotSupportedException();

        public void Kick(RoomKey room, KickCode code) => Send(new KickFrame(room, code));

        public void Restart() => throw new NotSupportedException();
    }
}
