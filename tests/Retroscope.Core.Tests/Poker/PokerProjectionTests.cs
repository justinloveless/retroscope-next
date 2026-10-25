using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

[Trait("Category", "Poker")]
public sealed class PokerProjectionTests {
    [Fact]
    public void The_snapshot_carries_when_the_session_was_created_in_unix_milliseconds() {
        var room = PokerHarness.Team();
        room.Join(Bob);
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Bob, new PokerRoundReveal(R(1)));

        var snapshot = PokerProjection.Snapshot(room.State, room.PresenceInfo());

        Assert.Equal(Created.ToUnixTimeMilliseconds(), snapshot.CreatedAt);
        Assert.Equal(PokerHarness.TeamId, snapshot.TeamId);
        Assert.Contains($"\"teamId\":\"{PokerHarness.TeamId}\",\"createdAt\":{Created.ToUnixTimeMilliseconds()},", PokerPropertyTests.Snap(room, Bob));
        Assert.Equal(Created, room.State.Clone().CreatedAt);
    }
}
