using System.Net;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Integration.Tests.Schema;
using static Retroscope.Integration.Tests.Admin.AdminHost;

namespace Retroscope.Integration.Tests.Admin;

[Trait("Category", "Admin")]
[Collection(SharedPostgres.Name)]
public sealed class AdminEndpointTests(PostgresFixture postgres) {
    [Fact]
    public async Task Admin_routes_refuse_anonymous_callers_and_non_admins() {
        await using var host = await StartAsync(postgres);
        var user = await host.CreateUserAsync("plain@example.com");

        var anonymous = await host.GetAsync(null, "/api/admin/users?q=");
        var plain = await host.GetAsync(user, "/api/admin/stats");
        var patch = await host.SendAsync(user, HttpMethod.Patch, $"/api/admin/users/{user.Id}", new { isAdmin = true });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.False(await host.ScalarAsync<bool>("select is_admin from users where id = $1", user.Id.Value));
    }

    [Fact]
    public async Task User_search_matches_an_email_prefix_or_a_name_and_masks_emails() {
        await using var host = await StartAsync(postgres);
        var admin = await host.CreateUserAsync("root@example.com", admin: true, fullName: "Root");
        await host.CreateUserAsync("grace@example.com", fullName: "Grace Hopper");
        await host.CreateUserAsync("gr_ce@example.com", fullName: "Someone");

        var byEmail = await (await host.GetAsync(admin, "/api/admin/users?q=GRACE@")).JsonAsync();
        var byName = await (await host.GetAsync(admin, "/api/admin/users?q=grace%20h")).JsonAsync();
        var wildcard = await (await host.GetAsync(admin, "/api/admin/users?q=gr_c")).JsonAsync();
        var percent = await (await host.GetAsync(admin, "/api/admin/users?q=%25")).JsonAsync();

        var found = Assert.Single(byEmail.EnumerateArray());
        Assert.Equal("g•••@example.com", found.GetProperty("email").GetString());
        Assert.Equal("Grace Hopper", found.GetProperty("name").GetString());
        Assert.False(found.GetProperty("isAdmin").GetBoolean());
        Assert.False(found.GetProperty("disabled").GetBoolean());
        Assert.Equal(found.GetProperty("id").GetString(), Assert.Single(byName.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("Someone", Assert.Single(wildcard.EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(percent.EnumerateArray());
    }

    [Fact]
    public async Task The_last_admin_can_be_neither_demoted_nor_disabled() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);

        var demote = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{root.Id}", new { isAdmin = false });
        var disable = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{root.Id}", new { disabled = true });

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await demote.CodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Equal("last_admin", await disable.CodeAsync());
        Assert.True(await host.ScalarAsync<bool>("select is_admin and disabled_at is null from users where id = $1", root.Id.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log where action like 'admin.%'"));
    }

    [Fact]
    public async Task Another_admin_can_be_demoted_until_one_remains() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var second = await host.CreateUserAsync("second@example.com", admin: true);
        var disabledAdmin = await host.CreateUserAsync("gone@example.com", admin: true);
        await host.ExecuteAsync("update users set disabled_at = now() where id = $1", disabledAdmin.Id.Value);

        var demote = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{second.Id}", new { isAdmin = false });
        // The disabled admin does not count: nobody could use those rights.
        var self = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{root.Id}", new { isAdmin = false });

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.False((await demote.JsonAsync()).GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'admin.user.revoke_admin' and actor_id = $1 and target = $2",
            root.Id.Value,
            second.Id.ToString()));
    }

    [Fact]
    public async Task Two_admins_demoting_each_other_at_once_leave_one_admin() {
        await using var host = await StartAsync(postgres);

        for (var round = 0; round < 5; round++) {
            await host.ExecuteAsync("update users set is_admin = false");
            var a = await host.CreateUserAsync($"a{round}@example.com", admin: true);
            var b = await host.CreateUserAsync($"b{round}@example.com", admin: true);

            var results = await Task.WhenAll(
                host.SendAsync(a, HttpMethod.Patch, $"/api/admin/users/{b.Id}", new { isAdmin = false }),
                host.SendAsync(b, HttpMethod.Patch, $"/api/admin/users/{a.Id}", new { isAdmin = false }));

            // The loser is refused last_admin, or loses its session first when the winner's change signs it out.
            Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from users where is_admin and disabled_at is null"));
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Disabling_a_user_revokes_their_sessions_kicks_their_sockets_and_is_audited() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var target = await host.CreateUserAsync("target@example.com");
        using var socket = await host.ConnectAsync(target);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(target, "/api/me")).StatusCode);

        var response = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{target.Id}", new { disabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("disabled").GetBoolean());
        Assert.Equal(CloseCodes.Unauthenticated, await socket.WaitForCloseAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync(target, "/api/me")).StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from sessions where user_id = $1", target.Id.Value));
        Assert.Contains(target.Id, host.Revoker.Principals);
        Assert.Single(host.Revoker.AuthSessions);
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'admin.user.disable' and actor_id = $1 and target = $2",
            root.Id.Value,
            target.Id.ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(
            null, HttpMethod.Post, "/api/auth/login", new { email = "target@example.com", password = "whatever-it-was" })).StatusCode);

        var enable = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{target.Id}", new { disabled = false });

        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.False((await enable.JsonAsync()).GetProperty("disabled").GetBoolean());
        Assert.True(await host.ScalarAsync<bool>("select disabled_at is null from users where id = $1", target.Id.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'admin.user.enable'"));
    }

    [Fact]
    public async Task Granting_admin_signs_the_user_out_and_the_next_session_has_the_rights() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var target = await host.CreateUserAsync("target@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(target, "/api/admin/stats")).StatusCode);

        var response = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{target.Id}", new { isAdmin = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync(target, "/api/admin/stats")).StatusCode);
        Assert.Contains(target.Id, host.Revoker.Principals);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'admin.user.grant_admin'"));
        var fresh = await NewSessionAsync(host, target);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(fresh, "/api/admin/stats")).StatusCode);
    }

    [Fact]
    public async Task A_patch_must_change_something_known_and_name_a_real_user() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);

        var empty = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{root.Id}", new { });
        var unknownMember = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{root.Id}", new { isAdmin = true, email = "x@y.z" });
        var missing = await host.SendAsync(root, HttpMethod.Patch, $"/api/admin/users/{Guid.NewGuid()}", new { disabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Team_search_counts_members_and_skips_deleted_teams() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var team = await TeamAsync(host, "Platform Crew", root.Id);
        await TeamAsync(host, "Platform Old", root.Id, deleted: true);

        var teams = await (await host.GetAsync(root, "/api/admin/teams?q=PLATFORM")).JsonAsync();

        var found = Assert.Single(teams.EnumerateArray());
        Assert.Equal(team.Value.ToString(), found.GetProperty("id").GetString());
        Assert.Equal("Platform Crew", found.GetProperty("name").GetString());
        Assert.Equal(1, found.GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task Membership_repair_goes_through_the_team_actor_keeps_one_owner_and_is_audited() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var owner = await host.CreateUserAsync("owner@example.com", fullName: "Olive");
        var newcomer = await host.CreateUserAsync("new@example.com", fullName: "Nina");
        var team = await TeamAsync(host, "Platform", owner.Id);
        var path = $"/api/admin/teams/{team.Value}/members";

        var added = await host.SendAsync(root, HttpMethod.Post, path, new { userId = newcomer.Id.Value });
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal("member", await RoleAsync(host, team, newcomer.Id));

        var promoted = await host.SendAsync(root, HttpMethod.Post, path, new { userId = newcomer.Id.Value, role = "owner" });
        Assert.Equal(HttpStatusCode.NoContent, promoted.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, newcomer.Id));
        Assert.Equal("admin", await RoleAsync(host, team, owner.Id));

        var demoteOwner = await host.SendAsync(root, HttpMethod.Post, path, new { userId = newcomer.Id.Value, role = "member" });
        var removeOwner = await host.SendAsync(root, HttpMethod.Delete, $"{path}/{newcomer.Id}");
        Assert.Equal(HttpStatusCode.Conflict, demoteOwner.StatusCode);
        Assert.Equal("owner_required", await demoteOwner.CodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, removeOwner.StatusCode);

        var removed = await host.SendAsync(root, HttpMethod.Delete, $"{path}/{owner.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null(await RoleAsync(host, team, owner.Id));
        // The live team room saw every change, not only the rows.
        var members = await host.Registry.QueryAsync<Core.Team.TeamState, Core.Team.TeamInput, string>(
            RoomKey.Team(team),
            view => ValueTask.FromResult(string.Join(',', view.State!.Members.Values.Select(x => $"{x.Name}:{x.Role}"))),
            Token);
        Assert.Equal("Nina:Owner", members);

        var audit = await host.ScalarAsync<string>("""
            select string_agg(action || ' ' || target || ' ' || (meta->>'role'), ', ' order by id)
            from audit_log where team_id = $1 and actor_id = $2
            """, team.Value, root.Id.Value);
        Assert.Equal($"admin.team.member.add {newcomer.Id} member, admin.team.owner.transfer {newcomer.Id} owner", audit);
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'admin.team.member.remove' and target = $1", owner.Id.ToString()));
    }

    [Fact]
    public async Task Making_a_non_member_the_owner_adds_them_then_transfers_ownership() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var owner = await host.CreateUserAsync("owner@example.com", fullName: "Olive");
        var outsider = await host.CreateUserAsync("out@example.com", fullName: "Otto");
        var team = await TeamAsync(host, "Platform", owner.Id);

        var promoted = await host.SendAsync(
            root, HttpMethod.Post, $"/api/admin/teams/{team.Value}/members", new { userId = outsider.Id.Value, role = "owner" });

        Assert.Equal(HttpStatusCode.NoContent, promoted.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, outsider.Id));
        Assert.Equal("admin", await RoleAsync(host, team, owner.Id));
        var audit = await host.ScalarAsync<string>(
            "select string_agg(action || ' ' || target, ', ' order by id) from audit_log where team_id = $1", team.Value);
        Assert.Equal($"admin.team.member.add {outsider.Id}, admin.team.owner.transfer {outsider.Id}", audit);
    }

    [Fact]
    public async Task An_owner_change_that_fails_to_promote_leaves_the_old_owner_in_place() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var owner = await host.CreateUserAsync("owner@example.com", fullName: "Olive");
        var member = await host.CreateUserAsync("member@example.com", fullName: "Mia");
        var team = await TeamAsync(host, "Platform", owner.Id);
        var path = $"/api/admin/teams/{team.Value}/members";
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(root, HttpMethod.Post, path, new { userId = member.Id.Value })).StatusCode);
        // Every promotion to owner in this team fails, as a crash between a demotion and a promotion would.
        await host.ExecuteAsync($$"""
            create function refuse_owner() returns trigger language plpgsql as $$
            begin
              if new.role = 'owner' and new.team_id = '{{team.Value}}' then
                raise exception 'promotion refused';
              end if;
              return new;
            end $$;
            create trigger refuse_owner before update on team_members for each row execute function refuse_owner();
            """);

        var promoted = await host.SendAsync(root, HttpMethod.Post, path, new { userId = member.Id.Value, role = "owner" });

        Assert.NotEqual(HttpStatusCode.NoContent, promoted.StatusCode);
        Assert.Equal("owner", await RoleAsync(host, team, owner.Id));
        Assert.Equal("member", await RoleAsync(host, team, member.Id));
        var members = await host.Registry.QueryAsync<Core.Team.TeamState, Core.Team.TeamInput, string>(
            RoomKey.Team(team),
            view => ValueTask.FromResult(string.Join(',', view.State!.Members.Values.OrderBy(x => x.Name).Select(x => $"{x.Name}:{x.Role}"))),
            Token);
        Assert.Equal("Mia:Member,Olive:Owner", members);
        Assert.Equal(0L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where team_id = $1 and action <> 'admin.team.member.add'", team.Value));
    }

    [Fact]
    public async Task Membership_repair_refuses_unknown_and_deleted_teams_and_unknown_users() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var deleted = await TeamAsync(host, "Gone", root.Id, deleted: true);
        var live = await TeamAsync(host, "Live", root.Id);

        var unknownTeam = await host.SendAsync(root, HttpMethod.Post, $"/api/admin/teams/{Guid.NewGuid()}/members", new { userId = root.Id.Value });
        var deletedTeam = await host.SendAsync(root, HttpMethod.Post, $"/api/admin/teams/{deleted.Value}/members", new { userId = root.Id.Value });
        var unknownUser = await host.SendAsync(root, HttpMethod.Post, $"/api/admin/teams/{live.Value}/members", new { userId = Guid.NewGuid() });
        var noUser = await host.SendAsync(root, HttpMethod.Post, $"/api/admin/teams/{live.Value}/members", new { role = "member" });
        var badRole = await host.SendAsync(root, HttpMethod.Post, $"/api/admin/teams/{live.Value}/members", new { userId = root.Id.Value, role = "boss" });

        Assert.Equal(HttpStatusCode.NotFound, unknownTeam.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletedTeam.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
    }

    [Fact]
    public async Task Stats_report_rooms_sockets_pool_rings_blobs_and_mail() {
        await using var host = await StartAsync(postgres);
        var root = await host.CreateUserAsync("root@example.com", admin: true);
        var session = new SessionId(Guid.CreateVersion7());
        await host.ExecuteAsync("insert into poker_sessions (id, code) values ($1, 'STATS1')", session.Value);
        using var socket = await host.ConnectAsync(root);
        await socket.SubscribeAsync(RoomKey.Poker(session));
        var board = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into retro_boards (id, code, title, visibility) values ($1, 'STATSB', 'B', 'link')", board);
        await host.ExecuteAsync("""
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes)
            values ($1, 'board_image', $2, 'image/png', 1234, sha256('x'), 'x')
            """, Guid.NewGuid(), board);
        await host.ExecuteAsync("insert into email_outbox (to_address, subject, body_text) values ('a@b.c', 's', 'b')");
        await host.ExecuteAsync("insert into email_outbox (to_address, subject, body_text) values ('a@b.c', 's', null)");

        var stats = await (await host.GetAsync(root, "/api/admin/stats")).JsonAsync();

        Assert.True(stats.GetProperty("rssBytes").GetInt64() > 0);
        Assert.True(stats.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(stats.GetProperty("managedHeapBytes").GetInt64() > 0);
        Assert.True(stats.GetProperty("liveRooms").GetInt32() >= 1);
        Assert.Equal(1, stats.GetProperty("sockets").GetInt32());
        Assert.True(stats.GetProperty("poolSize").GetInt32() > 0);
        Assert.True(stats.GetProperty("poolOpen").GetInt32() >= 1);
        Assert.True(stats.GetProperty("poolInUse").GetInt32() >= 0);
        Assert.True(stats.GetProperty("ringBytes").GetInt64() >= 0);
        Assert.Equal(1234, stats.GetProperty("blobBytes").GetInt64());
        Assert.Equal(0, stats.GetProperty("emailBudgetDrops").GetInt64());
        Assert.Equal(1, stats.GetProperty("outboxDepth").GetInt32());
    }

    internal static async Task<TeamId> TeamAsync(AdminHost host, string name, PrincipalId owner, bool deleted = false) {
        var team = new TeamId(Guid.CreateVersion7());
        await host.ExecuteAsync(
            "insert into teams (id, name, deleted_at) values ($1, $2, $3)", team.Value, name, deleted ? DateTimeOffset.UtcNow : DBNull.Value);
        await host.ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, 'owner')", team.Value, owner.Value);
        return team;
    }

    private static async Task<string?> RoleAsync(AdminHost host, TeamId team, PrincipalId user) {
        var role = await host.ScalarAsync<object>(
            "select coalesce((select role from team_members where team_id = $1 and user_id = $2), '')", team.Value, user.Value);
        return role is string { Length: > 0 } text ? text : null;
    }

    private static async Task<SignedIn> NewSessionAsync(AdminHost host, SignedIn user) {
        var token = Core.Security.OpaqueToken.New();
        var now = host.Time.GetUtcNow();
        await host.Service<Server.Data.Repos.SessionsRepo>().InsertAsync(
            Server.Data.Repos.SessionsRepo.Insert(
                Core.Security.TokenHash.Of(token),
                user.Id,
                Server.Auth.AuthMethod.Password,
                now,
                now + Server.Auth.Sessions.IdleLifetime,
                now + Server.Auth.Sessions.AbsoluteLifetime,
                "test"),
            Token);
        return user with { Token = token };
    }
}
