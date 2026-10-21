using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;

namespace Retroscope.Integration.Tests.Actors;

[Trait("Category", "Actors")]
public sealed class OutboundBufferTests {
    [Fact]
    public async Task Frames_leave_in_order_and_free_their_bytes() {
        var buffer = new OutboundBuffer(100);
        Assert.True(buffer.TryEnqueue([1, 2, 3]));
        Assert.True(buffer.TryEnqueue([4]));

        Assert.Equal([1, 2, 3], await buffer.DequeueAsync(Token));
        Assert.Equal(1, buffer.PendingBytes);
        Assert.Equal([4], await buffer.DequeueAsync(Token));
        Assert.Equal(0, buffer.PendingBytes);
    }

    [Fact]
    public async Task Overflowing_the_byte_cap_closes_with_4008_and_drops_what_was_pending() {
        var buffer = new OutboundBuffer(10);
        Assert.True(buffer.TryEnqueue(new byte[6]));

        Assert.False(buffer.TryEnqueue(new byte[6]));

        Assert.Equal(CloseCodes.SlowConsumer, await buffer.Closed);
        Assert.False(buffer.TryEnqueue([1]));
        Assert.Null(await buffer.DequeueAsync(Token));
    }

    [Fact]
    public async Task A_slow_subscriber_is_cut_off_without_slowing_the_room() {
        var room = new CounterRoom();
        var (alice, _) = await room.JoinAsync(Alice);
        var slow = new TestPeer(Bob, capacityBytes: 2_000);
        var actor = await room.ActorAsync();
        actor.Subscribe(slow, Bob);

        for (var q = 1; q <= 50; q++) {
            actor.Command(alice, Alice, Cmd(q, new Core.Protocol.PokerVoteSet(Round(1), 1)));
        }
        while ((await alice.UntilAsync<AckFrame>()).Q < 50) {
        }

        Assert.Equal(CloseCodes.SlowConsumer, await slow.Buffer.Closed.WaitAsync(Wait, Token));
        Assert.Equal(50, room.Store.Persisted!.Value);
    }
}
