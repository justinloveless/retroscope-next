using NpgsqlTypes;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>user_favorite_teams</c> → <c>team_favorites</c> (DESIGN §h.2). Favorites of merged accounts are united; the
/// earliest favorite time wins.
/// </summary>
public sealed class FavoritesMap : IImportStage {
    public int Order => 250;

    public string Name => "team favorites";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = (await context.Source.ReadAsync("public.teams", cancellationToken)).Select(x => x.Uuid("id")!.Value).ToHashSet();
        var tally = context.Report.Table("public.user_favorite_teams");
        var favorites = new Dictionary<(Guid User, Guid Team), DateTime>();
        foreach (var row in await context.Source.ReadAsync("public.user_favorite_teams", cancellationToken)) {
            var team = row.Uuid("team_id")!.Value;
            var createdAt = row.Timestamp("created_at") ?? context.ImportedAt;
            if (context.UserRef("public.user_favorite_teams", "user_id", row.Uuid("user_id")) is not { } user) {
                tally.Skip("no_account");
            } else if (!teams.Contains(team)) {
                tally.Skip("no_team");
            } else if (favorites.TryGetValue((user, team), out var existing)) {
                favorites[(user, team)] = existing < createdAt ? existing : createdAt;
                tally.Skip("merged_duplicate");
            } else {
                favorites[(user, team)] = createdAt;
                tally.Imported++;
            }
        }

        await TargetRows.CopyAsync(context.Target, "team_favorites", [
            ("user_id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("created_at", NpgsqlDbType.TimestampTz),
        ], favorites.Select(x => new object?[] { x.Key.User, x.Key.Team, x.Value }), cancellationToken);
    }
}

/// <summary>
/// <c>user_recent_activity</c> → <c>recent_activity</c>. Runs after the rooms are loaded, because an entry whose team,
/// board or poker session was not imported is skipped. Entries of merged accounts are united; the latest access wins.
/// </summary>
public sealed class RecentActivityMap : IImportStage {
    public int Order => 600;

    public string Name => "recent activity";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var entities = new Dictionary<string, (string Kind, HashSet<Guid> Ids)> {
            ["team"] = ("team", await TargetRows.IdsAsync(context.Target, "teams", cancellationToken)),
            ["board"] = ("board", await TargetRows.IdsAsync(context.Target, "retro_boards", cancellationToken)),
            ["poker_session"] = ("poker", await TargetRows.IdsAsync(context.Target, "poker_sessions", cancellationToken)),
        };
        var tally = context.Report.Table("public.user_recent_activity");
        var activity = new Dictionary<(Guid User, string Kind, Guid Entity), DateTime>();
        foreach (var row in await context.Source.ReadAsync("public.user_recent_activity", cancellationToken)) {
            var entity = row.Uuid("entity_id")!.Value;
            var lastAt = row.Timestamp("last_accessed_at") ?? row.Timestamp("created_at") ?? context.ImportedAt;
            if (context.UserRef("public.user_recent_activity", "user_id", row.Uuid("user_id")) is not { } user) {
                tally.Skip("no_account");
            } else if (!entities.TryGetValue(row.Text("entity_type") ?? "", out var target) || !target.Ids.Contains(entity)) {
                tally.Skip("no_entity");
            } else if (activity.TryGetValue((user, target.Kind, entity), out var existing)) {
                activity[(user, target.Kind, entity)] = existing > lastAt ? existing : lastAt;
                tally.Skip("merged_duplicate");
            } else {
                activity[(user, target.Kind, entity)] = lastAt;
                tally.Imported++;
            }
        }

        await TargetRows.CopyAsync(context.Target, "recent_activity", [
            ("user_id", NpgsqlDbType.Uuid), ("entity_kind", NpgsqlDbType.Text), ("entity_id", NpgsqlDbType.Uuid), ("last_at", NpgsqlDbType.TimestampTz),
        ], activity.Select(x => new object?[] { x.Key.User, x.Key.Kind, x.Key.Entity, x.Value }), cancellationToken);
    }
}
