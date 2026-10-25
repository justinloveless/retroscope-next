using System.Net;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.RoomHttp;

/// <summary>Boards belong to a team and open to its members only; team admins archive and delete them (UX18).</summary>
[Trait("Category", "RoomHttp")]
[Collection(SharedPostgres.Name)]
public sealed class BoardAccessTests(PostgresFixture postgres) {
    [Fact]
    public async Task Board_passwords_and_visibility_have_no_routes() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (owner, _, board, _) = await TeamBoardAsync(app, "Sprint");

        using var unlock = await app.PostAsync($"/api/retro/{board}/unlock", owner, new { password = "open sesame" });
        using var access = await app.SendAsync(HttpMethod.Patch, $"/api/retro/{board}", owner, new { visibility = "link" });

        Assert.Equal(HttpStatusCode.NotFound, unlock.StatusCode);
        Assert.Contains(access.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task Only_team_admins_archive_or_delete_and_non_members_see_nothing() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (owner, member, board, _) = await TeamBoardAsync(app, "Sprint");
        var stranger = RoomHttpApp.User(await app.UserAsync());

        using var memberArchive = await app.PostAsync($"/api/retro/{board}/archive", member);
        using var memberDelete = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", member, new { confirmTitle = "Sprint" });
        using var strangerArchive = await app.PostAsync($"/api/retro/{board}/archive", stranger);
        using var strangerDelete = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", stranger, new { confirmTitle = "Sprint" });
        using var ownerArchive = await app.PostAsync($"/api/retro/{board}/archive", owner);

        Assert.Equal(HttpStatusCode.Forbidden, memberArchive.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerArchive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownerArchive.StatusCode);
    }

    [Fact]
    public async Task Archive_and_unarchive_go_through_the_board() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (owner, member, board, _) = await TeamBoardAsync(app, "Sprint");
        var room = RoomKey.Board(new BoardId(board));
        var watcher = await app.SubscribeAsync(room, member);
        await watcher.UntilAsync<SnapFrame>();

        using var archived = await app.PostAsync($"/api/retro/{board}/archive", owner);
        var meta = await MetaAsync(watcher);
        var stored = await app.ScalarAsync<bool>("select archived_at is not null from retro_boards where id = $1", board);
        using var again = await app.PostAsync($"/api/retro/{board}/archive", owner);
        using var unarchived = await app.PostAsync($"/api/retro/{board}/unarchive", owner);
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
        var (owner, _, board, code) = await TeamBoardAsync(app, "Doomed");
        await app.Registry.QueryAsync<RetroState, RetroInput, int>(RoomKey.Board(new BoardId(board)), _ => ValueTask.FromResult(0), RoomHttpApp.Token);

        using var wrongTitle = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", owner, new { confirmTitle = "doomed" });
        using var deleted = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", owner, new { confirmTitle = "Doomed" });
        using var lookup = await app.GetAsync($"/api/retro/by-code/{code}", owner);
        using var twice = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{board}", owner, new { confirmTitle = "Doomed" });
        var found = await app.Registry.QueryAsync<RetroState, RetroInput, bool>(
            RoomKey.Board(new BoardId(board)), view => ValueTask.FromResult(view.Found), RoomHttpApp.Token);

        Assert.Equal("invalid", await RoomHttpApp.ProblemCodeAsync(wrongTitle, HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        Assert.False(found);
    }

    [Fact]
    public async Task Deleting_a_board_kicks_its_subscribers_and_a_later_subscribe_finds_nothing() {
        await using var app = await RoomHttpApp.StartAsync(postgres);
        var (owner, member, board, _) = await TeamBoardAsync(app, "Doomed");
        var room = RoomKey.Board(new BoardId(board));
        var subscriber = await app.SubscribeAsync(room, member);
        await subscriber.UntilAsync<SnapFrame>();

        using var deleted = await app.SendAsync(HttpMethod.Delete, $"/api/retro/{room.Id}", owner, new { confirmTitle = "Doomed" });
        var kick = await subscriber.UntilAsync<KickFrame>();
        var late = await app.SubscribeAsync(room, member);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(KickCode.Deleted, kick.Code);
        Assert.Equal(KickCode.NotFound, (await late.UntilAsync<KickFrame>()).Code);
    }

    private static async Task<RetroBoardMeta> MetaAsync(Actors.TestPeer peer) {
        while (true) {
            if ((await peer.UntilAsync<TxFrame>()).Ev.OfType<RetroBoardMeta>().FirstOrDefault() is { } meta) {
                return meta;
            }
        }
    }

    /// <summary>A board of a new team with an owner and a member; returns their callers, the board id and its code.</summary>
    private static async Task<(string Owner, string Member, Guid Board, string Code)> TeamBoardAsync(RoomHttpApp app, string title) {
        var owner = await app.UserAsync();
        var member = await app.UserAsync();
        var team = await app.TeamAsync((owner, "owner"), (member, "member"));
        var created = await RoomHttpApp.JsonAsync(
            await app.PostAsync($"/api/teams/{team}/boards", RoomHttpApp.User(member), new { title }), HttpStatusCode.Created);
        return (RoomHttpApp.User(owner), RoomHttpApp.User(member), created.GetProperty("id").GetGuid(), created.GetProperty("code").GetString()!);
    }
}
