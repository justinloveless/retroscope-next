using System.Net;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class ActionItemTests(PostgresFixture postgres) {
    [Fact]
    public async Task Comments_are_the_source_cards_projected_with_the_boards_anonymity_rules() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var scene = await Scene.CreateAsync(app);
        var tagger = AnonTagger.Parse(RoomHttpApp.AnonTagKey);
        var anonymous = Guid.CreateVersion7();
        var named = Guid.CreateVersion7();
        await app.ExecAsync(
            "insert into retro_comments (id, card_id, board_id, body, anon_tag, created_at) values ($1, $2, $3, 'Mine, quietly', $4, now() - interval '2 minutes')",
            anonymous,
            scene.Card,
            scene.Board,
            tagger.Tag(new BoardId(scene.Board), new PrincipalId(scene.Member)));
        await app.ExecAsync(
            "insert into retro_comments (id, card_id, board_id, body, author_id, created_at) values ($1, $2, $3, 'From Ann', $4, now() - interval '1 minute')",
            named,
            scene.Card,
            scene.Board,
            scene.Admin);

        var list = await RoomHttpApp.JsonAsync(
            await app.GetAsync($"/api/teams/{scene.Team}/action-items/{scene.Item}/comments", RoomHttpApp.User(scene.Member)));
        var items = list.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([anonymous, named], items.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(["id", "body", "anonymous", "createdAt", "mine"], items[0].EnumerateObject().Select(x => x.Name));
        Assert.True(items[0].GetProperty("anonymous").GetBoolean());
        Assert.True(items[0].GetProperty("mine").GetBoolean());
        Assert.Equal("Ann", items[1].GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(scene.Admin, items[1].GetProperty("author").GetProperty("pid").GetGuid());
        Assert.False(items[1].GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task A_posted_comment_goes_through_the_source_board() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var scene = await Scene.CreateAsync(app);
        var comment = Guid.CreateVersion7();
        var member = RoomHttpApp.User(scene.Member);

        using var posted = await app.PostAsync(
            $"/api/teams/{scene.Team}/action-items/{scene.Item}/comments", member, new { commentId = comment, body = "On it" });
        var created = await RoomHttpApp.JsonAsync(posted, HttpStatusCode.Created);
        using var repeated = await app.PostAsync(
            $"/api/teams/{scene.Team}/action-items/{scene.Item}/comments", member, new { commentId = comment, body = "On it" });
        var list = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{scene.Team}/action-items/{scene.Item}/comments", member));
        var stored = await app.ScalarAsync<Guid>("select author_id from retro_comments where id = $1", comment);

        Assert.Equal(comment, created.GetProperty("commentId").GetGuid());
        Assert.Equal(HttpStatusCode.Created, repeated.StatusCode);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("On it", item.GetProperty("body").GetString());
        Assert.True(item.GetProperty("mine").GetBoolean());
        Assert.False(item.GetProperty("anonymous").GetBoolean());
        Assert.Equal(scene.Member, stored);
        Assert.Contains($"comments {scene.Item} 1", app.Notices.Log);
    }

    [Fact]
    public async Task Comments_are_refused_to_outsiders_and_for_foreign_items() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var scene = await Scene.CreateAsync(app);
        var other = await Scene.CreateAsync(app);
        var outsider = RoomHttpApp.User(await app.UserAsync());
        var member = RoomHttpApp.User(scene.Member);
        var path = $"/api/teams/{scene.Team}/action-items/{scene.Item}/comments";

        using var outsiderGet = await app.GetAsync(path, outsider);
        using var outsiderPost = await app.PostAsync(path, outsider, new { commentId = Guid.CreateVersion7(), body = "Hi" });
        using var foreign = await app.GetAsync($"/api/teams/{scene.Team}/action-items/{other.Item}/comments", member);
        using var empty = await app.PostAsync(path, member, new { commentId = Guid.CreateVersion7(), body = "  " });
        using var noId = await app.PostAsync(path, member, new { body = "Hi" });
        using var extra = await app.PostAsync(path, member, new { commentId = Guid.CreateVersion7(), body = "Hi", anonymous = true });

        Assert.Equal(HttpStatusCode.NotFound, outsiderGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderPost.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
    }

    [Fact]
    public async Task The_action_item_list_filters_sorts_and_names_the_board() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var scene = await Scene.CreateAsync(app);
        var done = Guid.CreateVersion7();
        var assigned = Guid.CreateVersion7();
        await app.ExecAsync(
            "insert into team_action_items (id, team_id, text, created_at, done_at, done_by) values ($1, $2, 'Done thing', now() - interval '3 days', now(), $3)",
            done,
            scene.Team,
            scene.Admin);
        await app.ExecAsync(
            "insert into team_action_items (id, team_id, text, created_at, assigned_to) values ($1, $2, 'Assigned thing', now() - interval '2 days', $3)",
            assigned,
            scene.Team,
            scene.Member);
        var member = RoomHttpApp.User(scene.Member);

        var open = await ItemsAsync(app, $"/api/teams/{scene.Team}/action-items", member);
        var all = await ItemsAsync(app, $"/api/teams/{scene.Team}/action-items?status=all&sort=oldest", member);
        var finished = await ItemsAsync(app, $"/api/teams/{scene.Team}/action-items?status=done", member);
        var mine = await ItemsAsync(app, $"/api/teams/{scene.Team}/action-items?assignee={scene.Member}", member);
        var nobody = await ItemsAsync(app, $"/api/teams/{scene.Team}/action-items?assignee=unassigned", member);
        using var badStatus = await app.GetAsync($"/api/teams/{scene.Team}/action-items?status=later", member);

        Assert.Equal([scene.Item, assigned], open.Select(Id));
        Assert.Equal([done, assigned, scene.Item], all.Select(Id));
        Assert.Equal([done], finished.Select(Id));
        Assert.Equal([assigned], mine.Select(Id));
        Assert.Equal([scene.Item], nobody.Select(Id));
        Assert.Equal("Sprint 7", open[0].GetProperty("boardTitle").GetString());
        Assert.Equal(scene.Card, open[0].GetProperty("sourceCardId").GetGuid());
        Assert.Equal(0, open[0].GetProperty("commentCount").GetInt32());
        Assert.False(open[1].TryGetProperty("boardTitle", out _));
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> ItemsAsync(RoomHttpApp app, string path, string caller) {
        var page = await RoomHttpApp.JsonAsync(await app.GetAsync(path, caller));
        return [.. page.GetProperty("items").EnumerateArray()];
    }

    // A team whose admin Ann wrote an action-item card on a team board; Mia is a member.
    private sealed record Scene(Guid Team, Guid Admin, Guid Member, Guid Board, Guid Card, Guid Item) {
        public static async Task<Scene> CreateAsync(RoomHttpApp app) {
            var admin = await app.UserAsync("Ann");
            var member = await app.UserAsync("Mia");
            var team = await app.TeamAsync((admin, "admin"), (member, "member"));
            var board = Guid.CreateVersion7();
            var column = Guid.CreateVersion7();
            var card = Guid.CreateVersion7();
            var item = Guid.CreateVersion7();
            await app.ExecAsync(
                "insert into retro_boards (id, code, team_id, title, created_by, visibility) values ($1, $2, $3, 'Sprint 7', $4, 'team')",
                board,
                $"AI{board:N}"[..12],
                team,
                admin);
            await app.ExecAsync(
                "insert into retro_columns (id, board_id, title, color, sort_key, is_action_items) values ($1, $2, 'Actions', 'yellow', 0, true)",
                column,
                board);
            await app.ExecAsync(
                "insert into retro_cards (id, board_id, column_id, body, author_id) values ($1, $2, $3, 'Fix the build', $4)",
                card,
                board,
                column,
                admin);
            await app.ExecAsync(
                """
                insert into team_action_items (id, team_id, text, source_board_id, source_card_id, source_version, created_by, created_at)
                values ($1, $2, 'Fix the build', $3, $4, 1, $5, now() - interval '1 day')
                """,
                item,
                team,
                board,
                card,
                admin);
            return new Scene(team, admin, member, board, card, item);
        }
    }
}
