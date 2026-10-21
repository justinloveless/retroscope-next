using Retroscope.Core.Protocol;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;
using static Retroscope.Integration.Tests.RetroRooms.RetroScope;

namespace Retroscope.Integration.Tests.RetroRooms;

[Trait("Category", "RetroRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RetroAccessTests(PostgresFixture postgres) {
    [Fact]
    public async Task A_team_only_board_admits_members_with_the_roster_and_kicks_everyone_else() {
        await using var scope = await RetroScope.StartAsync(postgres);
        await scope.ExecuteAsync("update retro_boards set visibility = 'team' where id = $1", TeamBoard.Value);
        var registry = scope.NewRegistry();

        var bob = await SnapOrKickAsync(registry, Bob);
        var carol = await SnapOrKickAsync(registry, Carol);
        var gina = await SnapOrKickAsync(registry, Gina);

        var roster = Assert.IsType<RetroSnapshot>(Assert.IsType<SnapFrame>(bob).D).Members;
        Assert.Equal(["Alice", "Bob"], roster!.Select(x => x.Name));
        Assert.Equal(KickCode.Forbidden, Assert.IsType<KickFrame>(carol).Code);
        Assert.Equal(KickCode.Forbidden, Assert.IsType<KickFrame>(gina).Code);
    }

    [Fact]
    public async Task Non_members_on_a_link_board_get_the_snapshot_without_the_roster() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();

        var snap = Assert.IsType<SnapFrame>(await SnapOrKickAsync(registry, Carol));

        var board = Assert.IsType<RetroSnapshot>(snap.D);
        Assert.Null(board.Members);
        Assert.Null(board.EndorsementTypes);
        Assert.Equal("Sprint 1", board.Meta.Title);
    }

    [Theory]
    [InlineData("update retro_boards set deleted_at = now() where id = $1")]
    [InlineData("update teams set deleted_at = now() where id = (select team_id from retro_boards where id = $1)")]
    public async Task A_deleted_board_or_a_board_of_a_deleted_team_is_not_found(string delete) {
        await using var scope = await RetroScope.StartAsync(postgres);
        await scope.ExecuteAsync(delete, TeamBoard.Value);
        var registry = scope.NewRegistry();

        Assert.Equal(KickCode.NotFound, Assert.IsType<KickFrame>(await SnapOrKickAsync(registry, Alice)).Code);
    }

    private static async Task<ServerFrame> SnapOrKickAsync(ActorRegistry registry, Core.Room.Viewer viewer) {
        var peer = new TestPeer(viewer);
        await registry.SubscribeAsync(TeamBoardRoom, peer, viewer, null, null, Token);
        return await peer.NextAsync();
    }
}
