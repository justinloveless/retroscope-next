using System.Net;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Retro;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class RoomCreateTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_team_session_starts_with_one_placeholder_round_that_the_room_loads() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));

        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)), HttpStatusCode.Created);
        var session = new SessionId(created.GetProperty("id").GetGuid());
        var code = created.GetProperty("code").GetString()!;
        var state = await app.Registry.QueryAsync<PokerState, PokerInput, PokerState>(
            RoomKey.Poker(session), view => ValueTask.FromResult(view.State!), RoomHttpApp.Token);

        Assert.Equal(["id", "code"], created.EnumerateObject().Select(x => x.Name));
        Assert.Equal(RoomCode.GeneratedLength, code.Length);
        Assert.All(code, x => Assert.Contains(x, RoomCode.CrockfordAlphabet));
        Assert.Equal(new TeamId(team), state.TeamId);
        var round = Assert.Single(state.Rounds.Values);
        Assert.Null(round.TicketKey);
        Assert.Equal(round.Id, state.CurrentRoundId);
        Assert.Equal(2, state.NextRoundNumber);
    }

    [Fact]
    public async Task A_team_board_without_templates_gets_the_default_columns() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));

        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "  Friday  " }), HttpStatusCode.Created);
        var state = await BoardAsync(app, created);

        Assert.Equal("Friday", state.Title);
        Assert.Equal(new TeamId(team), state.TeamId);
        Assert.Null(state.Config.MaxVotes);
        Assert.Equal(["Good", "Bad", "Kudos", "Action Items"], state.Columns.Select(x => x.Title));
        Assert.Equal("Action Items", Assert.Single(state.Columns, x => x.ActionItems).Title);
    }

    [Fact]
    public async Task Create_rejects_unknown_members_and_bad_titles() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        var user = RoomHttpApp.User(member);

        using var unknown = await app.PostAsync($"/api/teams/{team}/boards", user, new { title = "x", teamId = Guid.NewGuid() });
        using var untitled = await app.PostAsync($"/api/teams/{team}/boards", user, new { title = "  " });
        using var tooLong = await app.PostAsync($"/api/teams/{team}/boards", user, new { title = new string('a', 201) });
        using var poker = await app.PostAsync($"/api/teams/{team}/poker-sessions", user, new { isAdmin = true });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(untitled, HttpStatusCode.BadRequest));
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(tooLong, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.BadRequest, poker.StatusCode);
    }

    /// <summary>Boards are team boards: no visibility, no password (UX18).</summary>
    [Fact]
    public async Task A_board_takes_no_visibility_or_password() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        var user = RoomHttpApp.User(member);

        using var visibility = await app.PostAsync($"/api/teams/{team}/boards", user, new { title = "x", visibility = "team" });
        using var password = await app.PostAsync($"/api/teams/{team}/boards", user, new { title = "x", password = "hunter22" });

        Assert.Equal(HttpStatusCode.BadRequest, visibility.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
    }

    [Fact]
    public async Task Quick_rooms_can_no_longer_be_created() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var user = RoomHttpApp.User(await app.UserAsync());

        using var poker = await app.PostAsync("/api/poker", user);
        using var retro = await app.PostAsync("/api/retro", user, new { title = "x" });

        Assert.Equal(HttpStatusCode.NotFound, poker.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, retro.StatusCode);
    }

    [Fact]
    public async Task A_team_board_takes_the_teams_default_template_or_the_chosen_one() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        var standard = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await app.ExecAsync(
            """
            insert into board_templates (id, team_id, name, is_default, max_votes_per_user, stages_enabled, vote_emoji)
            values ($1, $3, 'Standard', true, 5, true, '🔥'), ($2, $3, 'Other', false, null, false, '👍')
            """,
            standard,
            other,
            team);
        await app.ExecAsync(
            """
            insert into template_columns (id, template_id, title, color, sort_key, is_action_items) values
              (gen_random_uuid(), $1, 'Start', 'green', 0, false), (gen_random_uuid(), $1, 'Stop', 'red', 1, false),
              (gen_random_uuid(), $1, 'Do', 'blue', 2, true), (gen_random_uuid(), $2, 'Only', 'gray', 0, false)
            """,
            standard,
            other);

        var byDefault = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint 1" }), HttpStatusCode.Created);
        var chosen = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint 2", templateId = other }),
            HttpStatusCode.Created);
        using var foreign = await app.PostAsync(
            $"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint 3", templateId = Guid.NewGuid() });
        var first = await BoardAsync(app, byDefault);
        var second = await BoardAsync(app, chosen);

        Assert.Equal(new TeamId(team), first.TeamId);
        Assert.Equal(["Start", "Stop", "Do"], first.Columns.Select(x => x.Title));
        Assert.True(first.Columns[2].ActionItems);
        Assert.Equal(5, first.Config.MaxVotes);
        Assert.True(first.Config.StagesEnabled);
        Assert.Equal("🔥", first.Config.VoteEmoji);
        Assert.Equal(["Only"], second.Columns.Select(x => x.Title));
        Assert.Null(second.Config.MaxVotes);
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(foreign, HttpStatusCode.BadRequest));
        Assert.Contains($"boards {team} {byDefault.GetProperty("id").GetGuid()}", app.Notices.Log);
    }

    [Fact]
    public async Task Team_routes_are_for_members_of_teams_that_still_exist() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var outsider = RoomHttpApp.User(await app.UserAsync());
        var team = await app.TeamAsync((member, "member"));
        var deleted = await app.TeamAsync((member, "owner"));
        await app.ExecAsync("update teams set deleted_at = now() where id = $1", deleted);

        foreach (var (method, path, caller) in new[] {
            (HttpMethod.Get, $"/api/teams/{team}/boards", outsider),
            (HttpMethod.Post, $"/api/teams/{team}/boards", outsider),
            (HttpMethod.Get, $"/api/teams/{team}/poker-sessions", outsider),
            (HttpMethod.Post, $"/api/teams/{team}/poker-sessions", outsider),
            (HttpMethod.Get, $"/api/teams/{team}/action-items", outsider),
            (HttpMethod.Get, $"/api/teams/{deleted}/boards", RoomHttpApp.User(member)),
            (HttpMethod.Post, $"/api/teams/{deleted}/poker-sessions", RoomHttpApp.User(member)),
        }) {
            object? body = method == HttpMethod.Post ? path.EndsWith("/boards", StringComparison.Ordinal) ? new { title = "x" } : new { } : null;
            using var response = await app.SendAsync(method, path, caller, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var guest = await app.GetAsync($"/api/teams/{team}/boards", RoomHttpApp.Guest(await app.GuestAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);
    }

    [Fact]
    public async Task A_team_session_copies_settings_from_the_teams_latest_session() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));
        var first = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)), HttpStatusCode.Created);
        await app.ExecAsync(
            "update poker_sessions set presence_enabled = false, follow_spotlight = false where id = $1", first.GetProperty("id").GetGuid());

        var second = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(member)), HttpStatusCode.Created);
        var state = await app.Registry.QueryAsync<PokerState, PokerInput, PokerState>(
            RoomKey.Poker(new SessionId(second.GetProperty("id").GetGuid())), view => ValueTask.FromResult(view.State!), RoomHttpApp.Token);

        Assert.Equal(new TeamId(team), state.TeamId);
        Assert.False(state.PresenceEnabled);
        Assert.False(state.FollowSpotlight);
        Assert.Contains($"poker {second.GetProperty("id").GetGuid()}", app.Notices.Log);
    }

    private static Task<RetroState> BoardAsync(RoomHttpApp app, JsonElement created) {
        return app.Registry.QueryAsync<RetroState, RetroInput, RetroState>(
            RoomKey.Board(new BoardId(created.GetProperty("id").GetGuid())), view => ValueTask.FromResult(view.State!), RoomHttpApp.Token);
    }
}
