using System.Net;
using System.Text.Json;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

// §d: mentions are listed only on boards the user can still read, those of the user's teams (UX18); anonymous
// sources carry no author.
[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class MentionsTests(PostgresFixture postgres) {
    [Fact]
    public async Task Only_mentions_on_readable_boards_are_listed_newest_first() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var me = await app.UserAsync("Mia");
        var ann = await app.UserAsync("Ann");
        var myTeam = await app.TeamAsync((me, "member"), (ann, "member"));
        var otherTeam = await app.TeamAsync((ann, "owner"));
        var seed = new Seed(app, me, ann);

        await seed.CardAsync("my team", minutesAgo: 1, team: myTeam);
        await seed.CommentAsync("anonymous on my team", minutesAgo: 2, team: myTeam, anonymous: true);
        await seed.CardAsync("other team", minutesAgo: 3, team: otherTeam);
        await seed.CardAsync("deleted board", minutesAgo: 5, team: myTeam, deleted: true);
        await seed.CardAsync("names hidden", minutesAgo: 6, team: myTeam, showAuthors: false);
        await seed.CardAsync("deleted team", minutesAgo: 7, team: await DeletedTeamAsync(app, me));

        var page = await RoomHttpApp.JsonAsync(await app.GetAsync("/api/me/mentions", RoomHttpApp.User(me)));
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(["my team", "anonymous on my team", "names hidden"], items.Select(x => x.GetProperty("boardTitle").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        var first = items[0];
        Assert.Equal("card", first.GetProperty("sourceKind").GetString());
        Assert.Equal("Ann", first.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(seed.Body, first.GetProperty("body").GetString());
        Assert.StartsWith("M", first.GetProperty("boardCode").GetString(), StringComparison.Ordinal);
        Assert.Equal("comment", items[1].GetProperty("sourceKind").GetString());
        Assert.False(items[1].TryGetProperty("author", out _));
        Assert.False(items[2].TryGetProperty("author", out _));
    }

    [Fact]
    public async Task Leaving_the_team_drops_its_mentions() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var me = await app.UserAsync("Mia");
        var ann = await app.UserAsync("Ann");
        var team = await app.TeamAsync((me, "member"), (ann, "owner"));
        var seed = new Seed(app, me, ann);
        await seed.CardAsync("our board", minutesAgo: 1, team: team);
        await app.ExecAsync("update users set mentions_seen_at = now() - interval '1 day' where id = $1", me);

        var before = await RoomHttpApp.JsonAsync(await app.GetAsync("/api/me/mentions", RoomHttpApp.User(me)));
        await app.ExecAsync("delete from team_members where team_id = $1 and user_id = $2", team, me);
        var after = await RoomHttpApp.JsonAsync(await app.GetAsync("/api/me/mentions", RoomHttpApp.User(me)));

        Assert.Single(before.GetProperty("items").EnumerateArray());
        Assert.Empty(after.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Mentions_page_by_cursor_and_can_be_marked_seen() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var me = await app.UserAsync("Mia");
        var ann = await app.UserAsync("Ann");
        var team = await app.TeamAsync((me, "member"), (ann, "member"));
        var seed = new Seed(app, me, ann);
        for (var i = 0; i < 5; i++) {
            await seed.CardAsync($"board {i}", minutesAgo: i, team: team);
        }
        await app.ExecAsync("update users set mentions_seen_at = now() - interval '1 day' where id = $1", me);

        var titles = new List<string>();
        string? cursor = null;
        do {
            var page = await RoomHttpApp.JsonAsync(await app.GetAsync(
                $"/api/me/mentions?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}", RoomHttpApp.User(me)));
            titles.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("boardTitle").GetString()!));
            cursor = page.GetProperty("next").GetString();
        } while (cursor is not null);
        using var seen = await app.PostAsync("/api/me/mentions/seen", RoomHttpApp.User(me));
        var unread = await app.ScalarAsync<long>(
            "select count(*) from mentions m join users u on u.id = m.user_id where m.user_id = $1 and m.created_at > u.mentions_seen_at", me);
        using var guest = await app.GetAsync("/api/me/mentions", RoomHttpApp.Guest(await app.GuestAsync()));

        Assert.Equal(["board 0", "board 1", "board 2", "board 3", "board 4"], titles);
        Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);
        Assert.Equal(0, unread);
        Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);
    }

    private static async Task<Guid> DeletedTeamAsync(RoomHttpApp app, Guid member) {
        var team = await app.TeamAsync((member, "member"));
        await app.ExecAsync("update teams set deleted_at = now() where id = $1", team);
        return team;
    }

    private sealed class Seed(RoomHttpApp app, Guid me, Guid author) {
        private int _boards;

        public string Body => $"Thanks [[mention:{me}:Mia]]";

        public Task CardAsync(string title, int minutesAgo, Guid team, bool deleted = false, bool showAuthors = true) {
            return PostAsync(title, minutesAgo, team, deleted, showAuthors, comment: false, anonymous: false);
        }

        public Task CommentAsync(string title, int minutesAgo, Guid team, bool anonymous) {
            return PostAsync(title, minutesAgo, team, false, true, comment: true, anonymous);
        }

        private async Task PostAsync(
            string title,
            int minutesAgo,
            Guid team,
            bool deleted,
            bool showAuthors,
            bool comment,
            bool anonymous) {
            var board = Guid.CreateVersion7();
            var column = Guid.CreateVersion7();
            var card = Guid.CreateVersion7();
            await app.ExecAsync(
                """
                insert into retro_boards (id, code, team_id, title, created_by, show_author_names, deleted_at)
                values ($1, $2, $3, $4, $5, $6, $7)
                """,
                board,
                $"M{_boards++:D9}",
                team,
                title,
                author,
                showAuthors,
                deleted ? DateTimeOffset.UtcNow : null);
            await app.ExecAsync(
                "insert into retro_columns (id, board_id, title, color, sort_key) values ($1, $2, 'Notes', 'gray', 0)", column, board);
            await app.ExecAsync(
                "insert into retro_cards (id, board_id, column_id, body, author_id) values ($1, $2, $3, $4, $5)",
                card,
                board,
                column,
                comment ? "A card" : Body,
                author);
            var source = card;
            if (comment) {
                source = Guid.CreateVersion7();
                await app.ExecAsync(
                    "insert into retro_comments (id, card_id, board_id, body, author_id, anon_tag) values ($1, $2, $3, $4, $5, $6)",
                    source,
                    card,
                    board,
                    Body,
                    anonymous ? null : author,
                    anonymous ? new byte[32] : null);
            }
            await app.ExecAsync(
                "insert into mentions (source_kind, source_id, board_id, user_id, created_at) values ($1, $2, $3, $4, now() - $5 * interval '1 minute')",
                comment ? "comment" : "card",
                source,
                board,
                me,
                minutesAgo);
        }
    }
}
