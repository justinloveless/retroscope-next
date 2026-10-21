using System.Text.RegularExpressions;
using NpgsqlTypes;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>board_templates</c> + <c>template_columns</c> → the same tables (DESIGN §h.2). Templates without a team are
/// archived with their columns. When a team has several defaults the newest keeps the flag. Column order is the
/// source <c>position</c> (ties by creation, then id) renumbered 1…n.
/// </summary>
public sealed class TemplatesMap : IImportStage {
    public const int MaxName = 100;
    public const int MaxTitle = 100;
    public const int MaxEmoji = 8;
    public const string DefaultEmoji = "👍";

    public int Order => 230;

    public string Name => "board templates";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = (await context.Source.ReadAsync("public.teams", cancellationToken)).Select(x => x.Uuid("id")!.Value).ToHashSet();
        var templates = await context.Source.ReadAsync("public.board_templates", cancellationToken);
        var templateTally = context.Report.Table("public.board_templates");
        var kept = new List<SourceRow>();
        var archived = new HashSet<Guid>();
        foreach (var template in templates) {
            if (template.Uuid("team_id") is { } team && teams.Contains(team)) {
                kept.Add(template);
                templateTally.Imported++;
            } else {
                context.Archive.AddRow(template);
                archived.Add(template.Uuid("id")!.Value);
                templateTally.Archived++;
            }
        }

        var defaults = kept.Where(x => x.Bool("is_default") == true)
            .GroupBy(x => x.Uuid("team_id")!.Value)
            .Select(x => x.OrderByDescending(t => t.Timestamp("created_at")).ThenByDescending(t => t.Uuid("id")).First())
            .Select(x => x.Uuid("id")!.Value)
            .ToHashSet();
        foreach (var demoted in kept.Where(x => x.Bool("is_default") == true && !defaults.Contains(x.Uuid("id")!.Value))) {
            context.Report.Note("demotedDefaultTemplates", demoted.Uuid("id")!.Value.ToString());
        }

        await TargetRows.CopyAsync(context.Target, "board_templates", [
            ("id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("name", NpgsqlDbType.Text), ("is_default", NpgsqlDbType.Boolean),
            ("allow_anonymous", NpgsqlDbType.Boolean), ("voting_enabled", NpgsqlDbType.Boolean), ("max_votes_per_user", NpgsqlDbType.Smallint),
            ("show_author_names", NpgsqlDbType.Boolean), ("stages_enabled", NpgsqlDbType.Boolean), ("enforce_readiness", NpgsqlDbType.Boolean),
            ("allow_self_votes", NpgsqlDbType.Boolean), ("vote_emoji", NpgsqlDbType.Text), ("created_at", NpgsqlDbType.TimestampTz),
            ("updated_at", NpgsqlDbType.TimestampTz),
        ], kept.Select(x => Template(context, x, defaults)), cancellationToken);

        var keptIds = kept.Select(x => x.Uuid("id")!.Value).ToHashSet();
        await CopyColumnsAsync(context, keptIds, archived, cancellationToken);
    }

    /// <summary>
    /// <c>max_votes_per_user</c> as the target stores it: null (unlimited) for a missing or non-positive limit, at most 99.
    /// Shared with the retro board mapping, whose config has the same column.
    /// </summary>
    public static short? VoteLimit(int? limit) => limit is > 0 ? (short)Math.Min(limit.Value, 99) : null;

    /// <summary>A vote emoji the target accepts (1–8 characters), else 👍.</summary>
    public static string VoteEmoji(string? emoji) =>
        emoji?.Trim() is { Length: > 0 } trimmed && TargetRows.Length(trimmed) <= MaxEmoji ? trimmed : DefaultEmoji;

    private static object?[] Template(ImportContext context, SourceRow template, HashSet<Guid> defaults) {
        var id = template.Uuid("id")!.Value;
        var name = TargetRows.Clip(TargetRows.Plain(template.Text("name") ?? ""), MaxName);
        var createdAt = template.Timestamp("created_at") ?? context.ImportedAt;
        return [
            id,
            template.Uuid("team_id"),
            name.Length > 0 ? name : "Untitled template",
            defaults.Contains(id),
            template.Bool("allow_anonymous") ?? true,
            template.Bool("voting_enabled") ?? true,
            VoteLimit(template.Number("max_votes_per_user")),
            template.Bool("show_author_names") ?? true,
            template.Bool("retro_stages_enabled") ?? false,
            template.Bool("enforce_stage_readiness") ?? false,
            template.Bool("allow_self_votes") ?? true,
            VoteEmoji(template.Text("vote_emoji")),
            createdAt,
            template.Timestamp("updated_at") ?? createdAt,
        ];
    }

    private static async Task CopyColumnsAsync(ImportContext context, HashSet<Guid> kept, HashSet<Guid> archived, CancellationToken cancellationToken) {
        var tally = context.Report.Table("public.template_columns");
        var columns = new List<SourceRow>();
        foreach (var column in await context.Source.ReadAsync("public.template_columns", cancellationToken)) {
            var template = column.Uuid("template_id")!.Value;
            if (kept.Contains(template)) {
                columns.Add(column);
                tally.Imported++;
            } else if (archived.Contains(template)) {
                context.Archive.AddRow(column);
                tally.Archived++;
            } else {
                tally.Skip("no_template");
            }
        }

        var rows = new List<object?[]>();
        foreach (var template in columns.GroupBy(x => x.Uuid("template_id")!.Value)) {
            var ordered = template
                .OrderBy(x => x.Number("position"))
                .ThenBy(x => x.Timestamp("created_at"))
                .ThenBy(x => x.Uuid("id"))
                .ToList();
            for (var i = 0; i < ordered.Count; i++) {
                var column = ordered[i];
                var title = TargetRows.Clip(TargetRows.Plain(column.Text("title") ?? ""), MaxTitle);
                rows.Add([
                    column.Uuid("id")!.Value,
                    template.Key,
                    title.Length > 0 ? title : "Untitled",
                    ColumnPalette.FromTailwind(column.Text("color")),
                    i + 1,
                    column.Bool("is_action_items") == true,
                ]);
            }
        }

        await TargetRows.CopyAsync(context.Target, "template_columns", [
            ("id", NpgsqlDbType.Uuid), ("template_id", NpgsqlDbType.Uuid), ("title", NpgsqlDbType.Text), ("color", NpgsqlDbType.Text),
            ("sort_key", NpgsqlDbType.Integer), ("is_action_items", NpgsqlDbType.Boolean),
        ], rows, cancellationToken);
    }
}

/// <summary>
/// The original stored a column colour as Tailwind classes (<c>bg-green-100 border-green-300</c>). The target keeps
/// a palette name; the first <c>bg-*</c> hue in the palette wins and anything else is gray. Shared with the retro
/// column mapping.
/// </summary>
public static partial class ColumnPalette {
    public static readonly IReadOnlySet<string> Colors = new HashSet<string>(StringComparer.Ordinal) {
        "green", "red", "blue", "yellow", "purple", "pink", "indigo", "orange", "gray",
    };

    public const string Fallback = "gray";

    public static string FromTailwind(string? classes) {
        if (classes is null) {
            return Fallback;
        }
        foreach (Match match in Background().Matches(classes)) {
            var hue = match.Groups[1].Value.ToLowerInvariant();
            if (Colors.Contains(hue)) {
                return hue;
            }
        }
        return Colors.Contains(classes.Trim().ToLowerInvariant()) ? classes.Trim().ToLowerInvariant() : Fallback;
    }

    [GeneratedRegex(@"(?:^|\s)(?:dark:)?bg-([a-z]+)-\d{2,3}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Background();
}
