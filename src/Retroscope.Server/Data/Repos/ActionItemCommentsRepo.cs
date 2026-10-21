using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

/// <summary>Where an action item's comments live: the card it was created from, on another board (R22).</summary>
public sealed record ActionItemSource(BoardId Board, CardId Card);

public sealed class ActionItemCommentsRepo(NpgsqlDataSource dataSource) {
    /// <returns>Null when the item is not the team's, or its source card or board is gone.</returns>
    public async Task<ActionItemSource?> SourceAsync(TeamId team, ActionItemId item, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select i.source_board_id, i.source_card_id
            from team_action_items i
            join retro_boards b on b.id = i.source_board_id and b.deleted_at is null
            where i.id = $1 and i.team_id = $2 and i.source_card_id is not null
            """);
        command.Parameters.Add(new() { Value = item.Value });
        command.Parameters.Add(new() { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        return new ActionItemSource(new BoardId(reader.GetGuid(0)), new CardId(reader.GetGuid(1)));
    }
}
