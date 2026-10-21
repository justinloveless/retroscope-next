using Microsoft.Extensions.DependencyInjection;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Integration.Tests.Transport;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Integration.Tests.TeamRoom;

/// <summary>Two signed-in members on the team page, through the real server end to end.</summary>
[Trait("Category", "TeamRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RealServerTests(PostgresFixture postgres) {
    [Fact]
    public async Task Two_signed_in_members_see_each_others_action_item_changes() {
        await using var server = await RealServer.StartAsync(postgres);
        var ada = await server.SignUpAsync("Ada");
        var bob = await server.SignUpAsync("Bob");
        var team = await server.TeamAsync((ada, TeamRole.Owner), (bob, TeamRole.Member));
        var item = new ActionItemId(Guid.CreateVersion7());
        await server.ExecAsync("insert into team_action_items (id, team_id, text) values ($1, $2, 'Write the runbook')", item.Value, team.Value);
        var room = RoomKey.Team(team);
        await using var adaTab = await server.ConnectAsync(ada);
        await using var bobTab = await server.ConnectAsync(bob);
        Assert.IsType<SnapFrame>(await adaTab.SubscribeAsync(room));
        Assert.IsType<SnapFrame>(await bobTab.SubscribeAsync(room));

        var seen = bobTab.Count;
        await adaTab.AckedAsync(room, new TeamActionAssign(item, bob.Who.Id));
        var assigned = (TeamActionUpdated)await bobTab.WaitForEventAsync<TeamActionUpdated>(seen);
        Assert.Equal((item, (PrincipalId?)bob.Who.Id), (assigned.Item.Id, assigned.Item.AssignedTo));

        seen = adaTab.Count;
        await bobTab.AckedAsync(room, new TeamActionDone(item, Done: true));
        var done = (TeamActionUpdated)await adaTab.WaitForEventAsync<TeamActionUpdated>(seen);
        Assert.NotNull(done.Item.DoneAt);
        Assert.Equal(bob.Who.Id.Value, await server.ScalarAsync<Guid>("select done_by from team_action_items where id = $1", item.Value));
    }

    [Fact]
    public async Task Removing_a_member_kicks_their_live_team_socket() {
        await using var server = await RealServer.StartAsync(postgres);
        var ada = await server.SignUpAsync("Ada");
        var bob = await server.SignUpAsync("Bob");
        var team = await server.TeamAsync((ada, TeamRole.Owner), (bob, TeamRole.Member));
        var room = RoomKey.Team(team);
        await using var bobTab = await server.ConnectAsync(bob);
        Assert.IsType<SnapFrame>(await bobTab.SubscribeAsync(room));
        var registry = server.Services.GetRequiredService<ActorRegistry>();

        // The invite and membership routes belong to U24; they ask the team actor exactly like this.
        var result = await registry.AskAsync<TeamState, TeamInput>(room, new TeamInput.MemberRemoved(bob.Who.Id), null, RealServer.Token);

        Assert.Equal(AskStatus.Accepted, result.Status);
        Assert.Equal(KickCode.AccessRevoked, (await bobTab.WaitForAsync<KickFrame>()).Code);
    }
}
