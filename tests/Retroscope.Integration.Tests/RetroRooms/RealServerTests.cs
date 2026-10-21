using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Integration.Tests.Transport;

namespace Retroscope.Integration.Tests.RetroRooms;

/// <summary>Two signed-in members on a team board, through the real server end to end.</summary>
[Trait("Category", "RetroRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RealServerTests(PostgresFixture postgres) {
    [Fact]
    public async Task Two_signed_in_members_see_each_others_cards_and_anonymous_ones_name_nobody() {
        await using var server = await RealServer.StartAsync(postgres);
        var ada = await server.SignUpAsync("Ada");
        var bob = await server.SignUpAsync("Bob");
        var team = await server.TeamAsync((ada, TeamRole.Owner), (bob, TeamRole.Member));
        var board = Guid.CreateVersion7();
        var column = new ColumnId(Guid.CreateVersion7());
        await server.ExecAsync(
            "insert into retro_boards (id, code, team_id, title, created_by, visibility) values ($1, $2, $3, 'Sprint', $4, 'team')",
            board, $"B{board:N}"[..12], team.Value, ada.Who.Id.Value);
        await server.ExecAsync(
            "insert into retro_columns (id, board_id, title, color, sort_key, is_action_items) values ($1, $2, 'Went well', 'green', 0, false)",
            column.Value, board);
        var room = RoomKey.Board(new BoardId(board));
        await using var adaTab = await server.ConnectAsync(ada);
        await using var bobTab = await server.ConnectAsync(bob);
        Assert.IsType<SnapFrame>(await adaTab.SubscribeAsync(room));
        Assert.IsType<SnapFrame>(await bobTab.SubscribeAsync(room));

        var seen = bobTab.Count;
        var named = new CardId(Guid.CreateVersion7());
        await adaTab.AckedAsync(room, new RetroCardAdd(named, column, "Shipped it", Anonymous: false));
        var added = (RetroCardAdded)await bobTab.WaitForEventAsync<RetroCardAdded>(seen);
        Assert.Equal((named, "Shipped it", (PrincipalId?)ada.Who.Id), (added.Card.Id, added.Card.Body, added.Card.Author?.Pid));

        seen = bobTab.Count;
        var hidden = new CardId(Guid.CreateVersion7());
        await adaTab.AckedAsync(room, new RetroCardAdd(hidden, column, "Too many meetings", Anonymous: true));
        var anonymous = (RetroCardAdded)await bobTab.WaitForEventAsync<RetroCardAdded>(seen);
        Assert.Equal(hidden, anonymous.Card.Id);
        Assert.Null(anonymous.Card.Author);
        Assert.DoesNotContain(bobTab.Raw.Skip(seen), x => x.Contains(ada.Who.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1L, await server.ScalarAsync<long>("select count(*) from retro_cards where id = $1 and author_id is null", hidden.Value));
    }
}
