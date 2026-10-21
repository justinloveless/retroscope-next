using System.Net;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.TeamHttp;

// §j "authz table per route": every team route answered for every relationship to the team. Probes that pass
// authorization carry a body that fails validation (or name a missing row), so the table runs without changing the
// team: an allowed caller gets 200/204/400/404-of-the-row, a denied one 401/403/404-of-the-team.
[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class TeamHttpAuthzTests(PostgresFixture postgres) {
    private enum Who {
        Anonymous,
        Unverified,
        Outsider,
        Member,
        Admin,
        Owner,
    }

    private sealed record Probe(string Method, string Path, object? Body, IReadOnlyDictionary<Who, HttpStatusCode> Expected);

    [Fact]
    public async Task Every_route_answers_each_relationship_to_the_team_as_the_table_says() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var admin = await host.UserAsync("Adam");
        var member = await host.UserAsync("Mia");
        var outsider = await host.UserAsync("Otto");
        var unverified = await host.UserAsync("Una", verified: false);
        var team = await host.TeamAsync(owner, "Platform", (admin, "admin"), (member, "member"));
        var users = new Dictionary<Who, TestUser> {
            [Who.Anonymous] = TeamHttpHost.Anonymous,
            [Who.Unverified] = unverified,
            [Who.Outsider] = outsider,
            [Who.Member] = member,
            [Who.Admin] = admin,
            [Who.Owner] = owner,
        };
        var missing = Guid.CreateVersion7();
        var t = $"/api/teams/{team}";

        Probe[] table = [
            Row("GET", "/api/teams", null, ok: HttpStatusCode.OK, signedIn: true),
            Row("POST", "/api/teams", new { name = "" }, ok: HttpStatusCode.BadRequest, signedIn: true, unverified: HttpStatusCode.Forbidden),
            Row("GET", "/api/dashboard", null, ok: HttpStatusCode.OK, signedIn: true),
            Row("GET", "/api/me/endorsements", null, ok: HttpStatusCode.OK, signedIn: true),
            Row("POST", "/api/invites/accept", new { token = "nope" }, ok: HttpStatusCode.NotFound, signedIn: true),
            Public("POST", "/api/invites/preview", new { token = "nope" }, HttpStatusCode.NotFound),

            Team("GET", t, null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
            Team("PATCH", t, new { name = "" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.BadRequest),
            Team("DELETE", t, new { confirmName = "wrong" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.Forbidden, owner: HttpStatusCode.BadRequest),
            Team("PUT", $"{t}/favorite", null, member: HttpStatusCode.NoContent, admin: HttpStatusCode.NoContent),
            Team("DELETE", $"{t}/favorite", null, member: HttpStatusCode.NoContent, admin: HttpStatusCode.NoContent),
            Team("GET", $"{t}/members", null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
            Team("PATCH", $"{t}/members/{member.Guid}", new { role = "owner" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.Forbidden, owner: HttpStatusCode.BadRequest),
            Team("DELETE", $"{t}/members/{missing}", null, member: HttpStatusCode.NotFound, admin: HttpStatusCode.NotFound),
            Team("POST", $"{t}/transfer-ownership", new { userId = (Guid?)null }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.Forbidden, owner: HttpStatusCode.BadRequest),
            Team("GET", $"{t}/action-items", null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
            Team("GET", $"{t}/invites", null, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.OK),
            Team("POST", $"{t}/invites", new { kind = "link", expiresInDays = 0 }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.BadRequest),
            Team("DELETE", $"{t}/invites/{missing}", null, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.NotFound),
            Team("GET", $"{t}/templates", null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
            Team("POST", $"{t}/templates", new { name = "" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.BadRequest),
            Team("PUT", $"{t}/templates/{missing}", new { name = "" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.BadRequest),
            Team("DELETE", $"{t}/templates/{missing}", null, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.NotFound),
            Team("GET", $"{t}/endorsement-types", null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
            Team("POST", $"{t}/endorsement-types", new { name = "" }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.BadRequest),
            Team("PATCH", $"{t}/endorsement-types/{missing}", new { }, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.NotFound),
            Team("DELETE", $"{t}/endorsement-types/{missing}", null, member: HttpStatusCode.Forbidden, admin: HttpStatusCode.NotFound),
            Team("GET", $"{t}/endorsements/leaderboard?period=90d", null, member: HttpStatusCode.OK, admin: HttpStatusCode.OK),
        ];

        var wrong = new List<string>();
        foreach (var probe in table) {
            foreach (var (who, expected) in probe.Expected) {
                using var response = await host.SendAsync(users[who], new HttpMethod(probe.Method), probe.Path, probe.Body);
                if (response.StatusCode != expected) {
                    wrong.Add($"{probe.Method} {probe.Path} as {who}: expected {(int)expected}, got {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TeamHttpHost.Token)}");
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public async Task A_deleted_team_answers_every_member_like_a_missing_one() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);
        await host.ExecuteAsync("update teams set deleted_at = now() where id = $1", team);

        using var get = await host.GetAsync(owner, $"/api/teams/{team}");
        using var members = await host.GetAsync(owner, $"/api/teams/{team}/members");
        using var other = await host.GetAsync(owner, $"/api/teams/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, members.StatusCode);
        Assert.Equal(await other.Content.ReadAsStringAsync(TeamHttpHost.Token), await get.Content.ReadAsStringAsync(TeamHttpHost.Token));
    }

    private static Probe Row(string method, string path, object? body, HttpStatusCode ok, bool signedIn, HttpStatusCode? unverified = null) {
        var expected = new Dictionary<Who, HttpStatusCode> {
            [Who.Anonymous] = signedIn ? HttpStatusCode.Unauthorized : ok,
            [Who.Unverified] = unverified ?? ok,
            [Who.Outsider] = ok,
            [Who.Member] = ok,
        };
        return new Probe(method, path, body, expected);
    }

    private static Probe Public(string method, string path, object? body, HttpStatusCode ok) => Row(method, path, body, ok, signedIn: false);

    /// <param name="owner">Defaults to <paramref name="admin"/>: the owner can do everything an admin can.</param>
    private static Probe Team(
        string method,
        string path,
        object? body,
        HttpStatusCode member,
        HttpStatusCode admin,
        HttpStatusCode? owner = null) {
        var expected = new Dictionary<Who, HttpStatusCode> {
            [Who.Anonymous] = HttpStatusCode.Unauthorized,
            [Who.Unverified] = HttpStatusCode.NotFound,
            [Who.Outsider] = HttpStatusCode.NotFound,
            [Who.Member] = member,
            [Who.Admin] = admin,
            [Who.Owner] = owner ?? admin,
        };
        return new Probe(method, path, body, expected);
    }
}
