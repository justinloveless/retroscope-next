using Retroscope.Core.Markup;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// Every imported body is canonical Retro Markup within its limits, and its parsed tree holds only the node kinds
/// the retro dialect allows (§h.5, S28). The check runs on the <c>Doc</c>, not a string regex, so pasted
/// <c>&amp;lt;div&amp;gt;</c> text passes.
/// </summary>
public sealed class MarkupBodiesCheck : IVerifyCheck {
    public string Name => "markup";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var rows = await TargetQueries.RowsAsync(context.Target, """
            select 'retro_cards', id, body from retro_cards
            union all select 'retro_comments', id, body from retro_comments
            union all select 'team_action_items', id, text from team_action_items
            order by 1, 2
            """, cancellationToken);
        var failures = new List<string>();
        foreach (var row in rows) {
            var (table, id, body) = ((string)row[0]!, (Guid)row[1]!, (string)row[2]!);
            var field = table switch {
                "retro_cards" => MarkupField.Card,
                "retro_comments" => MarkupField.Comment,
                _ => MarkupField.ActionItem,
            };
            if (Problem(body, field) is { } problem) {
                failures.Add($"{table} {id}: {problem}");
            }
        }
        return TargetQueries.Summarize(failures);
    }

    public static string? Problem(string body, MarkupField field) {
        var check = MarkupLimits.Check(body, field);
        if (!check.IsValid) {
            return check.Error.ToString();
        }
        if (MarkupWriter.Write(check.Doc!) != body) {
            return "not canonical";
        }
        return check.Doc!.Descendants().FirstOrDefault(x => !IsAllowed(x)) is { } node ? $"{node.GetType().Name} is not allowed" : null;
    }

    private static bool IsAllowed(DocNode node) =>
        node is DocParagraph or DocBulletList or DocOrderedList or DocListItem or DocQuote or DocCodeBlock or DocText
            or DocStyled { Style: not DocStyle.Underline } or DocCode or DocLink or DocLineBreak or DocMention or DocImage;
}
