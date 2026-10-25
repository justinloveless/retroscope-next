using NpgsqlTypes;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>user_recent_activity</c> → <c>recent_activity</c>. Runs after the teams are loaded, because an entry whose team was
/// not imported is skipped. Only team entries are kept (UX18): board and poker entries fed the removed dashboard, so
/// they are skipped as <c>not_team</c>. Entries of merged accounts are united; the latest access wins.
/// </summary>
public sealed class RecentActivityMap : IImportStage {
    public int Order => 600;

    public string Name => "recent activity";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = await TargetRows.IdsAsync(context.Target, "teams", cancellationToken);
        var tally = context.Report.Table("public.user_recent_activity");
        var activity = new Dictionary<(Guid User, Guid Team), DateTime>();
        foreach (var row in await context.Source.ReadAsync("public.user_recent_activity", cancellationToken)) {
            var entity = row.Uuid("entity_id")!.Value;
            var lastAt = row.Timestamp("last_accessed_at") ?? row.Timestamp("created_at") ?? context.ImportedAt;
            if (context.UserRef("public.user_recent_activity", "user_id", row.Uuid("user_id")) is not { } user) {
                tally.Skip("no_account");
            } else if (row.Text("entity_type") != "team") {
                tally.Skip("not_team");
            } else if (!teams.Contains(entity)) {
                tally.Skip("no_entity");
            } else if (activity.TryGetValue((user, entity), out var existing)) {
                activity[(user, entity)] = existing > lastAt ? existing : lastAt;
                tally.Skip("merged_duplicate");
            } else {
                activity[(user, entity)] = lastAt;
                tally.Imported++;
            }
        }

        await TargetRows.CopyAsync(context.Target, "recent_activity", [
            ("user_id", NpgsqlDbType.Uuid), ("entity_kind", NpgsqlDbType.Text), ("entity_id", NpgsqlDbType.Uuid), ("last_at", NpgsqlDbType.TimestampTz),
        ], activity.Select(x => new object?[] { x.Key.User, "team", x.Key.Team, x.Value }), cancellationToken);
    }
}
