using System.Net;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class BoardAccessTests(PostgresFixture postgres) {
    [Fact]
    public async Task The_right_password_grants_access_for_that_password_only() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (creator, board, code) = await PasswordBoardAsync(app, "open sesame");
        var visitor = RoomHttpApp.Guest(await app.GuestAsync());

        using var wrong = await app.PostAsync($"/api/retro/{board}/unlock", visitor, new { password = "open says me" });
        using var right = await app.PostAsync($"/api/retro/{board}/unlock", visitor, new { password = "open sesame" });
        var lookup = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/retro/by-code/{code}", visitor));
        var epoch = await app.ScalarAsync<DateTime>("select password_epoch from board_access_grants where board_id = $1", board);
        var setAt = await app.ScalarAsync<DateTime>("select password_set_at from retro_boards where id = $1", board);

        Assert.Equal("wrong_password", await RoomHttpApp.ProblemCodeAsync(wrong, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal("granted", lookup.GetProperty("access").GetString());
        Assert.Equal(setAt, epoch);
        var peer = await app.SubscribeAsync(RoomKey.Board(new BoardId(board)), visitor);
        await peer.UntilAsync<SnapFrame>();
    }

    [Fact]
    public async Task Unlock_is_limited_per_principal_per_board_and_per_ip() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (_, board, _) = await PasswordBoardAsync(app, "open sesame");
        var (_, otherBoard, _) = await PasswordBoardAsync(app, "open sesame");
        var visitor = RoomHttpApp.Guest(await app.GuestAsync());

        for (var i = 0; i < 5; i++) {
            using var attempt = await app.PostAsync($"/api/retro/{board}/unlock", visitor, new { password = "guess" }, peer: $"192.0.2.{i + 1}");
            Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        }
        using var sixth = await app.PostAsync($"/api/retro/{board}/unlock", visitor, new { password = "open sesame" }, peer: "192.0.2.60");
        using var elsewhere = await app.PostAsync($"/api/retro/{otherBoard}/unlock", visitor, new { password = "guess" }, peer: "192.0.2.61");

        Assert.Equal("rate_limited", await RoomHttpApp.ProblemCodeAsync(sixth, HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);

        // 20 an hour from one address, however many guests it mints (S7).
        for (var i = 0; i < 20; i++) {
            var guest = RoomHttpApp.Guest(await app.GuestAsync());
            using var attempt = await app.PostAsync($"/api/retro/{board}/unlock", guest, new { password = "guess" }, peer: "198.51.100.200");
            Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        }
        using var overIp = await app.PostAsync(
            $"/api/retro/{board}/unlock", RoomHttpApp.Guest(await app.GuestAsync()), new { password = "open sesame" }, peer: "198.51.100.200");
        Assert.Equal("rate_limited", await RoomHttpApp.ProblemCodeAsync(overIp, HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Unlock_reveals_nothing_about_boards_without_a_password() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var owner = await app.UserAsync();
        var team = await app.TeamAsync((owner, "owner"));
        var teamBoard = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(owner), new { title = "Team only" }), HttpStatusCode.Created);
        var visitor = RoomHttpApp.User(await app.UserAsync());

        using var teamOnly = await app.PostAsync($"/api/retro/{teamBoard.GetProperty("id").GetGuid()}/unlock", visitor, new { password = "x" });
        using var missing = await app.PostAsync($"/api/retro/{Guid.NewGuid()}/unlock", visitor, new { password = "x" });

        Assert.Equal(HttpStatusCode.NotFound, teamOnly.StatusCode);
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(RoomHttpApp.Token),
            await teamOnly.Content.ReadAsStringAsync(RoomHttpApp.Token));
    }

    [Fact]
    public async Task A_password_change_drops_grants_and_kicks_their_holders() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (creator, board, code) = await PasswordBoardAsync(app, "open sesame");
        var room = RoomKey.Board(new BoardId(board));
        var visitor = RoomHttpApp.Guest(await app.GuestAsync());
        using (var unlocked = await app.PostAsync($"/api/retro/{board}/unlock", visitor, new { password = "open sesame" })) {
            Assert.Equal(HttpStatusCode.NoContent, unlocked.StatusCode);
        }
        var holder = await app.SubscribeAsync(room, visitor);
        await holder.UntilAsync<SnapFrame>();
        var owner = await app.SubscribeAsync(room, creator);
        await owner.UntilAsync<SnapFrame>();

        using var changed = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { password = "new secret" });
        var kick = await holder.UntilAsync<KickFrame>();
        var meta = await MetaAsync(owner);
        var lookup = await RoomHttpApp.JsonAsync(await app.GetAsync($"/api/retro/by-code/{code}", visitor));
        var grants = await app.ScalarAsync<long>("select count(*) from board_access_grants where board_id = $1", board);
        var state = await app.Registry.QueryAsync<RetroState, RetroInput, DateTimeOffset?>(
            room, view => ValueTask.FromResult(view.State!.PasswordSetAt), RoomHttpApp.Token);
        var setAt = await app.ScalarAsync<DateTime>("select password_set_at from retro_boards where id = $1", board);

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(KickCode.AccessRevoked, kick.Code);
        Assert.Equal(BoardVisibility.Password, meta.Visibility);
        Assert.Equal("password", lookup.GetProperty("access").GetString());
        Assert.Equal(0, grants);
        Assert.Equal(new DateTimeOffset(setAt, TimeSpan.Zero), state);
    }

    [Fact]
    public async Task Visibility_changes_follow_the_schema_rules() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var creator = RoomHttpApp.User(await app.UserAsync());
        var board = (await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var teamOnQuick = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { visibility = "team" });
        using var passwordWithoutOne = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { visibility = "password" });
        using var shortPassword = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { password = "abc" });
        using var passwordOnLink = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { visibility = "link", password = "abcdef" });
        using var unknown = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { visibility = "public" });
        using var locked = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { password = "abcdef" });
        var hashed = await app.ScalarAsync<bool>("select password_hash is not null and visibility = 'password' from retro_boards where id = $1", board);
        using var opened = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { visibility = "link" });
        var cleared = await app.ScalarAsync<bool>(
            "select password_hash is null and password_set_at is null and visibility = 'link' from retro_boards where id = $1", board);

        Assert.Equal(HttpStatusCode.BadRequest, teamOnQuick.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, passwordWithoutOne.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, passwordOnLink.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);
        Assert.True(hashed);
        Assert.Equal(HttpStatusCode.NoContent, opened.StatusCode);
        Assert.True(cleared);
    }

    [Fact]
    public async Task Only_board_managers_change_access_archive_or_delete() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var owner = await app.UserAsync();
        var member = await app.UserAsync();
        var team = await app.TeamAsync((owner, "owner"), (member, "member"));
        var board = (await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title = "Sprint" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var quickCreator = RoomHttpApp.User(await app.UserAsync());
        var quick = (await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", quickCreator), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var stranger = RoomHttpApp.User(await app.UserAsync());

        using var memberPatch = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", RoomHttpApp.User(member), new { visibility = "link" });
        using var memberArchive = await app.PostAsync($"/api/retro/{board}/archive", RoomHttpApp.User(member));
        using var memberDelete = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", RoomHttpApp.User(member), new { confirmTitle = "Sprint" });
        using var strangerPatch = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{quick}", stranger, new { visibility = "link" });
        using var strangerHidden = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", stranger, new { visibility = "link" });
        using var ownerPatch = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", RoomHttpApp.User(owner), new { visibility = "link" });

        Assert.Equal(HttpStatusCode.Forbidden, memberPatch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberArchive.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, strangerPatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerHidden.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownerPatch.StatusCode);
    }

    [Fact]
    public async Task Archive_and_unarchive_go_through_the_board() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var creator = RoomHttpApp.User(await app.UserAsync());
        var board = (await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var room = RoomKey.Board(new BoardId(board));
        var watcher = await app.SubscribeAsync(room, creator);
        await watcher.UntilAsync<SnapFrame>();

        using var archived = await app.PostAsync($"/api/retro/{board}/archive", creator);
        var meta = await MetaAsync(watcher);
        var stored = await app.ScalarAsync<bool>("select archived_at is not null from retro_boards where id = $1", board);
        using var again = await app.PostAsync($"/api/retro/{board}/archive", creator);
        using var unarchived = await app.PostAsync($"/api/retro/{board}/unarchive", creator);
        var restored = await app.ScalarAsync<bool>("select archived_at is null from retro_boards where id = $1", board);

        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        Assert.True(meta.Archived);
        Assert.True(stored);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unarchived.StatusCode);
        Assert.True(restored);
    }

    [Fact]
    public async Task Deleting_a_board_needs_its_title_and_hides_it() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var creator = RoomHttpApp.User(await app.UserAsync());
        var created = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator, new { title = "Doomed" }), HttpStatusCode.Created);
        var board = created.GetProperty("id").GetGuid();
        var code = created.GetProperty("code").GetString();
        await app.Registry.QueryAsync<RetroState, RetroInput, int>(RoomKey.Board(new BoardId(board)), _ => ValueTask.FromResult(0), RoomHttpApp.Token);

        using var wrongTitle = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", creator, new { confirmTitle = "doomed" });
        using var deleted = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", creator, new { confirmTitle = "Doomed" });
        using var lookup = await app.GetAsync($"/api/retro/by-code/{code}", creator);
        using var twice = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", creator, new { confirmTitle = "Doomed" });
        var found = await app.Registry.QueryAsync<RetroState, RetroInput, bool>(
            RoomKey.Board(new BoardId(board)), view => ValueTask.FromResult(view.Found), RoomHttpApp.Token);

        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(wrongTitle, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        Assert.False(found);
    }

    private static async Task<RetroBoardMeta> MetaAsync(Actors.TestPeer peer) {
        while (true) {
            if ((await peer.UntilAsync<TxFrame>()).Ev.OfType<RetroBoardMeta>().FirstOrDefault() is { } meta) {
                return meta;
            }
        }
    }

    private static async Task<(string Creator, Guid Board, string Code)> PasswordBoardAsync(RoomHttpApp app, string password) {
        var creator = RoomHttpApp.User(await app.UserAsync());
        var created = await RoomHttpApp.JsonAsync(await app.PostAsync("/api/retro", creator), HttpStatusCode.Created);
        var board = created.GetProperty("id").GetGuid();
        using var locked = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", creator, new { password });
        Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);
        return (creator, board, created.GetProperty("code").GetString()!);
    }
}
