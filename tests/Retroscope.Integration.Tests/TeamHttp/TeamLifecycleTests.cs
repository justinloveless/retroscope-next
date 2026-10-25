using System.Net;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Integration.Tests.TeamHttp;

[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class TeamLifecycleTests(PostgresFixture postgres) {
    [Fact]
    public async Task Creating_a_team_needs_a_verified_email() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var unverified = await host.UserAsync("Una", verified: false);

        using var response = await host.PostAsync(unverified, "/api/teams", new { name = "Platform" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("email_unverified", await TeamHttpHost.CodeAsync(response));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from teams"));
    }

    [Fact]
    public async Task A_new_team_has_its_creator_as_owner_the_default_template_and_endorsement_types() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");

        using var created = await host.PostAsync(owner, "/api/teams", new { name = "  Platform  ", description = "Core services" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = (await TeamHttpHost.JsonAsync(created)).GetProperty("id").GetGuid();

        Assert.Equal("owner", await host.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", team, owner.Guid));
        Assert.Equal(owner.Guid, await host.ScalarAsync<Guid>("select created_by from teams where id = $1", team));
        using var templates = await host.GetAsync(owner, $"/api/teams/{team}/templates");
        var template = Assert.Single((await TeamHttpHost.JsonAsync(templates)).GetProperty("items").EnumerateArray());
        Assert.True(template.GetProperty("isDefault").GetBoolean());
        Assert.Equal(["Good", "Bad", "Kudos", "Action Items"], template.GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        Assert.Equal([false, false, false, true], template.GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("actionItems").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, template.GetProperty("config").GetProperty("maxVotes").ValueKind);
        using var types = await host.GetAsync(owner, $"/api/teams/{team}/endorsement-types");
        Assert.Equal(
            ["Problem Solver", "Team Player", "Innovator"],
            (await TeamHttpHost.JsonAsync(types)).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.create' and team_id = $1", team));

        using var details = await host.GetAsync(owner, $"/api/teams/{team}");
        var json = await TeamHttpHost.JsonAsync(details);
        Assert.Equal("Platform", json.GetProperty("name").GetString());
        Assert.Equal("Core services", json.GetProperty("description").GetString());
        Assert.Equal("owner", json.GetProperty("role").GetString());
        Assert.Equal(3, json.GetProperty("maxEndorsements").GetInt32());
        Assert.False(json.GetProperty("jira").GetProperty("configured").GetBoolean());
        Assert.Equal(1, json.GetProperty("counts").GetProperty("members").GetInt32());
        using var me = await host.GetAsync(owner, "/api/me");
        var mine = Assert.Single((await TeamHttpHost.JsonAsync(me)).GetProperty("teams").EnumerateArray());
        Assert.Equal(team, mine.GetProperty("id").GetGuid());
        Assert.Equal("Platform", mine.GetProperty("name").GetString());
        Assert.Equal("owner", mine.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Settings_are_validated_written_and_fanned_out_to_the_team_room() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);
        var peer = await host.SubscribeAsync(owner, RoomKey.Team(new TeamId(team)));
        await peer.NextAsync<SnapFrame>();

        using var bad = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}", new { pointDescriptions = new Dictionary<string, string> { ["4"] = "no such card" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var tooMany = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}", new { maxEndorsementsPerBoard = 51 });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        using var patched = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}", new {
            name = "Platform Team",
            pointDescriptions = new Dictionary<string, string> { ["1"] = " tiny ", ["21"] = "" },
            maxEndorsementsPerBoard = 5,
        });

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var json = await TeamHttpHost.JsonAsync(patched);
        Assert.Equal("Platform Team", json.GetProperty("name").GetString());
        Assert.Equal("tiny", json.GetProperty("pointDescriptions").GetProperty("1").GetString());
        Assert.False(json.GetProperty("pointDescriptions").TryGetProperty("21", out _));
        Assert.Equal(5, json.GetProperty("maxEndorsements").GetInt32());
        var settings = Assert.IsType<TeamSettingsChanged>(Assert.Single((await peer.NextAsync<TxFrame>()).Ev));
        Assert.Equal("tiny", settings.PointDescriptions!["1"]);
        Assert.Equal(5, settings.MaxEndorsements);
    }

    [Fact]
    public async Task Deleting_needs_the_owner_and_the_exact_name_and_kicks_every_room_of_the_team() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"), (member, "member"));
        var session = new SessionId(Guid.CreateVersion7());
        await host.ExecuteAsync("insert into poker_sessions (id, code, team_id) values ($1, 'TEAMPOKER1', $2)", session.Value, team);
        var teamPeer = await host.SubscribeAsync(member, RoomKey.Team(new TeamId(team)));
        var pokerPeer = await host.SubscribeAsync(member, RoomKey.Poker(session), connection: 1);
        await teamPeer.NextAsync<SnapFrame>();
        await pokerPeer.UntilAsync<SnapFrame>();

        using var byAdmin = await host.SendAsync(admin, HttpMethod.Delete, $"/api/teams/{team}", new { confirmName = "Platform" });
        using var wrongName = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}", new { confirmName = "platform" });
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);

        using var deleted = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}", new { confirmName = "Platform" });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(KickCode.Deleted, (await teamPeer.UntilAsync<KickFrame>()).Code);
        Assert.Equal(KickCode.AccessRevoked, (await pokerPeer.UntilAsync<KickFrame>()).Code);
        Assert.True(await host.ScalarAsync<bool>("select deleted_at is not null from teams where id = $1", team));
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from team_members where team_id = $1", team));
        using var gone = await host.GetAsync(owner, $"/api/teams/{team}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var me = await host.GetAsync(member, "/api/me");
        Assert.Empty((await TeamHttpHost.JsonAsync(me)).GetProperty("teams").EnumerateArray());
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.delete' and team_id = $1", team));
    }

    [Fact]
    public async Task A_deleted_team_grants_its_old_members_nothing_after_a_restart() {
        var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = new TeamId(await host.TeamAsync(owner, "Platform", (member, "member")));
        using (var deleted = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team.Value}", new { confirmName = "Platform" })) {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        await host.DisposeAsync();

        await using var restarted = TeamHttpHost.Start(host.Database);
        var cache = restarted.Service<MembershipCache>();

        Assert.Null(cache.RoleIn(team, member.Id));
        Assert.Null(cache.RoleIn(team, owner.Id));
        cache.Forget(team);
        Assert.Empty((await cache.LoadAsync(team, TeamHttpHost.Token)).Roles);
        Assert.Equal(2L, await restarted.ScalarAsync<long>("select count(*) from team_members where team_id = $1", team.Value));
    }
}
