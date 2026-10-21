using System.Net;
using System.Text.RegularExpressions;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Hosting;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Integration.Tests.TeamHttp;

[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed partial class InviteTests(PostgresFixture postgres) {
    [Fact]
    public async Task Link_invites_only_make_members_and_their_link_can_be_copied_again() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var joiner = await host.UserAsync("Jo");
        var team = await host.TeamAsync(owner);

        using var asAdmin = await host.PostAsync(owner, $"/api/teams/{team}/invites", new { kind = "link", role = "admin" });
        using var tooLong = await host.PostAsync(owner, $"/api/teams/{team}/invites", new { kind = "link", expiresInDays = 31 });
        Assert.Equal(HttpStatusCode.BadRequest, asAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var created = await host.PostAsync(owner, $"/api/teams/{team}/invites", new { kind = "link", expiresInDays = 3 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invite = await TeamHttpHost.JsonAsync(created);
        Assert.Equal("member", invite.GetProperty("role").GetString());
        var url = invite.GetProperty("url").GetString()!;
        Assert.StartsWith($"{TestApp.PublicUrl}/invite#t=", url, StringComparison.Ordinal);
        var token = url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        Assert.Equal("member", await host.ScalarAsync<string>("select role from team_invites where id = $1", invite.GetProperty("id").GetGuid()));

        using var listed = await host.GetAsync(owner, $"/api/teams/{team}/invites");
        Assert.Equal(url, Assert.Single((await TeamHttpHost.JsonAsync(listed)).GetProperty("items").EnumerateArray()).GetProperty("url").GetString());

        using var preview = await host.PostAsync(TeamHttpHost.Anonymous, "/api/invites/preview", new { token });
        var shown = await TeamHttpHost.JsonAsync(preview);
        Assert.Equal("Platform", shown.GetProperty("teamName").GetString());
        Assert.Equal("link", shown.GetProperty("kind").GetString());
        Assert.False(shown.GetProperty("expired").GetBoolean());

        using var accepted = await host.PostAsync(joiner, "/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(team, (await TeamHttpHost.JsonAsync(accepted)).GetProperty("teamId").GetGuid());
        Assert.Equal("member", await host.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", team, joiner.Guid));
        Assert.Equal(1, await host.ScalarAsync<int>("select use_count from team_invites where team_id = $1", team));
    }

    [Fact]
    public async Task Accepting_a_member_link_never_demotes_an_admin() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"));
        var token = await LinkTokenAsync(host, owner, team);

        using var accepted = await host.PostAsync(admin, "/api/invites/accept", new { token });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("admin", await host.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", team, admin.Guid));
    }

    [Fact]
    public async Task Accepting_goes_through_the_team_actor_so_the_room_and_the_cache_see_the_new_member_at_once() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var joiner = await host.UserAsync("Jo");
        var team = await host.TeamAsync(owner);
        var room = await host.SubscribeAsync(owner, RoomKey.Team(new TeamId(team)));
        await room.NextAsync<SnapFrame>();
        var token = await LinkTokenAsync(host, owner, team);

        using var accepted = await host.PostAsync(joiner, "/api/invites/accept", new { token });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var members = Assert.IsType<TeamMembers>(Assert.Single((await room.NextAsync<TxFrame>()).Ev));
        Assert.Contains(members.Members, x => x.Pid == joiner.Id && x.Role == TeamRole.Member && x.Name == "Jo");
        Assert.Equal(TeamRole.Member, host.Service<MembershipCache>().RoleIn(new TeamId(team), joiner.Id));
        using var joined = await host.GetAsync(joiner, $"/api/teams/{team}");
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'team.member.add' and team_id = $1", team));
    }

    [Fact]
    public async Task An_email_invite_binds_to_its_verified_address_and_is_used_once() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var invited = await host.UserAsync("Ivy", email: "ivy@example.com");
        var stranger = await host.UserAsync("Sam");
        var team = await host.TeamAsync(owner);
        var token = await EmailTokenAsync(host, owner, team, "Ivy@Example.com", "member");

        using var wrongAddress = await host.PostAsync(stranger, "/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.Forbidden, wrongAddress.StatusCode);
        Assert.Equal("email_mismatch", await TeamHttpHost.CodeAsync(wrongAddress));

        await host.ExecuteAsync("update users set email_verified_at = null where id = $1", invited.Guid);
        using var unverified = await host.PostAsync(invited, "/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        Assert.Equal("email_unverified", await TeamHttpHost.CodeAsync(unverified));
        await host.ExecuteAsync("update users set email_verified_at = now() where id = $1", invited.Guid);

        using var accepted = await host.PostAsync(invited, "/api/invites/accept", new { token });
        using var again = await host.PostAsync(invited, "/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("member", await host.ScalarAsync<string>("select role from team_members where team_id = $1 and user_id = $2", team, invited.Guid));
        Assert.Equal(invited.Guid, await host.ScalarAsync<Guid>("select accepted_by from team_invites where team_id = $1", team));

        using var preview = await host.PostAsync(TeamHttpHost.Anonymous, "/api/invites/preview", new { token });
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        using var listed = await host.GetAsync(owner, $"/api/teams/{team}/invites");
        Assert.Empty((await TeamHttpHost.JsonAsync(listed)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Email_invites_need_a_verified_sender_and_the_owner_to_grant_admin() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var unverifiedAdmin = await host.UserAsync("Una", verified: false);
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"), (unverifiedAdmin, "admin"));

        using var unverified = await host.PostAsync(unverifiedAdmin, $"/api/teams/{team}/invites", new { kind = "email", email = "new@example.com", role = "member" });
        using var unverifiedLink = await host.PostAsync(unverifiedAdmin, $"/api/teams/{team}/invites", new { kind = "link", expiresInDays = 7 });
        using var adminGrantsAdmin = await host.PostAsync(admin, $"/api/teams/{team}/invites", new { kind = "email", email = "new@example.com", role = "admin" });
        using var adminGrantsMember = await host.PostAsync(admin, $"/api/teams/{team}/invites", new { kind = "email", email = "new@example.com", role = "member" });
        using var ownerGrantsAdmin = await host.PostAsync(owner, $"/api/teams/{team}/invites", new { kind = "email", email = "boss@example.com", role = "admin" });

        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        Assert.Equal("email_unverified", await TeamHttpHost.CodeAsync(unverified));
        Assert.Equal(HttpStatusCode.Created, unverifiedLink.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminGrantsAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, adminGrantsMember.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ownerGrantsAdmin.StatusCode);
        var listed = await TeamHttpHost.JsonAsync(await host.GetAsync(owner, $"/api/teams/{team}/invites"));
        var emails = listed.GetProperty("items").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "email").ToList();
        Assert.Equal(["b•••@example.com", "n•••@example.com"], emails.Select(x => x.GetProperty("email").GetString()));
        Assert.All(emails, x => Assert.False(x.TryGetProperty("url", out _)));
        Assert.DoesNotContain("new@example.com", listed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_invite_mail_quotes_the_team_name_without_anything_a_mail_client_would_link() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner, "Win \"prizes\" at https://evil.example/claim or www.evil.example now");

        await EmailTokenAsync(host, owner, team, "ivy@example.com", "member");

        var body = await host.ScalarAsync<string>("select body_text from email_outbox where to_address = 'ivy@example.com'");
        var subject = await host.ScalarAsync<string>("select subject from email_outbox where to_address = 'ivy@example.com'");
        Assert.Contains("\"Win prizes at or now\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", subject, StringComparison.Ordinal);
        Assert.Matches(InviteLink(), body);
    }

    [Fact]
    public async Task Over_the_email_budget_the_invite_is_made_and_the_mail_dropped() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);

        for (var i = 0; i < 4; i++) {
            using var created = await host.PostAsync(owner, $"/api/teams/{team}/invites", new { kind = "email", email = "ivy@example.com", role = "member" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        Assert.Equal(4L, await host.ScalarAsync<long>("select count(*) from team_invites where team_id = $1", team));
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from email_outbox where to_address = 'ivy@example.com'"));
    }

    [Fact]
    public async Task Revoked_and_expired_invites_are_refused_and_deleted_teams_hide_theirs() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var joiner = await host.UserAsync("Jo");
        var team = await host.TeamAsync(owner);
        var revokedToken = await LinkTokenAsync(host, owner, team);
        var expiredToken = await LinkTokenAsync(host, owner, team);
        var revokedId = await host.ScalarAsync<Guid>("select id from team_invites where team_id = $1 order by created_at limit 1", team);

        using var revoke = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/invites/{revokedId}", null);
        using var revokeAgain = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/invites/{revokedId}", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokeAgain.StatusCode);
        await host.ExecuteAsync("update team_invites set expires_at = now() - interval '1 minute' where team_id = $1 and id <> $2", team, revokedId);

        using var revoked = await host.PostAsync(joiner, "/api/invites/accept", new { token = revokedToken });
        using var expired = await host.PostAsync(joiner, "/api/invites/accept", new { token = expiredToken });
        using var preview = await host.PostAsync(TeamHttpHost.Anonymous, "/api/invites/preview", new { token = expiredToken });
        Assert.Equal((HttpStatusCode.Gone, "revoked"), (revoked.StatusCode, await TeamHttpHost.CodeAsync(revoked)));
        Assert.Equal((HttpStatusCode.Gone, "expired"), (expired.StatusCode, await TeamHttpHost.CodeAsync(expired)));
        Assert.True((await TeamHttpHost.JsonAsync(preview)).GetProperty("expired").GetBoolean());
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from team_members where user_id = $1", joiner.Guid));

        var live = await LinkTokenAsync(host, owner, team);
        await host.ExecuteAsync("update teams set deleted_at = now() where id = $1", team);
        using var deleted = await host.PostAsync(joiner, "/api/invites/accept", new { token = live });
        using var deletedPreview = await host.PostAsync(TeamHttpHost.Anonymous, "/api/invites/preview", new { token = live });
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletedPreview.StatusCode);
    }

    [Fact]
    public async Task An_invite_sealed_by_the_importer_layout_opens_for_the_list() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);
        var id = Guid.CreateVersion7();
        const string legacyToken = "legacy-token-from-supabase";
        var keyring = host.Service<Retroscope.Core.Security.Keyring>();
        var aad = new byte[16];
        id.TryWriteBytes(aad, bigEndian: true, out _);
        var box = keyring.Seal(Retroscope.Core.Security.KeyPurpose.InviteBox, System.Text.Encoding.UTF8.GetBytes(legacyToken), aad);
        await host.ExecuteAsync(
            """
            insert into team_invites (id, team_id, kind, role, token_hash, token_box, expires_at)
            values ($1, $2, 'link', 'member', sha256(convert_to($3, 'UTF8')), $4, now() + interval '1 day')
            """,
            id, team, legacyToken, box);

        using var listed = await host.GetAsync(owner, $"/api/teams/{team}/invites");

        Assert.Equal(
            $"{TestApp.PublicUrl}/invite#t={legacyToken}",
            Assert.Single((await TeamHttpHost.JsonAsync(listed)).GetProperty("items").EnumerateArray()).GetProperty("url").GetString());
    }

    private static async Task<string> LinkTokenAsync(TeamHttpHost host, TestUser admin, Guid team) {
        using var created = await host.PostAsync(admin, $"/api/teams/{team}/invites", new { kind = "link", expiresInDays = 7 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = (await TeamHttpHost.JsonAsync(created)).GetProperty("url").GetString()!;
        return url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
    }

    /// <summary>Creates an email invite and reads its token from the queued mail, as the recipient would.</summary>
    private static async Task<string> EmailTokenAsync(TeamHttpHost host, TestUser admin, Guid team, string email, string role) {
        using var created = await host.PostAsync(admin, $"/api/teams/{team}/invites", new { kind = "email", email, role });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False((await TeamHttpHost.JsonAsync(created)).TryGetProperty("url", out _));
        var body = await host.ScalarAsync<string>(
            "select body_text from email_outbox where to_address = $1 order by id desc limit 1",
            email.ToLowerInvariant());
        return InviteLink().Match(body).Groups[1].Value;
    }

    [GeneratedRegex(@"https://retro\.test/invite#t=([A-Za-z0-9_-]{43})")]
    private static partial Regex InviteLink();
}
