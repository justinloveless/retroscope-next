using System.Net;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class SessionDeleteTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_deleted_session_kicks_its_subscribers_and_leaves_no_rows() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var owner = await app.UserAsync();
        var member = await app.UserAsync();
        var team = await app.TeamAsync((owner, "owner"), (member, "member"));
        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var subscriber = await app.SubscribeAsync(RoomKey.Poker(new SessionId(id)), RoomHttpApp.User(member));
        await subscriber.UntilAsync<SnapFrame>();

        using var refused = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{id}", RoomHttpApp.User(member));
        using var deleted = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{id}", RoomHttpApp.User(owner));
        var kick = await subscriber.UntilAsync<KickFrame>();
        using var lookup = await app.GetAsync($"/api/poker/by-code/{created.GetProperty("code").GetString()}", RoomHttpApp.User(owner));
        using var again = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{id}", RoomHttpApp.User(owner));

        Assert.Equal("forbidden", await RoomHttpApp.ProblemCodeAsync(refused, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(KickCode.Deleted, kick.Code);
        Assert.Equal(0L, await app.ScalarAsync<long>("select count(*) from poker_sessions where id = $1", id));
        Assert.Equal(0L, await app.ScalarAsync<long>("select count(*) from poker_rounds where session_id = $1", id));
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Team_admins_delete_team_sessions_and_the_team_hears_of_it() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var admin = await app.UserAsync();
        var member = await app.UserAsync();
        var team = await app.TeamAsync((admin, "admin"), (member, "member"));
        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var watcher = await app.SubscribeAsync(RoomKey.Poker(new SessionId(id)), RoomHttpApp.User(member));
        await watcher.UntilAsync<SnapFrame>();
        var before = app.Notices.Log.Count;

        using var byMember = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{id}", RoomHttpApp.User(member));
        using var byAdmin = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{id}", RoomHttpApp.User(admin));

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
        Assert.Equal(KickCode.Deleted, (await watcher.UntilAsync<KickFrame>()).Code);
        Assert.Contains($"poker {id}", app.Notices.Log.Skip(before));
    }

    [Fact]
    public async Task Deleting_an_unknown_session_is_not_found() {
        await using var app = await RoomHttpApp.StartAsync(postgres);

        using var response = await app.SendAsync(HttpMethod.Delete, $"/api/poker/{Guid.NewGuid()}", RoomHttpApp.User(await app.UserAsync()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
