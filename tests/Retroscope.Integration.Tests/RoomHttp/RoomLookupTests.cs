using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

// S9: by-code, the shell and /api/boot all resolve codes through RoomResolver, answer missing and forbidden rooms the
// same way, and never name the team.
[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed partial class RoomLookupTests(PostgresFixture postgres) {
    [Fact]
    public async Task By_code_answers_id_and_access_only_and_ignores_case() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var guest = RoomHttpApp.Guest(await app.GuestAsync());
        var created = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/poker", guest), HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;

        var poker = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/poker/by-code/{code.ToLowerInvariant()}", guest));

        Assert.Equal(["id", "access"], poker.EnumerateObject().Select(x => x.Name));
        Assert.Equal(created.GetProperty("id").GetGuid(), poker.GetProperty("id").GetGuid());
        Assert.Equal("granted", poker.GetProperty("access").GetString());
    }

    [Fact]
    public async Task Missing_and_forbidden_rooms_answer_identically() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var owner = await app.UserAsync();
        var outsider = RoomHttpApp.User(await app.UserAsync());
        var team = await app.TeamAsync((owner, "owner"));
        var session = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(owner)), HttpStatusCode.Created);
        var board = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(owner), new { title = "Sprint" }), HttpStatusCode.Created);

        var responses = new List<(HttpStatusCode, string)>();
        foreach (var path in new[] {
            $"/api/poker/by-code/{session.GetProperty("code").GetString()}",
            "/api/poker/by-code/NOSUCHCODE",
            $"/api/retro/by-code/{board.GetProperty("code").GetString()}",
            "/api/retro/by-code/NOSUCHCODE",
            "/api/retro/by-code/%20bad%20",
        }) {
            using var response = await app.GetAsync(path, outsider);
            responses.Add((response.StatusCode, await response.Content.ReadAsStringAsync(RoomHttpApp.Token)));
        }

        Assert.All(responses, x => Assert.Equal(responses[0], x));
        Assert.Equal(HttpStatusCode.NotFound, responses[0].Item1);
        Assert.Contains("room_not_found", responses[0].Item2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_code_reports_a_password_board_without_revealing_more() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var creator = RoomHttpApp.User(await app.UserAsync());
        var visitor = RoomHttpApp.Guest(await app.GuestAsync());
        var board = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator), HttpStatusCode.Created);
        var id = board.GetProperty("id").GetGuid();
        using var locked = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{id}", creator, new { password = "open sesame" });
        Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);

        var seen = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/retro/by-code/{board.GetProperty("code").GetString()}", visitor));

        Assert.Equal(["id", "access"], seen.EnumerateObject().Select(x => x.Name));
        Assert.Equal("password", seen.GetProperty("access").GetString());
    }

    [Fact]
    public async Task Code_lookups_share_the_resolver_limit_per_ip() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var guest = RoomHttpApp.Guest(await app.GuestAsync());

        for (var i = 0; i < 30; i++) {
            using var response = await app.GetAsync(i % 2 == 0 ? "/api/poker/by-code/MISSING01" : "/api/retro/by-code/MISSING01", guest, "192.0.2.50");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var limited = await app.GetAsync("/api/retro/by-code/MISSING01", guest, "192.0.2.50");
        using var boot = await app.GetAsync("/api/boot?path=/retro/MISSING01", guest, "192.0.2.50");
        using var other = await app.GetAsync("/api/retro/by-code/MISSING01", guest, "192.0.2.51");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
        Assert.False(BootOf(await boot.Content.ReadAsStringAsync(RoomHttpApp.Token)).TryGetProperty("room", out _));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task The_shell_embeds_the_viewers_snapshot_for_a_poker_session() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var guest = RoomHttpApp.Guest(await app.GuestAsync());
        var created = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/poker", guest), HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;

        using var page = await app.GetAsync($"/poker/{code}", guest);
        var room = BootOf(await page.Content.ReadAsStringAsync(RoomHttpApp.Token)).GetProperty("room");

        Assert.Equal($"poker:{created.GetProperty("id").GetGuid()}", room.GetProperty("r").GetString());
        Assert.Equal("granted", room.GetProperty("access").GetString());
        Assert.Equal("snap", room.GetProperty("t").GetString());
        Assert.Equal(code, room.GetProperty("d").GetProperty("code").GetString());
        Assert.True(room.TryGetProperty("e", out _));
        Assert.True(room.TryGetProperty("s", out _));
        Assert.Single(room.GetProperty("d").GetProperty("rounds").EnumerateArray());
    }

    [Fact]
    public async Task Boot_embeds_board_snapshots_and_password_gates() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var creator = RoomHttpApp.User(await app.UserAsync());
        var visitor = RoomHttpApp.Guest(await app.GuestAsync());
        var board = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator, new { title = "Friday" }), HttpStatusCode.Created);
        var code = board.GetProperty("code").GetString()!;
        var key = $"board:{board.GetProperty("id").GetGuid()}";

        var open = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/retro/{code}", visitor));
        using (await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board.GetProperty("id").GetGuid()}", creator, new { password = "open sesame" })) {
        }
        var gated = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/retro/{code}", visitor));
        var mine = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/retro/{code}", creator));

        Assert.Equal(key, open.GetProperty("room").GetProperty("r").GetString());
        Assert.Equal("Friday", open.GetProperty("room").GetProperty("d").GetProperty("meta").GetProperty("title").GetString());
        Assert.Equal(["r", "access"], gated.GetProperty("room").EnumerateObject().Select(x => x.Name));
        Assert.Equal("password", gated.GetProperty("room").GetProperty("access").GetString());
        Assert.Equal("granted", mine.GetProperty("room").GetProperty("access").GetString());
        Assert.True(mine.GetProperty("room").TryGetProperty("d", out _));
    }

    [Fact]
    public async Task Boot_adds_no_room_for_hidden_rooms_or_another_teams_path() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var owner = await app.UserAsync();
        var team = await app.TeamAsync((owner, "owner"));
        var otherTeam = await app.TeamAsync((owner, "owner"));
        var session = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/poker-sessions", RoomHttpApp.User(owner)), HttpStatusCode.Created);
        var code = session.GetProperty("code").GetString();
        var outsider = RoomHttpApp.User(await app.UserAsync());

        var right = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/teams/{team}/poker/{code}", RoomHttpApp.User(owner)));
        var wrongTeam = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/teams/{otherTeam}/poker/{code}", RoomHttpApp.User(owner)));
        var forbidden = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/boot?path=/poker/{code}", outsider));
        var missing = await RoomHttpApp.JsonAsync(await app.GetAsync("/api/boot?path=/poker/NOSUCHCODE", outsider));

        Assert.True(right.TryGetProperty("room", out var room));
        Assert.DoesNotContain(team.ToString(), room.GetProperty("r").GetString(), StringComparison.Ordinal);
        Assert.False(wrongTeam.TryGetProperty("room", out _));
        Assert.False(forbidden.TryGetProperty("room", out _));
        Assert.False(missing.TryGetProperty("room", out _));
    }

    private static JsonElement BootOf(string body) {
        if (body.TrimStart().StartsWith('{')) {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.Clone();
        }
        var match = BootElement().Match(body);
        Assert.True(match.Success, body);
        using var document = JsonDocument.Parse(match.Groups[1].Value);
        return document.RootElement.Clone();
    }

    [GeneratedRegex("""<script type="application/json" id="boot">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex BootElement();
}
