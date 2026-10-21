using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>endorsement_types</c> → <c>endorsement_types</c> and <c>endorsement_settings</c> → <c>teams.max_endorsements_per_board</c>
/// (DESIGN §h.2). The type's <c>icon_url</c> always held an emoji; one longer than 8 characters becomes ⭐. A team
/// left without types gets the original's three defaults, with ids derived from the team so re-runs agree.
/// </summary>
public sealed class EndorsementTypesMap : IImportStage {
    public const int MaxName = 60;
    public const int MaxDescription = 200;
    public const int MaxEmoji = 8;
    public const string FallbackEmoji = "⭐";

    /// <summary><c>seed_default_endorsement_types</c> of the original (migration 20260311043131).</summary>
    public static readonly IReadOnlyList<(string Name, string Description, string Emoji)> Defaults = [
        ("Problem Solver", "Tackles tough technical challenges head-on", "🧩"),
        ("Team Player", "Goes above and beyond to help teammates", "🤝"),
        ("Innovator", "Brings creative ideas and fresh perspectives", "💡"),
    ];

    public int Order => 220;

    public string Name => "endorsement types";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = (await context.Source.ReadAsync("public.teams", cancellationToken)).Select(x => x.Uuid("id")!.Value).ToHashSet();
        var tally = context.Report.Table("public.endorsement_types");
        var rows = new List<object?[]>();
        var seeded = new HashSet<Guid>();
        foreach (var type in await context.Source.ReadAsync("public.endorsement_types", cancellationToken)) {
            var team = type.Uuid("team_id")!.Value;
            if (!teams.Contains(team)) {
                tally.Skip("no_team");
                continue;
            }
            var id = type.Uuid("id")!.Value;
            var name = TargetRows.Clip(TargetRows.Plain(type.Text("name") ?? ""), MaxName);
            var description = TargetRows.Clip(type.Text("description")?.Trim() ?? "", MaxDescription);
            rows.Add([
                id,
                team,
                name.Length > 0 ? name : "Endorsement",
                description.Length > 0 ? description : null,
                Emoji(context, id, type.Text("icon_url")),
                type.Number("position") ?? 0,
                type.Bool("is_default") ?? false,
                type.Timestamp("created_at") ?? context.ImportedAt,
            ]);
            seeded.Add(team);
            tally.Imported++;
        }

        foreach (var team in teams.Where(x => !seeded.Contains(x)).Order()) {
            for (var i = 0; i < Defaults.Count; i++) {
                var (name, description, emoji) = Defaults[i];
                rows.Add([DefaultTypeId(team, i + 1), team, name, description, emoji, i + 1, true, context.ImportedAt]);
            }
            context.Report.Count("seededEndorsementTypes");
        }

        await TargetRows.CopyAsync(context.Target, "endorsement_types", [
            ("id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("name", NpgsqlDbType.Text), ("description", NpgsqlDbType.Text),
            ("emoji", NpgsqlDbType.Text), ("sort_key", NpgsqlDbType.Integer), ("is_default", NpgsqlDbType.Boolean), ("created_at", NpgsqlDbType.TimestampTz),
        ], rows, cancellationToken);
        await ApplySettingsAsync(context, teams, cancellationToken);
    }

    public static Guid DefaultTypeId(Guid team, int position) => UuidV5.Create(team, $"endorsement-type:{position}");

    private static string Emoji(ImportContext context, Guid id, string? icon) {
        var emoji = icon?.Trim() ?? "";
        if (emoji.Length > 0 && TargetRows.Length(emoji) <= MaxEmoji) {
            return emoji;
        }
        context.Report.Note("replacedEndorsementEmoji", id.ToString());
        return FallbackEmoji;
    }

    private static async Task ApplySettingsAsync(ImportContext context, HashSet<Guid> teams, CancellationToken cancellationToken) {
        var tally = context.Report.Table("public.endorsement_settings");
        var limits = new List<(Guid Team, short Limit)>();
        foreach (var settings in await context.Source.ReadAsync("public.endorsement_settings", cancellationToken)) {
            var team = settings.Uuid("team_id")!.Value;
            if (!teams.Contains(team)) {
                tally.Skip("no_team");
                continue;
            }
            limits.Add((team, (short)Math.Clamp(settings.Number("max_endorsements_per_user_per_board") ?? 3, 0, 50)));
            tally.Imported++;
        }
        if (limits.Count == 0) {
            return;
        }

        await using var update = new NpgsqlCommand("""
            update teams set max_endorsements_per_board = s.limit_per_board
            from unnest($1::uuid[], $2::smallint[]) as s (team_id, limit_per_board)
            where teams.id = s.team_id
            """, context.Target) {
            Parameters = {
                new() { Value = limits.Select(x => x.Team).ToArray() },
                new() { Value = limits.Select(x => x.Limit).ToArray() },
            },
        };
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// <c>endorsements</c> → <c>endorsements</c>. Runs after the rooms are loaded, because an endorsement needs its board.
/// Both ends must be imported users (following merges); self-endorsements, including ones a merge creates, are
/// skipped, and merged duplicates collapse on the unique key, keeping the oldest.
/// </summary>
public sealed class EndorsementsMap : IImportStage {
    public int Order => 600;

    public string Name => "endorsements";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var boards = await TargetRows.IdsAsync(context.Target, "retro_boards", cancellationToken);
        var types = await TargetRows.IdsAsync(context.Target, "endorsement_types", cancellationToken);
        var teams = await TargetRows.IdsAsync(context.Target, "teams", cancellationToken);
        var tally = context.Report.Table("public.endorsements");
        var seen = new HashSet<(Guid, Guid, Guid, Guid)>();
        var rows = new List<object?[]>();
        var endorsements = await context.Source.ReadAsync("public.endorsements", cancellationToken);
        foreach (var row in endorsements.OrderBy(x => x.Timestamp("created_at")).ThenBy(x => x.Uuid("id"))) {
            var board = row.Uuid("board_id")!.Value;
            var team = row.Uuid("team_id")!.Value;
            var type = row.Uuid("endorsement_type_id")!.Value;
            var from = context.UserRef("public.endorsements", "from_user_id", row.Uuid("from_user_id"));
            var to = context.UserRef("public.endorsements", "to_user_id", row.Uuid("to_user_id"));
            var skip = !boards.Contains(board) ? "no_board"
                : !teams.Contains(team) ? "no_team"
                : !types.Contains(type) ? "no_type"
                : from is null || to is null ? "no_account"
                : from == to ? "self"
                : !seen.Add((board, from.Value, to.Value, type)) ? "duplicate"
                : null;
            if (skip is not null) {
                tally.Skip(skip);
                continue;
            }
            rows.Add([row.Uuid("id")!.Value, board, team, type, from, to, row.Timestamp("created_at") ?? context.ImportedAt]);
            tally.Imported++;
        }

        await TargetRows.CopyAsync(context.Target, "endorsements", [
            ("id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("type_id", NpgsqlDbType.Uuid),
            ("from_user_id", NpgsqlDbType.Uuid), ("to_user_id", NpgsqlDbType.Uuid), ("created_at", NpgsqlDbType.TimestampTz),
        ], rows, cancellationToken);
    }
}
