using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Integration.Tests.Transport;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>Two signed-in people in a team session, through the real server end to end.</summary>
[Trait("Category", "PokerRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RealServerTests(PostgresFixture postgres) {
    [Fact]
    public async Task Two_signed_in_clients_see_each_others_votes_and_the_reveal() {
        await using var server = await RealServer.StartAsync(postgres);
        var ada = await server.SignUpAsync("Ada");
        var bob = await server.SignUpAsync("Bob");
        var team = await server.TeamAsync((ada, TeamRole.Owner), (bob, TeamRole.Member));
        var (room, round) = await SessionAsync(server, team, ada);
        await using var adaTab = await server.ConnectAsync(ada);
        await using var bobTab = await server.ConnectAsync(bob);

        Assert.IsType<SnapFrame>(await adaTab.SubscribeAsync(room));
        Assert.IsType<SnapFrame>(await bobTab.SubscribeAsync(room));
        var seen = bobTab.Count;
        await adaTab.AckedAsync(room, new PokerVoteSet(round, 5, Lock: true));

        var locked = await bobTab.WaitForEventAsync<PokerVoteState>(seen);
        Assert.Equal(new PokerVoteState(round, ada.Who.Id, Status: VoteStatus.Locked), locked);

        seen = adaTab.Count;
        await bobTab.AckedAsync(room, new PokerVoteSet(round, 8, Lock: true));
        Assert.Equal(bob.Who.Id, ((PokerVoteState)await adaTab.WaitForEventAsync<PokerVoteState>(seen)).Pid);

        await adaTab.AckedAsync(room, new PokerRoundReveal(round));
        var revealed = (PokerRoundRevealed)await bobTab.WaitForEventAsync<PokerRoundRevealed>();
        Assert.Equal(
            [(ada.Who.Id, (short?)5), (bob.Who.Id, (short?)8)],
            revealed.Votes.Select(x => (x.Pid, x.Points)).OrderBy(x => x.Points));
        Assert.Equal(2L, await server.ScalarAsync<long>("select count(*) from poker_votes where round_id = $1 and locked", round.Value));
    }

    [Fact]
    public async Task A_non_member_is_refused_a_team_session() {
        await using var server = await RealServer.StartAsync(postgres);
        var ada = await server.SignUpAsync("Ada");
        var eve = await server.SignUpAsync("Eve");
        var team = await server.TeamAsync((ada, TeamRole.Owner));
        var (room, _) = await SessionAsync(server, team, ada);
        await using var tab = await server.ConnectAsync(eve);

        Assert.Equal(KickCode.Forbidden, Assert.IsType<KickFrame>(await tab.SubscribeAsync(room)).Code);
    }

    private static async Task<(RoomKey Room, RoundId Round)> SessionAsync(RealServer server, TeamId team, Member creator) {
        var session = Guid.CreateVersion7();
        var round = Guid.CreateVersion7();
        await server.ExecAsync(
            "insert into poker_sessions (id, code, team_id, created_by, next_round_number) values ($1, $2, $3, $4, 2)",
            session, $"R{session:N}"[..12], team.Value, creator.Who.Id.Value);
        await server.ExecAsync("insert into poker_rounds (id, session_id, number, sort_key) values ($1, $2, 1, 0)", round, session);
        await server.ExecAsync("update poker_sessions set current_round_id = $2 where id = $1", session, round);
        return (RoomKey.Poker(new SessionId(session)), new RoundId(round));
    }
}
