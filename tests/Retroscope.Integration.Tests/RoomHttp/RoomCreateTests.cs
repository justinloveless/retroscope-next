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
    public async Task A_quick_session_starts_with_one_placeholder_round_that_the_room_loads() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var guest = await app.GuestAsync();

        var created = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/poker", RoomHttpApp.Guest(guest)), HttpStatusCode.Created);
        var session = new SessionId(created.GetProperty("id").GetGuid());
        var code = created.GetProperty("code").GetString()!;
        var state = await app.Registry.QueryAsync<PokerState, PokerInput, PokerState>(
            RoomKey.Poker(session), view => ValueTask.FromResult(view.State!), RoomHttpApp.Token);

        Assert.Equal(["id", "code"], created.EnumerateObject().Select(x => x.Name));
        Assert.Equal(RoomCode.GeneratedLength, code.Length);
        Assert.All(code, x => Assert.Contains(x, RoomCode.CrockfordAlphabet));
        Assert.Null(state.TeamId);
        Assert.Equal(new PrincipalId(guest), state.CreatedBy);
        var round = Assert.Single(state.Rounds.Values);
        Assert.Null(round.TicketKey);
        Assert.Equal(round.Id, state.CurrentRoundId);
        Assert.Equal(2, state.NextRoundNumber);
    }

    [Fact]
    public async Task A_quick_board_gets_the_default_columns_and_three_votes() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var user = await app.UserAsync();

        var untitled = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", RoomHttpApp.User(user)), HttpStatusCode.Created);
        var titled = await RoomHttpApp.JsonAsync(
            await app.PostAsync("/api/retro", RoomHttpApp.User(user), new { title = "  Friday  " }), HttpStatusCode.Created);
        var state = await BoardAsync(app, untitled);

        Assert.Equal("Quick Retro Board", state.Title);
        Assert.Equal("Friday", (await BoardAsync(app, titled)).Title);
        Assert.Null(state.TeamId);
        Assert.Equal(new PrincipalId(user), state.CreatedBy);
        Assert.Equal(Core.Protocol.BoardVisibility.Link, state.Visibility);
        Assert.Equal(3, state.Config.MaxVotes);
        Assert.Equal(["Good", "Bad", "Kudos", "Action Items"], state.Columns.Select(x => x.Title));
        Assert.Equal("Action Items", Assert.Single(state.Columns, x => x.ActionItems).Title);
    }

    [Fact]
    public async Task Create_rejects_unknown_members_and_bad_titles() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var user = RoomHttpApp.User(await app.UserAsync());

        using var unknown = await app.PostAsync("/api/retro", user, new { title = "x", teamId = Guid.NewGuid() });
        using var tooLong = await app.PostAsync("/api/retro", user, new { title = new string('a', 201) });
        using var poker = await app.PostAsync("/api/poker", user, new { isAdmin = true });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(tooLong, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.BadRequest, poker.StatusCode);
    }

    [Theory]
    [InlineData("/api/poker")]
    [InlineData("/api/retro")]
    public async Task Quick_creates_are_limited_per_principal_and_per_ip(string path) {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var guest = RoomHttpApp.Guest(await app.GuestAsync());

        for (var i = 0; i < 10; i++) {
            using var ok = await app.PostAsync(path, guest, peer: $"192.0.2.{i + 1}");
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }
        using var principalLimited = await app.PostAsync(path, guest, peer: "192.0.2.99");

        // Fresh guests do not lift the IP's limit (S7).
        for (var i = 0; i < 30; i++) {
            using var ok = await app.PostAsync(path, RoomHttpApp.Guest(await app.GuestAsync()), peer: "198.51.100.77");
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }
        using var ipLimited = await app.PostAsync(path, RoomHttpApp.Guest(await app.GuestAsync()), peer: "198.51.100.77");

        Assert.Equal("rate_limited", await RoomHttpApp.ProblemCodeAsync(principalLimited, HttpStatusCode.TooManyRequests));
        Assert.Equal("rate_limited", await RoomHttpApp.ProblemCodeAsync(ipLimited, HttpStatusCode.TooManyRequests));
        Assert.True(principalLimited.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Quick_creates_need_a_principal() {
        await using var app = await RoomHttpApp.StartAsync(postgres);

        using var poker = await app.PostAsync("/api/poker", null);
        using var retro = await app.PostAsync("/api/retro", null);

        Assert.Equal(HttpStatusCode.Unauthorized, poker.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, retro.StatusCode);
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
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint 2", visibility = "link", templateId = other }),
            HttpStatusCode.Created);
        using var foreign = await app.PostAsync(
            $"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint 3", templateId = Guid.NewGuid() });
        var first = await BoardAsync(app, byDefault);
        var second = await BoardAsync(app, chosen);

        Assert.Equal(new TeamId(team), first.TeamId);
        Assert.Equal(Core.Protocol.BoardVisibility.Team, first.Visibility);
        Assert.Equal(["Start", "Stop", "Do"], first.Columns.Select(x => x.Title));
        Assert.True(first.Columns[2].ActionItems);
        Assert.Equal(5, first.Config.MaxVotes);
        Assert.True(first.Config.StagesEnabled);
        Assert.Equal("🔥", first.Config.VoteEmoji);
        Assert.Equal(Core.Protocol.BoardVisibility.Link, second.Visibility);
        Assert.Equal(["Only"], second.Columns.Select(x => x.Title));
        Assert.Null(second.Config.MaxVotes);
        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(foreign, HttpStatusCode.BadRequest));
        Assert.Contains($"boards {team} {byDefault.GetProperty("id").GetGuid()}", app.Notices.Log);
    }

    [Fact]
    public async Task A_team_password_board_is_hashed_and_open_to_members() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var member = await app.UserAsync();
        var team = await app.TeamAsync((member, "member"));

        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Secret", visibility = "password", password = "hunter22" }),
            HttpStatusCode.Created);
        using var missing = await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "x", visibility = "password" });
        using var stray = await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "x", password = "hunter22" });
        var hash = await app.ScalarAsync<string>("select password_hash from retro_boards where id = $1", created.GetProperty("id").GetGuid());

        Assert.StartsWith("$2a$10$", hash, StringComparison.Ordinal);
        Assert.True(Core.Security.Bcrypt.Verify("hunter22", hash));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stray.StatusCode);
        var lookup = await RoomHttpApp.JsonAsync(
            await app.GetAsync($"/api/retro/by-code/{created.GetProperty("code").GetString()}", RoomHttpApp.User(member)));
        Assert.Equal("granted", lookup.GetProperty("access").GetString());
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
