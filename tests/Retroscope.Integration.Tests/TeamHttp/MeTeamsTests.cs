using System.Net;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Integration.Tests.TeamHttp;

// UX18 decisions 8 and 10: me.teams feeds the header (sorted by name, with each team's open action items) and
// me.lastTeamId is where a signed-in '/' leads.
[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class MeTeamsTests(PostgresFixture postgres) {
    [Fact]
    public async Task Me_lists_the_teams_by_name_ignoring_case_with_their_open_action_items() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var stranger = await host.UserAsync("Sam");
        var beta = await host.TeamAsync(owner, "beta");
        var alpha = await host.TeamAsync(owner, "Alpha");
        var gamma = await host.TeamAsync(owner, "gamma");
        var theirs = await host.TeamAsync(stranger, "Theirs");
        await ActionItemAsync(host, alpha, done: false);
        await ActionItemAsync(host, alpha, done: false);
        await ActionItemAsync(host, alpha, done: true);
        await ActionItemAsync(host, gamma, done: false);
        await ActionItemAsync(host, theirs, done: false);

        var teams = (await MeAsync(host, owner)).GetProperty("teams").EnumerateArray().ToList();

        Assert.Equal([alpha, beta, gamma], teams.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(["Alpha", "beta", "gamma"], teams.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal([2, 0, 1], teams.Select(x => x.GetProperty("openActions").GetInt32()));
        Assert.All(teams, x => Assert.Equal("owner", x.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task A_user_who_visited_no_team_has_a_null_last_team() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        await host.TeamAsync(owner);

        var me = await MeAsync(host, owner);

        Assert.Equal(JsonValueKind.Null, me.GetProperty("lastTeamId").ValueKind);
    }

    [Fact]
    public async Task The_last_team_follows_team_page_visits_and_room_subscriptions() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var first = await host.TeamAsync(owner, "First", (member, "member"));
        var second = await host.TeamAsync(owner, "Second", (member, "member"));
        var third = await host.TeamAsync(owner, "Third", (member, "member"));
        var session = new SessionId(await CreateAsync(host, owner, $"/api/teams/{second}/poker-sessions", new { }));
        var board = new BoardId(await CreateAsync(host, owner, $"/api/teams/{third}/boards", new { title = "Retro" }));
        await host.ExecuteAsync("delete from recent_activity");

        using (var page = await host.GetAsync(member, $"/api/teams/{first}")) {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
        Assert.Equal(first, await LastTeamAsync(host, member));

        var poker = await host.SubscribeAsync(member, RoomKey.Poker(session));
        await poker.UntilAsync<SnapFrame>();
        Assert.Equal(second, await EventuallyAsync(host, member, second));

        var retro = await host.SubscribeAsync(member, RoomKey.Board(board), connection: 1);
        await retro.UntilAsync<SnapFrame>();
        Assert.Equal(third, await EventuallyAsync(host, member, third));

        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from recent_activity where user_id = $1 and entity_kind = 'team'", member.Guid));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from recent_activity where entity_kind <> 'team'"));
    }

    [Fact]
    public async Task The_last_team_skips_teams_the_user_left_and_deleted_teams() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var kept = await host.TeamAsync(owner, "Kept", (member, "member"));
        var left = await host.TeamAsync(owner, "Left", (member, "member"));
        var deleted = await host.TeamAsync(owner, "Deleted", (member, "member"));
        var now = DateTimeOffset.UtcNow;
        foreach (var (team, age) in new[] { (kept, 3), (left, 2), (deleted, 1) }) {
            await host.ExecuteAsync(
                "insert into recent_activity (user_id, entity_kind, entity_id, last_at) values ($1, 'team', $2, $3)",
                member.Guid, team, now - TimeSpan.FromMinutes(age));
        }
        await host.ExecuteAsync("delete from team_members where team_id = $1 and user_id = $2", left, member.Guid);
        await host.ExecuteAsync("update teams set deleted_at = now() where id = $1", deleted);

        Assert.Equal(kept, await LastTeamAsync(host, member));
    }

    [Fact]
    public async Task A_room_subscription_touches_its_team_at_most_once_per_debounce() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);
        var session = new SessionId(await CreateAsync(host, owner, $"/api/teams/{team}/poker-sessions", new { }));

        var first = await host.SubscribeAsync(owner, RoomKey.Poker(session));
        await first.UntilAsync<SnapFrame>();
        Assert.Equal(team, await EventuallyAsync(host, owner, team));
        var written = await host.ScalarAsync<DateTime>("select last_at from recent_activity where user_id = $1", owner.Guid);
        var again = await host.SubscribeAsync(owner, RoomKey.Poker(session), connection: 1);
        await again.UntilAsync<SnapFrame>();
        await Task.Delay(200, TeamHttpHost.Token);

        Assert.True(ActivityRepo.Debounce > TimeSpan.FromSeconds(1));
        Assert.Equal(written, await host.ScalarAsync<DateTime>("select last_at from recent_activity where user_id = $1", owner.Guid));
    }

    private static async Task<JsonElement> MeAsync(TeamHttpHost host, TestUser user) {
        using var response = await host.GetAsync(user, "/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TeamHttpHost.JsonAsync(response);
    }

    private static async Task<Guid?> LastTeamAsync(TeamHttpHost host, TestUser user) {
        var last = (await MeAsync(host, user)).GetProperty("lastTeamId");
        return last.ValueKind == JsonValueKind.Null ? null : last.GetGuid();
    }

    // A subscription records its visit off the actor's thread, so me sees it a moment later.
    private static async Task<Guid?> EventuallyAsync(TeamHttpHost host, TestUser user, Guid expected) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true) {
            var last = await LastTeamAsync(host, user);
            if (last == expected || DateTime.UtcNow > deadline) {
                return last;
            }
            await Task.Delay(20, TeamHttpHost.Token);
        }
    }

    private static async Task<Guid> CreateAsync(TeamHttpHost host, TestUser user, string path, object body) {
        using var response = await host.PostAsync(user, path, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await TeamHttpHost.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task ActionItemAsync(TeamHttpHost host, Guid team, bool done) {
        return host.ExecuteAsync(
            "insert into team_action_items (id, team_id, text, done_at) values ($1, $2, 'Do it', $3)",
            Guid.CreateVersion7(), team, done ? DateTimeOffset.UtcNow : null);
    }
}
