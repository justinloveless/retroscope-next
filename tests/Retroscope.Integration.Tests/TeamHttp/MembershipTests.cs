using System.Net;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Integration.Tests.TeamHttp;

[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class MembershipTests(PostgresFixture postgres) {
    [Fact]
    public async Task Member_lists_carry_names_avatars_and_roles_but_no_email_addresses() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));

        using var response = await host.GetAsync(member, $"/api/teams/{team}/members");

        var body = await response.Content.ReadAsStringAsync(TeamHttpHost.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(owner.Email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(member.Email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
        var members = (await TeamHttpHost.JsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal([owner.Guid, member.Guid], members.Select(x => x.GetProperty("pid").GetGuid()));
        Assert.Equal(["owner", "member"], members.Select(x => x.GetProperty("role").GetString()));
        Assert.Equal(["Olive", "Mia"], members.Select(x => x.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Only_the_owner_changes_roles_and_never_the_owners_own() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"), (member, "member"));

        using var byAdmin = await host.SendAsync(admin, HttpMethod.Patch, $"/api/teams/{team}/members/{member.Guid}", new { role = "admin" });
        using var ownersOwn = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}/members/{owner.Guid}", new { role = "admin" });
        using var promoted = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}/members/{member.Guid}", new { role = "admin" });

        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, ownersOwn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, promoted.StatusCode);
        Assert.Equal("admin", await RoleAsync(host, team, member));
        Assert.Equal(TeamRole.Admin, host.Service<MembershipCache>().RoleIn(new TeamId(team), member.Id));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.role.set' and team_id = $1", team));
    }

    [Fact]
    public async Task Admins_remove_members_only_the_owner_removes_admins_and_anyone_but_the_owner_may_leave() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var other = await host.UserAsync("Ada");
        var member = await host.UserAsync("Mia");
        var leaver = await host.UserAsync("Leo");
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"), (other, "admin"), (member, "member"), (leaver, "member"));

        using var adminRemovesAdmin = await host.SendAsync(admin, HttpMethod.Delete, $"/api/teams/{team}/members/{other.Guid}", null);
        using var memberRemovesMember = await host.SendAsync(member, HttpMethod.Delete, $"/api/teams/{team}/members/{leaver.Guid}", null);
        using var ownerLeaves = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/members/{owner.Guid}", null);
        using var adminRemovesOwner = await host.SendAsync(admin, HttpMethod.Delete, $"/api/teams/{team}/members/{owner.Guid}", null);
        Assert.Equal(HttpStatusCode.Forbidden, adminRemovesAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberRemovesMember.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, ownerLeaves.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, adminRemovesOwner.StatusCode);

        using var adminRemovesMember = await host.SendAsync(admin, HttpMethod.Delete, $"/api/teams/{team}/members/{member.Guid}", null);
        using var ownerRemovesAdmin = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/members/{other.Guid}", null);
        using var leaves = await host.SendAsync(leaver, HttpMethod.Delete, $"/api/teams/{team}/members/{leaver.Guid}", null);
        Assert.Equal(HttpStatusCode.NoContent, adminRemovesMember.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownerRemovesAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, leaves.StatusCode);

        Assert.Equal(
            new[] { owner.Guid, admin.Guid }.Order(),
            (await host.ScalarAsync<Guid[]>("select array_agg(user_id) from team_members where team_id = $1", team)).Order());
        using var gone = await host.GetAsync(member, $"/api/teams/{team}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.member.leave' and team_id = $1", team));
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.member.remove' and team_id = $1", team));
    }

    [Fact]
    public async Task A_removed_member_is_kicked_from_the_team_room() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));
        var peer = await host.SubscribeAsync(member, RoomKey.Team(new TeamId(team)));
        await peer.NextAsync<SnapFrame>();

        using var removed = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/members/{member.Guid}", null);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(KickCode.AccessRevoked, (await peer.UntilAsync<KickFrame>()).Code);
    }

    [Fact]
    public async Task Ownership_moves_to_one_member_and_the_old_owner_becomes_an_admin() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var outsider = await host.UserAsync("Otto");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));

        using var toOutsider = await host.PostAsync(owner, $"/api/teams/{team}/transfer-ownership", new { userId = outsider.Guid });
        using var toSelf = await host.PostAsync(owner, $"/api/teams/{team}/transfer-ownership", new { userId = owner.Guid });
        Assert.Equal(HttpStatusCode.NotFound, toOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, toSelf.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, owner));

        using var transferred = await host.PostAsync(owner, $"/api/teams/{team}/transfer-ownership", new { userId = member.Guid });

        Assert.Equal(HttpStatusCode.NoContent, transferred.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, member));
        Assert.Equal("admin", await RoleAsync(host, team, owner));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from team_members where team_id = $1 and role = 'owner'", team));
        using var oldOwnerDeletes = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}", new { confirmName = "Platform" });
        Assert.Equal(HttpStatusCode.Forbidden, oldOwnerDeletes.StatusCode);
        using var again = await host.PostAsync(owner, $"/api/teams/{team}/transfer-ownership", new { userId = owner.Guid });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.owner.transfer' and team_id = $1", team));
    }

    [Fact]
    public async Task A_transfer_that_fails_to_promote_leaves_the_old_owner_in_place() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));
        // Every promotion to owner in this team fails, as a crash between two statements would.
        await host.ExecuteAsync($$"""
            create function refuse_owner() returns trigger language plpgsql as $$
            begin
              if new.role = 'owner' and new.team_id = '{{team}}' then
                raise exception 'promotion refused';
              end if;
              return new;
            end $$;
            create trigger refuse_owner before update on team_members for each row execute function refuse_owner();
            """);

        using var transferred = await host.PostAsync(owner, $"/api/teams/{team}/transfer-ownership", new { userId = member.Guid });

        Assert.NotEqual(HttpStatusCode.NoContent, transferred.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, owner));
        Assert.Equal("member", await RoleAsync(host, team, member));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.owner.transfer'"));
    }

    private static Task<string> RoleAsync(TeamHttpHost host, Guid team, TestUser user) {
        return host.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", team, user.Guid);
    }
}
