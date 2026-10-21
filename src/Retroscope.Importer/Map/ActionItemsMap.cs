using NpgsqlTypes;
using Retroscope.Core.Markup;
using Retroscope.Importer.Html;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>team_action_items</c> (DESIGN §h.2), after the retro stage so the source board and card can be linked. An
/// action item copied from an anonymous card never records who created it (S6), and neither do its images. The
/// source <c>team_action_item_comments</c> table is archive-only (and absent in production).
/// </summary>
public sealed class ActionItemsMap : IImportStage {
    private const string Table = "public.team_action_items";

    public int Order => 450;

    public string Name => "action items";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var target = context.Target;
        var teams = await RetroTarget.IdsAsync(target, "select id from teams", cancellationToken);
        var boards = await RetroTarget.IdsAsync(target, "select id from retro_boards", cancellationToken);
        var cards = await RetroTarget.IdsAsync(target, "select id from retro_cards", cancellationToken);
        var anonymousCards = (await context.Source.ReadAsync("public.retro_items", cancellationToken))
            .Where(RetroAuthorship.IsAnonymous)
            .Select(x => x.Uuid("id")!.Value)
            .ToHashSet();

        var tally = context.Report.Table(Table);
        var images = new BoardImages();
        var items = new List<object?[]>();
        foreach (var row in await context.Source.ReadAsync(Table, cancellationToken)) {
            var teamId = row.Uuid("team_id")!.Value;
            if (!teams.Contains(teamId)) {
                tally.Skip("teamMissing");
                continue;
            }

            var boardId = row.Uuid("source_board_id") is { } board && boards.Contains(board) ? board : (Guid?)null;
            var cardId = row.Uuid("source_item_id") is { } card && cards.Contains(card) ? card : (Guid?)null;
            var anonymous = row.Uuid("source_item_id") is { } sourceCard && anonymousCards.Contains(sourceCard);
            var createdAt = row.Timestamp("created_at") ?? context.ImportedAt;
            var createdBy = Anonymity.CreatedBy(anonymous, context.PrincipalRef("team_action_items", "created_by", row.Uuid("created_by")));
            var done = row.Bool("done") == true;

            var text = HtmlToMarkup.Convert(row.Text("text"), new HtmlContext(MarkupField.ActionItem, boardId, id => context.Principals.User(id)));
            text.Report(context.Report, Table, row.PrimaryKey);
            images.Add(text, createdBy, anonymous, createdAt);

            items.Add([
                row.Uuid("id")!.Value,
                teamId,
                text.Markup,
                boardId,
                cardId,
                cardId is null ? 0 : 1,
                context.UserRef("team_action_items", "assigned_to", row.Uuid("assigned_to")),
                done ? row.Timestamp("done_at") ?? createdAt : null,
                done ? context.PrincipalRef("team_action_items", "done_by", row.Uuid("done_by")) : null,
                createdBy,
                createdAt,
            ]);
            tally.Imported++;
        }

        await images.WriteAsync(target, context.ImportedAt, cancellationToken);
        await RetroCopy.WriteAsync(target, "team_action_items",
            [("id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("text", NpgsqlDbType.Text), ("source_board_id", NpgsqlDbType.Uuid),
             ("source_card_id", NpgsqlDbType.Uuid), ("source_version", NpgsqlDbType.Integer), ("assigned_to", NpgsqlDbType.Uuid),
             ("done_at", NpgsqlDbType.TimestampTz), ("done_by", NpgsqlDbType.Uuid), ("created_by", NpgsqlDbType.Uuid), ("created_at", NpgsqlDbType.TimestampTz)],
            items.OrderBy(x => (Guid)x[0]!),
            x => x,
            cancellationToken);
    }
}
