using System.Net;
using System.Text.Json;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class RoomListTests(PostgresFixture postgres) {
    [Fact]
    public async Task The_session_list_counts_rounds_in_sql_as_the_room_does() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        var session = Guid.CreateVersion7();
        await app.ExecAsync("insert into poker_sessions (id, code, team_id, next_round_number) values ($1, 'STATS00001', $2, 8)", session, team);
        // Strip order: ABC-1 (parent EPIC-2), ABC-2 (no parent), ABC-3 (EPIC-1), placeholder, ABC-4 (EPIC-1),
        // a pending preview (EPIC-9), ABC-6 (EPIC-2). Revealed: rounds 1, 3 and 7.
        var rounds = new (int Number, int Sort, string? Ticket, string? Parent, bool Revealed, bool Pending)[] {
            (1, 0, "ABC-1", "EPIC-2", true, false),
            (2, 1, "ABC-2", null, false, false),
            (3, 2, "ABC-3", "EPIC-1", true, false),
            (4, 3, null, null, false, false),
            (5, 4, "ABC-4", "EPIC-1", false, false),
            (6, 5, "ABC-5", "EPIC-9", false, true),
            (7, 6, "ABC-6", "EPIC-2", true, false),
        };
        foreach (var round in rounds) {
            await app.ExecAsync(
                """
                insert into poker_rounds (id, session_id, number, sort_key, ticket_key, parent_key, state, is_pending,
                                          pending_owner_id, winner_points, revealed_at)
                values (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, case when $7 then $8 end,
                        case when $6 = 'revealed' then 3 end, case when $6 = 'revealed' then now() end)
                """,
                session,
                round.Number,
                round.Sort,
                round.Ticket,
                round.Parent,
                round.Revealed ? "revealed" : "voting",
                round.Pending,
                member);
        }
        var empty = Guid.CreateVersion7();
        await app.ExecAsync(
            "insert into poker_sessions (id, code, team_id, created_at) values ($1, 'EMPTY00001', $2, now() - interval '1 day')", empty, team);

        var page = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)));
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal([session, empty], items.Select(x => x.GetProperty("id").GetGuid()));
        var stats = items[0];
        Assert.Equal("STATS00001", stats.GetProperty("code").GetString());
        Assert.Equal(3, stats.GetProperty("pointed").GetInt32());
        Assert.Equal(6, stats.GetProperty("total").GetInt32());
        Assert.Equal(
            ["EPIC-2:2", "null:1", "EPIC-1:2"],
            stats.GetProperty("parentKeys").EnumerateArray().Select(x =>
                $"{(x.GetProperty("key").ValueKind == JsonValueKind.Null ? "null" : x.GetProperty("key").GetString())}:{x.GetProperty("count").GetInt32()}"));
        Assert.Equal(0, items[1].GetProperty("total").GetInt32());
        Assert.Empty(items[1].GetProperty("parentKeys").EnumerateArray());
        Assert.True(items[1].GetProperty("createdAt").GetInt64() < stats.GetProperty("createdAt").GetInt64());
    }

    [Fact]
    public async Task Lists_page_by_cursor() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        for (var i = 0; i < 5; i++) {
            await app.ExecAsync(
                "insert into retro_boards (id, code, team_id, title, created_at) values ($1, $2, $3, $4, now() - $5 * interval '1 minute')",
                Guid.CreateVersion7(),
                $"PAGEBOARD{i}",
                team,
                $"Board {i}",
                i);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do {
            var path = $"/api/teams/{team}/boards?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await RoomHttpApp.JsonAsync(await app.GetAsync(path, RoomHttpApp.User(member)));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);
        using var garbage = await app.GetAsync($"/api/teams/{team}/boards?cursor=not-a-cursor!", RoomHttpApp.User(member));

        Assert.Equal(["Board 0", "Board 1", "Board 2", "Board 3", "Board 4"], seen);
        Assert.Equal(3, pages);
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(garbage, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task The_board_list_splits_archived_boards_and_hides_deleted_ones() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var admin = await app.UserAsync();
        var team = await app.TeamAsync((admin, "admin"));
        var open = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(admin), new { title = "Open" }), HttpStatusCode.Created);
        var archived = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(admin), new { title = "Archived" }), HttpStatusCode.Created);
        var gone = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(admin), new { title = "Gone" }), HttpStatusCode.Created);
        using (var archive = await app.PostAsync($"/api/retro/{archived.GetProperty("id").GetGuid()}/archive", RoomHttpApp.User(admin))) {
            Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        }
        using (var delete = await app.SendAsync(
            HttpMethod.Delete, $"/api/retro/{gone.GetProperty("id").GetGuid()}", RoomHttpApp.User(admin), new { confirmTitle = "Gone" })) {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var active = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{team}/boards", RoomHttpApp.User(admin)));
        var old = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{team}/boards?archived=true", RoomHttpApp.User(admin)));

        var item = Assert.Single(active.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["id", "code", "title", "archived", "createdAt", "stage", "stagesEnabled", "cardCount", "lastActivityAt"],
            item.EnumerateObject().Select(x => x.Name));
        Assert.Equal(open.GetProperty("code").GetString(), item.GetProperty("code").GetString());
        var oldItem = Assert.Single(old.GetProperty("items").EnumerateArray());
        Assert.Equal("Archived", oldItem.GetProperty("title").GetString());
        Assert.True(oldItem.GetProperty("archived").GetBoolean());
        Assert.True(oldItem.GetProperty("archivedAt").GetInt64() > 0);
    }

    // UX18 decision 11: the team page shows each board's stage (when stages are on), its cards and its last activity,
    // and each session's last activity; nothing in a row claims a room is live.
    [Fact]
    public async Task List_rows_carry_the_stage_the_cards_and_the_last_activity() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var caller = RoomHttpApp.User(member);
        var team = await app.TeamAsync((member, "member"));
        var staged = (await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", caller, new { title = "Staged" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var plain = (await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", caller, new { title = "Plain" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var session = (await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", caller), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var boardActivity = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var sessionActivity = DateTimeOffset.Parse("2026-10-02T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await app.ExecAsync(
            "update retro_boards set stage = 'voting', stages_enabled = true, last_activity_at = $2 where id = $1", staged, boardActivity);
        for (var i = 0; i < 3; i++) {
            await app.ExecAsync(
                """
                insert into retro_cards (id, board_id, column_id, body)
                select gen_random_uuid(), $1, id, 'A card' from retro_columns where board_id = $1 order by sort_key limit 1
                """,
                staged);
        }
        await app.ExecAsync("update poker_sessions set last_activity_at = $2 where id = $1", session, sessionActivity);

        var boards = (await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{team}/boards", caller)))
            .GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        var sessions = (await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/teams/{team}/poker-sessions", caller)))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal("voting", boards[staged].GetProperty("stage").GetString());
        Assert.True(boards[staged].GetProperty("stagesEnabled").GetBoolean());
        Assert.Equal(3, boards[staged].GetProperty("cardCount").GetInt32());
        Assert.Equal(boardActivity.ToUnixTimeMilliseconds(), boards[staged].GetProperty("lastActivityAt").GetInt64());
        Assert.Equal("thinking", boards[plain].GetProperty("stage").GetString());
        Assert.False(boards[plain].GetProperty("stagesEnabled").GetBoolean());
        Assert.Equal(0, boards[plain].GetProperty("cardCount").GetInt32());
        Assert.True(boards[plain].GetProperty("lastActivityAt").GetInt64() >= boards[plain].GetProperty("createdAt").GetInt64() - 1000);
        Assert.False(boards[plain].TryGetProperty("visibility", out _));
        var row = Assert.Single(sessions);
        Assert.Equal(session, row.GetProperty("id").GetGuid());
        Assert.Equal(sessionActivity.ToUnixTimeMilliseconds(), row.GetProperty("lastActivityAt").GetInt64());
        Assert.Equal(
            ["id", "code", "createdAt", "pointed", "total", "parentKeys", "lastActivityAt"],
            row.EnumerateObject().Select(x => x.Name));
    }
}
