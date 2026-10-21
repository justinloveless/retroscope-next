using System.Net;
using System.Text.Json;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.TeamHttp;

[Trait("Category", "TeamHttp")]
[Collection(SharedPostgres.Name)]
public sealed class TeamListsTests(PostgresFixture postgres) {
    [Fact]
    public async Task Templates_are_saved_whole_and_one_of_them_is_the_default() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var team = await host.TeamAsync(owner);
        var columns = new object[] {
            new { title = "Start", color = "green", actionItems = false },
            new { title = "Stop", color = "red", actionItems = false },
            new { title = "Do", color = "purple", actionItems = true },
        };
        var config = new { maxVotes = 5, stagesEnabled = true, voteEmoji = "🔥" };

        using var twoActionColumns = await host.PostAsync(owner, $"/api/teams/{team}/templates", new {
            name = "Bad",
            columns = new[] { new { title = "A", color = "green", actionItems = true }, new { title = "B", color = "red", actionItems = true } },
        });
        using var unknownMember = await host.PostAsync(owner, $"/api/teams/{team}/templates", new { name = "Bad", columns, id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, twoActionColumns.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);

        using var created = await host.PostAsync(owner, $"/api/teams/{team}/templates", new { name = "Start/Stop/Do", columns, config, isDefault = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var template = await TeamHttpHost.JsonAsync(created);
        var id = template.GetProperty("id").GetGuid();
        Assert.Equal(5, template.GetProperty("config").GetProperty("maxVotes").GetInt32());
        Assert.Equal("🔥", template.GetProperty("config").GetProperty("voteEmoji").GetString());

        var listed = await ListAsync(host, owner, team);
        Assert.Equal(["Start/Stop/Do", "Default"], listed.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal([true, false], listed.Select(x => x.GetProperty("isDefault").GetBoolean()));
        Assert.Equal(["Start", "Stop", "Do"], listed[0].GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var defaultId = listed[1].GetProperty("id").GetGuid();
        using var restored = await host.SendAsync(owner, HttpMethod.Put, $"/api/teams/{team}/templates/{defaultId}", new {
            name = "Default",
            isDefault = true,
            columns = new[] { new { title = "Only", color = "gray", actionItems = false } },
        });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        listed = await ListAsync(host, owner, team);
        Assert.Equal([defaultId, id], listed.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(["Only"], listed[0].GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        Assert.False(listed[1].GetProperty("isDefault").GetBoolean());

        using var deleted = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/templates/{id}", null);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from template_columns where template_id = $1", id));

        var otherTeam = await host.TeamAsync(owner, "Other");
        using var foreign = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{otherTeam}/templates/{defaultId}", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Endorsement_types_are_created_edited_and_archived_and_the_leaderboard_counts_current_members() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var former = await host.UserAsync("Fay");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"), (former, "member"));

        using var created = await host.PostAsync(owner, $"/api/teams/{team}/endorsement-types", new { name = "Bug Hunter", emoji = "🐛", description = "" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var type = await TeamHttpHost.JsonAsync(created);
        var typeId = type.GetProperty("id").GetGuid();
        Assert.False(type.TryGetProperty("description", out _));
        using var edited = await host.SendAsync(owner, HttpMethod.Patch, $"/api/teams/{team}/endorsement-types/{typeId}", new { description = "Finds them all" });
        Assert.Equal("Finds them all", (await TeamHttpHost.JsonAsync(edited)).GetProperty("description").GetString());
        Assert.Equal("Bug Hunter", (await TeamHttpHost.JsonAsync(edited)).GetProperty("name").GetString());
        Assert.Equal(4, await host.ScalarAsync<int>("select sort_key from endorsement_types where id = $1", typeId));

        var board = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into retro_boards (id, code, team_id, title, visibility) values ($1, 'KUDOS00001', $2, 'Retro', 'team')", board, team);
        var problemSolver = await host.ScalarAsync<Guid>("select id from endorsement_types where team_id = $1 and name = 'Problem Solver'", team);
        await EndorseAsync(host, board, team, typeId, owner, member, TimeSpan.Zero);
        await EndorseAsync(host, board, team, problemSolver, owner, member, TimeSpan.Zero);
        await EndorseAsync(host, board, team, typeId, member, owner, TimeSpan.FromDays(45));
        await EndorseAsync(host, board, team, typeId, owner, former, TimeSpan.Zero);
        await host.ExecuteAsync("delete from team_members where user_id = $1", former.Guid);
        host.Service<Retroscope.Server.Data.Repos.MembershipCache>().Forget(new Retroscope.Core.Ids.TeamId(team));

        var recent = await LeaderboardAsync(host, member, team, "30d");
        var all = await LeaderboardAsync(host, member, team, "all");
        Assert.Equal([member.Guid], recent.Select(x => x.GetProperty("pid").GetGuid()));
        Assert.Equal(2, recent[0].GetProperty("total").GetInt32());
        Assert.Equal(1, recent[0].GetProperty("counts").GetProperty(typeId.ToString()).GetInt32());
        Assert.Equal([member.Guid, owner.Guid], all.Select(x => x.GetProperty("pid").GetGuid()));
        using var badPeriod = await host.GetAsync(member, $"/api/teams/{team}/endorsements/leaderboard?period=7d");
        Assert.Equal(HttpStatusCode.BadRequest, badPeriod.StatusCode);

        using var received = await host.GetAsync(member, "/api/me/endorsements");
        var mine = (await TeamHttpHost.JsonAsync(received)).EnumerateArray().ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, x => Assert.Equal("Olive", x.GetProperty("fromName").GetString()));
        Assert.All(mine, x => Assert.Equal("Platform", x.GetProperty("teamName").GetString()));

        using var archived = await host.SendAsync(owner, HttpMethod.Delete, $"/api/teams/{team}/endorsement-types/{typeId}", null);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        using var types = await host.GetAsync(member, $"/api/teams/{team}/endorsement-types");
        var listed = (await TeamHttpHost.JsonAsync(types)).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == typeId);
        Assert.True(listed.GetProperty("archived").GetBoolean());
        Assert.Equal(4L, await host.ScalarAsync<long>("select count(*) from endorsements where team_id = $1", team));
    }

    [Fact]
    public async Task Action_items_are_filtered_sorted_and_paged_with_their_board_titles() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));
        var board = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into retro_boards (id, code, team_id, title, visibility) values ($1, 'ITEMS00001', $2, 'Sprint 7', 'team')", board, team);
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await host.ExecuteAsync(
                """
                insert into team_action_items (id, team_id, text, source_board_id, assigned_to, done_at, created_at)
                values ($1, $2, $3, $4, $5, $6, $7)
                """,
                id, team, $"item {i}", i == 4 ? null : board, i % 2 == 0 ? member.Guid : null, i == 3 ? start.AddDays(5) : null, start.AddDays(i));
        }

        var open = await ItemsAsync(host, member, team, "");
        Assert.Equal([ids[4], ids[2], ids[1], ids[0]], open.Items);
        var done = await ItemsAsync(host, member, team, "status=done");
        Assert.Equal([ids[3]], done.Items);
        var mine = await ItemsAsync(host, member, team, $"status=all&assignee={member.Guid}&sort=oldest");
        Assert.Equal([ids[0], ids[2], ids[4]], mine.Items);
        var unassigned = await ItemsAsync(host, member, team, "status=all&assignee=unassigned");
        Assert.Equal([ids[3], ids[1]], unassigned.Items);

        var first = await ItemsAsync(host, member, team, "status=all&limit=2");
        Assert.Equal([ids[4], ids[3]], first.Items);
        var second = await ItemsAsync(host, member, team, $"status=all&limit=2&cursor={Uri.EscapeDataString(first.Next!)}");
        Assert.Equal([ids[2], ids[1]], second.Items);
        var third = await ItemsAsync(host, member, team, $"status=all&limit=2&cursor={Uri.EscapeDataString(second.Next!)}");
        Assert.Equal([ids[0]], third.Items);
        Assert.Null(third.Next);

        using var raw = await host.GetAsync(member, $"/api/teams/{team}/action-items?status=all&sort=oldest&limit=1");
        var item = Assert.Single((await TeamHttpHost.JsonAsync(raw)).GetProperty("items").EnumerateArray());
        Assert.Equal("Sprint 7", item.GetProperty("boardTitle").GetString());
        Assert.Equal(board, item.GetProperty("sourceBoardId").GetGuid());
        Assert.Equal(member.Guid, item.GetProperty("assignedTo").GetGuid());
        Assert.Equal("item 0", item.GetProperty("text").GetString());
        using var bad = await host.GetAsync(member, $"/api/teams/{team}/action-items?status=closed&cursor=nope");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_dashboard_lists_teams_favorites_and_the_rooms_the_user_can_still_open() {
        await using var host = await TeamHttpHost.StartAsync(postgres);
        var owner = await host.UserAsync("Olive");
        var member = await host.UserAsync("Mia");
        var team = await host.TeamAsync(owner, "Platform", (member, "member"));
        var hidden = await host.TeamAsync(owner, "Hidden");
        (await host.SendAsync(member, HttpMethod.Put, $"/api/teams/{team}/favorite", null)).Dispose();
        var teamBoard = Guid.CreateVersion7();
        var hiddenBoard = Guid.CreateVersion7();
        var quickBoard = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        await host.ExecuteAsync("insert into retro_boards (id, code, team_id, title, visibility) values ($1, 'DASH000001', $2, 'Team retro', 'team')", teamBoard, team);
        await host.ExecuteAsync("insert into retro_boards (id, code, team_id, title, visibility) values ($1, 'DASH000002', $2, 'Secret retro', 'team')", hiddenBoard, hidden);
        await host.ExecuteAsync("insert into retro_boards (id, code, title, visibility, created_by) values ($1, 'DASH000003', 'My quick retro', 'link', $2)", quickBoard, member.Guid);
        await host.ExecuteAsync("insert into poker_sessions (id, code, team_id) values ($1, 'DASHPOKER1', $2)", session, team);
        foreach (var (kind, id) in new[] { ("board", teamBoard), ("board", hiddenBoard), ("poker", session) }) {
            await host.ExecuteAsync("insert into recent_activity (user_id, entity_kind, entity_id) values ($1, $2, $3)", member.Guid, kind, id);
        }

        using var response = await host.GetAsync(member, "/api/dashboard");

        var json = await TeamHttpHost.JsonAsync(response);
        Assert.Equal([team], json.GetProperty("teams").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal([team], json.GetProperty("favorites").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        var boards = json.GetProperty("boards").EnumerateArray().ToList();
        Assert.Equal(new[] { teamBoard, quickBoard }.Order(), boards.Select(x => x.GetProperty("id").GetGuid()).Order());
        Assert.Equal("Platform", boards.Single(x => x.GetProperty("id").GetGuid() == teamBoard).GetProperty("teamName").GetString());
        var poker = Assert.Single(json.GetProperty("sessions").EnumerateArray());
        Assert.Equal("DASHPOKER1", poker.GetProperty("code").GetString());
        Assert.Equal(team, poker.GetProperty("teamId").GetGuid());

        using var visit = await host.GetAsync(member, $"/api/teams/{hidden}");
        Assert.Equal(HttpStatusCode.NotFound, visit.StatusCode);
        using var own = await host.GetAsync(member, $"/api/teams/{team}");
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from recent_activity where user_id = $1 and entity_kind = 'team'", member.Guid));
    }

    private static async Task<List<JsonElement>> ListAsync(TeamHttpHost host, TestUser user, Guid team) {
        using var response = await host.GetAsync(user, $"/api/teams/{team}/templates");
        return [.. (await TeamHttpHost.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<List<JsonElement>> LeaderboardAsync(TeamHttpHost host, TestUser user, Guid team, string period) {
        using var response = await host.GetAsync(user, $"/api/teams/{team}/endorsements/leaderboard?period={period}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await TeamHttpHost.JsonAsync(response)).GetProperty("entries").EnumerateArray()];
    }

    private static Task EndorseAsync(TeamHttpHost host, Guid board, Guid team, Guid type, TestUser from, TestUser to, TimeSpan age) {
        return host.ExecuteAsync(
            """
            insert into endorsements (id, board_id, team_id, type_id, from_user_id, to_user_id, created_at)
            values ($1, $2, $3, $4, $5, $6, now() - $7)
            """,
            Guid.CreateVersion7(), board, team, type, from.Guid, to.Guid, age);
    }

    private static async Task<(List<Guid> Items, string? Next)> ItemsAsync(TeamHttpHost host, TestUser user, Guid team, string query) {
        using var response = await host.GetAsync(user, $"/api/teams/{team}/action-items?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TeamHttpHost.JsonAsync(response);
        var next = json.GetProperty("nextCursor");
        return (
            [.. json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())],
            next.ValueKind == JsonValueKind.Null ? null : next.GetString());
    }
}
